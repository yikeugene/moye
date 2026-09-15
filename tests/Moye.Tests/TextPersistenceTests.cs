using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Moye.Controls;
using Moye.Models;
using Moye.Services;
using PdfSharp.Pdf.IO;

namespace Moye.Tests;

public sealed class TextPersistenceTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void LegacyJsonDefaultsToRegularUprightLeftAlignedText()
    {
        var text = JsonSerializer.Deserialize<NoteText>("""
            {"text":"舊筆記\nLegacy notes","fontFamily":"Microsoft JhengHei","fontSize":26,"color":"#FF123456"}
            """, DocumentJson.Options)!;
        Assert.False(text.Bold);
        Assert.False(text.Italic);
        Assert.Equal(NoteTextAlignment.Left, text.Alignment);
        Assert.Equal("舊筆記\nLegacy notes", text.Text);
        Assert.Equal(26, text.FontSize);
        Assert.Equal(0, (int)NoteTextAlignment.Left);
        Assert.Equal(1, (int)NoteTextAlignment.Center);
        Assert.Equal(2, (int)NoteTextAlignment.Right);
    }

    [Fact]
    public async Task StyledUnicodeTextSurvivesSqliteReopenAndBackupRestoreAsNewCopies()
    {
        using var sourceDirectory = new StorageTestDirectory();
        using var destinationDirectory = new StorageTestDirectory();
        var texts = (from alignment in Enum.GetValues<NoteTextAlignment>()
                     from bold in new[] { false, true }
                     from italic in new[] { false, true }
                     select new NoteText
                     {
                         Text = $"繁體中文 🖋\n{alignment} — 字體格式", FontFamily = "Microsoft JhengHei",
                         FontSize = 27, Color = "#FF1649A3", Bold = bold, Italic = italic, Alignment = alignment
                     }).ToList();
        var original = new NotebookDocument { Title = "文字樣式", Pages = [new NotePage { Texts = texts }] };
        using (var writer = new SqliteNotebookRepository(sourceDirectory.DatabasePath)) await writer.SaveAsync(original);
        using var source = new SqliteNotebookRepository(sourceDirectory.DatabasePath);
        var reopened = (await source.LoadAsync(original.Id))!;
        Assert.Equal(texts, reopened.Pages[0].Texts);
        var snapshot = reopened.Snapshot();
        reopened.Pages[0].Texts[0].Bold = !reopened.Pages[0].Texts[0].Bold;
        Assert.Equal(texts, snapshot.Pages[0].Texts);

        var path = Path.Combine(sourceDirectory.Root, "styled-text.moye");
        await new BackupService(source).ExportAsync(path, [snapshot]);
        using (var archive = ZipFile.OpenRead(path))
            Assert.Equal(2, ReadJson(archive.GetEntry("manifest.json")!)["version"]!.GetValue<int>());
        using var destination = new SqliteNotebookRepository(destinationDirectory.DatabasePath);
        var restored = Assert.Single(await new BackupService(destination).ImportAsync(path));
        Assert.NotEqual(original.Id, restored.Id);
        Assert.NotEqual(original.Pages[0].Id, restored.Pages[0].Id);
        Assert.Equal(texts.Count, restored.Pages[0].Texts.Count);
        for (var index = 0; index < texts.Count; index++)
        {
            var actual = restored.Pages[0].Texts[index];
            Assert.NotEqual(texts[index].Id, actual.Id);
            Assert.Equal(texts[index], actual with { Id = texts[index].Id });
        }
        await destination.SaveAsync(restored);
        Assert.Equal(restored.Pages[0].Texts, (await destination.LoadAsync(restored.Id))!.Pages[0].Texts);
        Assert.Equal(texts, (await source.LoadAsync(original.Id))!.Pages[0].Texts);
    }

    [Fact]
    public async Task FormattingOnlyEditIsCommittedWithoutChangingTextContent()
    {
        using var directory = new StorageTestDirectory();
        using var repository = new SqliteNotebookRepository(directory.DatabasePath);
        var text = new NoteText { Text = "Keep this content 原文保留" };
        var document = new NotebookDocument { Pages = [new NotePage { Texts = [text] }] };
        await repository.SaveAsync(document);
        text.Bold = true;
        text.Italic = true;
        text.Alignment = NoteTextAlignment.Right;
        await repository.SaveAsync(document);
        var restored = Assert.Single((await repository.LoadAsync(document.Id))!.Pages[0].Texts);
        Assert.Equal(text, restored);
    }

    [Fact]
    public async Task ArchiveWithoutStyleFieldsRestoresOriginalDefaults()
    {
        using var directory = new StorageTestDirectory();
        using var repository = new SqliteNotebookRepository(directory.DatabasePath);
        var original = new NotebookDocument { Pages = [new NotePage { Texts = [new NoteText { Text = "舊版文字" }] }] };
        var path = Path.Combine(directory.Root, "legacy-text.moye");
        var backup = new BackupService(repository);
        await backup.ExportAsync(path, [original]);
        RewriteTextJson(path, text =>
        {
            text.Remove("bold");
            text.Remove("italic");
            text.Remove("alignment");
        });
        var text = Assert.Single(Assert.Single(await backup.ImportAsync(path)).Pages[0].Texts);
        Assert.Equal("舊版文字", text.Text);
        Assert.False(text.Bold);
        Assert.False(text.Italic);
        Assert.Equal(NoteTextAlignment.Left, text.Alignment);
    }

    [Theory]
    [InlineData("alignment", "-1")]
    [InlineData("alignment", "3")]
    [InlineData("fontSize", "0")]
    [InlineData("fontSize", "1001")]
    [InlineData("width", "0")]
    public async Task InvalidArchiveStyleAndExistingTextBoundsAreRejectedBeforeAssetsAreWritten(string field, string value)
    {
        using var sourceDirectory = new StorageTestDirectory();
        using var destinationDirectory = new StorageTestDirectory();
        using var source = new SqliteNotebookRepository(sourceDirectory.DatabasePath);
        using var destination = new SqliteNotebookRepository(destinationDirectory.DatabasePath);
        var asset = await source.PutAssetAsync("sample.png", "image/png", [1, 2, 3]);
        var document = new NotebookDocument
        {
            Pages = [new NotePage { Texts = [new NoteText { Text = "格式驗證" }], Images = [new NoteImage { AssetId = asset.Id }] }]
        };
        var path = Path.Combine(sourceDirectory.Root, "invalid-style.moye");
        await new BackupService(source).ExportAsync(path, [document]);
        RewriteTextJson(path, text => text[field] = JsonNode.Parse(value));
        await Assert.ThrowsAsync<InvalidDataException>(() => new BackupService(destination).ImportAsync(path));
        Assert.Empty(await destination.ListAsync());
        await Assert.ThrowsAsync<FileNotFoundException>(() => destination.GetAssetAsync(asset.Id));
    }

    [Theory]
    [InlineData(NoteTextAlignment.Left, false, false)]
    [InlineData(NoteTextAlignment.Center, true, false)]
    [InlineData(NoteTextAlignment.Right, false, true)]
    [InlineData(NoteTextAlignment.Left, true, true)]
    public async Task PdfVectorTypographyMatchesEditorWrappingAlignmentAndStyle(NoteTextAlignment alignment, bool bold, bool italic)
    {
        using var directory = new StorageTestDirectory();
        using var repository = new SqliteNotebookRepository(directory.DatabasePath);
        var text = new NoteText
        {
            X = 48, Y = 36, Width = 220, Height = 208, FontSize = 24, Color = "#FF111111",
            Text = "Styled words\n繁體中文換行測試是一段筆記 Example text.",
            Bold = bold, Italic = italic, Alignment = alignment
        };
        var page = new NotePage { Width = 340, Height = 280, Texts = [text] };
        var expected = await Sta(() => RenderEditor(page));
        var path = Path.Combine(directory.Root, "styled-text.pdf");
        var service = new PdfService(repository);
        await service.ExportAsync(path, new NotebookDocument { Pages = [page] });
        using (var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Import))
        {
            var content = Encoding.Latin1.GetString(pdf.Pages[0].Contents.CreateSingleContent().Stream.UnfilteredValue);
            Assert.Contains(" c", content);
            Assert.DoesNotContain("Tj", content);
            Assert.DoesNotContain(" Do", content);
        }
        var imported = Assert.Single(await service.ImportAsync(path));
        var actual = await service.RenderAsync(imported, 1);
        Assert.Equal(expected.PixelWidth, actual.PixelWidth);
        Assert.Equal(expected.PixelHeight, actual.PixelHeight);
        var before = DarkPixels(expected);
        var after = DarkPixels(actual);
        Assert.NotEmpty(before);
        Assert.NotEmpty(after);
        var lineHeight = text.FontSize * 1.4;
        var visibleLines = 0;
        for (var line = 0; line < 6; line++)
        {
            var start = text.Y + line * lineHeight;
            var first = before.Where(point => point.Y >= start && point.Y < start + lineHeight).ToArray();
            var second = after.Where(point => point.Y >= start && point.Y < start + lineHeight).ToArray();
            Assert.Equal(first.Length == 0, second.Length == 0);
            if (first.Length == 0) continue;
            visibleLines++;
            output.WriteLine($"Line {line}: editor X={first.Min(point => point.X)}..{first.Max(point => point.X)}, Y={first.Min(point => point.Y)}..{first.Max(point => point.Y)}, pixels={first.Length}; PDF X={second.Min(point => point.X)}..{second.Max(point => point.X)}, Y={second.Min(point => point.Y)}..{second.Max(point => point.Y)}, pixels={second.Length}.");
            Assert.InRange(Math.Abs(first.Min(point => point.X) - second.Min(point => point.X)), 0, 2);
            Assert.InRange(Math.Abs(first.Max(point => point.X) - second.Max(point => point.X)), 0, 2);
            Assert.InRange(Math.Abs(first.Min(point => point.Y) - second.Min(point => point.Y)), 0, 2);
            Assert.InRange(Math.Abs(first.Max(point => point.Y) - second.Max(point => point.Y)), 0, 2);
            Assert.InRange((double)second.Length / first.Length, .8, 1.2);
        }
        Assert.True(visibleLines >= 3, "The fixture must exercise explicit newlines and automatic Unicode wrapping.");
    }

    private static BitmapSource RenderEditor(NotePage page)
    {
        var editor = new PageEditor(page, _ => throw new InvalidOperationException("No assets in this text fixture."));
        editor.Measure(new Size(page.Width, page.Height));
        editor.Arrange(new Rect(0, 0, page.Width, page.Height));
        editor.UpdateLayout();
        // The editor's document view hides selection/scrollbar chrome and resets temporary scroll offsets.
        using var stream = new MemoryStream(editor.CreateThumbnail((int)page.Width), writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    [Fact]
    public async Task PdfClipsItalicOverhangAndPartlyVisibleLastLineToTheTextBox()
    {
        using var directory = new StorageTestDirectory();
        using var repository = new SqliteNotebookRepository(directory.DatabasePath);
        var text = new NoteText
        {
            X = 25, Y = 25, Width = 110, Height = 61, FontFamily = "Arial", FontSize = 32,
            Text = "fffff\nClipped line", Bold = true, Italic = true, Alignment = NoteTextAlignment.Right, Color = "#FF111111"
        };
        var page = new NotePage { Width = 180, Height = 150, Texts = [text] };
        var expected = DarkPixels(await Sta(() => RenderEditor(page)));
        var service = new PdfService(repository);
        var path = Path.Combine(directory.Root, "clipped-text.pdf");
        await service.ExportAsync(path, new NotebookDocument { Pages = [page] });
        var imported = Assert.Single(await service.ImportAsync(path));
        var actual = DarkPixels(await service.RenderAsync(imported, 1));
        Assert.NotEmpty(actual);
        Assert.All(actual, point =>
        {
            Assert.InRange(point.X, (int)text.X, (int)(text.X + text.Width) - 1);
            Assert.InRange(point.Y, (int)text.Y, (int)(text.Y + text.Height) - 1);
        });
        var secondLineStart = text.Y + text.FontSize * 1.4;
        Assert.Contains(expected, point => point.Y >= secondLineStart);
        Assert.Contains(actual, point => point.Y >= secondLineStart);
        Assert.InRange(Math.Abs(expected.Max(point => point.Y) - actual.Max(point => point.Y)), 0, 2);
        Assert.InRange((double)actual.Count / expected.Count, .8, 1.2);
    }

    private static List<(int X, int Y)> DarkPixels(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        converted.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        var result = new List<(int X, int Y)>();
        for (var y = 0; y < bitmap.PixelHeight; y++)
            for (var x = 0; x < bitmap.PixelWidth; x++)
            {
                var offset = (y * bitmap.PixelWidth + x) * 4;
                if (bytes[offset] < 150 && bytes[offset + 1] < 150 && bytes[offset + 2] < 150)
                    result.Add((x, y));
            }
        return result;
    }

    private static void RewriteTextJson(string path, Action<JsonObject> edit)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var document = ReadJson(archive.GetEntry("notebooks/000000.json")!);
        edit(document["pages"]![0]!["texts"]![0]!.AsObject());
        var bytes = Encoding.UTF8.GetBytes(document.ToJsonString());
        Replace(archive, "notebooks/000000.json", bytes);
        var manifest = ReadJson(archive.GetEntry("manifest.json")!);
        manifest["notebooks"]![0]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Replace(archive, "manifest.json", Encoding.UTF8.GetBytes(manifest.ToJsonString()));
    }

    private static JsonNode ReadJson(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return JsonNode.Parse(stream)!;
    }

    private static void Replace(ZipArchive archive, string path, byte[] bytes)
    {
        archive.GetEntry(path)!.Delete();
        using var stream = archive.CreateEntry(path).Open();
        stream.Write(bytes);
    }

    private static Task<T> Sta<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception exception) { completion.SetException(exception); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
