using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MfgInspectionSystem.Data.Entities;

/// E의 DB는 31컬럼, 너의 코드는 17컬럼.
/// 매핑 가능한 것은 [Column]으로 연결, E에 없는 것은 [NotMapped].
/// E에만 있는 컬럼(cam2 등)은 DB에서 NULL 허용이라 무시해도 OK.
[Table("inspection_results")]
public class InspectionResult
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("timestamp")]
    public DateTime InspectedAt { get; set; } = DateTime.UtcNow;

    [Column("product_id")]
    public string? ProductId { get; set; }

    [Column("correlation_id")]
    public string? CorrelationId { get; set; }

    [Column("product_type")]
    public string? ProductType { get; set; }

    [Column("yolo_class")]
    public string? YoloClass { get; set; }

    [Column("yolo_confidence")]
    public double? YoloConfidence { get; set; }

    [Column("yolo_all_detections")]
    public string? AllDetectionsJson { get; set; }

    [Column("opencv_pin_count")]
    public int? PinCount { get; set; }

    [Column("opencv_blur_score")]
    public double? BlurScore { get; set; }

    [Column("opencv_roi_centered")]
    public bool? RoiAligned { get; set; }

    [Column("result")]
    public string? Verdict { get; set; }

    [Column("defect_detail")]
    public string? DefectDetail { get; set; }

    [Column("cam1_image_path")]
    public string? Cam1ImagePath { get; set; }

    [Column("model_version")]
    public string? ModelVersion { get; set; }

    [Column("inference_time_ms")]
    public int? InferenceTimeMs { get; set; }

    [Column("environment_temp")]
    public double? EnvironmentTemp { get; set; }

    [Column("environment_humidity")]
    public double? EnvironmentHumidity { get; set; }
}
