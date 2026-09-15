using System.IO;

namespace Moye.Services;

/// <summary>Preserves the inner cause of dependency and storage failures.</summary>
public sealed class ErrorReporter(string logPath)
{
    public string Report(Exception exception)
    {
        var message = Describe(exception);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
            File.AppendAllText(logPath, $"{DateTimeOffset.Now:O} {exception}\n\n");
            return message + "\n\nDetails were saved to:\n" + logPath;
        }
        catch { return message; } // Reporting must not replace the original failure.
    }

    public static string Describe(Exception exception)
    {
        for (Exception? cause = exception; cause is not null; cause = cause.InnerException)
        {
            if (cause.HResult == unchecked((int)0x800711C7))
                return "Windows application control blocked a component required by Moye. " +
                    "Install the latest Moye update. If it is still blocked, share the error log with support or your administrator." +
                    "\n\n" + cause.Message;
        }
        var root = exception.GetBaseException();
        return ReferenceEquals(root, exception) ? exception.Message : exception.Message + "\n\n" + root.Message;
    }
}
