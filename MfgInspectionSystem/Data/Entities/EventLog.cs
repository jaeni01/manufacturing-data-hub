using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MfgInspectionSystem.Data.Entities;

[Table("event_log")]
public class EventLog
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [Column("event_type")]
    public string? EventType { get; set; }

    [Column("severity")]
    public string? Severity { get; set; }

    [Column("source")]
    public string? Source { get; set; }

    [Column("message")]
    public string? Message { get; set; }

    [Column("actor")]
    public string? Actor { get; set; }

    [Column("reason")]
    public string? Reason { get; set; }

    [Column("correlation_id")]
    public string? CorrelationId { get; set; }

    [Column("prev_hash")]
    public string? PrevHash { get; set; }

    [Column("record_hash")]
    public string? RecordHash { get; set; }

    // E의 DB에는 'details' (json) 컬럼이 있고
    // 너의 코드의 RelatedInspectionId는 없음 → NotMapped
    [NotMapped]
    public long? RelatedInspectionId { get; set; }

    [Column("details")]
    public string? Details { get; set; }
}
