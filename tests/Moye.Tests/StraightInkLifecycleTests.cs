using System.IO;
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

namespace Moye.Tests;

/// <summary>
/// Exercises the compiled collection/commit hooks with in-memory WPF objects.
/// Reflection supplies packet/session state without pretending to be a device.
/// Native input delivery, renderer transition and pressure hardware need a pen.
/// </summary>
public sealed class StraightInkLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeldLineCommitsOneStraightStrokeBeforeSavingAndKeepsItsStyle(bool highlighter)
    {
        Sta(() =>
        {
            var page = new NotePage();
            var editor = new PageEditor(page, _ => throw new InvalidOperationException());
            editor.SetTool(highlighter ? InkTool.Highlighter : InkTool.Pen, Colors.Blue, 3);
            var canvas = editor.InkCanvas;
            var samples = Samples();
            var session = Session(canvas);
            session.Begin(samples, canvas.DefaultDrawingAttributes, 100);
            Assert.True(session.TryStraighten(750));
            Invoke(canvas, "UpdateStraightPreview");
            Assert.NotNull(canvas.StraightLinePreview);
            Assert.Empty(canvas.Strokes);
            Assert.Empty(page.InkData);

            var changes = 0;
            editor.ContentChanged += (_, _) => changes++;
            var nativeStroke = new Stroke(samples.Clone(), canvas.DefaultDrawingAttributes.Clone());
            // This is the order in WPF InkCanvas.RaiseGestureOrStrokeCollected.
            canvas.Strokes.Add(nativeStroke);
            Invoke(canvas, "OnStrokeCollected", new InkCanvasStrokeCollectedEventArgs(nativeStroke));

            Assert.Same(nativeStroke, Assert.Single(canvas.Strokes));
            Assert.Equal(1, changes);
            Assert.Null(canvas.StraightLinePreview);
            Assert.False(session.IsActive);
            var saved = Assert.Single(new StrokeCollection(new MemoryStream(page.InkData)));
            Assert.Equal(highlighter, saved.DrawingAttributes.IsHighlighter);
            Assert.Equal(highlighter, saved.DrawingAttributes.IgnorePressure);
            Assert.Equal(Colors.Blue, saved.DrawingAttributes.Color);
            Assert.False(saved.DrawingAttributes.FitToCurve);
            Assert.Equal(samples.Count, saved.StylusPoints.Count);
            Assert.All(saved.StylusPoints, point => Assert.InRange(point.Y, 99.95, 100.05));
            Assert.InRange(saved.StylusPoints[0].PressureFactor, .19f, .21f);
            Assert.InRange(saved.StylusPoints[^1].PressureFactor, .89f, .91f);
            editor.CommitPendingEdits();
            editor.CommitPendingEdits();
            Assert.Equal(1, changes);
        });
    }

    [Fact]
    public void ReleaseWithoutHoldingKeepsTheFreehandPointsAndClearsTheSession()
    {
        Sta(() =>
        {
            var canvas = new PenInkCanvas();
            var samples = Samples();
            Session(canvas).Begin(samples, new DrawingAttributes { FitToCurve = true }, 0);
            Assert.False(Session(canvas).TryStraighten(649));
            var nativeStroke = new Stroke(samples.Clone(), new DrawingAttributes { FitToCurve = true });
            canvas.Strokes.Add(nativeStroke);
            Invoke(canvas, "OnStrokeCollected", new InkCanvasStrokeCollectedEventArgs(nativeStroke));
            Assert.Equal(104, nativeStroke.StylusPoints[1].Y);
            Assert.True(nativeStroke.DrawingAttributes.FitToCurve);
            Assert.False(Session(canvas).IsActive);
            Assert.Null(canvas.StraightLinePreview);
        });
    }

    [Fact]
    public void CaptureLossAfterNativeCommitClearsContactAndAllowsTheNextStroke()
    {
        Sta(() =>
        {
            var canvas = new PenInkCanvas();
            var session = Session(canvas);
            session.Begin(Samples(), new DrawingAttributes(), 0);
            Assert.True(session.TryStraighten(650));
            Invoke(canvas, "SetPenContact", true);
            // Capture loss can queue cleanup before native StrokeCollected runs.
            Invoke(canvas, "CompleteLostCapture");
            var nativeStroke = new Stroke(Samples());
            canvas.Strokes.Add(nativeStroke);
            Invoke(canvas, "OnStrokeCollected", new InkCanvasStrokeCollectedEventArgs(nativeStroke));
            PumpDispatcher();
            Assert.False(canvas.IsPenDown);
            Assert.False(session.IsActive);
            canvas.FinishInput();
            Assert.Single(canvas.Strokes);
            Assert.Equal(InkCanvasEditingMode.Ink, canvas.EditingMode);
            session.Begin(Samples(), new DrawingAttributes(), 1000);
            Assert.False(session.IsStraightened);
            Assert.True(session.TryStraighten(1650));
            canvas.FinishInput();
            Assert.Null(canvas.StraightLinePreview);
        });
    }

    [Fact]
    public void ToolChangeAndDisabledHoldCannotLeavePreviewOrPendingRecognition()
    {
        Sta(() =>
        {
            var canvas = new PenInkCanvas();
            var session = Session(canvas);
            session.Begin(Samples(), new DrawingAttributes(), 0);
            Assert.True(session.TryStraighten(650));
            Invoke(canvas, "UpdateStraightPreview");
            canvas.SetRequestedMode(InkCanvasEditingMode.EraseByPoint);
            Assert.False(session.IsActive);
            Assert.Null(canvas.StraightLinePreview);
            Assert.Empty(canvas.Strokes);
            canvas.HoldToStraightenEnabled = false;
            canvas.SetRequestedMode(InkCanvasEditingMode.Ink);
            Invoke(canvas, "BeginStraightening", Samples(), null);
            Assert.False(session.IsActive);
            Assert.Null(canvas.StraightLinePreview);
        });
    }

    private static StylusPointCollection Samples() => new()
    {
        new StylusPoint(20, 100, .2f), new StylusPoint(70, 104, .4f),
        new StylusPoint(120, 98, .7f), new StylusPoint(200, 100, .9f)
    };
    private static HoldToStraightenSession Session(PenInkCanvas canvas) =>
        (HoldToStraightenSession)typeof(PenInkCanvas).GetField("_straightening", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(canvas)!;
    private static void Invoke(PenInkCanvas canvas, string name, params object?[] args) =>
        typeof(PenInkCanvas).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(canvas, args);
    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Ink lifecycle test exceeded 30 seconds.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
