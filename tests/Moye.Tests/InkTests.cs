using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using Moye.Controls;
using Moye.Models;
using Moye.ViewModels;

namespace Moye.Tests;

public sealed class InkTests
{
    [Fact]
    public void CommittedStrokePreservesPressureAndExistingSnapshot()
    {
        Sta(() =>
        {
            var page = new NotePage();
            var editor = CreateEditor(page);
            var changes = 0;
            editor.ContentChanged += (_, _) => changes++;
            editor.InkCanvas.Strokes.Add(PressureStroke());
            editor.CommitPendingEdits();
            var snapshot = page.Snapshot();
            var firstBytes = snapshot.InkData;
            var restored = new StrokeCollection(new MemoryStream(firstBytes));
            Assert.Single(restored);
            Assert.InRange(restored[0].StylusPoints[0].PressureFactor, .19f, .21f);
            Assert.InRange(restored[0].StylusPoints[2].PressureFactor, .89f, .91f);
            editor.InkCanvas.Strokes.Add(PressureStroke());
            editor.CommitPendingEdits();
            Assert.Equal(2, changes);
            Assert.NotSame(firstBytes, page.InkData);
            Assert.Single(new StrokeCollection(new MemoryStream(snapshot.InkData)));
            Assert.Equal(2, new StrokeCollection(new MemoryStream(page.InkData)).Count);
            editor.CommitPendingEdits();
            Assert.Equal(2, changes);
        });
    }

    [Fact]
    public void LassoCopyRecolorDeletePersistsAndReloads()
    {
        Sta(() =>
        {
            var page = new NotePage();
            var editor = CreateEditor(page);
            editor.InkCanvas.Strokes.Add(PressureStroke());
            editor.CommitPendingEdits();
            editor.SelectAllInk();
            editor.DuplicateSelection();
            Assert.Equal(2, editor.InkCanvas.Strokes.Count);
            editor.ApplyColorToSelection(Colors.Red);
            editor.CommitPendingEdits();
            var saved = new StrokeCollection(new MemoryStream(page.InkData));
            Assert.Equal(Colors.Red, saved[1].DrawingAttributes.Color);
            // ISF encodes coordinates in HIMETRIC, so a sub-pixel quantization is expected.
            Assert.InRange(saved[1].StylusPoints[0].X - saved[0].StylusPoints[0].X, 19.95, 20.05);
            editor.DeleteSelection();
            editor.Reload(page.Snapshot());
            Assert.Single(editor.InkCanvas.Strokes);
            Assert.NotEqual(Colors.Red, editor.InkCanvas.Strokes[0].DrawingAttributes.Color);
        });
    }

    [Fact]
    public void PartialEraserSplitsInkAndPreservesBothSurvivingParts()
    {
        Sta(() =>
        {
            var page = new NotePage();
            var editor = CreateEditor(page);
            editor.InkCanvas.Strokes.Add(new Stroke(new StylusPointCollection(Enumerable.Range(0, 21).Select(i => new StylusPoint(20 + i * 10, 100, .5f)))));
            editor.CommitPendingEdits();
            editor.InkCanvas.Strokes.Erase(new[] { new Point(120, 90), new Point(120, 110) }, new EllipseStylusShape(16, 16));
            editor.CommitPendingEdits();
            var restored = new StrokeCollection(new MemoryStream(page.InkData));
            Assert.Equal(2, restored.Count);
            Assert.True(restored[0].GetBounds().Right < restored[1].GetBounds().Left);
            Assert.True(restored[0].GetBounds().Left < 25);
            Assert.True(restored[1].GetBounds().Right > 215);
        });
    }

    [Theory]
    [InlineData(InkTool.PointEraser, InkCanvasEditingMode.EraseByPoint)]
    [InlineData(InkTool.StrokeEraser, InkCanvasEditingMode.EraseByStroke)]
    public void EraserSelectionUsesNativeModeAndRetainsTailEraserWhenWriting(InkTool tool, InkCanvasEditingMode expected)
    {
        Sta(() =>
        {
            var editor = CreateEditor(new NotePage());
            editor.SetTool(tool, Colors.Black, 4);
            Assert.Equal(expected, editor.InkCanvas.EditingMode);
            Assert.Equal(expected, editor.InkCanvas.EditingModeInverted);
            editor.SetTool(InkTool.Pen, Colors.Blue, 2.5);
            Assert.Equal(InkCanvasEditingMode.Ink, editor.InkCanvas.EditingMode);
            Assert.Equal(expected, editor.InkCanvas.EditingModeInverted);
            editor.InkCanvas.Strokes.Add(PressureStroke());
            editor.SetTool(InkTool.Lasso, Colors.Black, 4);
            editor.SelectAllInk();
            Assert.Single(editor.InkCanvas.GetSelectedStrokes());
            editor.SetTool(tool, Colors.Black, 4);
            Assert.Empty(editor.InkCanvas.GetSelectedStrokes());
            Assert.Equal(expected, editor.InkCanvas.EditingMode);
        });
    }

