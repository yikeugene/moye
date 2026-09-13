using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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
    private readonly HashSet<string> _overflowingTexts = [];
    private readonly HashSet<TextBox> _composingTexts = [];
    private NoteText _textDefaults = new() { FontFamily = "Segoe UI, Microsoft JhengHei", FontSize = 22, Color = "#FF25334A" };
    private string? _lastTextId;
    private bool _suppressTextSelectionNotifications;
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
    public NoteText? SelectedText => _selectedItem?.Item as NoteText;
    public bool HasTextOverflow => SelectedText is { } text && _overflowingTexts.Contains(text.Id);
    public bool IsTextComposing => _composingTexts.Count > 0;
    public bool IsPenDown => _ink.IsPenDown || _objectPenDown;
    public bool IsInputActive => IsPenDown || IsTextComposing || _ink.IsMouseCaptureWithin || _ink.IsStylusCaptureWithin || _frames.Any(f => f.IsMouseCaptureWithin || f.IsStylusCaptureWithin);
    public event EventHandler? ContentChanged;
    public event EventHandler? VisualContentChanged;
    public event EventHandler? PenContactChanged;
    public event EventHandler? TextSelectionChanged;
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
            SelectItem(null);
            _overflowingTexts.Clear();
            _composingTexts.Clear();
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
            if (frame.ItemContent is TextBox text)
            {
                text.IsReadOnly = !editObjects;
                text.VerticalScrollBarVisibility = editObjects ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
                if (!editObjects) text.ScrollToVerticalOffset(0);
            }
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
        CommitPendingEdits();
        var item = _textDefaults with
        {
            Id = Guid.NewGuid().ToString("N"),
            X = Math.Clamp(location.X, 0, Math.Max(0, Page.Width - 100)),
            Y = Math.Clamp(location.Y, 0, Math.Max(0, Page.Height - 60)),
            Text = text
        };
        item.Width = Math.Min(640, Math.Min(Page.Width - item.X, Math.Max(60, Page.Width - item.X - 32)));
        item.Height = Math.Min(item.Height, Page.Height - item.Y);
        Page.Texts.Add(item);
        var frame = AddTextFrame(item);
        _tool = InkTool.Text;
        ApplyTool();
        SelectItem(frame);
        RefreshTextLayout(frame, grow: true);
        RequestTextFocus(frame, atEnd: true);
        MarkContentDirty();
        FlushChanges();
        return item;
    }

    public void AddTextAt(double x, double y) => AddTextAt(new Point(x, y));

    public NoteText BeginTyping()
    {
        CommitPendingEdits();
        if (SelectedText is not null) { FocusSelectedText(); return SelectedText!; }
        var recent = Page.Texts.FirstOrDefault(text => text.Id == _lastTextId) ?? Page.Texts.LastOrDefault();
        if (recent is null) return AddTextAt(new Point(72, 72));
        _tool = InkTool.Text;
        ApplyTool();
        SelectItem(_frames.First(frame => ReferenceEquals(frame.Item, recent)));
        FocusSelectedText();
        return recent;
    }

    public void FocusSelectedText()
    {
        if (_selectedItem?.ItemContent is not TextBox) return;
        _tool = InkTool.Text;
        ApplyTool();
        RequestTextFocus(_selectedItem, atEnd: false);
    }

    /// <summary>Only typography is copied; notebook content and pen settings are independent.</summary>
    public void SetTextDefaults(NoteText template)
    {
        _textDefaults = new NoteText
        {
            FontFamily = string.IsNullOrWhiteSpace(template.FontFamily) ? _textDefaults.FontFamily : template.FontFamily,
            FontSize = double.IsFinite(template.FontSize) ? Math.Clamp(template.FontSize, 6, 128) : _textDefaults.FontSize,
            Bold = template.Bold, Italic = template.Italic,
            Alignment = Enum.IsDefined(template.Alignment) ? template.Alignment : NoteTextAlignment.Left,
            Color = BrushFrom(template.Color) is SolidColorBrush brush ? brush.Color.ToString() : "#FF25334A"
        };
    }

    /// <summary>Formats the whole text box without replacing its native text/undo buffer.</summary>
    public void ApplyTextStyle(string? fontFamily = null, double? fontSize = null, bool? bold = null,
        bool? italic = null, NoteTextAlignment? alignment = null, Color? color = null, bool restoreFocus = true)
    {
        if (_selectedItem?.Item is not NoteText text || _selectedItem.ItemContent is not TextBox box) return;
        var family = string.IsNullOrWhiteSpace(fontFamily) ? text.FontFamily : fontFamily;
        var size = fontSize is { } value && double.IsFinite(value) ? Math.Clamp(value, 6, 128) : text.FontSize;
        var align = alignment is { } candidate && Enum.IsDefined(candidate) ? candidate : text.Alignment;
        var colorText = color?.ToString() ?? text.Color;
        var newBold = bold ?? text.Bold;
        var newItalic = italic ?? text.Italic;
        var selectionStart = box.SelectionStart;
        var selectionLength = box.SelectionLength;
        // Resolve the font before mutating the model, so an invalid family cannot
        // leave an edit partially applied.
        var resolvedFamily = new FontFamily(family);
        if (text.FontFamily != family || text.FontSize != size || text.Bold != newBold || text.Italic != newItalic || text.Alignment != align || text.Color != colorText)
        {
            text.FontFamily = family; text.FontSize = size; text.Bold = newBold;
            text.Italic = newItalic; text.Alignment = align; text.Color = colorText;
            box.FontFamily = resolvedFamily; box.FontSize = size;
            box.FontWeight = text.Bold ? FontWeights.Bold : FontWeights.Normal;
            box.FontStyle = text.Italic ? FontStyles.Italic : FontStyles.Normal;
            box.TextAlignment = ToTextAlignment(align);
            box.Foreground = BrushFrom(colorText);
            System.Windows.Documents.Block.SetLineHeight(box, size * 1.4);
            RefreshTextLayout(_selectedItem, grow: true);
            MarkContentDirty();
            FlushChanges();
            NotifyTextSelectionChanged();
        }
        box.Select(selectionStart, selectionLength);
        if (restoreFocus) RequestTextFocus(_selectedItem, atEnd: false);
    }

    public void ToggleTextList(bool numbered)
    {
        if (_selectedItem?.ItemContent is not TextBox box) return;
        ApplyTextEdit(box, TextEditing.ToggleList(box.Text, box.SelectionStart, box.SelectionLength, numbered));
        RequestTextFocus(_selectedItem, atEnd: false);
    }

    private void ApplyTextEdit(TextBox box, TextEditing.Edit edit)
    {
        // SelectedText inside one change block participates in native Ctrl+Z;
        // assigning Text would discard that history and composition state.
        box.BeginChange();
        try
        {
            box.Select(edit.Start, edit.Length);
            box.SelectedText = edit.Replacement;
            box.Select(edit.SelectionStart, edit.SelectionLength);
        }
        finally { box.EndChange(); }
        FlushChanges();
    }

    private void RequestTextFocus(NoteItemFrame frame, bool atEnd)
    {
        var box = (TextBox)frame.ItemContent;
        var start = atEnd ? box.Text.Length : box.SelectionStart;
        var length = atEnd ? 0 : box.SelectionLength;
        box.Select(start, length);
        void FocusText()
        {
            if (_selectedItem != frame || _tool is not (InkTool.Text or InkTool.Select) || !box.IsLoaded) return;
            box.Focus();
            Keyboard.Focus(box);
            // The selection is retained by TextBox across focus changes. Do not
            // restore a stale saved caret after intervening keyboard input.
        }
        if (box.IsLoaded)
        {
            if (box.IsKeyboardFocusWithin) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FocusText));
        }
        else
        {
            RoutedEventHandler? loaded = null;
            loaded = (_, _) =>
            {
                box.Loaded -= loaded;
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FocusText));
            };
            box.Loaded += loaded;
        }
    }

    public void DeleteSelection()
    {
        if (_selectedItem is not null)
        {
            if (_selectedItem.Item is NoteText text) { Page.Texts.Remove(text); _overflowingTexts.Remove(text.Id); }
            if (_selectedItem.ItemContent is TextBox box) _composingTexts.Remove(box);
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
        if (SelectedText is not null)
        {
            ApplyTextStyle(color: color);
            return;
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
            FontWeight = text.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = text.Italic ? FontStyles.Italic : FontStyles.Normal,
            TextAlignment = ToTextAlignment(text.Alignment),
            Foreground = BrushFrom(text.Color), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Padding = new Thickness(0),
            AcceptsReturn = true, AcceptsTab = false, TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Language = XmlLanguage.GetLanguage("zh-HK")
        };
        System.Windows.Documents.Block.SetLineHeight(box, text.FontSize * 1.4);
        System.Windows.Documents.Block.SetLineStackingStrategy(box, LineStackingStrategy.BlockLineHeight);
        InputMethod.SetIsInputMethodEnabled(box, true);
        box.Resources[typeof(ScrollViewer)] = TextScrollViewerStyle();
        // Windows themes add their own margins around the content host even
        // when TextBox.Padding is zero. Give the native editor a host whose
        // origin and available width exactly match its persisted page rectangle.
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(FocusableProperty, false);
        host.SetValue(MarginProperty, new Thickness(0));
        host.SetValue(Control.PaddingProperty, new Thickness(0));
        host.SetBinding(ScrollViewer.HorizontalScrollBarVisibilityProperty, TemplateBinding("HorizontalScrollBarVisibility"));
        host.SetBinding(ScrollViewer.VerticalScrollBarVisibilityProperty, TemplateBinding("VerticalScrollBarVisibility"));
        box.Template = new ControlTemplate(typeof(TextBox)) { VisualTree = host };
        var frame = AddFrame(text, box, text.X, text.Y, text.Width, text.Height);
        box.TextChanged += (_, _) =>
        {
            if (_loading || text.Text == box.Text) return;
            text.Text = box.Text;
            RefreshTextLayout(frame, grow: true);
            MarkContentDirty();
        };
        box.GotKeyboardFocus += (_, _) => { if (!box.IsReadOnly) SelectItem(frame); };
        box.LostKeyboardFocus += (_, _) => { _composingTexts.Remove(box); FlushChanges(); };
        TextCompositionManager.AddPreviewTextInputStartHandler(box, (_, _) => _composingTexts.Add(box));
        TextCompositionManager.AddPreviewTextInputUpdateHandler(box, (_, _) => _composingTexts.Add(box));
        box.PreviewTextInput += (_, _) => _composingTexts.Remove(box);
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None || box.IsReadOnly || _composingTexts.Contains(box)) return;
            var edit = TextEditing.ContinueList(box.Text, box.SelectionStart, box.SelectionLength);
            if (edit is null) return;
            ApplyTextEdit(box, edit);
            e.Handled = true;
        };
        RefreshTextLayout(frame, grow: false);
        return frame;
    }

    private static TextAlignment ToTextAlignment(NoteTextAlignment alignment) => alignment switch
    {
        NoteTextAlignment.Center => TextAlignment.Center,
        NoteTextAlignment.Right => TextAlignment.Right,
        _ => TextAlignment.Left
    };

    private static Style TextScrollViewerStyle()
    {
        // Overlay the scrollbar instead of subtracting its width from the fixed
        // page coordinates used by the PDF renderer. Keep WPF's named parts so
        // native caret scrolling, keyboard navigation and IME remain intact.
        var grid = new FrameworkElementFactory(typeof(Grid));
        var presenter = new FrameworkElementFactory(typeof(ScrollContentPresenter), "PART_ScrollContentPresenter");
        presenter.SetBinding(ContentPresenter.ContentProperty, TemplateBinding("Content"));
        presenter.SetBinding(ContentPresenter.ContentTemplateProperty, TemplateBinding("ContentTemplate"));
        presenter.SetBinding(ScrollContentPresenter.CanContentScrollProperty, TemplateBinding("CanContentScroll"));
        grid.AppendChild(presenter);
        var scrollbar = new FrameworkElementFactory(typeof(ScrollBar), "PART_VerticalScrollBar");
        scrollbar.SetValue(ScrollBar.OrientationProperty, Orientation.Vertical);
        scrollbar.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        scrollbar.SetValue(FrameworkElement.WidthProperty, SystemParameters.VerticalScrollBarWidth);
        scrollbar.SetBinding(RangeBase.MaximumProperty, TemplateBinding("ScrollableHeight"));
        scrollbar.SetBinding(RangeBase.ValueProperty, TemplateBinding("VerticalOffset"));
        scrollbar.SetBinding(ScrollBar.ViewportSizeProperty, TemplateBinding("ViewportHeight"));
        scrollbar.SetBinding(UIElement.VisibilityProperty, TemplateBinding("ComputedVerticalScrollBarVisibility"));
        grid.AppendChild(scrollbar);
        return new Style(typeof(ScrollViewer))
        {
            Setters = { new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ScrollViewer)) { VisualTree = grid }) }
        };
    }

    private static Binding TemplateBinding(string path) => new(path) { RelativeSource = RelativeSource.TemplatedParent, Mode = BindingMode.OneWay };

    private void RefreshTextLayout(NoteItemFrame frame, bool grow)
    {
        if (frame.Item is not NoteText text || frame.ItemContent is not TextBox box) return;
        // Use the same typeface, wrapping width and 1.4 line height as PDF export.
        // A final blank line still needs room for the insertion caret.
        var content = box.Text.Length == 0 || box.Text.EndsWith('\n') || box.Text.EndsWith('\r') ? box.Text + " " : box.Text;
        var formatted = new FormattedText(content, CultureInfo.GetCultureInfo("zh-HK"), FlowDirection.LeftToRight,
            new Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch), box.FontSize, box.Foreground,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = NoteTextLayout.ContentWidth(text.Width),
            TextAlignment = box.TextAlignment,
            LineHeight = box.FontSize * 1.4
        };
        var requiredHeight = Math.Ceiling(formatted.Height);
        var availableHeight = Math.Max(1, Page.Height - text.Y);
        if (grow)
        {
            var height = Math.Min(availableHeight, Math.Max(text.Height, requiredHeight));
            if (Math.Abs(text.Height - height) > .01)
            {
                text.Height = height;
                frame.Height = height;
                MarkContentDirty();
            }
        }
        var overflow = requiredHeight > text.Height + .5;
        var changed = overflow ? _overflowingTexts.Add(text.Id) : _overflowingTexts.Remove(text.Id);
        if (changed && ReferenceEquals(SelectedText, text)) NotifyTextSelectionChanged();
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
                RefreshTextLayout(frame, grow: false);
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
        var previousText = SelectedText;
        _selectedItem?.SetSelected(false);
        _selectedItem = frame;
        _selectedItem?.SetSelected(true);
        if (SelectedText is { } text) _lastTextId = text.Id;
        if (previousText is not null || SelectedText is not null) NotifyTextSelectionChanged();
    }

    private void NotifyTextSelectionChanged()
    {
        if (!_suppressTextSelectionNotifications) TextSelectionChanged?.Invoke(this, EventArgs.Empty);
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
        var textViews = _frames.Select(frame => frame.ItemContent).OfType<TextBox>()
            .Select(box => (Box: box, Offset: box.VerticalOffset, Visibility: box.VerticalScrollBarVisibility)).ToArray();
        _suppressTextSelectionNotifications = true;
        SelectItem(null);
        if (strokes.Count > 0) _ink.Select(new StrokeCollection());
        try
        {
            foreach (var view in textViews)
            {
                view.Box.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
                view.Box.ScrollToVerticalOffset(0);
            }
            return _lastThumbnail = RenderThumbnail(width);
        }
        finally
        {
            ApplyTool();
            SelectItem(selected);
            if (_tool == InkTool.Lasso) _ink.Select(strokes);
            foreach (var view in textViews)
            {
                view.Box.VerticalScrollBarVisibility = view.Visibility;
                view.Box.ScrollToVerticalOffset(view.Offset);
            }
            _suppressTextSelectionNotifications = false;
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
