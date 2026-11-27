using System.IO;
using System.Runtime;
using System.Windows;
using System.Windows.Threading;

namespace nSpectrum.App;

public partial class App : Application
{
    /// <summary>%LOCALAPPDATA%\nSpectrum\nSpectrum.log: unexpected errors, newest last.</summary>
    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "nSpectrum", "nSpectrum.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        // Keep the GC out of the way of the audio and render threads.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        // A bug in a meter painter or a menu handler must not close the app with no trace.
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log("Unhandled exception (fatal)", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log("Unobserved task exception", a.Exception); a.SetObserved(); };
        Rack.RackTextures.Register(Resources); // before the main window loads: its faceplates use them
        base.OnStartup(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("UI thread exception", e.Exception);
        e.Handled = true;
        MessageBox.Show(MainWindow, $"Something went wrong: {e.Exception.Message}\n\nnSpectrum keeps running. Details were written to\n{LogPath}",
            "nSpectrum", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Appends an error to the log file. Never throws.</summary>
    public static void Log(string what, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > 1_000_000) info.Delete(); // keep it small
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {what}: {ex}{Environment.NewLine}");
        }
        catch
        {
            // Logging is best effort.
        }
    }
}
