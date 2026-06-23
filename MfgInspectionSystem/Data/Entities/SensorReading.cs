using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MfgInspectionSystem.Data.Entities;

[Table("sensor_readings")]
public class SensorReading
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("timestamp")]
    public DateTime Timestamp { get; set; }

    [Column("source")]
    public string? Source { get; set; }

    [Column("quality")]
    public string? Quality { get; set; }

    [Column("seq")]
    public long Seq { get; set; }

    [Column("mqtt_topic")]
    public string? MqttTopic { get; set; }

    [Column("transport")]
    public string? Transport { get; set; }

    [Column("temperature")]
    public double? Temperature { get; set; }

    [Column("humidity")]
    public double? Humidity { get; set; }

    [Column("gas_value")]
    public int? GasValue { get; set; }

    [Column("gas_status")]
    public string? GasStatus { get; set; }
}
