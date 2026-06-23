using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using MfgInspectionSystem.UI;

namespace MfgInspectionSystem.UI.Controls;

public class NodeStatusRow : UserControl
{
    private readonly StatusDot _dot;
    private readonly Label     _lblName;
    private readonly Panel     _chip;
    private string _chipText  = "대기";
    private Color  _chipColor = DesignTokens.Neutral;

    public string NodeName
    {
        get => _lblName.Text;
        set => _lblName.Text = value;
    }

    public void SetState(StatusDot.DotState state, string chipText)
    {
        _dot.State = state;
        _chipText  = chipText;
        _chipColor = state switch
        {
            StatusDot.DotState.Ok       => DesignTokens.Ok,
            StatusDot.DotState.Warn     => DesignTokens.Warn,
            StatusDot.DotState.Critical => DesignTokens.Critical,
            _                           => DesignTokens.Neutral,
        };
        _chip.Invalidate();
    }

    public NodeStatusRow()
    {
        DoubleBuffered = true;
        BackColor      = Color.Transparent;

        _dot = new StatusDot { Anchor = AnchorStyles.None };

        _lblName = new Label
        {
            Font      = DesignTokens.FontBody,
            ForeColor = DesignTokens.TextPrimary,
            AutoSize  = false,
            Dock      = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _chip = new Panel
        {
            Size      = new Size(70, 20),
            BackColor = Color.Transparent,
            Anchor    = AnchorStyles.None,
        };
        _chip.Paint += ChipPaint;

        var tbl = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 3,
            RowCount    = 1,
            BackColor   = Color.Transparent,
            Padding     = new Padding(0),
            Margin      = new Padding(0),
        };
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 20f));
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74f));
        tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        tbl.Controls.Add(_dot,     0, 0);
        tbl.Controls.Add(_lblName, 1, 0);
        tbl.Controls.Add(_chip,    2, 0);

        Controls.Add(tbl);
    }

    private void ChipPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode       = SmoothingMode.AntiAlias;
        g.TextRenderingHint   = TextRenderingHint.ClearTypeGridFit;

        var r = new Rectangle(0, 0, _chip.Width - 1, _chip.Height - 1);
        using var path = CardPanel.RoundedRect(r, _chip.Height / 2);

        using (var b = new SolidBrush(Color.FromArgb(25, _chipColor)))
            g.FillPath(b, path);
        using (var p = new Pen(_chipColor, 1f))
            g.DrawPath(p, path);

        var sf = new StringFormat
        {
            Alignment     = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        using var tb = new SolidBrush(_chipColor);
        g.DrawString(_chipText, DesignTokens.FontLabel, tb,
            new RectangleF(0, 0, _chip.Width, _chip.Height), sf);
    }
}
