using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

[assembly: InternalsVisibleTo("Moye.Tests")]

namespace Moye.Services;

/// <summary>Optional local conversion through an installed LibreOffice console launcher.</summary>
public sealed class LibreOfficePdfConverter : IOfficePdfConverter
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".docx", ".odt", ".pptx", ".ppsx", ".odp" };
    private readonly string? _executablePath;
    private readonly TimeSpan _timeout;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<ConversionResult>> _run;

    public LibreOfficePdfConverter(string? executablePath = null, TimeSpan? timeout = null)
        : this(executablePath, timeout ?? TimeSpan.FromMinutes(2), RunProcessAsync) { }

    internal LibreOfficePdfConverter(string? executablePath, TimeSpan timeout,
        Func<ProcessStartInfo, CancellationToken, Task<ConversionResult>> run)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(timeout), "The conversion timeout must be between zero and two minutes.");
        _executablePath = executablePath is null ? null : LocalPath(executablePath);
        _timeout = timeout;
        _run = run ?? throw new ArgumentNullException(nameof(run));
    }

    public static bool SupportsExtension(string pathOrExtension) =>
        !string.IsNullOrWhiteSpace(pathOrExtension) && Extensions.Contains(Path.GetExtension(pathOrExtension));

    public static bool IsAvailableFor(string pathOrExtension) =>
        SupportsExtension(pathOrExtension) && FindInstalledExecutable() is not null;

    public static string? FindInstalledExecutable()
    {
        // Do not search the current directory or PATH for an executable named soffice.
        foreach (var programFiles in new[]
                 { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
                 .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(programFiles, "LibreOffice", "program", "soffice.com");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public async Task ConvertAsync(string sourcePath, string outputPdfPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = LocalPath(sourcePath);
        var destination = LocalPath(outputPdfPath);
        if (!SupportsExtension(source)) throw new NotSupportedException("LibreOffice conversion supports DOCX, ODT, PPTX, PPSX, and ODP files.");
        if (!string.Equals(Path.GetExtension(destination), ".pdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The conversion output must be a PDF file.", nameof(outputPdfPath));
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The conversion output must not replace the source document.", nameof(outputPdfPath));
        if (!File.Exists(source)) throw new FileNotFoundException("The document to convert could not be found.", source);
        await Task.Run(() =>
        {
            if (OdfDocumentValidation.SupportsExtension(Path.GetExtension(source))) OdfDocumentValidation.Validate(source, cancellationToken);
            else OfficeDocumentValidation.Validate(source, cancellationToken);
        }, cancellationToken).ConfigureAwait(false);
        var executable = _executablePath ?? FindInstalledExecutable();
        if (executable is null || !File.Exists(executable))
            throw new NotSupportedException("LibreOffice is not installed. Install LibreOffice to convert this document, or save it as PDF in its original application.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        var token = deadline.Token;
        var work = Directory.CreateTempSubdirectory("Moye-LibreOffice-").FullName;
        string? pendingOutput = null;
        var canCleanWork = true;
        try
        {
            var profile = Path.Combine(work, "profile");
            var convertedDirectory = Path.Combine(work, "output");
            Directory.CreateDirectory(Path.Combine(profile, "user"));
            Directory.CreateDirectory(convertedDirectory);
            await File.WriteAllTextAsync(Path.Combine(profile, "user", "registrymodifications.xcu"),
                ProfileConfiguration, new UTF8Encoding(false), token).ConfigureAwait(false);
            var start = CreateStartInfo(executable, source, convertedDirectory, profile);
            token.ThrowIfCancellationRequested();
            var result = await _run(start, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (result.ExitCode != 0)
                throw new IOException("LibreOffice could not convert the document. " + result.Error.Trim());
            var converted = Path.Combine(convertedDirectory, Path.GetFileNameWithoutExtension(source) + ".pdf");
            await ValidatePdfAsync(converted, token).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            pendingOutput = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var input = new FileStream(converted, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            await using (var output = new FileStream(pendingOutput, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await input.CopyToAsync(output, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(pendingOutput, destination, true);
            pendingOutput = null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("LibreOffice conversion took too long and was stopped. Try saving the document as PDF in its original application.");
        }
        catch (ProcessCleanupException) { canCleanWork = false; throw; }
        finally
        {
            if (pendingOutput is not null) TryDeleteFile(pendingOutput);
            // The runner completes only after its owned process has stopped. This
            // directory was created here and never points at the user's LO profile.
            if (canCleanWork) TryDeleteDirectory(work);
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, string source, string outputDirectory, string profile)
    {
        var extension = Path.GetExtension(source);
        if (!SupportsExtension(source)) throw new NotSupportedException("The document format is not supported by the LibreOffice converter.");
        var filter = extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) || extension.Equals(".odt", StringComparison.OrdinalIgnoreCase)
            ? "writer_pdf_Export" : "impress_pdf_Export";
        var start = new ProcessStartInfo
        {
            FileName = executable, WorkingDirectory = Path.GetDirectoryName(outputDirectory)!,
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "-env:UserInstallation=" + new Uri(Path.GetFullPath(profile) + Path.DirectorySeparatorChar).AbsoluteUri,
            "--headless", "--nologo", "--nodefault", "--norestore", "--convert-to", "pdf:" + filter + ":" + PdfOptions,
            "--outdir", outputDirectory, source
        }) start.ArgumentList.Add(argument);
        return start;
    }

    // Upstream option names: help.libreoffice.org/latest/en-US/text/shared/guide/pdf_params.html
    private const string PdfOptions = """
        {"ExportFormFields":{"type":"boolean","value":"false"},"ExportBookmarks":{"type":"boolean","value":"false"},"ExportBookmarksToPDFDestination":{"type":"boolean","value":"false"},"ExportLinksRelativeFsys":{"type":"boolean","value":"false"},"ExportHiddenSlides":{"type":"boolean","value":"true"}}
        """;

    // These keys are defined in LibreOffice's Common.xcs and Writer.xcs schemas:
    // github.com/LibreOffice/core/tree/master/officecfg/registry/schema/org/openoffice/Office
    // The CLI uses MacroExecMode.USE_CONFIG / UpdateDocMode.ACCORDING_TO_CONFIG.
    // This profile is defense in depth, not a network or document-parser sandbox.
    private const string ProfileConfiguration = """
        <?xml version="1.0" encoding="UTF-8"?>
        <oor:items xmlns:oor="http://openoffice.org/2001/registry">
          <item oor:path="/org.openoffice.Office.Common/Security/Scripting">
            <prop oor:name="MacroSecurityLevel" oor:op="fuse"><value>3</value></prop>
            <prop oor:name="DisableMacrosExecution" oor:op="fuse"><value>true</value></prop>
            <prop oor:name="DisableActiveContent" oor:op="fuse"><value>true</value></prop>
            <prop oor:name="BlockUntrustedRefererLinks" oor:op="fuse"><value>true</value></prop>
            <prop oor:name="SecureURL" oor:op="fuse"><value/></prop>
          </item>
          <item oor:path="/org.openoffice.Office.Writer/Content/Update">
            <prop oor:name="Link" oor:op="fuse"><value>2</value></prop>
            <prop oor:name="Field" oor:op="fuse"><value>false</value></prop>
            <prop oor:name="Chart" oor:op="fuse"><value>false</value></prop>
          </item>
        </oor:items>
        """;

    private static async Task<ConversionResult> RunProcessAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = start };
        cancellationToken.ThrowIfCancellationRequested();
        try { if (!process.Start()) throw new IOException("LibreOffice could not be started."); }
        catch (Win32Exception exception) { throw new IOException("LibreOffice could not be started. Check that it is installed and allowed to run on this device.", exception); }
        Task<string> stdout = Task.FromResult("");
        Task<string> stderr = Task.FromResult("");
        try
        {
            process.StandardInput.Close();
            stdout = DrainAsync(process.StandardOutput);
            stderr = DrainAsync(process.StandardError);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await stdout.WaitAsync(cancellationToken).ConfigureAwait(false);
            var error = await stderr.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new(process.ExitCode, error);
        }
        catch (Exception conversionError)
        {
            // Only this Process instance and its descendants are ever terminated.
            // A fresh UserInstallation prevents forwarding to an existing LO session.
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                throw new ProcessCleanupException("The document converter could not be confirmed stopped. Its temporary profile has been retained.",
                    new AggregateException(conversionError, cleanupError));
            }
            throw;
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var result = new StringBuilder();
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            if (result.Length < 4096) result.Append(buffer, 0, Math.Min(count, 4096 - result.Length));
        return result.ToString();
    }

    private static async Task ValidatePdfAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) throw new IOException("LibreOffice did not produce a PDF. The document may be encrypted, damaged, or unsupported.");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var header = new byte[5];
        if (await file.ReadAsync(header, token).ConfigureAwait(false) != header.Length || !header.AsSpan().SequenceEqual("%PDF-"u8))
            throw new IOException("LibreOffice did not produce a valid PDF file.");
    }

    private static string LocalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && (!uri.IsFile || uri.IsUnc))
            throw new NotSupportedException("Office conversion requires local file paths.");
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            throw new NotSupportedException("Office conversion requires local file paths.");
        return fullPath;
    }

    private static void TryDeleteFile(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private static void TryDeleteDirectory(string path) { try { Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    internal sealed record ConversionResult(int ExitCode, string Error);
    private sealed class ProcessCleanupException(string message, Exception inner) : IOException(message, inner);
}
