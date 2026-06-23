using System.Drawing;

namespace MfgInspectionSystem.UI;

public static class DesignTokens
{
    // ── Surface ──
    public static readonly Color BgWindow     = Color.FromArgb(244, 246, 248);
    public static readonly Color BgCard       = Color.White;
    public static readonly Color BgHeaderDark = Color.FromArgb(27, 44, 78);
    public static readonly Color BgSidebar    = Color.FromArgb(22, 35, 62);
    public static readonly Color Border       = Color.FromArgb(229, 231, 235);

    // ── Text ──
    public static readonly Color TextPrimary   = Color.FromArgb(17, 24, 39);
    public static readonly Color TextSecondary = Color.FromArgb(107, 114, 128);
    public static readonly Color HeaderText    = Color.FromArgb(107, 114, 128);
    public static readonly Color TextOnDark    = Color.FromArgb(243, 244, 246);

    // ── Status ──
    public static readonly Color Ok       = Color.FromArgb(16, 185, 129);
    public static readonly Color Warn     = Color.FromArgb(245, 158, 11);
    public static readonly Color Critical = Color.FromArgb(239, 68, 68);
    public static readonly Color Info     = Color.FromArgb(59, 130, 246);
    public static readonly Color Neutral  = Color.FromArgb(156, 163, 175);

    // ── Verdict ──
    public static readonly Color Pass   = Color.FromArgb(34, 197, 94);
    public static readonly Color Defect = Color.FromArgb(220, 38, 38);
    public static readonly Color Hold   = Color.FromArgb(234, 179, 8);

    // ── Typography ──
    public const string FontFamily = "Segoe UI";
    public static readonly Font FontBody        = new(FontFamily, 9f);
    public static readonly Font FontBodyBold    = new(FontFamily, 9f, FontStyle.Bold);
    public static readonly Font FontLabel       = new(FontFamily, 8.5f);
    public static readonly Font FontSection     = new(FontFamily, 8.5f);
    public static readonly Font FontMetric      = new(FontFamily, 22f, FontStyle.Bold);
    public static readonly Font FontMetricSmall = new(FontFamily, 13f, FontStyle.Bold);
    public static readonly Font FontMono        = new("Consolas", 8.5f);

    // ── Spacing ──
    public const int SpacingSm = 8;
    public const int SpacingMd = 12;
    public const int SpacingLg = 16;

    // ── Radius ──
    public const int RadiusMd = 6;
    public const int RadiusLg = 8;
}
