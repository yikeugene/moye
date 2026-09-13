using System.IO;
using System.Windows;
using Moye.Services;

namespace Moye;

public partial class App : Application
{
    private System.Threading.Mutex? _instanceMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        // UI language is English; document text and the user's number/date formats are preserved.
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        base.OnStartup(e);
        // Keep one writer/UI per library. Test launches can use an isolated directory.
        var dataIndex = Array.IndexOf(e.Args, "--data-dir");
        string? dbPath = dataIndex >= 0 && dataIndex + 1 < e.Args.Length ? Path.Combine(Path.GetFullPath(e.Args[dataIndex + 1]), "moye.db") : null;
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dbPath ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))));
        _instanceMutex = new(true, "Local\\Moye-" + key[..24], out bool first);
        if (!first) { MessageBox.Show("Moye is already open. Please use the existing window.", "Moye"); Shutdown(); return; }
        DispatcherUnhandledException += (_, args) =>
        {
            try { Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Moye")); File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Moye", "error.log"), $"{DateTimeOffset.Now:O} {args.Exception}\n"); } catch { }
            MessageBox.Show("The operation could not be completed. Previously saved notes are unaffected.\n\n" + args.Exception.Message, "Moye", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        var window = new MainWindow(new SqliteNotebookRepository(dbPath));
        MainWindow = window; window.Show();
    }
    protected override void OnExit(ExitEventArgs e) { _instanceMutex?.Dispose(); base.OnExit(e); }
}
