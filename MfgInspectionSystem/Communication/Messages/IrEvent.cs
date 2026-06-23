namespace MfgInspectionSystem.Communication.Messages;

public class IrEvent
{
    public string Sensor { get; set; } = "";   // "S1".."S6"
    public string State { get; set; } = "";    // "blocked" | "clear"
    public long Timestamp { get; set; }
    public int Sequence { get; set; }
    public DateTime ReceivedAt { get; } = DateTime.UtcNow;

    public bool IsBlocked => State == "blocked";
}

public class EstopEvent
{
    public string Reason { get; set; } = "unknown";
    public long Timestamp { get; set; }
    public DateTime ReceivedAt { get; } = DateTime.UtcNow;
}

public class HeartbeatEvent
{
    public long Uptime { get; set; }
    public int Sequence { get; set; }
    public DateTime ReceivedAt { get; } = DateTime.UtcNow;
}

public class AckEvent
{
    public int Sequence { get; set; }
}
