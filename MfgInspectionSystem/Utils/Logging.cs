using MfgInspectionSystem.Config;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace MfgInspectionSystem.Utils;

public static class AppLogging
{
    public static void Configure(AppConfig cfg)
    {
        var level = Enum.TryParse<LogEventLevel>(cfg.Logging.MinimumLevel, out var parsed)
            ? parsed : LogEventLevel.Information;

        Directory.CreateDirectory(Path.GetDirectoryName(cfg.Logging.FilePath) ?? "logs");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("node", "pc-line1")
            .Enrich.WithProperty("service", "winforms-control")
            .Enrich.WithProperty("env", cfg.Environment)
            .Enrich.WithProperty("client_id", cfg.Mqtt.ClientId)
            .Enrich.WithMachineName()
            .Enrich.WithProcessId()
            .Enrich.WithThreadId()
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
            .WriteTo.File(
                formatter: new CompactJsonFormatter(),
                path: cfg.Logging.FilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30)
            .CreateLogger();

        Log.Information("Logging configured: env={Env} level={Level}", cfg.Environment, level);
    }
}
