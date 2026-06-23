using MfgInspectionSystem.Communication;
using MfgInspectionSystem.Communication.Messages;
using MfgInspectionSystem.Config;
using MfgInspectionSystem.Data;
using MfgInspectionSystem.Models;
using MfgInspectionSystem.Observability;
using MfgInspectionSystem.Vision;
using Serilog;
using Serilog.Context;

namespace MfgInspectionSystem.Core;

public class InspectionPipeline
{
    private readonly YoloClient _yolo;
    private readonly OpenCvPostProcessor _opencv;
    private readonly ProductDecisionQueue _queue;
    private readonly DbWriter _db;
    private readonly AppConfig _config;
    private readonly MqttSubscriber? _mqtt;     // nullable — optional env snapshot

    private readonly SemaphoreSlim _inspectionLock = new(1, 1);

    public event Action<string>? OnLog;
    public event Action<ProductDecision>? OnInspectionCompleted;

    public InspectionPipeline(YoloClient yolo, OpenCvPostProcessor opencv,
        ProductDecisionQueue queue, DbWriter db, AppConfig config,
        MqttSubscriber? mqtt = null)
    {
        _yolo   = yolo;
        _opencv = opencv;
        _queue  = queue;
        _db     = db;
        _config = config;
        _mqtt   = mqtt;
    }

    public async Task RunAsync()
    {
        if (!await _inspectionLock.WaitAsync(0))
        {
            Log.Warning("Inspection already in progress, skipping S2 trigger");
            return;
        }

        string productId = ProductDecision.GenerateId();
        string correlationId = Guid.NewGuid().ToString();
        using var productLog = LogContext.PushProperty("product_id", productId);
        using var correlationLog = LogContext.PushProperty("correlation_id", correlationId);
        Log.Information("Inspection started: {Id}", productId);
        OnLog?.Invoke($"[Inspection] {productId} 시작");

        try
        {
            // Step 1: Trigger delay
            await Task.Delay(_config.Vision.CameraTriggerDelayMs);

            // Step 2: Capture image
            byte[]? imageBytes = await _yolo.CaptureSnapshotAsync(_config.Vision.Cam1Url);
            if (imageBytes == null || imageBytes.Length == 0)
            {
                OnLog?.Invoke($"[Inspection] {productId} 이미지 캡처 실패 → HOLD");
                EnqueueAndWrite(BuildHold(productId, correlationId, "capture_failed"));
                return;
            }

            // Step 3: Save image
            var (imagePath, imageDbPath) = SaveImage(productId, imageBytes);

            // Step 4: YOLO inference
            // 전처리 ON 시 CLAHE 대비 향상 후 추론 — 저장/표시는 원본 imageBytes 그대로
            // 효과 없으면 appsettings.json Vision.EnableYoloPreprocessing: false 로 끄기
            byte[] inferBytes = _config.Vision.EnableYoloPreprocessing
                ? OpenCvPostProcessor.PreprocessForYolo(imageBytes)
                : imageBytes;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var yoloResult = await _yolo.InferCam1Async(inferBytes);
            sw.Stop();
            AppMetrics.YoloLatency.Observe(sw.Elapsed.TotalSeconds);
            if (!yoloResult.Success || yoloResult.Detections.Count == 0)
            {
                string reason = yoloResult.Success
                    ? "detections=0 (빈 배열 — 서버 응답 키 또는 모델 미감지)"
                    : $"error={yoloResult.Error}";
                OnLog?.Invoke($"[Inspection] {productId} YOLO 실패 → HOLD ({reason})");
                EnqueueAndWrite(BuildHold(productId, correlationId, "yolo_failed", imagePath, imageDbPath));
                return;
            }

            // Step 5: Best detection
            // _defective > _normal > plain class > background
            // (NMS가 완벽하지 않을 때 같은 부품에 대해 plain + _normal이 동시에
            //  검출될 수 있음 → 명시적 판정 클래스를 우선 선택)
            var best = SelectBestDetection(yoloResult.Detections);
            if (best.Confidence < _config.Yolo.ConfidenceThreshold)
            {
                OnLog?.Invoke($"[Inspection] {productId} low confidence {best.Confidence:F2} → HOLD");
                EnqueueAndWrite(BuildHold(productId, correlationId, "low_confidence", imagePath, imageDbPath, best,
                    yoloResult.AllDetectionsJson));
                return;
            }

            // Step 6: OpenCV post-processing
            var postResult = _opencv.Process(imageBytes, best);

            // Step 7: Final verdict
            Verdict verdict = DetermineVerdict(best, postResult);

            var decision = new ProductDecision
            {
                ProductId          = productId,
                CorrelationId      = correlationId,
                ProductType        = NormalizeClass(best.ClassName),
                Verdict            = verdict,
                DefectDetail       = BuildDefectDetail(best, postResult, verdict),
                YoloConfidence     = best.Confidence,
                YoloClass          = best.ClassName,
                AllDetectionsJson  = yoloResult.AllDetectionsJson,
                PinCount           = postResult.PinCount,
                BlurScore          = postResult.BlurScore,
                RoiAligned         = postResult.RoiAligned,
                Cam1ImagePath      = imagePath,
                Cam1ImageDbPath    = imageDbPath,
                ModelVersion       = yoloResult.ModelVersion,
                InferenceTimeMs    = yoloResult.InferenceTimeMs,
                InspectedAt        = DateTime.UtcNow,
                // Snapshot ambient conditions from MQTT cache at the moment of inspection
                EnvironmentTemp     = _mqtt?.Temperature,
                EnvironmentHumidity = _mqtt?.Humidity
            };

            EnqueueAndWrite(decision);
            _ = _mqtt?.PublishInspectionResultAsync(decision);

            OnLog?.Invoke($"[Inspection] {productId} → {verdict} " +
                $"(class={best.ClassName}, conf={best.Confidence:F2}, pins={postResult.PinCount}, blur={postResult.BlurScore:F0})");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Inspection pipeline exception: {Id}", productId);
            OnLog?.Invoke($"[Inspection] {productId} 예외: {ex.Message} → HOLD");
            EnqueueAndWrite(BuildHold(productId, correlationId, "exception"));
        }
        finally
        {
            _inspectionLock.Release();
        }
    }

