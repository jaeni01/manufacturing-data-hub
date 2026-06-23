using System.Net.Http.Headers;
using MfgInspectionSystem.Communication.Messages;
using MfgInspectionSystem.Config;
using Newtonsoft.Json.Linq;
using Serilog;

namespace MfgInspectionSystem.Communication;

public class YoloClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly YoloConfig _cfg;
    private bool _disposed;

    public bool IsHealthy { get; private set; }
    public string ModelVersion { get; private set; } = "unknown";

    /// <summary>Fires when IsHealthy transitions: true = service recovered, false = service down.</summary>
    public event Action<bool>? OnHealthChanged;

    public YoloClient(YoloConfig cfg)
    {
        _cfg = cfg;
        _http = new HttpClient
        {
            BaseAddress = new Uri(cfg.ServiceUrl),
            Timeout = TimeSpan.FromMilliseconds(cfg.TimeoutMs * 3)
        };
    }

    public async Task<bool> CheckHealthAsync()
    {
        bool prev = IsHealthy;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var resp = await _http.GetAsync("/health", cts.Token);
            if (resp.IsSuccessStatusCode)
            {
                var json = JObject.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
                IsHealthy = json["status"]?.ToString() == "ok";
                ModelVersion = json["model_version"]?.ToString()
                    ?? json["model"]?.ToString()
                    ?? "unknown";
                if (prev != IsHealthy) OnHealthChanged?.Invoke(IsHealthy);
                return IsHealthy;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "YOLO health check failed");
        }
        IsHealthy = false;
        if (prev != IsHealthy) OnHealthChanged?.Invoke(false);
        return false;
    }

    public async Task<YoloInferResult> InferCam1Async(byte[] imageBytes)
    {
        Log.Information("[YOLO] InferCam1 begin: BaseAddress={Base}, image={N}bytes",
            _http.BaseAddress, imageBytes.Length);
        try
        {
            using var content = new MultipartFormDataContent();
            var imageContent = new ByteArrayContent(imageBytes);
            imageContent.Headers.ContentType = MediaTypeHeaderValue.Parse("image/jpeg");
            content.Add(imageContent, "image", "capture.jpg");

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(_cfg.TimeoutMs));
            var resp = await _http.PostAsync("/infer/cam1", content, cts.Token);
            resp.EnsureSuccessStatusCode();

            var rawBody = await resp.Content.ReadAsStringAsync();
            Log.Information("[YOLO] response ({Len}b): {Raw}",
                rawBody.Length,
                rawBody.Length <= 400 ? rawBody : rawBody[..400]);

            var json = JObject.Parse(rawBody);

            // 서버마다 detections 배열 키 이름이 다름 — 순서대로 시도
            var detectionsArr = json["detections"] as JArray
                ?? json["results"]     as JArray
                ?? json["predictions"] as JArray
                ?? json["objects"]     as JArray;
            var detections = ParseDetections(detectionsArr);

            Log.Information("[YOLO] parsed: count={N} top={Top} conf={Conf}",
                detections.Count,
                json["top_class"]?.ToString() ?? detections.FirstOrDefault()?.ClassName ?? "none",
                json["top_confidence"]?.Value<double>() ?? detections.FirstOrDefault()?.Confidence ?? 0);

            return new YoloInferResult
            {
                Success = true,
                InferenceTimeMs = json["inference_time_ms"]?.Value<int>()
                    ?? json["inference_ms"]?.Value<int>() ?? 0,
                TopClass = json["top_class"]?.ToString() ?? detections.FirstOrDefault()?.ClassName,
                TopConfidence = json["top_confidence"]?.Value<double>()
                    ?? detections.FirstOrDefault()?.Confidence ?? 0,
                ModelVersion = json["model_version"]?.ToString() ?? ModelVersion,
                AllDetectionsJson = detectionsArr?.ToString(),
                Detections = detections
            };
        }
        catch (TaskCanceledException)
        {
            Log.Warning("YOLO inference timeout ({Timeout}ms)", _cfg.TimeoutMs);
            return new YoloInferResult { Success = false, Error = "timeout" };
        }
        catch (HttpRequestException ex)
        {
            Log.Warning(ex, "YOLO service unavailable");
            bool wasHealthy = IsHealthy;
            IsHealthy = false;
            if (wasHealthy) OnHealthChanged?.Invoke(false);
            return new YoloInferResult { Success = false, Error = "service_down" };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "YOLO inference error");
            return new YoloInferResult { Success = false, Error = ex.Message };
        }
    }

    public async Task<byte[]?> CaptureSnapshotAsync(string snapshotUrl)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var resp = await _http.GetAsync(snapshotUrl, cts.Token);
            if (resp.IsSuccessStatusCode)
                return await resp.Content.ReadAsByteArrayAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Camera snapshot failed: {Url}", snapshotUrl);
        }
        return null;
    }

    private static List<YoloDetection> ParseDetections(JArray? arr)
    {
        var list = new List<YoloDetection>();
        if (arr == null) return list;

        foreach (var item in arr)
        {
            var bbox = item["bbox"];
            double x = bbox?["x"]?.Value<double>() ?? item["x1"]?.Value<double>() ?? 0;
            double y = bbox?["y"]?.Value<double>() ?? item["y1"]?.Value<double>() ?? 0;
            double w = bbox?["w"]?.Value<double>() ?? (item["x2"]?.Value<double>() - x) ?? 0;
            double h = bbox?["h"]?.Value<double>() ?? (item["y2"]?.Value<double>() - y) ?? 0;

            list.Add(new YoloDetection
            {
                ClassName = item["class"]?.ToString() ?? item["class_name"]?.ToString() ?? "",
                Confidence = item["confidence"]?.Value<double>() ?? 0,
                X = x, Y = y, W = w, H = h
            });
        }

        return list.OrderByDescending(d => d.Confidence).ToList();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
