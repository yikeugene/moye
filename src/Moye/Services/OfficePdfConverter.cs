namespace Moye.Services;

/// <summary>Uses a local renderer; no document is sent to a conversion service.</summary>
public sealed class OfficePdfConverter : IOfficePdfConverter
{
    public async Task ConvertAsync(string sourcePath, string outputPdfPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OfficeConversionProcess.IsAvailableFor(System.IO.Path.GetExtension(sourcePath)))
            await new OfficeConversionProcess().ConvertAsync(sourcePath, outputPdfPath, cancellationToken);
        else if (LibreOfficePdfConverter.IsAvailableFor(sourcePath))
            await new LibreOfficePdfConverter().ConvertAsync(sourcePath, outputPdfPath, cancellationToken);
        else
            throw new InvalidOperationException("Office document import needs Microsoft Word/PowerPoint or LibreOffice installed on this computer. " +
                "ODT and ODP files require LibreOffice. Install a local converter, or save your document as PDF and import that copy. " +
                "PDF import works without Office.");
        // Office slide jumps and table-of-contents links refer to the old
        // document order. Keep their printed appearance as static annotations.
        await OfficePdfPreparation.PrepareAsync(outputPdfPath, cancellationToken);
    }
}
