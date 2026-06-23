using MfgInspectionSystem.Communication;
using MfgInspectionSystem.Config;
using MfgInspectionSystem.Data;
using MfgInspectionSystem.Models;
using MfgInspectionSystem.Observability;
using Serilog;
using Serilog.Context;

namespace MfgInspectionSystem.Core;

public class SortingController
{
    private readonly SerialManager _serial;
    private readonly ProductDecisionQueue _queue;
    private readonly DbWriter _db;
    private readonly SortingConfig _cfg;
    private readonly MqttSubscriber? _mqtt;

    private ProductDecision? _awaitingVerification;
    private CancellationTokenSource? _verifyTimeoutCts;

    public event Action<string>? OnLog;
    public event Action<ProductDecision, bool>? OnVerificationResult;

    public SortingController(SerialManager serial, ProductDecisionQueue queue,
        DbWriter db, SortingConfig cfg, MqttSubscriber? mqtt = null)
    {
        _serial = serial;
        _queue  = queue;
        _db     = db;
        _cfg    = cfg;
        _mqtt   = mqtt;
    }

    public async Task OnS3TriggeredAsync()
    {
        var decision = _queue.Dequeue();

        if (decision == null)
        {
            Log.Warning("S3 triggered but queue is empty — HOLD fallback");
            OnLog?.Invoke("[Sorting] 큐 비어있음! HOLD 처리 (안전측)");
            await _serial.SetServoB(90);
            await Task.Delay(_cfg.ServoReturnDelayMs);
            await _serial.SetServoB(0);
            return;
        }

        using var productLog = LogContext.PushProperty("product_id", decision.ProductId);
        using var correlationLog = LogContext.PushProperty("correlation_id", decision.CorrelationId);

        // Determine and persist the expected verification sensor before any servo action
        // so it is available in OnVerificationSensor and in the DB record.
        decision.ExpectedSensor = decision.Verdict switch
        {
            Verdict.PASS   => _cfg.PassSensor,
            Verdict.DEFECT => _cfg.DefectSensor,
            Verdict.HOLD   => _cfg.HoldSensor,
            _              => ""
        };

        OnLog?.Invoke($"[Sorting] {decision.ProductId} → {decision.Verdict} (expected={decision.ExpectedSensor})");

        // PASS: 서보 명령 없음 (Arduino가 타이머로 처리, 0 명령 전송 시 오동작)
        // DEFECT/HOLD: 판정 1회만 전송, return은 Arduino 자체 복귀 처리
        bool cmdOk = true;
        switch (decision.Verdict)
        {
            case Verdict.PASS:
                break;
            case Verdict.DEFECT:
                cmdOk = await _serial.SetServoA(90);
                break;
            case Verdict.HOLD:
                cmdOk = await _serial.SetServoB(90);
                break;
        }

        if (!cmdOk)
            OnLog?.Invoke($"[Sorting] {decision.ProductId} 서보 명령 실패!");

        await Task.Delay(_cfg.ServoReturnDelayMs);

        // PASS 판정: ExpectedSensor가 빈 문자열 → 검증 센서 없음 → 즉시 verified=true 자동 완료
        if (string.IsNullOrEmpty(decision.ExpectedSensor))
        {
            decision.Verified            = true;
            decision.SortedAt            = DateTime.UtcNow;
            decision.VerificationSensor  = "";
            decision.VerificationResult  = "skipped";
            decision.ExpectedSensor      = "none";   // DB 명확화용 (제어 흐름은 이미 통과)
            OnLog?.Invoke($"[Sorting] {decision.ProductId} PASS → 검증 센서 없음, 자동 완료");
            using var pl = LogContext.PushProperty("product_id",    decision.ProductId);
            using var cl = LogContext.PushProperty("correlation_id", decision.CorrelationId);
            AppMetrics.SortingVerification.WithLabels("ok").Inc();
            OnVerificationResult?.Invoke(decision, true);
            _ = Task.Run(() => _db.WriteSortingResult(decision, true));
            _ = _mqtt?.PublishSortingResultAsync(decision);
            return;
        }

        // DEFECT / HOLD: 검증 타임아웃 시작 (S2 또는 S3 센서 대기)
        _awaitingVerification = decision;
        StartVerificationTimeout();
    }

    public void OnVerificationSensor(string sensor)
    {
        if (_awaitingVerification == null) return;

        _verifyTimeoutCts?.Cancel();
        var decision = _awaitingVerification;
        _awaitingVerification = null;

        // ExpectedSensor was already set by OnS3TriggeredAsync at dequeue time.
        string expected = decision.ExpectedSensor ?? "";

        bool matched = sensor == expected;
        decision.Verified           = matched;
        decision.SortedAt           = DateTime.UtcNow;
        decision.VerificationSensor = sensor;            // actual sensor that fired
        decision.VerificationResult = matched ? "ok" : "fail";

        if (matched)
            OnLog?.Invoke($"[Sorting] 검증 성공: {decision.ProductId} → {sensor}");
        else
            OnLog?.Invoke($"[Sorting] 검증 실패! {decision.ProductId}: expected={expected}, actual={sensor}");

        using var productLog = LogContext.PushProperty("product_id", decision.ProductId);
        using var correlationLog = LogContext.PushProperty("correlation_id", decision.CorrelationId);
        AppMetrics.SortingVerification.WithLabels(matched ? "ok" : "fail").Inc();
        OnVerificationResult?.Invoke(decision, matched);
        _ = Task.Run(() => _db.WriteSortingResult(decision, matched));
        _ = _mqtt?.PublishSortingResultAsync(decision);

        if (!matched)
        {
            _ = _mqtt?.PublishAlarmAsync("sorting_mismatch", "warning", new
            {
                product_id     = decision.ProductId,
                expected_sensor = expected,
                actual_sensor  = sensor,
                correlation_id = decision.CorrelationId
            });
        }
    }

    private void StartVerificationTimeout()
    {
        _verifyTimeoutCts?.Cancel();
        _verifyTimeoutCts = new CancellationTokenSource();
        var token = _verifyTimeoutCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_cfg.VerificationTimeoutMs, token);

                if (_awaitingVerification != null)
                {
                    var dec = _awaitingVerification;
                    _awaitingVerification = null;
                    dec.Verified            = false;
                    dec.SortedAt            = DateTime.UtcNow;
                    dec.VerificationResult  = "timeout";

                    using var productLog = LogContext.PushProperty("product_id", dec.ProductId);
                    using var correlationLog = LogContext.PushProperty("correlation_id", dec.CorrelationId);
                    Log.Warning("Verification timeout: {Id}", dec.ProductId);
                    OnLog?.Invoke($"[Sorting] 검증 타임아웃: {dec.ProductId}");
                    AppMetrics.SortingVerification.WithLabels("timeout").Inc();
                    OnVerificationResult?.Invoke(dec, false);
                    _ = Task.Run(() => _db.WriteSortingResult(dec, false));
                    _ = _mqtt?.PublishSortingResultAsync(dec);
                    _ = _mqtt?.PublishAlarmAsync("sorting_timeout", "warning", new
                    {
                        product_id     = dec.ProductId,
                        expected_sensor = dec.ExpectedSensor,
                        correlation_id = dec.CorrelationId
                    });
                }
            }
            catch (OperationCanceledException) { }
        }, token);
    }
}
