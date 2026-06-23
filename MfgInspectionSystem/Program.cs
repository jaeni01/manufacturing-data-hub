using MfgInspectionSystem.Config;
using MfgInspectionSystem.Observability;
using MfgInspectionSystem.Utils;
using Serilog;

namespace MfgInspectionSystem;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Load config early so logging is configured before anything else
        var cfg = AppConfig.Load();
        AppLogging.Configure(cfg);
        if (cfg.Metrics.Enabled)
            AppMetrics.Start(cfg.Metrics.PrometheusPort);

        Log.Information("=== MfgInspectionSystem starting ===");

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) =>
        {
            Log.Fatal(e.Exception, "Unhandled UI thread exception");
            MessageBox.Show($"예기치 못한 오류가 발생했습니다:\n{e.Exception.Message}",
                "Critical Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled domain exception");
        };

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(cfg));

        Log.Information("=== MfgInspectionSystem stopped ===");
        AppMetrics.Stop();
        Log.CloseAndFlush();
    }
}
