using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Moye.Models;

namespace Moye.Controls;

/// <summary>
/// A single page in fixed DIP coordinates. The shell owns zoom, virtualization,
/// history and persistence; completed edits update Page before ContentChanged fires.
/// </summary>
public sealed class PageEditor : Grid
{
    private readonly Func<string, Task<AssetData>> _loadAsset;
    private readonly PaperVisual _paper = new();
    private readonly Canvas _items = new() { Background = Brushes.Transparent, ClipToBounds = true };
    private readonly PenInkCanvas _ink = new();
    private readonly StrokePreviewVisual _linePreview = new();
    private readonly DispatcherTimer _editTimer;
    private readonly List<NoteItemFrame> _frames = [];
    private NoteItemFrame? _selectedItem;
    private bool _loading;
    private bool _inkDirty;
    private bool _contentDirty;
    private bool _committing;
    private bool _objectPenDown;
    private int _generation;
    private byte[]? _lastThumbnail;
    private InkTool _tool = InkTool.Pen;
    private Color _color = Color.FromRgb(37, 51, 74);
    private double _width = 3;
    private bool _pressureSensitivity = true, _smoothing = true, _exactWidth, _eraseHighlightOnly;
    private double _opacity = 1, _eraserSize = 20;

    public NotePage Page { get; private set; }
    public PenInkCanvas InkCanvas => _ink;
    public bool IsPenDown => _ink.IsPenDown || _objectPenDown;
    public bool IsInputActive => IsPenDown || _ink.IsMouseCaptureWithin || _ink.IsStylusCaptureWithin || _frames.Any(f => f.IsMouseCaptureWithin || f.IsStylusCaptureWithin);
    public event EventHandler? ContentChanged;
    public event EventHandler? VisualContentChanged;
    public event EventHandler? PenContactChanged;
    public event EventHandler<string>? AssetLoadFailed;