    private Verdict DetermineVerdict(YoloDetection det, OpenCvPostResult post)
    {
        string cls = (det.ClassName ?? "").ToLower();

        // D의 9클래스 체계: _defective면 바로 DEFECT
        if (cls.Contains("defective"))
            return Verdict.DEFECT;

        // background면 HOLD
        if (cls == "background")
            return Verdict.HOLD;

        // Blur check (v2: use BlurThresholdPass as the single gate;
        // BlurThresholdHold is used only in BuildDefectDetail for logging granularity)
        double blurGate = _config.Vision.HasV2BlurThresholds
            ? _config.Vision.BlurThresholdPass
            : _config.Vision.BlurLaplacianThreshold;

        if (post.BlurScore < blurGate)
            return Verdict.HOLD;

        // Pin count:
        //   - bbox 없으면(w=0 or h=0) OpenCV ROI 무효 → PinCount=0 오탐 방지
        //   - "_normal" 클래스는 YOLO가 정상 명시 → 핀 수 재확인 불필요 (모델 신뢰)
        //   - 평문 클래스(transistor/capacitor/regulator) + bbox 있음 → 핀 수 검사 적용
        bool isExplicitlyNormal = cls.Contains("_normal");
        bool hasBbox = det.W > 0 && det.H > 0;
        int expected = (hasBbox && !isExplicitlyNormal) ? ExpectedPins(NormalizeClass(cls)) : -1;
        if (expected > 0 && post.PinCount != expected)
            return Verdict.DEFECT;

        if (!post.RoiAligned)
            return Verdict.HOLD;

        return Verdict.PASS;
    }

    /// <summary>
    /// NMS 미완전으로 같은 물체에 대해 plain + _normal 혹은 plain + _defective가
    /// 동시에 검출될 때 올바른 판정 클래스를 선택한다.
    /// 우선순위: _defective(3) > _normal(2) > plain class(1) > background(0)
    /// 같은 우선순위 내에서는 Confidence 높은 쪽 선택.
    /// </summary>
    private static YoloDetection SelectBestDetection(List<YoloDetection> detections)
    {
        static int Priority(string cls) =>
            cls.Contains("defective") ? 3 :
            cls.Contains("_normal")   ? 2 :
            cls == "background"       ? 0 : 1;

        return detections
            .OrderByDescending(d => Priority(d.ClassName.ToLower()))
            .ThenByDescending(d => d.Confidence)
            .First();
    }

    private static int ExpectedPins(string cls) => cls switch
    {
        "transistor" => 3,
        "capacitor"  => 2,
        "regulator"  => 3,
        _            => -1
    };