    [Fact]
    public void WholeStrokeHitKeepsNeighborAndCommitsOneUndoableIsfEdit()
    {
        Sta(() =>
        {
            var page = new NotePage();
            var document = new NotebookDocument { Pages = [page] };
            var editor = CreateEditor(page);
            var target = PressureStroke();
            var neighbor = PressureStroke();
            neighbor.Transform(new Matrix(1, 0, 0, 1, 0, 160), false);
            neighbor.DrawingAttributes.Color = Colors.Blue;
            editor.InkCanvas.Strokes.Add(new StrokeCollection { target, neighbor });
            editor.CommitPendingEdits();
            editor.SetTool(InkTool.StrokeEraser, Colors.Black, 4);

            var history = new NotebookHistory();
            history.Reset(document);
            var changes = 0;
            editor.ContentChanged += (_, _) => { changes++; history.Record(document); };

            // Exercise WPF's incremental stroke hit testing and whole-stroke
            // removal without creating a window or injecting pointer events.
            var hitTester = editor.InkCanvas.Strokes.GetIncrementalStrokeHitTester(editor.InkCanvas.EraserShape);
            hitTester.StrokeHit += (_, hit) => editor.InkCanvas.Strokes.Remove(hit.HitStroke);
            hitTester.AddPoints(new[] { new Point(70, 40), new Point(70, 80) });
            hitTester.EndHitTesting();
            Assert.Same(neighbor, Assert.Single(editor.InkCanvas.Strokes));
            editor.CommitPendingEdits();
            editor.CommitPendingEdits();
            Assert.Equal(1, changes);

            var saved = Assert.Single(new StrokeCollection(new MemoryStream(page.InkData)));
            Assert.Equal(Colors.Blue, saved.DrawingAttributes.Color);
            Assert.Equal(3, saved.StylusPoints.Count);
            Assert.InRange(saved.StylusPoints[0].Y, 199.95, 200.05);
            Assert.InRange(saved.StylusPoints[0].PressureFactor, .19f, .21f);
            Assert.InRange(saved.StylusPoints[2].PressureFactor, .89f, .91f);

            var undone = Assert.IsType<NotebookDocument>(history.Undo());
            Assert.Equal(2, new StrokeCollection(new MemoryStream(undone.Pages[0].InkData)).Count);
            Assert.False(history.CanUndo);
            var redone = Assert.IsType<NotebookDocument>(history.Redo());
            Assert.Single(new StrokeCollection(new MemoryStream(redone.Pages[0].InkData)));
        });
    }

    [Fact]
    public void ChineseTextDuplicateAndThumbnailWorkWithoutAWindow()
    {
        Sta(() =>
        {
            var page = new NotePage { Template = PaperTemplate.Grid };
            var editor = CreateEditor(page);
            editor.AddTextAt(new Point(60, 90), "繁體中文筆記\n第二行 English 123");
            editor.DuplicateSelection();
            Assert.Equal(2, page.Texts.Count);
            Assert.NotEqual(page.Texts[0].Id, page.Texts[1].Id);
            Assert.Equal(page.Texts[0].Text, page.Texts[1].Text);
            editor.DeleteSelection();
            var thumbnail = editor.CreateThumbnail();
            Assert.True(thumbnail.Length > 100);
            Assert.Equal(new byte[] { 137, 80, 78, 71 }, thumbnail.Take(4).ToArray());
            Assert.Single(page.Texts);
        });
    }

    private static PageEditor CreateEditor(NotePage page) => new(page, _ => Task.FromException<AssetData>(new InvalidOperationException("No images in fixture.")));

    private static Stroke PressureStroke() => new(new StylusPointCollection
    {
        new StylusPoint(30, 40, .2f), new StylusPoint(70, 60, .5f), new StylusPoint(100, 80, .9f)
    });

    private static void Sta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Ink test exceeded 30 seconds.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