    public PageEditor(NotePage page, Func<string, Task<AssetData>> loadAsset)
    {
        Page = page;
        _loadAsset = loadAsset;
        ClipToBounds = true;
        Background = Brushes.White;
        SnapsToDevicePixels = true;
        Children.Add(_paper);
        Children.Add(_items);
        Children.Add(_ink);
        Children.Add(_linePreview);
        _ink.StraightLinePreviewChanged += (_, _) => _linePreview.SetStroke(_ink.StraightLinePreview);
        _editTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(350) };
        _editTimer.Tick += (_, _) =>
        {
            if (IsInputActive) return;
            CommitPendingEdits();
        };
        _ink.StrokeCollected += (_, _) => { MarkInkDirty(); FlushChanges(); };
        _ink.StrokeErased += (_, _) => MarkInkDirty();
        _ink.StrokeErasing += (_, e) => { if (_eraseHighlightOnly && !e.Stroke.DrawingAttributes.IsHighlighter) e.Cancel = true; };
        _ink.SelectionMoved += (_, _) => { MarkInkDirty(); FlushChanges(); };
        _ink.SelectionResized += (_, _) => { MarkInkDirty(); FlushChanges(); };
        _ink.InputCompleted += (_, _) => FlushChanges();
        _ink.PenContactChanged += (_, _) => PenContactChanged?.Invoke(this, EventArgs.Empty);
        _ink.SelectionChanged += (_, _) => { if (_ink.GetSelectedStrokes().Count > 0) SelectItem(null); };
        _items.MouseLeftButtonDown += (_, e) =>
        {
            if (PenInkCanvas.IsTouch(e.StylusDevice)) return;
            if (e.OriginalSource == _items)
            {
                SelectItem(null);
                if (_tool == InkTool.Text)
                {
                    // Complete placement here; a ListBox ancestor must not move
                    // keyboard focus back to its current item after this click.
                    e.Handled = true;
                    AddTextAt(e.GetPosition(_items));
                }
            }
        };
        PreviewKeyDown += HandleKeyDown;
        PreviewStylusDown += (_, e) =>
        {
            if (_tool is not (InkTool.Text or InkTool.Select) || PenInkCanvas.IsTouch(e.StylusDevice)) return;
            SetObjectPenContact(true);
        };
        AddHandler(Stylus.StylusUpEvent, new StylusEventHandler((_, e) =>
        {
            if (!PenInkCanvas.IsTouch(e.StylusDevice)) SetObjectPenContact(false);
        }), true);
        LostStylusCapture += (_, _) => SetObjectPenContact(false);
        Unloaded += (_, _) => CommitPendingEdits();
        Reload(page);
    }

    public void Reload(NotePage page)
    {
        CommitPendingEdits();
        _loading = true;
        try
        {
            _generation++;
            _lastThumbnail = null;
            Page = page;
            Width = Math.Max(1, page.Width);
            Height = Math.Max(1, page.Height);
            _paper.Template = page.Template;
            _paper.PdfBackground = null;
            UnsubscribeStrokes(_ink.Strokes);
            _ink.Strokes = page.InkData.Length == 0 ? new StrokeCollection() : new StrokeCollection(new MemoryStream(page.InkData, false));
            SubscribeStrokes(_ink.Strokes);
            _selectedItem = null;
            _items.Children.Clear();
            _frames.Clear();
            foreach (var image in page.Images) AddImageFrame(image);
            foreach (var text in page.Texts) AddTextFrame(text);
            _inkDirty = _contentDirty = false;
            ApplyTool();
        }
        finally { _loading = false; }
    }

    public void SetTool(InkTool tool, Color color, double width)
    {
        CommitPendingEdits();
        _tool = tool;
        _color = color;
        _width = Math.Clamp(width, .5, 80);
        ApplyTool();
    }

    private void ApplyTool()
    {
        _ink.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = Color.FromArgb((byte)Math.Round(_color.A * (_tool == InkTool.Highlighter ? 1 : _opacity)), _color.R, _color.G, _color.B),
            Width = _tool == InkTool.Highlighter && !_exactWidth ? Math.Max(12, _width * 4) : _width,
            Height = _tool == InkTool.Highlighter && !_exactWidth ? Math.Max(12, _width * 4) : _width,
            IsHighlighter = _tool == InkTool.Highlighter,
            IgnorePressure = !_pressureSensitivity || (_tool == InkTool.Highlighter && !_exactWidth),
            FitToCurve = _smoothing,
            StylusTip = _tool == InkTool.Highlighter ? StylusTip.Rectangle : StylusTip.Ellipse
        };
        _ink.EraserShape = new EllipseStylusShape(_eraserSize, _eraserSize);
        // InkCanvas.Select changes EditingMode even when clearing a selection.
        // Clear first, then apply the requested tool so erasers stay erasers.
        if (_tool != InkTool.Lasso && _ink.GetSelectedStrokes().Count > 0) _ink.Select(new StrokeCollection());
        _ink.SetRequestedMode(_tool switch
        {
            InkTool.Pen or InkTool.Highlighter => InkCanvasEditingMode.Ink,
            InkTool.StrokeEraser => InkCanvasEditingMode.EraseByStroke,
            InkTool.PointEraser => InkCanvasEditingMode.EraseByPoint,
            InkTool.Lasso => InkCanvasEditingMode.Select,
            _ => InkCanvasEditingMode.None
        });
        // A pen's inverted/tail eraser follows the same remembered eraser mode.
        if (_tool is InkTool.StrokeEraser or InkTool.PointEraser)
            _ink.EditingModeInverted = _tool == InkTool.StrokeEraser ? InkCanvasEditingMode.EraseByStroke : InkCanvasEditingMode.EraseByPoint;
        var editObjects = _tool is InkTool.Text or InkTool.Select;
        _ink.IsHitTestVisible = !editObjects;
        _items.IsHitTestVisible = editObjects;
        foreach (var frame in _frames)
        {
            if (frame.ItemContent is TextBox text) text.IsReadOnly = !editObjects;
        }
        if (!editObjects) SelectItem(null);
        Cursor = _tool == InkTool.Hand ? Cursors.Hand : Cursors.Arrow;
    }

    public void SetPdfBackground(BitmapSource source)
    {
        _paper.PdfBackground = source;
        VisualContentChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task SetPdfBackgroundAsync(BitmapSource source)
    {
        SetPdfBackground(source);
        return Task.CompletedTask;
    }

    public void RefreshPaper() { _paper.Template = Page.Template; _paper.InvalidateVisual(); }

    private sealed class StrokePreviewVisual : FrameworkElement
    {
        private Stroke? _stroke;
        public StrokePreviewVisual() { IsHitTestVisible = false; ClipToBounds = true; }
        public void SetStroke(Stroke? stroke) { _stroke = stroke; InvalidateVisual(); }
        protected override void OnRender(DrawingContext context) { base.OnRender(context); _stroke?.Draw(context); }
    }

    public void AddImage(NoteImage item)
    {
        Page.Images.Add(item);
        var frame = AddImageFrame(item);
        _tool = InkTool.Select;
        ApplyTool();
        SelectItem(frame);
        MarkContentDirty();
        FlushChanges();
    }

    public NoteText AddTextAt(Point location, string text = "")
    {
        var item = new NoteText
        {
            X = Math.Clamp(location.X, 0, Math.Max(0, Page.Width - 100)),
            Y = Math.Clamp(location.Y, 0, Math.Max(0, Page.Height - 60)),
            Text = text, Color = _color.ToString()
        };
        item.Width = Math.Min(item.Width, Page.Width - item.X);
        item.Height = Math.Min(item.Height, Page.Height - item.Y);
        Page.Texts.Add(item);
        var frame = AddTextFrame(item);
        _tool = InkTool.Text;
        ApplyTool();
        SelectItem(frame);
        var box = (TextBox)frame.ItemContent;
        void FocusInsertedText()
        {
            if (_selectedItem != frame || _tool is not (InkTool.Text or InkTool.Select) || !box.IsLoaded) return;
            box.Focus();
            Keyboard.Focus(box);
            box.CaretIndex = box.Text.Length;
        }
        // Inserting the frame invalidates layout. WPF cannot focus a TextBox's
        // TextBoxView until its template/layout has been realized, and the current
        // routed mouse event can otherwise overwrite an immediate focus request.
        if (box.IsLoaded) Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FocusInsertedText));
        else
        {
            RoutedEventHandler? loaded = null;
            loaded = (_, _) =>
            {
                box.Loaded -= loaded;
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FocusInsertedText));
            };
            box.Loaded += loaded;
        }
        MarkContentDirty();
        FlushChanges();
        return item;
    }

    public void AddTextAt(double x, double y) => AddTextAt(new Point(x, y));

    public void DeleteSelection()
    {
        if (_selectedItem is not null)
        {
            if (_selectedItem.Item is NoteText text) Page.Texts.Remove(text);
            if (_selectedItem.Item is NoteImage image) Page.Images.Remove(image);
            _items.Children.Remove(_selectedItem);
            _frames.Remove(_selectedItem);
            SelectItem(null);
            MarkContentDirty();
        }
        else
        {
            var selected = _ink.GetSelectedStrokes();
            if (selected.Count == 0) return;
            _ink.Strokes.Remove(selected);
            MarkInkDirty();
        }
        FlushChanges();
    }

    public void DuplicateSelection()
    {
        if (_selectedItem?.Item is NoteText text)
        {
            var copy = text with { Id = Guid.NewGuid().ToString("N"), X = Math.Min(text.X + 20, Math.Max(0, Page.Width - text.Width)), Y = Math.Min(text.Y + 20, Math.Max(0, Page.Height - text.Height)) };
            Page.Texts.Add(copy);
            SelectItem(AddTextFrame(copy));
            MarkContentDirty();
        }
        else if (_selectedItem?.Item is NoteImage image)
        {
            var copy = image with { Id = Guid.NewGuid().ToString("N"), X = Math.Min(image.X + 20, Math.Max(0, Page.Width - image.Width)), Y = Math.Min(image.Y + 20, Math.Max(0, Page.Height - image.Height)) };
            Page.Images.Add(copy);
            SelectItem(AddImageFrame(copy));
            MarkContentDirty();
        }
        else
        {
            var selected = _ink.GetSelectedStrokes();
            if (selected.Count == 0) return;
            var copy = selected.Clone();
            var bounds = copy.GetBounds();
            copy.Transform(new Matrix(1, 0, 0, 1, Math.Min(20, Page.Width - bounds.Right), Math.Min(20, Page.Height - bounds.Bottom)), false);
            _ink.Strokes.Add(copy);
            _ink.Select(copy);
            MarkInkDirty();
        }
        FlushChanges();
    }

    public void SelectAllInk()
    {
        _tool = InkTool.Lasso;
        ApplyTool();
        _ink.Select(_ink.Strokes);
    }

    public void ConfigureWriting(WritingPreset preset, double eraserSize, bool eraseHighlightOnly)
    {
        CommitPendingEdits();
        _pressureSensitivity = preset.PressureSensitivity;
        _smoothing = preset.Smoothing;
        _opacity = Math.Clamp(preset.Opacity, .1, 1);
        _exactWidth = true;
        _eraserSize = Math.Clamp(eraserSize, 12, 120);
        _eraseHighlightOnly = eraseHighlightOnly;
        ApplyTool();
    }

    /// <summary>Editable ISF clipboard payload; the source remains untouched.</summary>
    public byte[]? ExportSelectedInk()
    {
        CommitPendingEdits();
        var selected = _ink.GetSelectedStrokes();
        if (selected.Count == 0) return null;
        using var stream = new MemoryStream();
        selected.Save(stream);
        return stream.ToArray();
    }

    public bool ImportInk(byte[] bytes)
    {
        if (bytes.Length == 0) return false;
        // Parse completely before touching the document or changing history.
        var strokes = new StrokeCollection(new MemoryStream(bytes, false));
        if (strokes.Count == 0) return false;
        var bounds = strokes.GetBounds();
        if (bounds.IsEmpty || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height))
            throw new InvalidDataException("The clipboard ink has invalid bounds.");
        var scale = Math.Min(1, Math.Min(Math.Max(1, Page.Width - 32) / Math.Max(1, bounds.Width), Math.Max(1, Page.Height - 32) / Math.Max(1, bounds.Height)));
        if (scale < 1) { strokes.Transform(new Matrix(scale, 0, 0, scale, 0, 0), true); bounds = strokes.GetBounds(); }
        var x = Math.Min(72, Math.Max(0, Page.Width - bounds.Width - 16));
        var y = Math.Min(72, Math.Max(0, Page.Height - bounds.Height - 16));
        strokes.Transform(new Matrix(1, 0, 0, 1, x - bounds.Left, y - bounds.Top), false);
        CommitPendingEdits();
        _tool = InkTool.Lasso; ApplyTool();
        _ink.Strokes.Add(strokes); _ink.Select(strokes);
        MarkInkDirty(); FlushChanges();
        return true;
    }

    public void ApplyWidthToSelection(double width)
    {
        if (!double.IsFinite(width)) return;
        foreach (var stroke in _ink.GetSelectedStrokes())
        {
            stroke.DrawingAttributes.Width = Math.Clamp(width, .5, 24);
            stroke.DrawingAttributes.Height = Math.Clamp(width, .5, 24);
        }
        FlushChanges();
    }

    public void ApplyColorToSelection(Color color)
    {
        if (_selectedItem?.Item is NoteText text && _selectedItem.ItemContent is TextBox box)
        {
            text.Color = color.ToString();
            box.Foreground = new SolidColorBrush(color);
            MarkContentDirty();
        }
        else
        {
            foreach (var stroke in _ink.GetSelectedStrokes()) stroke.DrawingAttributes.Color = color;
        }
        FlushChanges();
    }

    public void CommitPendingEdits()
    {
        if (_committing || _loading) return;
        _committing = true;
        try { _ink.FinishInput(); SetObjectPenContact(false); FlushChanges(); }
        finally { _committing = false; }
    }

    private void FlushChanges()
    {
        if (_loading || (!_inkDirty && !_contentDirty)) return;
        _editTimer.Stop();
        if (_inkDirty)
        {
            using var stream = new MemoryStream();
            _ink.Strokes.Save(stream);
            Page.InkData = stream.ToArray();
        }
        _inkDirty = _contentDirty = false;
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MarkInkDirty()
    {
        if (_loading) return;
        _inkDirty = true;
        _editTimer.Start();
    }

    private void MarkContentDirty()
    {
        if (_loading) return;
        _contentDirty = true;
        _editTimer.Start();
    }

    private void SubscribeStrokes(StrokeCollection strokes)
    {
        strokes.StrokesChanged += StrokesChanged;
        foreach (var stroke in strokes) SubscribeStroke(stroke);
    }

    private void UnsubscribeStrokes(StrokeCollection strokes)
    {
        strokes.StrokesChanged -= StrokesChanged;
        foreach (var stroke in strokes) UnsubscribeStroke(stroke);
    }

    private void StrokesChanged(object? sender, StrokeCollectionChangedEventArgs e)
    {
        foreach (var stroke in e.Removed) UnsubscribeStroke(stroke);
        foreach (var stroke in e.Added) SubscribeStroke(stroke);
        MarkInkDirty();
    }

    private void SubscribeStroke(Stroke stroke)
    {
        stroke.StylusPointsChanged += StrokeChanged;
        stroke.DrawingAttributesChanged += StrokeAttributesChanged;
    }

    private void UnsubscribeStroke(Stroke stroke)
    {
        stroke.StylusPointsChanged -= StrokeChanged;
        stroke.DrawingAttributesChanged -= StrokeAttributesChanged;
    }

    private void StrokeChanged(object? sender, EventArgs e) => MarkInkDirty();
    private void StrokeAttributesChanged(object? sender, PropertyDataChangedEventArgs e) => MarkInkDirty();

    private NoteItemFrame AddTextFrame(NoteText text)
    {
        var box = new TextBox
        {
            Text = text.Text, FontFamily = new FontFamily(text.FontFamily), FontSize = text.FontSize,
            Foreground = BrushFrom(text.Color), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Padding = new Thickness(0),
            AcceptsReturn = true, AcceptsTab = false, TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Language = XmlLanguage.GetLanguage("zh-HK")
        };
        System.Windows.Documents.Block.SetLineHeight(box, text.FontSize * 1.4);
        System.Windows.Documents.Block.SetLineStackingStrategy(box, LineStackingStrategy.BlockLineHeight);
        InputMethod.SetIsInputMethodEnabled(box, true);
        var frame = AddFrame(text, box, text.X, text.Y, text.Width, text.Height);
        box.TextChanged += (_, _) =>
        {
            if (_loading || text.Text == box.Text) return;
            text.Text = box.Text;
            MarkContentDirty();
        };
        box.LostKeyboardFocus += (_, _) => FlushChanges();
        return frame;
    }

    private NoteItemFrame AddImageFrame(NoteImage image)
    {
        var picture = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
        var frame = AddFrame(image, picture, image.X, image.Y, image.Width, image.Height);
        _ = LoadImageAsync(picture, image.AssetId, _generation);
        return frame;
    }

    private async Task LoadImageAsync(Image target, string assetId, int generation)
    {
        try
        {
            var asset = await _loadAsset(assetId);
            var bitmap = await Task.Run(() =>
            {
                using var stream = new MemoryStream(asset.Bytes, false);
                var source = new BitmapImage();
                source.BeginInit();
                source.CacheOption = BitmapCacheOption.OnLoad;
                source.StreamSource = stream;
                source.EndInit();
                source.Freeze();
                return source;
            });
            if (generation == _generation)
            {
                target.Source = bitmap;
                VisualContentChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            if (generation != _generation) return;
            target.ToolTip = "Unable to load image: " + ex.Message;
            if (target.Parent is NoteItemFrame frame)
            {
                frame.Background = new SolidColorBrush(Color.FromRgb(255, 237, 237));
                frame.Children.Add(new TextBlock { Text = "Unable to load image", Foreground = Brushes.Firebrick, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
            }
            AssetLoadFailed?.Invoke(this, "Unable to load image: " + ex.Message);
            VisualContentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private NoteItemFrame AddFrame(object item, FrameworkElement content, double x, double y, double width, double height)
    {
        var frame = new NoteItemFrame(item, content) { Width = width, Height = height };
        Canvas.SetLeft(frame, x);
        Canvas.SetTop(frame, y);
        _frames.Add(frame);
        _items.Children.Add(frame);
        frame.Selected += (_, _) => SelectItem(frame);
        frame.GeometryChanged += (_, _) =>
        {
            if (item is NoteText text)
            {
                text.X = Canvas.GetLeft(frame); text.Y = Canvas.GetTop(frame);
                text.Width = frame.Width; text.Height = frame.Height;
            }
            else if (item is NoteImage image)
            {
                image.X = Canvas.GetLeft(frame); image.Y = Canvas.GetTop(frame);
                image.Width = frame.Width; image.Height = frame.Height;
            }
            MarkContentDirty();
        };
        frame.EditCompleted += (_, _) => FlushChanges();
        return frame;
    }

    private void SelectItem(NoteItemFrame? frame)
    {
        if (_selectedItem == frame) return;
        _selectedItem?.SetSelected(false);
        _selectedItem = frame;
        _selectedItem?.SetSelected(true);
    }

    private void SetObjectPenContact(bool value)
    {
        if (_objectPenDown == value) return;
        _objectPenDown = value;
        PenContactChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandleKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        if (e.Key == Key.Delete) { DeleteSelection(); e.Handled = true; }
        if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control) { DuplicateSelection(); e.Handled = true; }
        if (e.Key == Key.Escape) { SelectItem(null); _ink.Select(new StrokeCollection()); }
    }

    public byte[] CreateThumbnail(int width = 180)
    {
        // Thumbnail timers run between pen packets. They must never change the
        // native editing mode, selection, or capture while a contact is active.
        if (IsInputActive) return _lastThumbnail ?? RenderThumbnail(width);
        CommitPendingEdits();
        var selected = _selectedItem;
        var strokes = _ink.GetSelectedStrokes();
        SelectItem(null);
        if (strokes.Count > 0) _ink.Select(new StrokeCollection());
        try
        {
            return _lastThumbnail = RenderThumbnail(width);
        }
        finally
        {
            ApplyTool();
            SelectItem(selected);
            if (_tool == InkTool.Lasso) _ink.Select(strokes);
        }
    }

    private byte[] RenderThumbnail(int width)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            Measure(new Size(Width, Height));
            Arrange(new Rect(0, 0, Width, Height));
        }
        UpdateLayout();
        var scale = Math.Clamp(width, 40, 1000) / Width;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(this) { Stretch = Stretch.Fill }, null, new Rect(0, 0, Width * scale, Height * scale));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(Width * scale), (int)Math.Ceiling(Height * scale), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static Brush BrushFrom(string color)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); }
        catch (FormatException) { return new SolidColorBrush(Color.FromRgb(37, 51, 74)); }
    }
}
