using MfgInspectionSystem.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MfgInspectionSystem.Data;

public class MfgDbContext : DbContext
{
    private readonly string _connStr;

    public DbSet<InspectionResult> InspectionResults { get; set; } = null!;
    public DbSet<SortingResult> SortingResults { get; set; } = null!;
    public DbSet<SensorReading> SensorReadings { get; set; } = null!;
    public DbSet<EventLog> EventLogs { get; set; } = null!;
    public DbSet<AlarmLog> AlarmLogs { get; set; } = null!;

    public MfgDbContext(string connectionString) => _connStr = connectionString;

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        var serverVersion = new MySqlServerVersion(new Version(8, 0, 0));
        options.UseMySql(_connStr, serverVersion,
            opts => opts.CommandTimeout(10));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // EF Core가 자동으로 테이블/컬럼을 생성하지 않도록
        // 기존 테이블에 매핑만 함 (E가 이미 만들어놓은 스키마 사용)
        base.OnModelCreating(modelBuilder);
    }
}
