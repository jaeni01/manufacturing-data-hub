using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MfgInspectionSystem.Data.Entities;

[Table("sorting_results")]
public class SortingResult
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("product_id")]
    public string? ProductId { get; set; }

    [Column("correlation_id")]
    public string? CorrelationId { get; set; }

    [Column("verdict")]
    public string? Verdict { get; set; }

    [Column("sorted_at")]
    public DateTime? SortedAt { get; set; }

    [Column("verified")]
    public bool Verified { get; set; }

    [Column("verification_result")]
    public string? VerificationResult { get; set; }

    [Column("verification_sensor")]
    public string? VerificationSensor { get; set; }

    [Column("expected_sensor")]
    public string? ExpectedSensor { get; set; }

    [Column("expected_route")]
    public string? ExpectedRoute { get; set; }
}
