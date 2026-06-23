using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using MfgInspectionSystem.UI;

namespace MfgInspectionSystem.UI.Controls;

public class StatusDot : Control
{
    public enum DotState { Ok, Warn, Critical, Neutral }

    private DotState _state = DotState.Neutral;

    public DotState State
    {
        get => _state;
        set { _state = value; Invalidate(); }
    }

    public StatusDot()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor |
                 ControlStyles.OptimizedDoubleBuffer         |
                 ControlStyles.AllPaintingInWmPaint          |
                 ControlStyles.UserPaint, true);
        Size      = new Size(12, 12);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color color = _state switch
        {
            DotState.Ok       => DesignTokens.Ok,
            DotState.Warn     => DesignTokens.Warn,
            DotState.Critical => DesignTokens.Critical,
            _                 => DesignTokens.Neutral,
        };

        using (var b = new SolidBrush(Color.FromArgb(40, color)))
            g.FillEllipse(b, 0, 0, Width - 1, Height - 1);

        var inner = new Rectangle(2, 2, Width - 5, Height - 5);
        using (var b = new SolidBrush(color))
            g.FillEllipse(b, inner);
    }
}
