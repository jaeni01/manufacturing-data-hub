namespace MfgInspectionSystem.Communication.Messages;

public class SensorData
{
    public string Metric { get; set; } = "";   // "temperature" | "humidity" | "gas"
    public double Value { get; set; }
    public string Unit { get; set; } = "";
    public string Quality { get; set; } = "good";
    public string? GasStatus { get; set; }
    public string? Source { get; set; }
    public long Seq { get; set; }
    public DateTime Timestamp { get; set; }
    public string MqttTopic { get; set; } = "";
}
