using System.Drawing;
using System.Windows.Forms;
using MfgInspectionSystem.UI;

namespace MfgInspectionSystem.UI.Controls;

public class MetricCard : UserControl
{
    private readonly Label _lblCaption;
    private readonly Label _lblValue;
    private readonly Label _lblUnit;

    public string Caption
    {
        get => _lblCaption.Text;
        set => _lblCaption.Text = value;
    }

    public string Value
    {
        get => _lblValue.Text;
        set => _lblValue.Text = value;
    }

    public string Unit
    {
        get => _lblUnit.Text;
        set => _lblUnit.Text = value;
    }

    public Color ValueColor
    {
        get => _lblValue.ForeColor;
        set => _lblValue.ForeColor = value;
    }

    public MetricCard()
    {
        DoubleBuffered = true;
        BackColor      = Color.Transparent;

        _lblCaption = new Label
        {
            Font      = DesignTokens.FontLabel,
            ForeColor = DesignTokens.TextSecondary,
            AutoSize  = false,
            Dock      = DockStyle.Top,
            Height    = 18,
            TextAlign = ContentAlignment.BottomLeft,
            Padding   = new Padding(2, 0, 0, 0),
        };

        _lblUnit = new Label
        {
            Font      = DesignTokens.FontLabel,
            ForeColor = DesignTokens.TextSecondary,
            AutoSize  = false,
            Dock      = DockStyle.Bottom,
            Height    = 18,
            TextAlign = ContentAlignment.TopLeft,
            Padding   = new Padding(2, 0, 0, 0),
        };

        _lblValue = new Label
        {
            Text      = "—",
            Font      = DesignTokens.FontMetric,
            ForeColor = DesignTokens.TextPrimary,
            AutoSize  = false,
            Dock      = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding   = new Padding(2, 0, 0, 0),
        };

        Controls.Add(_lblValue);    // Fill
        Controls.Add(_lblUnit);     // Bottom
        Controls.Add(_lblCaption);  // Top
    }
}
