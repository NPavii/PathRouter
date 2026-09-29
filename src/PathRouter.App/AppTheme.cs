using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace PathRouter.App;

/// <summary>
/// Светлая/тёмная тема приложения. Канвасы (Win2D) читают палитру напрямую,
/// стандартные контролы — через RootGrid.RequestedTheme.
/// Выбор сохраняется в %LocalAppData%\PathRouter\settings.json.
/// </summary>
public static class AppTheme
{
    private static readonly string SettingsPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PathRouter", "settings.json");

    public static bool IsDark { get; private set; }
    public static event Action? Changed;

    public static void Load()
    {
        try
        {
            if (System.IO.File.Exists(SettingsPath))
                IsDark = System.IO.File.ReadAllText(SettingsPath).Contains("\"dark\": true");
        }
        catch { /* некритично */ }
    }

    public static void Toggle()
    {
        IsDark = !IsDark;
        try
        {
            var dir = System.IO.Path.GetDirectoryName(SettingsPath)!;
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(SettingsPath, IsDark ? "{\"dark\": true}" : "{\"dark\": false}");
        }
        catch { /* некритично */ }
        Changed?.Invoke();
    }

    private static Color C(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);

    // ---------- палитра ----------

    public static Color Bg            => IsDark ? C(255, 24, 25, 29) : C(255, 250, 251, 253);
    public static Color PanelBg       => IsDark ? C(255, 32, 33, 38) : C(255, 243, 244, 248);
    public static Color ActionsBg     => IsDark ? C(255, 38, 39, 45) : C(255, 235, 237, 244);
    public static Color Divider       => IsDark ? C(255, 55, 57, 65) : C(255, 216, 219, 228);
    public static Color DropZoneBg    => IsDark ? C(255, 40, 44, 58) : C(255, 232, 236, 247);
    public static Color DropZoneBrush => IsDark ? C(255, 74, 82, 115) : C(255, 185, 198, 238);

    public static Color SourceFill    => IsDark ? C(255, 42, 46, 61) : C(255, 238, 243, 255);
    public static Color DestFill      => IsDark ? C(255, 38, 39, 44) : C(255, 244, 244, 244);
    public static Color DestBorder    => IsDark ? C(255, 72, 74, 84) : C(255, 200, 200, 200);
    public static Color EdgeColor     => IsDark ? C(255, 96, 101, 122) : C(255, 154, 167, 199);
    public static Color TextDark      => IsDark ? C(255, 229, 231, 239) : C(255, 40, 45, 60);
    public static Color TextGray      => IsDark ? C(255, 152, 157, 172) : C(255, 110, 115, 130);
    public static Color GridDot       => IsDark ? C(40, 66, 70, 90) : C(60, 120, 130, 160);
    public static Color ConservedBg   => IsDark ? C(255, 36, 40, 51) : C(255, 238, 241, 247);
    public static Color ConservedBr   => IsDark ? C(255, 96, 104, 128) : C(255, 140, 155, 185);
    public static Color ChainColor    => IsDark ? C(200, 130, 148, 240) : C(200, 79, 107, 237);

    // не зависят от темы
    public static Color Accent     => C(255, 79, 107, 237);
    public static Color AccentDark => C(255, 43, 74, 203);
    public static Color UpdateColor => C(255, 247, 99, 12);
    public static Color ErrorRed   => C(255, 196, 43, 28);
    public static Color OkGreen    => C(255, 60, 160, 90);

    public static Brush Brush(Color c) => new SolidColorBrush(c);
}