    private static string NormalizeClass(string cls) => cls switch
    {
        "transistor" or "transistor_defective" or "transistor_normal" => "transistor",
        "capacitor"  or "capacitor_defective"  or "capacitor_normal"  => "capacitor",
        "regulator"  or "regulator_defective"  or "regulator_normal"  => "regulator",
        _                                                              => cls
    };

    private string? BuildDefectDetail(YoloDetection det, OpenCvPostResult post, Verdict verdict)
    {
        if (verdict == Verdict.PASS) return null;

        if (verdict == Verdict.HOLD)
        {
            if (_config.Vision.HasV2BlurThresholds)
            {
                if (post.BlurScore < _config.Vision.BlurThresholdHold)
                    return $"blur_severe={post.BlurScore:F0}";
                if (post.BlurScore < _config.Vision.BlurThresholdPass)
                    return $"blur_borderline={post.BlurScore:F0}";
            }
            else if (post.BlurScore < _config.Vision.BlurLaplacianThreshold)
            {
                return $"blur_score={post.BlurScore:F0}";
            }
            if (!post.RoiAligned)
                return "roi_not_aligned";
            return $"low_conf={det.Confidence:F2}";
        }

        if (verdict == Verdict.DEFECT)
        {
            string cls = (det.ClassName ?? "").ToLower();
            // DEFECT via class name (_defective) → bbox 없어도 확정 불량
            if (cls.Contains("defective"))
                return $"yolo_class={det.ClassName}";
            // DEFECT via pin count (bbox 있을 때만 도달)
            int expected = ExpectedPins(NormalizeClass(cls));
            return expected > 0
                ? $"pin_count={post.PinCount}/expected={expected}"
                : $"pin_count={post.PinCount}";
        }

        return null;
    }

    /// <summary>
    /// Enqueue the decision into the sorting queue and, only on success, persist the
    /// inspection result to the DB and fire the UI callback.
    /// On queue overflow the product is skipped: a CRITICAL event_log entry is written
    /// instead, and no inspection_result row is created (the physical product becomes
    /// untracked — an operator must intervene).
    /// </summary>
    private void EnqueueAndWrite(ProductDecision d)
    {
        bool enqueued = _queue.Enqueue(d);
        if (!enqueued)
        {
            Log.Error("Decision queue overflow: {ProductId} ({Verdict}) — product skipped",
                d.ProductId, d.Verdict);
            _db.WriteEventLog("queue_overflow", "CRITICAL", "inspection_pipeline",
                $"Decision queue overflow: {d.ProductId} ({d.Verdict}) — product not tracked");
            OnLog?.Invoke($"[Inspection] ⚠ 큐 가득참! 제품 스킵: {d.ProductId}");
            return;                 // no DB inspection row, no UI update — overflow is the record
        }

        _db.WriteInspectionResult(d);
        AppMetrics.Inspections.WithLabels(d.Verdict.ToString(), d.ProductType).Inc();
        OnInspectionCompleted?.Invoke(d);
    }

    private ProductDecision BuildHold(string id, string correlationId, string reason,
        string? imagePath = null, string? imageDbPath = null,
        YoloDetection? det = null, string? allDetectionsJson = null)
    {
        return new ProductDecision
        {
            ProductId           = id,
            CorrelationId       = correlationId,
            ProductType         = det != null ? NormalizeClass(det.ClassName) : "unknown",
            Verdict             = Verdict.HOLD,
            DefectDetail        = reason,
            YoloConfidence      = det?.Confidence ?? 0,
            YoloClass           = det?.ClassName,
            AllDetectionsJson   = allDetectionsJson,
            Cam1ImagePath       = imagePath,
            Cam1ImageDbPath     = imageDbPath,
            InspectedAt         = DateTime.UtcNow,
            EnvironmentTemp     = _mqtt?.Temperature,
            EnvironmentHumidity = _mqtt?.Humidity
        };
    }

    private (string fullPath, string dbPath) SaveImage(string productId, byte[] bytes)
    {
        string basePath   = _config.Vision.ImageBasePath;
        string dateFolder = DateTime.Now.ToString("yyyyMMdd");
        string dir        = Path.Combine(basePath, "inspection_images", dateFolder);
        Directory.CreateDirectory(dir);
        string fullPath = Path.Combine(dir, $"{productId}_cam1.jpg");
        File.WriteAllBytes(fullPath, bytes);

        // DB용: Flask static 기준 상대경로 (슬래시)
        string dbPath = $"inspection_images/{dateFolder}/{productId}_cam1.jpg";
        return (fullPath, dbPath);
    }
}
