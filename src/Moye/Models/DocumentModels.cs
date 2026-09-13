using System.Text.Json;

namespace Moye.Models;

// Numeric values are persisted in existing notebooks and .moye backups.
public enum PaperTemplate { Plain = 0, Ruled = 1, Grid = 2, DotGrid = 3, Cornell = 4, Graph = 5 }
public enum InkTool { Pen, Highlighter, StrokeEraser, PointEraser, Lasso, Text, Select, Hand }
public enum NoteTextAlignment { Left = 0, Center = 1, Right = 2 }

public static class NoteTextLayout
{
    // WPF TextBoxView reserves this margin for its bidi caret even when TextBox.Padding is zero.
    public const double HorizontalInset = 2;
    public static double ContentWidth(double boxWidth) => Math.Max(1, boxWidth - 2 * HorizontalInset);
}

public sealed class NotebookDocument
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Untitled Notebook";
    public string Folder { get; set; } = "My Notes";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<NotePage> Pages { get; set; } = [];
    public NotebookDocument Snapshot() => new() { Id = Id, Title = Title, Folder = Folder, CreatedUtc = CreatedUtc, ModifiedUtc = ModifiedUtc, Pages = Pages.Select(p => p.Snapshot()).ToList() };
}

public sealed class NotebookSummary
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Folder { get; set; } = "";
    public DateTimeOffset ModifiedUtc { get; set; }
    public int PageCount { get; set; }
    public string PageCountText => $"{PageCount} {(PageCount == 1 ? "page" : "pages")}";
}

public sealed class NotePage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public double Width { get; set; } = 793.700787;
    public double Height { get; set; } = 1122.519685;
    public PaperTemplate Template { get; set; }
    // Byte arrays are immutable snapshots. Replace rather than mutate them.
    public byte[] InkData { get; set; } = [];
    public List<NoteText> Texts { get; set; } = [];
    public List<NoteImage> Images { get; set; } = [];
    public PdfPageSource? Pdf { get; set; }
    public NotePage Snapshot() => new() { Id = Id, Width = Width, Height = Height, Template = Template, InkData = InkData, Texts = Texts.Select(t => t with {}).ToList(), Images = Images.Select(i => i with {}).ToList(), Pdf = Pdf is null ? null : Pdf with {} };
}

public sealed record NoteText
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public double X { get; set; } = 72;
    public double Y { get; set; } = 72;
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 130;
    public string Text { get; set; } = "";
    public string FontFamily { get; set; } = "Microsoft JhengHei";
    public double FontSize { get; set; } = 22;
    // Additive format fields: older documents remain regular, upright, and left aligned.
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public NoteTextAlignment Alignment { get; set; } = NoteTextAlignment.Left;
    public string Color { get; set; } = "#FF25334A";
}

public sealed record NoteImage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AssetId { get; set; } = "";
    public double X { get; set; } = 72;
    public double Y { get; set; } = 72;
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 240;
}

public sealed record PdfPageSource
{
    public string AssetId { get; set; } = "";
    public int PageIndex { get; set; }
    public int Rotation { get; set; }
    public double CropX { get; set; }
    public double CropY { get; set; }
    public double CropWidth { get; set; }
    public double CropHeight { get; set; }
}

public sealed record AssetData(string Id, string FileName, string ContentType, byte[] Bytes);

public static class DocumentJson
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };
}
