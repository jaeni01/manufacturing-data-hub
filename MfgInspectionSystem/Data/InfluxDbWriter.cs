using InfluxDB.Client;
using InfluxDB.Client.Api.Domain;
using InfluxDB.Client.Writes;
using MfgInspectionSystem.Communication.Messages;
using MfgInspectionSystem.Config;
using MfgInspectionSystem.Models;
using Serilog;

namespace MfgInspectionSystem.Data;

/// <summary>
/// Writes time-series data to InfluxDB 2.x (Hardening — Level 3 Historian).
/// Sensor telemetry is bridged separately by Telegraf (MQTT → InfluxDB);
/// this writer handles metrics that are uniquely available to the C# process:
///   • yolo_inference  — per-inspection YOLO latency + confidence
///   • queue_depth     — ProductDecisionQueue depth snapshots
/// Token must be stored in appsettings.secret.json (not appsettings.json).
/// </summary>
public class InfluxDbWriter : IDisposable
{
    private readonly InfluxDBClient? _client;
    private readonly InfluxDbConfig _cfg;
    private bool _disposed;

    public bool IsEnabled => _cfg.Enabled && _client != null;

    public InfluxDbWriter(InfluxDbConfig cfg)
    {
        _cfg = cfg;
        if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.Token))
        {
            Log.Information("[InfluxDB] Disabled (Enabled={E}, token present={T})",
                cfg.Enabled, !string.IsNullOrWhiteSpace(cfg.Token));
            return;
        }

        try
        {
            _client = new InfluxDBClient(cfg.Url, cfg.Token);
            Log.Information("[InfluxDB] Client created: {Url} org={Org} bucket={Bucket}",
                cfg.Url, cfg.Org, cfg.Bucket);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[InfluxDB] Client creation failed");
        }
    }

    /// <summary>
    /// Write per-inspection YOLO metrics (measurement: yolo_inference).
    /// Called by MainForm after InspectionPipeline.OnInspectionCompleted.
    /// </summary>
    public void WriteInspectionMetric(ProductDecision d)
    {
        if (!IsEnabled) return;
        try
        {
            var point = PointData
                .Measurement("yolo_inference")
                .Tag("model",   d.ModelVersion ?? "unknown")
                .Tag("camera",  "cam1")
                .Tag("verdict", d.Verdict.ToString())
                .Tag("product_type", d.ProductType)
                .Field("latency_ms",  (double)d.InferenceTimeMs)
                .Field("confidence",  d.YoloConfidence)
                .Field("pin_count",   (long)d.PinCount)
                .Field("blur_score",  d.BlurScore)
                .Timestamp(d.InspectedAt, WritePrecision.Ms);

            using var writeApi = _client!.GetWriteApi();
            writeApi.WritePoint(point, _cfg.Bucket, _cfg.Org);

            Log.Debug("[InfluxDB] yolo_inference written: {Id} verdict={V} latency={L}ms",
                d.ProductId, d.Verdict, d.InferenceTimeMs);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[InfluxDB] WriteInspectionMetric failed");
        }
    }

    /// <summary>
    /// Write ProductDecisionQueue depth snapshot (measurement: queue_depth).
    /// Call periodically (e.g. every 5s) or on enqueue/dequeue.
    /// </summary>
    public void WriteQueueDepth(int depth)
    {
        if (!IsEnabled) return;
        try
        {
            var point = PointData
                .Measurement("queue_depth")
                .Tag("line", "line1")
                .Field("depth", (long)depth)
                .Timestamp(DateTime.UtcNow, WritePrecision.Ms);

            using var writeApi = _client!.GetWriteApi();
            writeApi.WritePoint(point, _cfg.Bucket, _cfg.Org);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[InfluxDB] WriteQueueDepth failed");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client?.Dispose();
    }
}
