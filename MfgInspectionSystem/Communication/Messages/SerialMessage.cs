using Newtonsoft.Json;

namespace MfgInspectionSystem.Communication.Messages;

public class SerialCommand
{
    [JsonProperty("cmd")]
    public string Command { get; set; } = "";

    [JsonProperty("target")]
    public string? Target { get; set; }

    [JsonProperty("value")]
    public int Value { get; set; }

    [JsonProperty("seq")]
    public int Sequence { get; set; }
}

public class SerialEvent
{
    [JsonProperty("evt")]
    public string EventType { get; set; } = "";

    [JsonProperty("sensor")]
    public string? Sensor { get; set; }

    [JsonProperty("state")]
    public string? State { get; set; }

    [JsonProperty("ts")]
    public long Timestamp { get; set; }

    [JsonProperty("seq")]
    public int Sequence { get; set; }

    [JsonProperty("reason")]
    public string? Reason { get; set; }

    [JsonProperty("uptime")]
    public long Uptime { get; set; }
}
