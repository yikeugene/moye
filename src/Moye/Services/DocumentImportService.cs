using System.IO;
using Moye.Models;

namespace Moye.Services;

/// <summary>Office documents become local PDF backgrounds; the source file is never edited.</summary>
public sealed class DocumentImportService(IPdfService pdf, IOfficePdfConverter converter)
{
    public const string FileFilter = "Supported documents|*.pdf;*.docx;*.pptx;*.ppsx;*.odt;*.odp|PDF documents|*.pdf|Word documents|*.docx|PowerPoint presentations|*.pptx;*.ppsx|OpenDocument files|*.odt;*.odp";
    public static bool IsSupported(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".pdf" or ".docx" or ".pptx" or ".ppsx" or ".odt" or ".odp";

    public async Task<IReadOnlyList<NotePage>> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupported(path)) throw new NotSupportedException("Choose a PDF, DOCX, PPTX, PPSX, ODT or ODP document.");
        var source = Path.GetFullPath(path);
        if (Path.GetExtension(source).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return await pdf.ImportAsync(source, cancellationToken);

        // Conversion works on a disposable copy, including when Office writes
        // lock files next to the document. Nothing is added until PDF validation succeeds.
        var work = Path.Combine(Path.GetTempPath(), "MoyeImport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var input = Path.Combine(work, Path.GetFileName(source));
            await using (var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            await using (var to = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await from.CopyToAsync(to, cancellationToken);
            var output = Path.Combine(work, Path.GetFileNameWithoutExtension(source) + ".pdf");
            await converter.ConvertAsync(input, output, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(output) || new FileInfo(output).Length == 0)
                throw new InvalidDataException("The document converter did not produce a PDF. No pages were imported.");
            return await pdf.ImportAsync(output, cancellationToken);
        }
        finally
        {
            // A converter may briefly retain a lock after failure. Cleanup must
            // not hide the original error or turn a successful import into one.
            try { Directory.Delete(work, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
