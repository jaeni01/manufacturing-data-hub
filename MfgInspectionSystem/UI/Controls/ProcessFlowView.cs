using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using MfgInspectionSystem.UI;

namespace MfgInspectionSystem.UI.Controls;

public class ProcessFlowView : Control
{
    public record Step(string Title, string Line2, string Line3, Color Accent);

    public List<Step> Steps { get; set; } = new();

    public ProcessFlowView()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor |
                 ControlStyles.OptimizedDoubleBuffer         |
                 ControlStyles.AllPaintingInWmPaint          |
                 ControlStyles.UserPaint, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Steps.Count == 0) return;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        int n    = Steps.Count;
        int gap  = 28;
        int boxW = (Width - gap * (n - 1) - 8) / n;
        int boxH = Height - 12;
        int topY = 6;

        var sfC = new StringFormat
        {
            Alignment     = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };

        for (int i = 0; i < n; i++)
        {
            int x    = 4 + i * (boxW + gap);
            var rect = new Rectangle(x, topY, boxW, boxH);

            using var path = RoundedRect(rect, DesignTokens.RadiusMd);
            using (var b = new SolidBrush(Color.FromArgb(22, Steps[i].Accent)))
                g.FillPath(b, path);
            using (var p = new Pen(Steps[i].Accent, 1.5f))
                g.DrawPath(p, path);

            // 세로 중앙 정렬 — 박스 높이에 맞게 동적 계산
            const int titleH  = 20;
            const int lineH   = 16;
            const int spacing = 3;
            int contentH = titleH + spacing + lineH + spacing + lineH;
            int yPad     = Math.Max(2, (rect.Height - contentH) / 2);
            float xPad   = 6f;

            // Title (bold)
            var r1 = new RectangleF(rect.X + xPad, rect.Y + yPad, rect.Width - xPad * 2, titleH);
            using (var b = new SolidBrush(DesignTokens.TextPrimary))
                g.DrawString(Steps[i].Title, DesignTokens.FontBodyBold, b, r1, sfC);

            // Line 2
            if (!string.IsNullOrEmpty(Steps[i].Line2))
            {
                var r2 = new RectangleF(rect.X + xPad, rect.Y + yPad + titleH + spacing, rect.Width - xPad * 2, lineH);
                using (var b = new SolidBrush(DesignTokens.TextSecondary))
                    g.DrawString(Steps[i].Line2, DesignTokens.FontLabel, b, r2, sfC);
            }

            // Line 3 (status, colored)
            if (!string.IsNullOrEmpty(Steps[i].Line3))
            {
                var r3 = new RectangleF(rect.X + xPad, rect.Y + yPad + titleH + spacing + lineH + spacing, rect.Width - xPad * 2, lineH);
                using (var b = new SolidBrush(Steps[i].Accent))
                    g.DrawString(Steps[i].Line3, DesignTokens.FontLabel, b, r3, sfC);
            }

            // Arrow between boxes
            if (i < n - 1)
            {
                int ax = x + boxW + 4;
                int ay = topY + boxH / 2;
                using var pen = new Pen(DesignTokens.Info, 2f);
                pen.EndCap = LineCap.ArrowAnchor;
                g.DrawLine(pen, ax, ay, ax + gap - 8, ay);
            }
        }
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X,         r.Y,          d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y,          d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d,   0, 90);
        path.AddArc(r.X,         r.Bottom - d, d, d,  90, 90);
        path.CloseFigure();
        return path;
    }
}
