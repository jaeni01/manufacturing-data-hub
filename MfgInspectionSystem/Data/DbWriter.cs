using System.Threading.Channels;
using MfgInspectionSystem.Communication.Messages;
using MfgInspectionSystem.Data.Entities;
using MfgInspectionSystem.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace MfgInspectionSystem.Data;

/// <summary>
/// Async, batching DB writer.
///
/// Two separate channels are used:
///   _criticalQueue  — InspectionResult, SortingResult, EventLog, AlarmLog
///                     DropWrite on full: TryWrite returns false and we log loudly.
///                     These records must not evict earlier critical records.
///   _sensorQueue    — SensorReading (high-frequency MQTT telemetry)
///                     DropOldest on full: oldest sensor reading is silently evicted.
///
/// Critical records are flushed with priority before sensor readings in each batch.
/// </summary>
public class DbWriter : IDisposable
{
    private readonly string _connStr;

    // Critical: InspectionResult, SortingResult, EventLog, AlarmLog
    private readonly Channel<object> _criticalQueue = Channel.CreateBounded<object>(
        new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropWrite });

    // Telemetry: SensorReading — safe to drop oldest under heavy load
    private readonly Channel<object> _sensorQueue = Channel.CreateBounded<object>(
        new BoundedChannelOptions(1500) { FullMode = BoundedChannelFullMode.DropOldest });

    private CancellationTokenSource? _cts;
    private readonly object _hashLock = new();
    private bool _hashInitialized;
    private string? _lastEventHash;
    private bool _disposed;

    public event Action<string>? OnDbError;
    public bool IsConnected { get; private set; }

    public DbWriter(string connectionString) => _connStr = connectionString;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ProcessLoop(_cts.Token));
    }

    public bool TestConnection()
    {
        try
        {
            using var db = new MfgDbContext(_connStr);
            if (!db.Database.CanConnect())
            {
                IsConnected = false;
                return false;
            }
            // 스키마 관리는 E(Platform Engineer) 담당 — C# 앱은 DML만 수행.
            InitializeHashState(db);
            IsConnected = true;
            Log.Information("DB connection OK — tables verified/created");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DB connection test failed");
            OnDbError?.Invoke($"DB 연결 실패: {ex.Message}");
            IsConnected = false;
            return false;
        }
    }

    // ── Write helpers ──────────────────────────────────────────────────────────

    /// <summary>Enqueue a critical record. Logs an error (and does NOT silently discard) if the queue is full.</summary>
    private void WriteCritical(object entity, string description)
    {
        if (!_criticalQueue.Writer.TryWrite(entity))
            Log.Error("DB critical queue full — record dropped: {Desc}", description);
    }

    public void WriteInspectionResult(ProductDecision d)
    {
        var entity = new InspectionResult
        {
            ProductId           = d.ProductId,
            CorrelationId       = d.CorrelationId,
            InspectedAt         = d.InspectedAt,
            ProductType         = d.ProductType,
            YoloClass           = d.YoloClass,
            YoloConfidence      = d.YoloConfidence,
            AllDetectionsJson   = d.AllDetectionsJson,
            PinCount            = d.PinCount,
            BlurScore           = d.BlurScore,
            RoiAligned          = d.RoiAligned,
            Verdict             = d.Verdict.ToString(),
            DefectDetail        = d.DefectDetail,
            Cam1ImagePath       = d.Cam1ImageDbPath ?? d.Cam1ImagePath,
            ModelVersion        = d.ModelVersion,
            InferenceTimeMs     = d.InferenceTimeMs,
            EnvironmentTemp     = d.EnvironmentTemp,
            EnvironmentHumidity = d.EnvironmentHumidity
        };
        WriteCritical(entity, $"InspectionResult:{d.ProductId}");
    }

    public void WriteSortingResult(ProductDecision d, bool verified)
    {
        var entity = new SortingResult
        {
            ProductId          = d.ProductId,
            CorrelationId      = d.CorrelationId,
            Verdict            = d.Verdict.ToString(),
            SortedAt           = d.SortedAt,
            Verified           = verified,
            VerificationResult = d.VerificationResult,              // "skipped" | "ok" | "fail" | "timeout"
            ExpectedSensor     = d.ExpectedSensor,                  // 센서명 또는 "none"
            VerificationSensor = d.VerificationSensor,              // 실제 감지된 센서 (타임아웃 시 null)
            ExpectedRoute      = d.Verdict.ToString().ToLower()     // "pass" | "defect" | "hold"
        };
        WriteCritical(entity, $"SortingResult:{d.ProductId}");
    }

    public void WriteSensorReading(SensorData data)
    {
        // Sensor readings go to the separate telemetry channel.
        // DropOldest is intentional — high-frequency telemetry can be thinned under load.
        var entity = new SensorReading
        {
            Timestamp  = data.Timestamp,
            Source     = data.Source,
            Quality    = data.Quality,
            Seq        = data.Seq,
            MqttTopic  = data.MqttTopic,
            Transport  = "mqtt"
        };
        switch (data.Metric)
        {
            case "temperature": entity.Temperature = data.Value; break;
            case "humidity":    entity.Humidity    = data.Value; break;
            case "gas":
                entity.GasValue  = (int)data.Value;
                entity.GasStatus = data.GasStatus;
                break;
        }
        _sensorQueue.Writer.TryWrite(entity);   // DropOldest handles overflow silently
    }

    public void WriteEventLog(string eventType, string severity, string source, string message,
        string? actor = null, string? reason = null, string? correlationId = null, string? details = null)
    {
        WriteCritical(new EventLog
        {
            Timestamp = AuditChainVerifier.RoundToMilliseconds(DateTime.UtcNow),
            EventType = eventType,
            Severity  = severity.ToLower(),
            Source    = source,
            Message   = message,
            Actor     = actor ?? (source.Equals("operator", StringComparison.OrdinalIgnoreCase) ? "operator" : "system"),
            Reason    = reason ?? message,
            CorrelationId = correlationId,
            Details   = details
        }, $"EventLog:{eventType}");
    }

    public void WriteAlarmLog(string alarmType, string severity, string message)
    {
        WriteCritical(new AlarmLog
        {
            Timestamp = DateTime.UtcNow,
            AlarmType = alarmType,
            Severity  = severity.ToLower(),
            Message   = message
        }, $"AlarmLog:{alarmType}");
    }

    // ── Background processing loop ─────────────────────────────────────────────

    private async Task ProcessLoop(CancellationToken ct)
    {
        var batch     = new List<object>(50);
        var lastFlush = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Drain critical queue first (higher priority)
                while (batch.Count < 50 && _criticalQueue.Reader.TryRead(out var crit))
                    batch.Add(crit);

                // Fill remaining batch capacity with sensor readings
                while (batch.Count < 50 && _sensorQueue.Reader.TryRead(out var sens))
                    batch.Add(sens);

                bool shouldFlush = batch.Count >= 50 ||
                    (batch.Count > 0 && (DateTime.UtcNow - lastFlush).TotalSeconds >= 1);

                if (shouldFlush)
                {
                    await FlushBatch(batch);
                    batch.Clear();
                    lastFlush = DateTime.UtcNow;
                }
                else
                {
                    await Task.Delay(200, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Error(ex, "DbWriter loop error");
                batch.Clear();
                await Task.Delay(2000, ct).ContinueWith(_ => { });
            }
        }

        // Flush whatever remains in both queues on shutdown
        while (_criticalQueue.Reader.TryRead(out var r)) batch.Add(r);
        while (_sensorQueue.Reader.TryRead(out var s))   batch.Add(s);
        if (batch.Count > 0)
            await FlushBatch(batch);
    }

    private async Task FlushBatch(List<object> batch)
    {
        const int maxAttempts = 2;
        Exception? lastEx = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var db = new MfgDbContext(_connStr);
                EnsureHashInitialized(db);
                var lastHash = _lastEventHash;

                foreach (var item in batch)
                {
                    if (item is EventLog eventLog)
                    {
                        eventLog.PrevHash   = lastHash;
                        eventLog.RecordHash = AuditChainVerifier.ComputeRecordHash(eventLog);
                        lastHash            = eventLog.RecordHash;
                    }

                    switch (item)
                    {
                        case InspectionResult ir: db.InspectionResults.Add(ir); break;
                        case SortingResult    sr: db.SortingResults.Add(sr);    break;
                        case SensorReading    sn: db.SensorReadings.Add(sn);    break;
                        case EventLog         el: db.EventLogs.Add(el);         break;
                        case AlarmLog         al: db.AlarmLogs.Add(al);         break;
                    }
                }

                await db.SaveChangesAsync();
                _lastEventHash = lastHash;
                IsConnected    = true;
                Log.Debug("DB flush: {Count} items (attempt {A})", batch.Count, attempt);
                return; // 성공
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransientDbError(ex))
            {
                lastEx = ex;
                string inner = ex.InnerException?.Message ?? ex.Message;
                Log.Warning("DB flush attempt {A} failed (transient), 1초 후 재시도 — {Inner}", attempt, inner);
                await Task.Delay(1000);
            }
            catch (Exception ex)
            {
                lastEx = ex;
                break;
            }
        }

        // 모든 재시도 실패
        string innerMsg = lastEx?.InnerException?.Message ?? lastEx?.Message ?? "unknown";

        // 스키마 불일치(컬럼 없음 등)는 재시도해도 무의미 — 연결 오류와 구분해서 표시
        bool isSchemaError = innerMsg.Contains("Unknown column", StringComparison.OrdinalIgnoreCase)
                          || innerMsg.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase)
                          || innerMsg.Contains("Table", StringComparison.OrdinalIgnoreCase);

        if (isSchemaError)
        {
            Log.Fatal(lastEx, "DB schema mismatch — 테이블/컬럼 불일치. fix_schema.sql 실행 후 재시작 필요. inner: {Inner}", innerMsg);
            OnDbError?.Invoke($"[스키마 오류] {innerMsg} — fix_schema.sql 실행 후 앱 재시작");
        }
        else
        {
            IsConnected = false;
            Log.Error(lastEx, "DB flush failed ({Count} items lost) — inner: {Inner}", batch.Count, innerMsg);
            OnDbError?.Invoke($"DB 쓰기 실패: {innerMsg}");
        }
    }

    /// <summary>연결/타임아웃 계열 오류 → 재시도 가능. 제약조건 위반 등 → 재시도 불가.</summary>
    private static bool IsTransientDbError(Exception ex)
    {
        var inner = ex.InnerException?.Message ?? ex.Message;
        return inner.Contains("connection", StringComparison.OrdinalIgnoreCase)
            || inner.Contains("timeout",    StringComparison.OrdinalIgnoreCase)
            || inner.Contains("deadlock",   StringComparison.OrdinalIgnoreCase)
            || inner.Contains("lost",       StringComparison.OrdinalIgnoreCase)   // "MySQL connection lost"
            || inner.Contains("Unable to connect", StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureHashInitialized(MfgDbContext db)
    {
        if (_hashInitialized) return;
        InitializeHashState(db);
    }

    private void InitializeHashState(MfgDbContext db)
    {
        lock (_hashLock)
        {
            if (_hashInitialized) return;

            _lastEventHash = db.EventLogs
                .AsNoTracking()
                .Where(e => e.RecordHash != null && e.RecordHash != "")
                .OrderByDescending(e => e.Id)
                .Select(e => e.RecordHash)
                .FirstOrDefault();

            _hashInitialized = true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _criticalQueue.Writer.TryComplete();
        _sensorQueue.Writer.TryComplete();
        _cts?.Dispose();
    }
}
