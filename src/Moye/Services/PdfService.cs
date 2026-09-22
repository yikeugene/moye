using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Moye.Models;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Windows.Storage.Streams;
using NativePdf = Windows.Data.Pdf.PdfDocument;
using NativePage = Windows.Data.Pdf.PdfPage;

namespace Moye.Services;

/// <summary>Original PDF bytes remain assets; the notebook owns all editable overlays.</summary>
public sealed class PdfService(INotebookRepository repository) : IPdfService
{
    private const long CacheLimit = 128L * 1024 * 1024;
    private const long MaxPixels = 16L * 1024 * 1024;
    private readonly SemaphoreSlim _renderGate = new(2);
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _cache = [];
    private readonly LinkedList<CacheEntry> _recent = [];
    private long _cacheBytes;

    public async Task<IReadOnlyList<NotePage>> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.Length == 0) throw new InvalidDataException("The PDF file is empty.");
        // Validate both readers before storing an asset or adding notebook pages.
        var metadata = await RunStaAsync(() => ReadMetadata(bytes), cancellationToken);
        using var input = await NativeStreamAsync(bytes, cancellationToken);
        NativePdf native;
        try { native = await NativePdf.LoadFromStreamAsync(input).AsTask(cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new InvalidDataException("Windows could not open this PDF. Save it as a standard, unencrypted PDF and try again.", ex); }
        if (native.PageCount != metadata.Count) throw new InvalidDataException("The PDF readers reported different page counts. The file was not imported.");
        for (uint index = 0; index < native.PageCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var page = native.GetPage(index);
                var size = page.Size;
                if (!ValidDimension(size.Width) || !ValidDimension(size.Height))
                    throw new InvalidDataException("The page has an unsupported size.");
                metadata[(int)index].Width = size.Width;
                metadata[(int)index].Height = size.Height;
                await page.PreparePageAsync().AsTask(cancellationToken);
                // Bound BOTH dimensions: width alone can request an enormous
                // raster for a narrow page, or round a panoramic page to zero height.
                var probeScale = 32 / Math.Max(size.Width, size.Height);
                using var output = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(output, new Windows.Data.Pdf.PdfPageRenderOptions
                {
                    DestinationWidth = (uint)Math.Max(1, Math.Round(size.Width * probeScale)),
                    DestinationHeight = (uint)Math.Max(1, Math.Round(size.Height * probeScale)),
                    IsIgnoringHighContrast = true
                }).AsTask(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidDataException($"Unable to read PDF page {index + 1}. The file was not imported. " +
                    "Try saving a fresh PDF copy from the source application.", ex);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var asset = await repository.PutAssetAsync(Path.GetFileName(path), "application/pdf", bytes);
        foreach (var page in metadata) page.Pdf!.AssetId = asset.Id;
        return metadata;
    }

    public async Task<BitmapSource> RenderAsync(NotePage page, double scale, CancellationToken cancellationToken = default)
    {
        if (!ValidDimension(page.Width) || !ValidDimension(page.Height)) throw new InvalidDataException("Invalid page dimensions.");
        scale = double.IsFinite(scale) ? Math.Clamp(scale, .1, 6) : 1;
        scale = Math.Min(scale, Math.Min(8192 / page.Width, 8192 / page.Height));
        scale = Math.Min(scale, Math.Sqrt(MaxPixels / (page.Width * page.Height)));
        var width = Math.Max(1, (int)Math.Ceiling(page.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(page.Height * scale));
        var key = page.Pdf is { } source
            ? $"{source.AssetId}:{source.PageIndex}:{width}:{height}"
            : $"paper:{page.Template}:{page.Width:R}:{page.Height:R}:{width}:{height}";
        cancellationToken.ThrowIfCancellationRequested();
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                _recent.Remove(cached); _recent.AddFirst(cached);
                return cached.Value.Bitmap;
            }
        }
        await _renderGate.WaitAsync(cancellationToken);
        try
        {
            BitmapSource bitmap;
            if (page.Pdf is null)
                bitmap = await RunStaAsync(() => RenderPaper(page, width, height), cancellationToken);
            else
            {
                var asset = await repository.GetAssetAsync(page.Pdf.AssetId);
                using var input = await NativeStreamAsync(asset.Bytes, cancellationToken);
                var document = await NativePdf.LoadFromStreamAsync(input).AsTask(cancellationToken);
                if (page.Pdf.PageIndex < 0 || page.Pdf.PageIndex >= document.PageCount)
                    throw new InvalidDataException("The original PDF page could not be found.");
                using var nativePage = document.GetPage((uint)page.Pdf.PageIndex);
                bitmap = await RenderNativePageAsync(nativePage, width, height, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            lock (_cacheLock)
            {
                if (_cache.Remove(key, out var previous)) { _recent.Remove(previous); _cacheBytes -= previous.Value.Bytes; }
                var entry = new CacheEntry(key, bitmap, (long)bitmap.PixelWidth * bitmap.PixelHeight * 4);
                _cache[key] = _recent.AddFirst(entry); _cacheBytes += entry.Bytes;
                while (_cacheBytes > CacheLimit && _recent.Last is { } oldest)
                { _recent.RemoveLast(); _cache.Remove(oldest.Value.Key); _cacheBytes -= oldest.Value.Bytes; }
            }
            return bitmap;
        }
        finally { _renderGate.Release(); }
    }

    public async Task ExportAsync(string path, NotebookDocument document, CancellationToken cancellationToken = default)
    {
        var snapshot = document.Snapshot();
        if (snapshot.Pages.Count == 0) throw new InvalidOperationException("Add a page to the notebook first.");
        var assets = new Dictionary<string, AssetData>();
        foreach (var id in snapshot.Pages.SelectMany(p => p.Images.Select(i => i.AssetId)
                     .Concat(p.Pdf is null ? [] : new[] { p.Pdf.AssetId })).Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            assets[id] = await repository.GetAssetAsync(id);
        }
        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The export folder does not exist.");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await RunStaAsync(() =>
            {
                using var result = new PdfDocument();
                using var imageResources = new ExportImages();
                // We write standards-compliant zlib streams below, including the checksum trailer.
                result.Options.CompressContentStreams = false;
                result.Info.Title = snapshot.Title;
                result.Info.Creator = "Moye";
                foreach (var note in snapshot.Pages)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PdfPage outputPage;
                    if (note.Pdf is { } source)
                    {
                        // Open each source separately: duplicated/reordered source pages never share a mutable import mapping.
                        using var stream = new MemoryStream(assets[source.AssetId].Bytes, false);
                        using var original = OpenStaticPdf(stream);
                        if (source.PageIndex < 0 || source.PageIndex >= original.PageCount)
                            throw new InvalidDataException("The original PDF page could not be found.");
                        var originalPage = original.Pages[source.PageIndex];
                        outputPage = result.AddPage(originalPage);
                        if (AnnotationCount(outputPage) != AnnotationCount(originalPage))
                            throw new InvalidDataException("The existing PDF annotations could not be fully preserved. The export file was not overwritten.");
                    }
                    else
                    {
                        outputPage = result.AddPage();
                        outputPage.Width = XUnit.FromPoint(note.Width * .75);
                        outputPage.Height = XUnit.FromPoint(note.Height * .75);
                    }
                    using var graphics = XGraphics.FromPdfPage(outputPage, XGraphicsPdfPageOptions.Append);
                    graphics.MultiplyTransform(PageTransform(note, outputPage));
                    graphics.IntersectClip(new XRect(0, 0, note.Width, note.Height));
                    if (note.Pdf is null) DrawPaper(graphics, note);
                    var objectsBeforeImages = result.Internals.GetAllObjects().ToHashSet();
                    foreach (var image in note.Images)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var picture = imageResources.Get(image.AssetId, assets[image.AssetId].Bytes);
                        graphics.DrawImage(picture, image.X, image.Y, image.Width, image.Height);
                    }
                    NormalizeNewImageStreams(result, objectsBeforeImages);
                    foreach (var text in note.Texts) DrawText(graphics, text);
                    if (note.InkData.Length > 0)
                    {
                        using var inkStream = new MemoryStream(note.InkData, false);
                        var strokes = new StrokeCollection(inkStream);
                        // InkCanvas groups highlighters by RGB, applies opacity once to each group,
                        // and presents those groups below normal strokes. Union prevents darker overlaps.
                        foreach (var group in strokes.Where(s => s.DrawingAttributes.IsHighlighter)
                                     .GroupBy(s => Color.FromRgb(s.DrawingAttributes.Color.R, s.DrawingAttributes.Color.G, s.DrawingAttributes.Color.B)))
                        {
                            Geometry? union = null;
                            foreach (var stroke in group)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                union = union is null ? stroke.GetGeometry() : Geometry.Combine(union, stroke.GetGeometry(), GeometryCombineMode.Union, null);
                            }
                            var color = group.Key;
                            graphics.DrawPath(new XSolidBrush(XColor.FromArgb(128, color.R, color.G, color.B)),
                                ToPdfPath(union!));
                        }
                        foreach (var stroke in strokes.Where(s => !s.DrawingAttributes.IsHighlighter))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var attributes = stroke.DrawingAttributes;
                            var color = attributes.Color;
                            var brush = new XSolidBrush(XColor.FromArgb(color.A, color.R, color.G, color.B));
                            graphics.DrawPath(brush, ToPdfPath(stroke.GetGeometry()));
                        }
                    }
                    graphics.Dispose(); // Commit the appended content before encoding it.
                    WriteValidFlate(outputPage.Contents.Elements.GetDictionary(outputPage.Contents.Elements.Count - 1)!);
                }
                cancellationToken.ThrowIfCancellationRequested();
                using var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                result.Save(file, false);
                file.Flush(true);
                return true;
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static List<NotePage> ReadMetadata(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        using var document = OpenStaticPdf(stream);
        if (document.PageCount == 0) throw new InvalidDataException("The PDF has no pages.");
        var pages = new List<NotePage>(document.PageCount);
        for (var index = 0; index < document.PageCount; index++)
        {
            var page = document.Pages[index];
            var crop = VisibleBox(page);
            var rotation = ((page.Rotate % 360) + 360) % 360;
            if (rotation % 90 != 0) throw new InvalidDataException($"PDF page {index + 1}: The page rotation is not supported.");
            pages.Add(new NotePage { Pdf = new PdfPageSource { PageIndex = index, Rotation = rotation,
                CropX = crop.X1, CropY = crop.Y1, CropWidth = crop.Width, CropHeight = crop.Height } });
        }
        return pages;
    }

    private static PdfDocument OpenStaticPdf(Stream stream)
    {
        PdfDocument document;
        try { document = PdfReader.Open(stream, PdfDocumentOpenMode.Import); }
        catch (Exception ex) { throw new InvalidDataException("This PDF could not be imported. Use a standard, unencrypted PDF.", ex); }
        try
        {
            if (document.SecuritySettings.IsEncrypted) throw new InvalidDataException("Encrypted PDFs are not supported. Save an unencrypted copy and try again.");
            var catalog = document.Internals.Catalog;
            if (HasInteractiveForm(catalog) || ReadPdfEntry(catalog, "/Perms") is not null)
                throw new InvalidDataException("PDF forms and digital signatures are not supported. Export a standard PDF first.");
            foreach (var item in document.Internals.GetAllObjects().OfType<PdfDictionary>())
                if (item.Elements.GetName("/Type") == "/Sig" || item.Elements.GetName("/FT") == "/Sig")
                    throw new InvalidDataException("PDFs containing digital signatures are not supported.");
            for (var pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
            {
                try { ValidateStaticPage(document.Pages[pageIndex]); }
                catch (InvalidDataException ex) { throw new InvalidDataException($"PDF page {pageIndex + 1}: {ex.Message}", ex); }
            }
            return document;
        }
        catch { document.Dispose(); throw; }
    }

    private static void ValidateStaticPage(PdfPage page)
    {
        _ = VisibleBox(page);
        var userUnit = page.Elements.GetReal("/UserUnit");
        if (userUnit != 0 && userUnit != 1) throw new InvalidDataException("The PDF uses custom page units. Save it as a standard PDF first.");
        var annotations = page.Elements.GetArray("/Annots");
        if (annotations is null) return;
        for (var index = 0; index < annotations.Elements.Count; index++)
        {
            var annotation = annotations.Elements.GetDictionary(index);
            var subtype = annotation?.Elements.GetName("/Subtype");
            if (subtype is not ("/Text" or "/FreeText" or "/Square" or "/Circle" or "/Highlight" or "/Underline"
                or "/StrikeOut" or "/Squiggly" or "/Ink" or "/Stamp" or "/Line" or "/Polygon" or "/PolyLine" or "/Caret" or "/Popup" or "/Link"))
                throw new InvalidDataException("The PDF contains unsupported interactive annotations. Flatten its annotations before importing.");
            var action = ReadPdfEntry(annotation!, "/A");
            if (ReadPdfEntry(annotation!, "/Dest") is not null || ReadPdfEntry(annotation!, "/AA") is not null ||
                (action is not null && (action is not PdfDictionary actionDictionary || actionDictionary.Elements.GetName("/S") != "/URI")))
                throw new InvalidDataException("The PDF contains annotations that are interactive or reference other pages. Flatten them before importing to preserve their content when pages are reordered.");
        }
    }

    private static bool HasInteractiveForm(PdfDictionary catalog)
    {
        var entry = ReadPdfEntry(catalog, "/AcroForm");
        if (entry is null) return false;
        if (entry is not PdfDictionary form) return true;
        // PDF producers may leave the form dictionary and default appearance
        // metadata after flattening its last field. An empty shell has no
        // interactive content to lose when importing or exporting page copies.
        var fields = ReadPdfEntry(form, "/Fields");
        if (fields is not null && (fields is not PdfArray array || array.Elements.Count != 0)) return true;
        if (ReadPdfEntry(form, "/XFA") is not null ||
            (ReadPdfEntry(form, "/SigFlags") is not null && form.Elements.GetInteger("/SigFlags") != 0)) return true;
        var calculations = ReadPdfEntry(form, "/CO");
        return calculations is not null && (calculations is not PdfArray order || order.Elements.Count != 0);
    }

    private static PdfItem? ReadPdfEntry(PdfDictionary dictionary, string key)
    {
        var value = dictionary.Elements[key];
        if (value is PdfReference reference) value = reference.Value;
        // PDF dictionary null values, including indirect null objects, mean
        // the same thing as a missing optional entry (ISO 32000-1, 7.3.9).
        return value is PdfNull or PdfNullObject ? null : value;
    }

    private static int AnnotationCount(PdfPage page) => page.Elements.GetArray("/Annots")?.Elements.Count ?? 0;

    private static PdfRectangle VisibleBox(PdfPage page)
    {
        var media = page.MediaBox;
        var crop = page.HasCropBox ? page.CropBox : media;
        var visible = new PdfRectangle(new XPoint(Math.Max(media.X1, crop.X1), Math.Max(media.Y1, crop.Y1)),
            new XPoint(Math.Min(media.X2, crop.X2), Math.Min(media.Y2, crop.Y2)));
        if (!ValidDimension(visible.Width) || !ValidDimension(visible.Height)) throw new InvalidDataException("Invalid PDF crop area.");
        return visible;
    }

    private static XMatrix PageTransform(NotePage note, PdfPage page)
    {
        if (note.Pdf is null) return new XMatrix(.75, 0, 0, .75, 0, 0);
        var crop = VisibleBox(page);
        var rotation = ((page.Rotate % 360) + 360) % 360;
        var width = rotation is 90 or 270 ? crop.Height : crop.Width;
        var height = rotation is 90 or 270 ? crop.Width : crop.Height;
        var sx = width / note.Width;
        var sy = height / note.Height;
        // XGraphics uses top-left coordinates on the UNROTATED media page.
        // Map visible, rotated DIPs to original PDF coordinates, then flip PDF Y once.
        var h = page.Height.Point;
        return rotation switch
        {
            0 => new XMatrix(sx, 0, 0, sy, crop.X1, h - crop.Y2),
            90 => new XMatrix(0, -sx, sy, 0, crop.X1, h - crop.Y1),
            180 => new XMatrix(-sx, 0, 0, -sy, crop.X2, h - crop.Y1),
            270 => new XMatrix(0, sx, -sy, 0, crop.X2, h - crop.Y2),
            _ => throw new InvalidDataException("The PDF page rotation is not supported.")
        };
    }

    private static XGraphicsPath ToPdfPath(Geometry geometry)
    {
        var path = PathGeometry.CreateFromGeometry(geometry);
        // PDFsharp 6.2.4 clones the WPF geometry but leaves its separate PDF
        // fill mode at Alternate. Ink uses Nonzero: Alternate cuts white holes
        // where its pressure contours overlap. Preserve each geometry's rule,
        // including the counters in outlined text, without rasterizing it.
        return new XGraphicsPath(path)
        {
            FillMode = path.FillRule == FillRule.Nonzero ? XFillMode.Winding : XFillMode.Alternate
        };
    }

    private static void DrawText(XGraphics graphics, NoteText text)
    {
        if (string.IsNullOrEmpty(text.Text)) return;
        var color = (Color)ColorConverter.ConvertFromString(text.Color);
        var formatted = new FormattedText(text.Text, CultureInfo.GetCultureInfo("zh-HK"), FlowDirection.LeftToRight,
            new Typeface(new FontFamily(text.FontFamily), text.Italic ? FontStyles.Italic : FontStyles.Normal,
                text.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal), text.FontSize, Brushes.Black, 1)
        {
            // Include the partially visible last line, then apply the same box clip as TextBox.
            MaxTextWidth = NoteTextLayout.ContentWidth(text.Width), MaxTextHeight = Math.Max(1, text.Height + text.FontSize * 1.4),
            LineHeight = text.FontSize * 1.4, Trimming = TextTrimming.None,
            TextAlignment = text.Alignment switch
            {
                NoteTextAlignment.Center => TextAlignment.Center,
                NoteTextAlignment.Right => TextAlignment.Right,
                _ => TextAlignment.Left
            }
        };
        var geometry = formatted.BuildGeometry(new Point(text.X + NoteTextLayout.HorizontalInset, text.Y));
        var state = graphics.Save();
        graphics.IntersectClip(new XRect(text.X, text.Y, Math.Max(1, text.Width), Math.Max(1, text.Height)));
        graphics.DrawPath(new XSolidBrush(XColor.FromArgb(color.A, color.R, color.G, color.B)), ToPdfPath(geometry));
        graphics.Restore(state);
    }

    private static void NormalizeNewImageStreams(PdfDocument document, HashSet<PdfObject> existingObjects)
    {
        // PDFsharp 6.2.4 FlateDecode.Encode omits zlib's Adler-32 trailer. Some PDF readers
        // tolerate it, but Windows.Data.Pdf drops these RGB images and their alpha masks.
        // Re-encode only images created by this export; never alter imported PDF resources.
        foreach (var dictionary in document.Internals.GetAllObjects().Where(o => !existingObjects.Contains(o)).OfType<PdfDictionary>())
        {
            if (dictionary.Elements.GetName("/Subtype") != "/Image" ||
                dictionary.Elements.GetName("/Filter") != "/FlateDecode") continue;
            WriteValidFlate(dictionary);
        }
    }

    private static void WriteValidFlate(PdfDictionary dictionary)
    {
        var raw = dictionary.Stream.UnfilteredValue;
        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.SmallestSize, true)) zlib.Write(raw);
        dictionary.Stream.Value = buffer.ToArray();
        dictionary.Elements.SetName("/Filter", "/FlateDecode");
        dictionary.Elements.Remove("/DecodeParms"); // The raw bytes already have any predictor removed.
    }

    private static void DrawPaper(XGraphics graphics, NotePage page)
    {
        graphics.DrawRectangle(XBrushes.White, 0, 0, page.Width, page.Height);
        foreach (var line in Controls.PaperPattern.Lines(page.Template, page.Width, page.Height))
            graphics.DrawLine(new XPen(XColor.FromArgb(line.Color.R, line.Color.G, line.Color.B), line.Thickness),
                line.Start.X, line.Start.Y, line.End.X, line.End.Y);
        foreach (var dot in Controls.PaperPattern.Dots(page.Template, page.Width, page.Height))
            graphics.DrawEllipse(new XSolidBrush(XColor.FromArgb(dot.Color.R, dot.Color.G, dot.Color.B)),
                dot.Center.X - dot.Radius, dot.Center.Y - dot.Radius, dot.Radius * 2, dot.Radius * 2);
    }

    private static BitmapSource RenderPaper(NotePage page, int width, int height)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.PushTransform(new ScaleTransform(width / page.Width, height / page.Height));
            Controls.PaperPattern.Draw(drawing, page.Template, page.Width, page.Height);
            drawing.Pop();
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }

    private static async Task<InMemoryRandomAccessStream> NativeStreamAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        var stream = new InMemoryRandomAccessStream();
        try
        {
            using var writer = new DataWriter(stream);
            writer.WriteBytes(bytes);
            await writer.StoreAsync().AsTask(cancellationToken);
            writer.DetachStream(); stream.Seek(0); return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private static async Task<BitmapSource> RenderNativePageAsync(NativePage page, int width, int height, CancellationToken cancellationToken)
    {
        await page.PreparePageAsync().AsTask(cancellationToken);
        using var output = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(output, new Windows.Data.Pdf.PdfPageRenderOptions
        { DestinationWidth = (uint)width, DestinationHeight = (uint)height, IsIgnoringHighContrast = true }).AsTask(cancellationToken);
        output.Seek(0);
        using var reader = new DataReader(output.GetInputStreamAt(0));
        await reader.LoadAsync((uint)output.Size).AsTask(cancellationToken);
        var bytes = new byte[(int)output.Size]; reader.ReadBytes(bytes);
        using var stream = new MemoryStream(bytes, false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        // Windows' PDF renderer can include the desktop scale factor in its PNG on high-DPI systems.
        // Decode to the requested pixel budget so callers always get scale × page DIPs, on every display.
        bitmap.DecodePixelWidth = width; bitmap.DecodePixelHeight = height;
        bitmap.StreamSource = stream; bitmap.EndInit();
        bitmap.Freeze(); return bitmap;
    }

    private static bool ValidDimension(double value) => double.IsFinite(value) && value > 0 && value < 100000;

    private static Task<T> RunStaAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { cancellationToken.ThrowIfCancellationRequested(); completion.TrySetResult(action()); }
            catch (OperationCanceledException) { completion.TrySetCanceled(cancellationToken); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true, Name = "Moye PDF worker" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }

    private sealed record CacheEntry(string Key, BitmapSource Bitmap, long Bytes);

    private sealed class ExportImages : IDisposable
    {
        private readonly Dictionary<string, (MemoryStream Stream, XImage Image)> _images = [];
        public XImage Get(string id, byte[] bytes)
        {
            if (_images.TryGetValue(id, out var existing)) return existing.Image;
            var stream = new MemoryStream(bytes, false);
            try
            {
                var image = XImage.FromStream(stream);
                _images[id] = (stream, image); return image;
            }
            catch { stream.Dispose(); throw; }
        }
        public void Dispose()
        { foreach (var resource in _images.Values) { resource.Image.Dispose(); resource.Stream.Dispose(); } }
    }
}
