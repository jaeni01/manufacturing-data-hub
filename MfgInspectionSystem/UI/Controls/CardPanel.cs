using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using MfgInspectionSystem.UI;

namespace MfgInspectionSystem.UI.Controls;

public class CardPanel : Panel
{
    public string Title      { get; set; } = "";
    public Color  TitleColor { get; set; } = DesignTokens.TextSecondary;
    public int    Radius     { get; set; } = DesignTokens.RadiusMd;

    public CardPanel()
    {
        DoubleBuffered = true;
        BackColor      = Color.Transparent;
        Padding        = new Padding(16, 30, 16, 16);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedRect(rect, Radius);

        using (var b = new SolidBrush(DesignTokens.BgCard))
            g.FillPath(b, path);

        using (var p = new Pen(DesignTokens.Border, 1))
            g.DrawPath(p, path);

        if (!string.IsNullOrEmpty(Title))
        {
            using var b = new SolidBrush(DesignTokens.HeaderText);
            g.DrawString(Title, DesignTokens.FontSection, b, 14f, 10f);
        }

        base.OnPaint(e);
    }

    internal static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X,          r.Y,          d, d, 180, 90);
        path.AddArc(r.Right - d,  r.Y,          d, d, 270, 90);
        path.AddArc(r.Right - d,  r.Bottom - d, d, d,   0, 90);
        path.AddArc(r.X,          r.Bottom - d, d, d,  90, 90);
        path.CloseFigure();
        return path;
    }
}
