using System.IO;
using System.Text.Json;
using System.Windows;

namespace PurviewPdfViewer;

/// <summary>
/// Bepaalt de startgrootte van het hoofdvenster op basis van het beschikbare schermoppervlak
/// (rekening houdend met schaalvergroting) en onthoudt grootte, positie en maximaliseren per gebruiker.
/// Een opgeslagen positie die niet (meer) op een scherm valt, bijv. na het loskoppelen van een
/// tweede monitor, wordt genegeerd.
/// </summary>
internal static class WindowPlacement
{
    private const double PreferredWidth = 1180;
    private const double PreferredHeight = 860;
    private const double ScreenFraction = 0.85;

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PurviewPdfViewer", "window.json");

    private sealed record Saved(double Left, double Top, double Width, double Height, bool Maximized);

    public static void Apply(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        var work = SystemParameters.WorkArea; // primair scherm, in device-independent pixels

        var saved = Load();
        if (saved != null && IsVisibleOnAnyScreen(saved))
        {
            window.Width = Math.Max(window.MinWidth, saved.Width);
            window.Height = Math.Max(window.MinHeight, saved.Height);
            window.Left = saved.Left;
            window.Top = saved.Top;
            if (saved.Maximized) window.WindowState = WindowState.Maximized;
            return;
        }

        // Standaard: liefst 1180×860, maar nooit groter dan 85% van het werkgebied.
        var width = Math.Min(PreferredWidth, work.Width * ScreenFraction);
        var height = Math.Min(PreferredHeight, work.Height * ScreenFraction);

        // Minimumgrootte mag het venster niet groter maken dan het scherm.
        window.MinWidth = Math.Min(window.MinWidth, work.Width);
        window.MinHeight = Math.Min(window.MinHeight, work.Height);

        window.Width = Math.Max(window.MinWidth, width);
        window.Height = Math.Max(window.MinHeight, height);
        window.Left = work.Left + (work.Width - window.Width) / 2;
        window.Top = work.Top + (work.Height - window.Height) / 2;
    }

    public static void Save(Window window)
    {
        try
        {
            // RestoreBounds = grootte/positie vóór maximaliseren.
            var bounds = window.WindowState == WindowState.Normal
                ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
                : window.RestoreBounds;
            if (bounds.IsEmpty || bounds.Width < 100 || bounds.Height < 100) return;

            var saved = new Saved(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                window.WindowState == WindowState.Maximized);

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(saved));
        }
        catch
        {
            // Onthouden is een gemak, geen vereiste.
        }
    }

    private static Saved? Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(FilePath)) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>De titelbalk moet grotendeels binnen het (virtuele) scherm vallen en het venster mag niet groter zijn dan dat.</summary>
    private static bool IsVisibleOnAnyScreen(Saved s)
    {
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

        var titleBar = new Rect(s.Left + 40, s.Top, Math.Max(1, s.Width - 80), 30);
        return virtualScreen.Contains(titleBar.TopLeft)
            && virtualScreen.Contains(titleBar.BottomRight)
            && s.Width <= virtualScreen.Width
            && s.Height <= virtualScreen.Height;
    }
}
