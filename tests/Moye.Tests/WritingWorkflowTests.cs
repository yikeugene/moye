using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Moye.Controls;
using Moye.Models;
using Moye.ViewModels;

namespace Moye.Tests;

/// <summary>
/// In-memory WPF workflow coverage. These tests neither create a Window nor use
/// the system clipboard. Eraser filtering invokes the native cancellable hook;
/// it does not simulate physical pen delivery, capture or eraser hit testing.
/// </summary>
public sealed class WritingWorkflowTests
{
    private static readonly Guid StrokeMetadata = new("473e58a6-3497-4a12-8e6f-6fb0487f7a23");
    private static readonly Guid AttributeMetadata = new("94d1fe37-c36d-4ad0-bb67-4c28aa7cb7ae");

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void PenPresetAppliesOpacityPressureSmoothingAndExactWidth(bool pressure, bool smoothing)
    {
        Sta(() =>
        {
            var editor = CreateEditor(new());
            var preset = new WritingPreset
            {
                Tool = InkTool.Pen, Color = "#C8123456", Width = 3.75,
                Opacity = .6, PressureSensitivity = pressure, Smoothing = smoothing
            };
            Configure(editor, preset);

            var attributes = editor.InkCanvas.DefaultDrawingAttributes;
            Assert.Equal(Color.FromArgb(120, 18, 52, 86), attributes.Color);
            Assert.Equal(3.75, attributes.Width);
            Assert.Equal(3.75, attributes.Height);
            Assert.Equal(!pressure, attributes.IgnorePressure);
            Assert.Equal(smoothing, attributes.FitToCurve);
            Assert.False(attributes.IsHighlighter);
            Assert.Equal(StylusTip.Ellipse, attributes.StylusTip);
            Assert.Equal(InkCanvasEditingMode.Ink, editor.InkCanvas.EditingMode);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HighlighterUsesPresetWidthWithoutLegacyMultiplierAndKeepsNativeCompositing(bool pressure)
    {
        Sta(() =>
        {
            var editor = CreateEditor(new());
            var preset = new WritingPreset
            {
                Tool = InkTool.Highlighter, Color = "#FFF4CF58", Width = 7.25,
                Opacity = .5, PressureSensitivity = pressure, Smoothing = false
            };
            Configure(editor, preset);

            var attributes = editor.InkCanvas.DefaultDrawingAttributes;
            Assert.True(attributes.IsHighlighter);
            Assert.Equal(StylusTip.Rectangle, attributes.StylusTip);
            Assert.Equal(7.25, attributes.Width);
            Assert.Equal(7.25, attributes.Height);
            Assert.Equal(!pressure, attributes.IgnorePressure);
            Assert.False(attributes.FitToCurve);
            // WPF highlighter composition already applies its half-opacity.
            Assert.Equal(Color.FromRgb(244, 207, 88), attributes.Color);
        });
    }

    [Theory]
    [InlineData(WritingPreferences.MinimumEraserSize)]
    [InlineData(48)]
    [InlineData(WritingPreferences.MaximumEraserSize)]
    public void EraserSizeMatchesThePersistedPreferenceRange(double size)
    {
        Sta(() =>
        {
            var editor = CreateEditor(new());
            editor.ConfigureWriting(new WritingPreset(), size, false);
            editor.SetTool(InkTool.PointEraser, Colors.Black, 2);

            Assert.Equal(size, editor.InkCanvas.EraserShape.Width);
            Assert.Equal(size, editor.InkCanvas.EraserShape.Height);
            Assert.Equal(InkCanvasEditingMode.EraseByPoint, editor.InkCanvas.EditingMode);
        });
    }

    [Fact]
    public void CrossPageInkCopyPreservesSourcePressureMetadataAndCreatesOneUndoableEdit()
    {
        Sta(() =>
        {
            var sourcePage = new NotePage();
            var targetPage = new NotePage();
            var source = CreateEditor(sourcePage);
            var target = CreateEditor(targetPage);
            var pen = PressureStroke(20, 30, false, "pen");
            var highlight = PressureStroke(80, 50, true, "highlight");
            var excluded = PressureStroke(220, 160, false, "unselected");
            source.InkCanvas.Strokes.Add(new StrokeCollection { pen, highlight, excluded });
            source.CommitPendingEdits();
            var existingTarget = PressureStroke(350, 450, false, "existing target");
            target.InkCanvas.Strokes.Add(existingTarget);
            target.CommitPendingEdits();
            var sourceBefore = sourcePage.InkData.ToArray();
            var targetBefore = targetPage.InkData.ToArray();
            source.SetTool(InkTool.Lasso, Colors.Black, 3);
            source.InkCanvas.Select(new StrokeCollection { pen, highlight });
            var sourceChanges = 0;
            source.ContentChanged += (_, _) => sourceChanges++;
            var document = new NotebookDocument { Pages = [sourcePage, targetPage] };
            var history = new NotebookHistory();
            history.Reset(document);
            var targetChanges = 0;
            target.ContentChanged += (_, _) => { targetChanges++; history.Record(document); };

            var payload = source.ExportSelectedInk();
            Assert.NotNull(payload);
            var payloadBefore = payload.ToArray();
            Assert.True(target.ImportInk(payload));
            target.CommitPendingEdits();

            Assert.Equal(0, sourceChanges);
            Assert.Equal(1, targetChanges);
            Assert.Equal(sourceBefore, sourcePage.InkData);
            Assert.Equal(payloadBefore, payload);
            Assert.Equal(3, source.InkCanvas.Strokes.Count);
            Assert.Same(existingTarget, target.InkCanvas.Strokes[0]);
            Assert.Equal(3, target.InkCanvas.Strokes.Count);
            Assert.Equal(2, target.InkCanvas.GetSelectedStrokes().Count);
            var saved = Decode(targetPage.InkData);
            var copied = Decode(payload);
            AssertProfile(copied[0], saved[1]);
            AssertProfile(copied[1], saved[2]);
            Assert.Equal("existing target", saved[0].GetPropertyData(StrokeMetadata));
            AssertInsidePage(target.InkCanvas.GetSelectedStrokes().GetBounds(), targetPage);
            Assert.InRange(target.InkCanvas.GetSelectedStrokes().GetBounds().Left, 71.95, 72.05);
            Assert.InRange(target.InkCanvas.GetSelectedStrokes().GetBounds().Top, 71.95, 72.05);
            Assert.True(history.CanUndo);
            var undone = history.Undo()!;
            Assert.Equal(sourceBefore, undone.Pages[0].InkData);
            Assert.Equal(targetBefore, undone.Pages[1].InkData);
            Assert.False(history.CanUndo);
            var redone = history.Redo()!;
            Assert.Equal(3, Decode(redone.Pages[1].InkData).Count);
        });
    }

    [Fact]
    public void OversizedClipboardInkScalesIntoPageAndKeepsPressureAndMetadata()
    {
        Sta(() =>
        {
            var page = new NotePage { Width = 300, Height = 240 };
            var target = CreateEditor(page);
            var original = PressureStroke(0, 0, false, "oversized");
            original.StylusPoints = new StylusPointCollection
            {
                new StylusPoint(-300, -200, .2f), new StylusPoint(600, 400, .6f), new StylusPoint(1500, 1100, .9f)
            };
            var payload = Encode(new StrokeCollection { original });
            var sourceCopy = payload.ToArray();
            var originalWidth = original.GetBounds().Width;
            var changes = 0;
            target.ContentChanged += (_, _) => changes++;

            Assert.True(target.ImportInk(payload));

            Assert.Equal(1, changes);
            Assert.Equal(sourceCopy, payload);
            var pasted = Assert.Single(target.InkCanvas.Strokes);
            Assert.True(pasted.GetBounds().Width < originalWidth);
            AssertInsidePage(pasted.GetBounds(), page);
            Assert.Equal(original.StylusPoints.Count, pasted.StylusPoints.Count);
            Assert.Equal("oversized", pasted.GetPropertyData(StrokeMetadata));
            Assert.Equal(73, pasted.DrawingAttributes.GetPropertyData(AttributeMetadata));
            AssertPressure(original, pasted);
            AssertInsidePage(Assert.Single(Decode(page.InkData)).GetBounds(), page, tolerance: .15);
        });
    }

    [Fact]
    public void LargeButFittingInkIsRepositionedWithoutUnnecessaryScaling()
    {
        Sta(() =>
        {
            var page = new NotePage { Width = 300, Height = 240 };
            var target = CreateEditor(page);
            var stroke = new Stroke(new StylusPointCollection
            {
                new StylusPoint(0, 0, .5f), new StylusPoint(240, 180, .5f)
            }, new DrawingAttributes { Width = 2, Height = 2, IgnorePressure = true, FitToCurve = false });
            var payload = Encode(new StrokeCollection { stroke });
            var originalBounds = Assert.Single(Decode(payload)).GetBounds();

            Assert.True(target.ImportInk(payload));

            var bounds = Assert.Single(target.InkCanvas.Strokes).GetBounds();
            Assert.Equal(originalBounds.Width, bounds.Width, 6);
            Assert.Equal(originalBounds.Height, bounds.Height, 6);
            Assert.True(bounds.Left < 72);
            Assert.True(bounds.Top < 72);
            AssertInsidePage(bounds, page);
        });
    }

    [Fact]
    public void EmptyAndMalformedClipboardPayloadsDoNotChangeInkSelectionOrHistory()
    {
        Sta(() =>
        {
            var page = new NotePage();
            var editor = CreateEditor(page);
            var existing = PressureStroke(30, 40, false, "keep");
            editor.InkCanvas.Strokes.Add(existing);
            editor.CommitPendingEdits();
            editor.SelectAllInk();
            var before = page.InkData.ToArray();
            var changes = 0;
            editor.ContentChanged += (_, _) => changes++;

            Assert.False(editor.ImportInk([]));
            Assert.False(editor.ImportInk(Encode(new StrokeCollection())));
            Assert.ThrowsAny<Exception>(() => editor.ImportInk([1, 3, 5, 7, 9]));

            Assert.Equal(0, changes);
            Assert.Equal(before, page.InkData);
            Assert.Same(existing, Assert.Single(editor.InkCanvas.Strokes));
            Assert.Same(existing, Assert.Single(editor.InkCanvas.GetSelectedStrokes()));
            Assert.Equal(InkCanvasEditingMode.Select, editor.InkCanvas.EditingMode);
        });
    }

    [Fact]
    public void ExportWithoutInkSelectionReturnsNullAndLeavesDocumentUntouched()
    {
        Sta(() =>
        {
            var page = new NotePage();
            var editor = CreateEditor(page);
            editor.InkCanvas.Strokes.Add(PressureStroke(20, 30, false, "keep"));
            editor.CommitPendingEdits();
            var before = page.InkData.ToArray();
            var changes = 0;
            editor.ContentChanged += (_, _) => changes++;

            Assert.Null(editor.ExportSelectedInk());

            Assert.Equal(before, page.InkData);
            Assert.Equal(0, changes);
        });
    }

    [Fact]
    public void SelectedWidthChangePreservesOtherInkAndRecordsOneUndoableEdit()
    {
        Sta(() =>
        {
            var page = new NotePage();
            var editor = CreateEditor(page);
            var selected = PressureStroke(20, 30, false, "selected");
            var untouched = PressureStroke(160, 130, true, "untouched");
            editor.InkCanvas.Strokes.Add(new StrokeCollection { selected, untouched });
            editor.CommitPendingEdits();
            editor.InkCanvas.Select(new StrokeCollection { selected });
            var originalBytes = page.InkData.ToArray();
            var originalPressure = selected.StylusPoints.Select(p => p.PressureFactor).ToArray();
            var document = new NotebookDocument { Pages = [page] };
            var history = new NotebookHistory();
            history.Reset(document);
            var changes = 0;
            editor.ContentChanged += (_, _) => { changes++; history.Record(document); };

            editor.ApplyWidthToSelection(5.5);
            editor.CommitPendingEdits();

            Assert.Equal(1, changes);
            Assert.Equal(5.5, selected.DrawingAttributes.Width);
            Assert.Equal(5.5, selected.DrawingAttributes.Height);
            Assert.Equal(9, untouched.DrawingAttributes.Width);
            Assert.Equal(originalPressure, selected.StylusPoints.Select(p => p.PressureFactor));
            Assert.Equal("selected", selected.GetPropertyData(StrokeMetadata));
            Assert.Equal(originalBytes, history.Undo()!.Pages[0].InkData);
            Assert.False(history.CanUndo);
            Assert.InRange(Decode(history.Redo()!.Pages[0].InkData)[0].DrawingAttributes.Width, 5.45, 5.55);
        });
    }

    [Theory]
    [InlineData(InkTool.Pen, InkCanvasEditingMode.Ink)]
    [InlineData(InkTool.Highlighter, InkCanvasEditingMode.Ink)]
    [InlineData(InkTool.PointEraser, InkCanvasEditingMode.EraseByPoint)]
    [InlineData(InkTool.StrokeEraser, InkCanvasEditingMode.EraseByStroke)]
    [InlineData(InkTool.Lasso, InkCanvasEditingMode.Select)]
    public void CreatingThumbnailPreservesRequestedToolLassoSelectionAndDocument(InkTool tool, InkCanvasEditingMode expectedMode)
    {
        Sta(() =>
        {
            var page = new NotePage();
            var editor = CreateEditor(page);
            var stroke = PressureStroke(30, 40, false, "thumbnail content");
            editor.InkCanvas.Strokes.Add(stroke);
            editor.CommitPendingEdits();
            editor.SetTool(tool, Colors.Blue, 3);
            if (tool == InkTool.Lasso) editor.InkCanvas.Select(new StrokeCollection { stroke });
            var before = page.InkData.ToArray();
            var attributes = editor.InkCanvas.DefaultDrawingAttributes.Clone();
            var changes = 0;
            editor.ContentChanged += (_, _) => changes++;

            var thumbnail = editor.CreateThumbnail();

            Assert.True(thumbnail.Length > 100);
            Assert.Equal(new byte[] { 137, 80, 78, 71 }, thumbnail.Take(4).ToArray());
            Assert.Equal(expectedMode, editor.InkCanvas.EditingMode);
            if (tool == InkTool.Lasso) Assert.Same(stroke, Assert.Single(editor.InkCanvas.GetSelectedStrokes()));
            else Assert.Empty(editor.InkCanvas.GetSelectedStrokes());
            Assert.Equal(attributes.Color, editor.InkCanvas.DefaultDrawingAttributes.Color);
            Assert.Equal(attributes.Width, editor.InkCanvas.DefaultDrawingAttributes.Width);
            Assert.Equal(attributes.IsHighlighter, editor.InkCanvas.DefaultDrawingAttributes.IsHighlighter);
            Assert.Equal(before, page.InkData);
            Assert.Equal(0, changes);
        });
    }

    [Theory]
    [InlineData(InkTool.PointEraser, true, false, true)]
    [InlineData(InkTool.PointEraser, true, true, false)]
    [InlineData(InkTool.StrokeEraser, true, false, true)]
    [InlineData(InkTool.StrokeEraser, true, true, false)]
    [InlineData(InkTool.PointEraser, false, false, false)]
    [InlineData(InkTool.StrokeEraser, false, false, false)]
    public void HighlighterOnlyEraserCancelsNativeDeletionOfOrdinaryInk(InkTool tool, bool highlightsOnly, bool highlighter, bool shouldCancel)
    {
        Sta(() =>
        {
            var page = new NotePage();
            var editor = CreateEditor(page);
            editor.ConfigureWriting(new WritingPreset(), 32, highlightsOnly);
            editor.SetTool(tool, Colors.Black, 2);
            var stroke = PressureStroke(20, 30, highlighter, "eraser target");
            editor.InkCanvas.Strokes.Add(stroke);
            editor.CommitPendingEdits();
            var before = page.InkData.ToArray();
            // InkCanvas constructs this event internally; use its real argument
            // type and protected notification hook without inventing device input.
            var constructor = typeof(InkCanvasStrokeErasingEventArgs).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, binder: null, [typeof(Stroke)], modifiers: null);
            Assert.NotNull(constructor);
            var notification = (InkCanvasStrokeErasingEventArgs)constructor.Invoke([stroke]);

            typeof(InkCanvas).GetMethod("OnStrokeErasing", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(editor.InkCanvas, [notification]);

            Assert.Equal(shouldCancel, notification.Cancel);
            Assert.Equal(before, page.InkData);
            Assert.Same(stroke, Assert.Single(editor.InkCanvas.Strokes));
            Assert.Equal(tool == InkTool.PointEraser ? InkCanvasEditingMode.EraseByPoint : InkCanvasEditingMode.EraseByStroke,
                editor.InkCanvas.EditingMode);
        });
    }

    private static void Configure(PageEditor editor, WritingPreset preset)
    {
        editor.SetTool(preset.Tool, (Color)ColorConverter.ConvertFromString(preset.Color), preset.Width);
        editor.ConfigureWriting(preset, 32, false);
    }

    private static PageEditor CreateEditor(NotePage page) => new(page, _ => throw new InvalidOperationException("No image assets in this fixture."));

    private static Stroke PressureStroke(double x, double y, bool highlighter, string name)
    {
        var stroke = new Stroke(new StylusPointCollection
        {
            new StylusPoint(x, y, .2f), new StylusPoint(x + 24, y + 12, .6f), new StylusPoint(x + 48, y + 20, .9f)
        }, new DrawingAttributes
        {
            Color = highlighter ? Colors.Gold : Color.FromArgb(160, 18, 52, 86),
            Width = highlighter ? 9 : 3, Height = highlighter ? 9 : 3,
            IsHighlighter = highlighter, IgnorePressure = false, FitToCurve = false,
            StylusTip = highlighter ? StylusTip.Rectangle : StylusTip.Ellipse
        });
        stroke.AddPropertyData(StrokeMetadata, name);
        stroke.DrawingAttributes.AddPropertyData(AttributeMetadata, 73);
        return stroke;
    }

    private static byte[] Encode(StrokeCollection strokes)
    {
        using var stream = new MemoryStream();
        strokes.Save(stream);
        return stream.ToArray();
    }

    private static StrokeCollection Decode(byte[] bytes) => new(new MemoryStream(bytes, false));

    private static void AssertProfile(Stroke expected, Stroke actual)
    {
        AssertPressure(expected, actual);
        Assert.Equal(expected.DrawingAttributes.Color, actual.DrawingAttributes.Color);
        Assert.Equal(expected.DrawingAttributes.IsHighlighter, actual.DrawingAttributes.IsHighlighter);
        Assert.Equal(expected.DrawingAttributes.IgnorePressure, actual.DrawingAttributes.IgnorePressure);
        Assert.Equal(expected.DrawingAttributes.FitToCurve, actual.DrawingAttributes.FitToCurve);
        Assert.Equal(expected.DrawingAttributes.StylusTip, actual.DrawingAttributes.StylusTip);
        Assert.InRange(actual.DrawingAttributes.Width, expected.DrawingAttributes.Width - .05, expected.DrawingAttributes.Width + .05);
        Assert.InRange(actual.DrawingAttributes.Height, expected.DrawingAttributes.Height - .05, expected.DrawingAttributes.Height + .05);
        Assert.Equal(expected.GetPropertyData(StrokeMetadata), actual.GetPropertyData(StrokeMetadata));
        Assert.Equal(expected.DrawingAttributes.GetPropertyData(AttributeMetadata), actual.DrawingAttributes.GetPropertyData(AttributeMetadata));
    }

    private static void AssertPressure(Stroke expected, Stroke actual)
    {
        Assert.Equal(expected.StylusPoints.Count, actual.StylusPoints.Count);
        for (var i = 0; i < expected.StylusPoints.Count; i++)
            Assert.InRange(actual.StylusPoints[i].PressureFactor, expected.StylusPoints[i].PressureFactor - .002f, expected.StylusPoints[i].PressureFactor + .002f);
    }

    private static void AssertInsidePage(Rect bounds, NotePage page, double tolerance = .0001)
    {
        Assert.True(bounds.Left >= -tolerance && bounds.Top >= -tolerance, $"Ink starts outside page: {bounds}.");
        Assert.True(bounds.Right <= page.Width + tolerance && bounds.Bottom <= page.Height + tolerance, $"Ink ends outside {page.Width} × {page.Height}: {bounds}.");
    }

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Writing workflow test exceeded 30 seconds.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
