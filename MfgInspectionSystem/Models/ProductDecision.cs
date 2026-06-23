namespace MfgInspectionSystem.Models;

public enum Verdict { PASS, DEFECT, HOLD }

public class ProductDecision
{
    public string ProductId { get; set; } = GenerateId();
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString();
    public string ProductType { get; set; } = "unknown";
    public Verdict Verdict { get; set; } = Verdict.HOLD;
    public string? DefectDetail { get; set; }
    public double YoloConfidence { get; set; }
    public string? YoloClass { get; set; }
    public string? AllDetectionsJson { get; set; }
    public int PinCount { get; set; }
    public double BlurScore { get; set; }
    public bool RoiAligned { get; set; }
    public string? Cam1ImagePath { get; set; }    // 절대경로 — C# UI에서 File.Exists/load용
    public string? Cam1ImageDbPath { get; set; } // 상대경로 — DB 저장용 (Flask static 기준)
    public string? ModelVersion { get; set; }
    public int InferenceTimeMs { get; set; }
    public DateTime InspectedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SortedAt { get; set; }
    public bool Verified { get; set; }
    public string? VerificationSensor { get; set; }
    /// <summary>Sensor that should trigger for this verdict (set by SortingController at dequeue time).</summary>
    public string? ExpectedSensor { get; set; }
    /// <summary>Outcome of physical route verification: "skipped" | "ok" | "fail" | "timeout".</summary>
    public string? VerificationResult { get; set; }
    /// <summary>Ambient temperature snapped from MqttSubscriber cache at inspection time.</summary>
    public double? EnvironmentTemp { get; set; }
    /// <summary>Ambient humidity snapped from MqttSubscriber cache at inspection time.</summary>
    public double? EnvironmentHumidity { get; set; }

    public static string GenerateId() =>
        $"PRD-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpper()}";
}
