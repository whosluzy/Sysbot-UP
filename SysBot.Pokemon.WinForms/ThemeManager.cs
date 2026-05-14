using SysBot.Pokemon.WinForms;
using System.Drawing;

public static class ThemeManager
{
    public static ThemeColors CurrentColors { get; } = new ThemeColors
    {
        PanelBase = Color.FromArgb(31, 30, 68),
        Shadow    = Color.FromArgb(20, 19, 57),
        Hover     = Color.FromArgb(31, 30, 68),
        ForeColor = Color.White,
    };

    public static void ApplyTheme(Main form)
    {
        var colors = CurrentColors;

        form.panelMain.BackColor     = Color.FromArgb(10, 10, 40);
        form.panelLeftSide.BackColor = colors.PanelBase;
        form.panelTitleBar.BackColor = colors.PanelBase;

        form.shadowPanelTop.BackColor  = colors.Shadow;
        form.shadowPanelLeft.BackColor = colors.Shadow;
        form.panel1.BackColor = colors.Shadow;
        form.panel2.BackColor = colors.Shadow;
        form.panel3.BackColor = colors.Shadow;
        form.panel4.BackColor = colors.Shadow;
        form.panel5.BackColor = colors.Shadow;
        form.panel6.BackColor = colors.Shadow;

        form.btnBots.BackColor = colors.PanelBase;
        form.btnHub.BackColor  = colors.PanelBase;
        form.btnLogs.BackColor = colors.PanelBase;

        form.btnBots.ForeColor = colors.ForeColor;
        form.btnHub.ForeColor  = colors.ForeColor;
        form.btnLogs.ForeColor = colors.ForeColor;
        form.lblTitle.ForeColor = colors.ForeColor;

        form.SetupThemeAwareButtons();
    }

    public static ThemeColors GetCurrentColors() => CurrentColors;
}

public class ThemeColors
{
    public Color PanelBase { get; set; }
    public Color Shadow    { get; set; }
    public Color Hover     { get; set; }
    public Color ForeColor { get; set; }
}
