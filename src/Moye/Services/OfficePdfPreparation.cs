using System.IO;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Moye.Services;

/// <summary>
/// Makes a disposable Office-generated PDF independent of its original page
/// navigation. Never use this on a directly imported PDF or an Office source.
/// Annotation appearances and ordinary external URI links remain intact.
/// </summary>
public static class OfficePdfPreparation
{
    public static async Task PrepareAsync(string convertedPdf, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(convertedPdf);
        var bytes = await File.ReadAllBytesAsync(destination, cancellationToken);
        await Task.Run(() => Prepare(destination, bytes, cancellationToken), cancellationToken);
    }

    private static void Prepare(string destination, byte[] bytes, CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(bytes, false);
        using var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        var changed = false;
        foreach (var page in document.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var annotations = page.Elements.GetArray("/Annots");
            if (annotations is null) continue;
            for (var index = 0; index < annotations.Elements.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var annotation = annotations.Elements.GetDictionary(index);
                if (annotation is null) continue; // The normal PDF validator reports malformed annotations.
                changed |= PdfAnnotationActions.MakeStatic(annotation);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!changed) return; // Preserve the exact converter output when there is nothing to normalize.

        document.Options.CompressContentStreams = false;
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!,
            "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                document.Save(output, false);
                output.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Replace(temporary, destination, null);
        }
        finally
        {
            // The conversion directory has an outer cleanup owner as well.
            // A transient cleanup failure must not hide a parse/write/cancel error.
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

}
