using System.Windows;

namespace PurviewPdfViewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Ondersteunt "Openen met" en slepen op de .exe: eerste argument = pad naar PDF.
        var fileToOpen = e.Args.FirstOrDefault(a => a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        var window = new MainWindow(fileToOpen);
        MainWindow = window;
        window.Show();
    }
}
