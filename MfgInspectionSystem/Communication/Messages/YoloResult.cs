namespace MfgInspectionSystem.Communication.Messages;

public class YoloInferResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int InferenceTimeMs { get; set; }
    public string? TopClass { get; set; }
    public double TopConfidence { get; set; }
    public string? ModelVersion { get; set; }
    public string? AllDetectionsJson { get; set; }
    public List<YoloDetection> Detections { get; set; } = new();
}

public class YoloDetection
{
    public string ClassName { get; set; } = "";
    public double Confidence { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }

    public double X1 => X;
    public double Y1 => Y;
    public double X2 => X + W;
    public double Y2 => Y + H;
}
