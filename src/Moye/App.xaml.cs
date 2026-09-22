using System.IO;
using System.Windows;
using Moye.Services;

namespace Moye;

/// <summary>One resolved library location for storage, preferences, and the single-instance lock.</summary>
public sealed record LibraryLocation(string DatabasePath, string PreferencesPath, string MutexName)
{
    public static LibraryLocation Resolve(string? dataDirectory = null, string? localApplicationData = null)
    {
        var directory = dataDirectory ?? Path.Combine(
            localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Moye");
        var databasePath = Path.GetFullPath(Path.Combine(Path.GetFullPath(directory), "moye.db"));
        // Windows path spelling and trailing separators must not create another writer for the same library.
        var identity = databasePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).ToUpperInvariant();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
        return new LibraryLocation(databasePath,
            Path.Combine(Path.GetDirectoryName(databasePath)!, "writing-preferences.json"), "Local\\Moye-" + hash[..24]);
    }
}

public partial class App : Application
{
    private System.Threading.Mutex? _instanceMutex;
    protected override async void OnStartup(StartupEventArgs e)
    {
        // UI language is English; document text and the user's number/date formats are preserved.
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        base.OnStartup(e);
        // The isolated converter has no notebook library, preferences or main window.
        if (e.Args.Length > 0 && e.Args[0] == "--convert-office")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (e.Args.Length != 3) { Console.Error.WriteLine("--convert-office requires an input document and output PDF."); Shutdown(1); }
            else Shutdown(OfficeConversionWorker.Run(e.Args[1], e.Args[2]));
            return;
        }
        var checkIndex = Array.IndexOf(e.Args, "--check-storage");
        if (checkIndex >= 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                if (checkIndex + 1 >= e.Args.Length) throw new ArgumentException("--check-storage requires a new test directory.");
                await StorageSelfCheck.RunAsync(e.Args[checkIndex + 1]);
                Shutdown(0);
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); Shutdown(1); }
            return;
        }
        // Keep one writer/UI per library. Test launches can use an isolated directory.
        var dataIndex = Array.IndexOf(e.Args, "--data-dir");
        var location = LibraryLocation.Resolve(dataIndex >= 0 && dataIndex + 1 < e.Args.Length ? e.Args[dataIndex + 1] : null);
        var errors = new ErrorReporter(Path.Combine(Path.GetDirectoryName(location.DatabasePath)!, "error.log"));
        _instanceMutex = new(true, location.MutexName, out bool first);
        if (!first) { MessageBox.Show("Moye is already open. Please use the existing window.", "Moye"); Shutdown(); return; }
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("The operation could not be completed. Previously saved notes are unaffected.\n\n" + errors.Report(args.Exception), "Moye", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        var window = new MainWindow(new SqliteNotebookRepository(location.DatabasePath), new WritingPreferencesStore(location.PreferencesPath), errors);
        MainWindow = window; window.Show();
    }
    protected override void OnExit(ExitEventArgs e) { _instanceMutex?.Dispose(); base.OnExit(e); }
}
