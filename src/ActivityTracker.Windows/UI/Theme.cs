using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ActivityTracker.Windows.UI;

/// <summary>
/// Colori e caratteri della finestra del report. Superfici monocrome in tre gradini, bordi sottili, un solo colore
/// d'accento per lo stato (verde = traccia, ambra = da guardare, rosso = errore). Segue il tema app chiaro/scuro di
/// Windows. Segoe UI ha le cifre a larghezza fissa: percentuali e durate restano in colonna.
/// </summary>
public sealed record Palette(Color Canvas, Color Surface, Color Hairline, Color Ink, Color Body, Color Mute,
                             Color Accent, Color Attention, Color Error, Color ButtonHover)
{
    public static readonly Palette Light = new(
        Canvas: Color.FromArgb(0xFB, 0xFB, 0xFB), Surface: Color.FromArgb(0xF3, 0xF3, 0xF3), Hairline: Color.FromArgb(0xE3, 0xE3, 0xE3),
        Ink: Color.FromArgb(0x1A, 0x1A, 0x1A), Body: Color.FromArgb(0x33, 0x33, 0x33), Mute: Color.FromArgb(0x6A, 0x6B, 0x6C),
        Accent: Color.FromArgb(0x1E, 0x9E, 0x63), Attention: Color.FromArgb(0xB0, 0x6A, 0x00), Error: Color.FromArgb(0xC4, 0x2B, 0x1C),
        ButtonHover: Color.FromArgb(0xE9, 0xE9, 0xE9));

    public static readonly Palette Dark = new(
        Canvas: Color.FromArgb(0x10, 0x11, 0x11), Surface: Color.FromArgb(0x18, 0x19, 0x1A), Hairline: Color.FromArgb(0x2A, 0x2C, 0x2D),
        Ink: Color.FromArgb(0xF4, 0xF4, 0xF6), Body: Color.FromArgb(0xCD, 0xCD, 0xCD), Mute: Color.FromArgb(0x9C, 0x9C, 0x9D),
        Accent: Color.FromArgb(0x59, 0xD4, 0x99), Attention: Color.FromArgb(0xFF, 0xC5, 0x33), Error: Color.FromArgb(0xFF, 0x61, 0x61),
        ButtonHover: Color.FromArgb(0x24, 0x27, 0x28));

    public static Palette Current => AppsUseLightTheme() ? Light : Dark;

    private static bool AppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
        catch (Exception)
        {
            return true;
        }
    }
}

public static class Fonts
{
    public static readonly Font Title = new("Segoe UI Semibold", 11f);
    public static readonly Font Body = new("Segoe UI", 9.75f);
    public static readonly Font BodyStrong = new("Segoe UI Semibold", 9.75f);
    public static readonly Font Caption = new("Segoe UI", 8.25f);
}

public static class Icons
{
    /// <summary>Icona incorporata nell'eseguibile, alla misura richiesta.</summary>
    public static Icon Load(string name, Size size)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException("icona mancante: " + name);
        return new Icon(stream, size);
    }
}

internal static class Dwm
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    /// <summary>Angoli arrotondati di Windows 11 (su Windows 10 non fa nulla).</summary>
    public static void RoundCorners(IntPtr hwnd)
    {
        var v = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref v, sizeof(int));
    }

    public static void DarkTitleBar(IntPtr hwnd, bool dark)
    {
        var v = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int));
    }
}
