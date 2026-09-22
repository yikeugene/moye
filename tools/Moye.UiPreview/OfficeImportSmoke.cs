using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Moye.Models;
using Moye.Services;

namespace Moye.UiPreview;

/// <summary>Explicit opt-in real-converter QA, using only generated fixtures and a fresh library.</summary>
internal static class OfficeImportSmoke
{
    public static async Task<int> RunAsync(string root)
    {
        var source = Path.Combine(root, "artifacts", "office-import-qa", "source");
        var output = Path.Combine(root, "artifacts", "office-import-qa", "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        Console.WriteLine("Synthetic Office import results: " + output);
        using var fixtures = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(source, "expected.json")));
        using var repository = new SqliteNotebookRepository(Path.Combine(output, "moye.db"));
        await repository.InitializeAsync();
        var pdf = new PdfService(repository);
        var importer = new DocumentImportService(pdf, new OfficePdfConverter());
        var reports = new List<object>();
        foreach (var fixture in fixtures.RootElement.GetProperty("files").EnumerateArray())
        {
            var name = fixture.GetProperty("name").GetString()!;
            var path = Path.Combine(source, name);
            var before = await File.ReadAllBytesAsync(path);
            var hash = Convert.ToHexStringLower(SHA256.HashData(before));
            Require(hash == fixture.GetProperty("sha256").GetString(), "Synthetic fixture changed before conversion.");
            var pages = await importer.ImportAsync(path);
            Require(pages.Count == fixture.GetProperty("pages").GetInt32(),
                $"{name}: expected {fixture.GetProperty("pages").GetInt32()} pages/slides, imported {pages.Count}.");
            for (var index = 0; index < pages.Count; index++)
            {
                Require(Math.Abs(pages[index].Width - fixture.GetProperty("pageWidthDip").GetDouble()) < 2, "Page width changed.");
                Require(Math.Abs(pages[index].Height - fixture.GetProperty("pageHeightDip").GetDouble()) < 2, "Page height changed.");
                var bitmap = await pdf.RenderAsync(pages[index], 1);
                await Sta(() => SavePng(bitmap, Path.Combine(output, name + $"-page-{index + 1}.png")));
            }
            var asset = await repository.GetAssetAsync(pages[0].Pdf!.AssetId);
            await File.WriteAllBytesAsync(Path.Combine(output, name + "-converted.pdf"), asset.Bytes);
            var notebook = new NotebookDocument { Title = "Synthetic " + name, Pages = pages.ToList() };
            NotebookStructure.Normalize(notebook);
            notebook.Pages[0].Texts.Add(new NoteText { X = 60, Y = 180, Text = "Moye annotation 筆記", Color = "#FF2563EB" });
            notebook.Pages[0].InkData = await Sta(() =>
            {
                var ink = new StrokeCollection
                {
                    new Stroke(new StylusPointCollection { new StylusPoint(70, 140, .3f), new StylusPoint(180, 140, .9f) },
                        new DrawingAttributes { Color = Colors.Blue, Width = 5, Height = 5, IgnorePressure = false })
                };
                using var buffer = new MemoryStream(); ink.Save(buffer); return buffer.ToArray();
            });
            await repository.SaveAsync(notebook);
            using var reopenedRepository = new SqliteNotebookRepository(Path.Combine(output, "moye.db"));
            await reopenedRepository.InitializeAsync();
            var reopened = await reopenedRepository.LoadAsync(notebook.Id) ?? throw new InvalidDataException("Saved note did not reopen.");
            Require(reopened.Pages.Count == pages.Count && reopened.Pages[0].InkData.SequenceEqual(notebook.Pages[0].InkData), "Editable ink/pages did not survive save/reopen.");
            var backupPath = Path.Combine(output, name + ".moye");
            var backup = new BackupService(repository);
            await backup.ExportAsync(backupPath, [reopened]);
            var restored = (await backup.ImportAsync(backupPath)).Single();
            Require(restored.Id != notebook.Id && restored.Pages.Count == pages.Count &&
                restored.Pages[0].InkData.SequenceEqual(notebook.Pages[0].InkData), "Backup restoration lost editable contents.");
            var exportPath = Path.Combine(output, name + "-annotated.pdf");
            await pdf.ExportAsync(exportPath, restored);
            var roundTrip = await pdf.ImportAsync(exportPath);
            Require(roundTrip.Count == pages.Count, "Annotated PDF lost pages.");
            await Sta(() => SavePng(pdf.RenderAsync(roundTrip[0], 1).GetAwaiter().GetResult(), Path.Combine(output, name + "-annotated.png")));
            var after = await File.ReadAllBytesAsync(path);
            Require(before.SequenceEqual(after), "The source Office document was changed.");
            reports.Add(new { name, pages = pages.Count, sourceSha256 = hash, conversion = "passed", saveReopen = "passed", backupRestore = "passed", annotatedPdfRoundTrip = "passed" });
            Console.WriteLine(name + ": conversion, layout dimensions, original bytes, save/reopen, backup and PDF round trip passed.");
        }
        await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
        await File.WriteAllTextAsync(Path.Combine(root, "artifacts", "office-import-qa", "latest-run.txt"), output);
        return 0;
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private static Task<T> Sta<T>(Func<T> action)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { result.SetResult(action()); } catch (Exception ex) { result.SetException(ex); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return result.Task;
    }
    private static Task Sta(Action action) => Sta(() => { action(); return true; });
}
