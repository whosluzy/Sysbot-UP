using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

public class FancyButton : Button
{
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color GlowColor { get; set; } = Color.Cyan;

    public FancyButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent;
        ForeColor = Color.White;
        DoubleBuffered = true;

        try
        {
            Font = SysBot.Pokemon.WinForms.FontManager.Get("Enter The Grid", 10F, FontStyle.Regular);
        }
        catch
        {
            Font = new Font(FontFamily.GenericSansSerif, 10F, FontStyle.Regular);
        }
    }
}
