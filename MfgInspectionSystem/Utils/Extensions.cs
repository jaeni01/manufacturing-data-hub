namespace MfgInspectionSystem.Utils;

public static class Extensions
{
    public static void SafeInvoke(this Control control, Action action)
    {
        if (control.IsDisposed || !control.IsHandleCreated) return;
        if (control.InvokeRequired)
            control.BeginInvoke(action);
        else
            action();
    }

    public static string ToKoreanLabel(this Models.Verdict verdict) => verdict switch
    {
        Models.Verdict.PASS => "PASS ✓",
        Models.Verdict.DEFECT => "DEFECT ✗",
        Models.Verdict.HOLD => "HOLD ⚠",
        _ => verdict.ToString()
    };

    public static Color ToVerdictColor(this Models.Verdict verdict) => verdict switch
    {
        Models.Verdict.PASS => Color.FromArgb(0, 180, 80),
        Models.Verdict.DEFECT => Color.FromArgb(220, 50, 50),
        Models.Verdict.HOLD => Color.FromArgb(255, 165, 0),
        _ => Color.Gray
    };

    public static Color ToStateColor(this Core.SystemState state) => state switch
    {
        Core.SystemState.IDLE => Color.LightGray,
        Core.SystemState.RUNNING => Color.FromArgb(144, 238, 144),
        Core.SystemState.PAUSED => Color.LightYellow,
        Core.SystemState.EMERGENCY => Color.FromArgb(255, 80, 80),
        _ => Color.White
    };
}
