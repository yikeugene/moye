using Moye.Models;
using System.Windows.Media.Imaging;

namespace Moye.ViewModels;

public sealed class PageViewModel(NotePage page, int number, double zoom) : ObservableObject
{
    public NotePage Page { get; } = page;
    public int Number { get; } = number;
    private double _zoom = zoom;
    private BitmapSource? _thumbnail;
    public double Zoom { get => _zoom; set { if (Set(ref _zoom, value)) { Notify(nameof(DisplayWidth)); Notify(nameof(DisplayHeight)); } } }
    public double DisplayWidth => Page.Width * Zoom;
    public double DisplayHeight => Page.Height * Zoom;
    public string Caption => $"{Number:D2}  ·  {(Page.Pdf is not null ? "PDF" : Page.Template switch { PaperTemplate.Grid => "Grid", PaperTemplate.Ruled => "Ruled", _ => "Blank" })}";
    public BitmapSource? Thumbnail { get => _thumbnail; set => Set(ref _thumbnail, value); }
}
