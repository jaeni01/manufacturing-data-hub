using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MfgInspectionSystem.Data.Entities;

// E의 DB에 alarm_log 테이블 존재 확인됨
[Table("alarm_log")]
public class AlarmLog
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [Column("alarm_type")]
    public string? AlarmType { get; set; }

    [Column("severity")]
    public string? Severity { get; set; }

    [Column("message")]
    public string? Message { get; set; }

    [Column("acknowledged")]
    public bool Acknowledged { get; set; }
}
