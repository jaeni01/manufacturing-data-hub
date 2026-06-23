using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MfgInspectionSystem.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MfgInspectionSystem.Data;

public sealed record AuditChainVerificationResult(int Checked, int Broken, long? FirstBrokenId);

public static class AuditChainVerifier
{
    public static AuditChainVerificationResult Verify(string connectionString)
    {
        using var db = new MfgDbContext(connectionString);
        var rows = db.EventLogs
            .AsNoTracking()
            .Where(e => e.RecordHash != null && e.RecordHash != "")
            .OrderBy(e => e.Id)
            .ToList();

        var broken = 0;
        long? firstBrokenId = null;
        string? previousHash = null;

        foreach (var row in rows)
        {
            if (!string.Equals(row.PrevHash, previousHash, StringComparison.OrdinalIgnoreCase))
            {
                broken++;
                firstBrokenId ??= row.Id;
            }

            var expected = ComputeRecordHash(row);
            if (!string.Equals(row.RecordHash, expected, StringComparison.OrdinalIgnoreCase))
            {
                broken++;
                firstBrokenId ??= row.Id;
            }

            previousHash = row.RecordHash;
        }

        return new AuditChainVerificationResult(rows.Count, broken, firstBrokenId);
    }

    public static string ComputeRecordHash(EventLog row)
    {
        var payload = string.Join("|",
            row.PrevHash ?? "",
            row.EventType ?? "",
            row.Severity ?? "",
            row.Source ?? "",
            row.Message ?? "",
            row.Actor ?? "",
            NormalizeTimestamp(row.Timestamp),
            row.Reason ?? "",
            row.CorrelationId ?? "",
            row.Details ?? "");

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static DateTime RoundToMilliseconds(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }

    private static string NormalizeTimestamp(DateTime value) =>
        RoundToMilliseconds(value).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
