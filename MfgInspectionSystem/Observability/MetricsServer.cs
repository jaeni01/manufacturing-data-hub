using Prometheus;
using Serilog;

namespace MfgInspectionSystem.Observability;

public static class AppMetrics
{
    public static readonly Counter Inspections = Metrics.CreateCounter(
        "mfg_inspections_total",
        "Total inspections by result and product type",
        new CounterConfiguration { LabelNames = new[] { "result", "product_type" } });

    public static readonly Histogram YoloLatency = Metrics.CreateHistogram(
        "mfg_yolo_inference_seconds",
        "YOLO inference latency in seconds",
        new HistogramConfiguration
        {
            Buckets = Histogram.ExponentialBuckets(0.005, 2, 10)
        });

    public static readonly Gauge QueueDepth = Metrics.CreateGauge(
        "mfg_decision_queue_depth",
        "Current ProductDecisionQueue depth");

    public static readonly Counter MqttMessages = Metrics.CreateCounter(
        "mfg_mqtt_messages_total",
        "MQTT messages by topic and direction",
        new CounterConfiguration { LabelNames = new[] { "topic", "direction" } });

    public static readonly Counter SortingVerification = Metrics.CreateCounter(
        "mfg_sorting_verification_total",
        "Sorting verification outcome",
        new CounterConfiguration { LabelNames = new[] { "result" } });

    public static readonly Gauge SystemState = Metrics.CreateGauge(
        "mfg_system_state",
        "System state, 1 when active for that label",
        new GaugeConfiguration { LabelNames = new[] { "state" } });

    private static MetricServer? _server;

    public static void Start(int port = 9091)
    {
        if (_server != null) return;

        try
        {
            _server = new MetricServer(hostname: "127.0.0.1", port: port);
            _server.Start();
            Log.Information("Prometheus metrics endpoint started: http://127.0.0.1:{Port}/metrics", port);
        }
        catch (Exception ex)
        {
            _server?.Dispose();
            _server = null;
            Log.Error(ex, "Prometheus metrics endpoint failed to start on port {Port}", port);
        }
    }

    public static void Stop()
    {
        _server?.Dispose();
        _server = null;
    }
}
