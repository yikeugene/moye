using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Moye.Controls;

namespace Moye.Tests;

/// <summary>
/// Detached WPF routed-event, command, and rendered-pixel tests. These exercise
/// gesture grouping without claiming physical touch/pen input was delivered.
/// </summary>
public sealed class StrokeWidthPickerTests
{
    [Fact]
    public void LoadingWidthsPreservesPrecisionAndNeverEmitsAUserEditOrCommit()
    {
        Sta(() =>
        {
            var picker = new StrokeWidthPicker();
            var changes = 0; var commits = 0;
            picker.StrokeWidthChanged += (_, _) => changes++;
            picker.StrokeWidthCommitted += (_, _) => commits++;
            foreach (var width in new[] { .5, 1.7008123456789, 11.3386123456789, 24 })
            {
                picker.StrokeWidth = width;
                Assert.Equal(width, picker.StrokeWidth);
                picker.CommitPendingWidth();
            }
            picker.StrokeWidth = double.NaN;
            Assert.Equal(24, picker.StrokeWidth);
            picker.StrokeWidth = double.PositiveInfinity;
            Assert.Equal(24, picker.StrokeWidth);
            picker.StrokeWidth = 100;
            Assert.Equal(24, picker.StrokeWidth);
            picker.StrokeWidth = 0;
            Assert.Equal(.5, picker.StrokeWidth);
            Pump();
            Assert.Equal(0, changes);
            Assert.Equal(0, commits);
        });
    }

    [Fact]
    public void ManyValuesDuringOneThumbDragPreviewContinuouslyButCommitOnce()
    {
        Sta(() =>
        {
            var picker = new StrokeWidthPicker();
            var (slider, thumb) = PrepareSlider(picker);
            var previews = new List<double>(); var commits = new List<double>();
            picker.StrokeWidthChanged += (_, _) => previews.Add(picker.StrokeWidth);
            picker.StrokeWidthCommitted += (_, _) => commits.Add(picker.StrokeWidth);
            StartDrag(thumb);
            foreach (var width in new[] { 2.5, 3.75, 8d, 6.125 }) slider.Value = width;
            Assert.Equal(new[] { 2.5, 3.75, 8d, 6.125 }, previews);
            Assert.Empty(commits);
            EndDrag(thumb);
            picker.CommitPendingWidth(); // Popup close after the native release.
            Pump();
            Assert.Equal(new[] { 6.125 }, commits);
        });
    }

    [Fact]
    public void KeyboardCommandsAndDiscreteValueChangesCommitEachAdjustment()
    {
        Sta(() =>
        {
            var picker = new StrokeWidthPicker { StrokeWidth = 5 };
            var (slider, _) = PrepareSlider(picker);
            var commits = new List<double>();
            picker.StrokeWidthCommitted += (_, _) => commits.Add(picker.StrokeWidth);
            // These are the same routed commands used by the arrow/PageUp keys.
            Slider.IncreaseSmall.Execute(null, slider);
            Assert.Equal(5 + slider.SmallChange, picker.StrokeWidth, 10);
            Slider.DecreaseLarge.Execute(null, slider);
            Assert.Equal(5 + slider.SmallChange - slider.LargeChange, picker.StrokeWidth, 10);
            slider.Value = 7.25;
            Assert.Equal(3, commits.Count);
            Assert.Equal(7.25, commits[^1]);
            picker.CommitPendingWidth();
            Assert.Equal(3, commits.Count);
        });
    }

    [Fact]
    public void CleanupQueuedByPreviousGestureCannotCommitTheNextDragEarly()
    {
        Sta(() =>
        {
            var picker = new StrokeWidthPicker();
            var (slider, thumb) = PrepareSlider(picker);
            var commits = new List<double>();
            picker.StrokeWidthCommitted += (_, _) => commits.Add(picker.StrokeWidth);
            StartDrag(thumb); slider.Value = 3;
            // Mouse/stylus release or capture loss queues this same callback.
            typeof(StrokeWidthPicker).GetMethod("QueueCommit", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(picker, null);
            EndDrag(thumb);
            Assert.Equal(new[] { 3d }, commits);

            StartDrag(thumb); slider.Value = 6;
            Pump(); // Process the previous drag's pending cleanup during this drag.
            Assert.Equal(new[] { 3d }, commits);
            slider.Value = 9;
            Assert.Equal(new[] { 3d }, commits);
            EndDrag(thumb);
            Assert.Equal(new[] { 3d, 9d }, commits);
        });
    }

    [Fact]
    public void PreviewPixelsChangeImmediatelyWithWidthAndColor()
    {
        Sta(() =>
        {
            var picker = new StrokeWidthPicker { StrokeWidth = 2 };
            picker.ConfigurePreview(Colors.Blue, false, 1, false, false);
            var thin = RenderSample(picker);
            picker.StrokeWidth = 12;
            var thick = RenderSample(picker);
            Assert.True(CountBlue(thick) > CountBlue(thin) * 3,
                "Increasing the width must visibly expand the sample's ink coverage.");
            var blue = Center(thick);
            Assert.InRange(blue.B, 250, 255); Assert.InRange(blue.R, 0, 5);
            picker.ConfigurePreview(Colors.Red, false, 1, false, false);
            var red = Center(RenderSample(picker));
            Assert.InRange(red.R, 250, 255); Assert.InRange(red.B, 0, 5);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviewUsesPenOpacityAndNativeHalfOpacityForHighlighter(bool highlighter)
    {
        Sta(() =>
        {
            var picker = new StrokeWidthPicker { StrokeWidth = 12 };
            // Pen combines alpha 128 with opacity .5; highlighter ignores that
            // opacity setting and uses WPF's fixed 50% highlighting treatment.
            picker.ConfigurePreview(highlighter ? Colors.Red : Color.FromArgb(128, 255, 0, 0),
                highlighter, highlighter ? .1 : .5, false, false);
            var color = Center(RenderSample(picker));
            Assert.InRange(color.R, 250, 255);
            var expected = highlighter ? 127 : 191;
            Assert.InRange(color.G, expected - 2, expected + 2);
            Assert.InRange(color.B, expected - 2, expected + 2);
        });
    }

    private static (Slider Slider, Thumb Thumb) PrepareSlider(StrokeWidthPicker picker)
    {
        picker.Measure(new Size(320, 160)); picker.Arrange(new Rect(0, 0, 320, 160)); picker.UpdateLayout();
        var slider = (Slider)typeof(StrokeWidthPicker).GetField("_slider", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(picker)!;
        slider.ApplyTemplate();
        var track = Assert.IsType<Track>(slider.Template.FindName("PART_Track", slider));
        return (slider, track.Thumb);
    }

    private static void StartDrag(Thumb thumb) => thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
    private static void EndDrag(Thumb thumb) => thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });

    private static byte[] RenderSample(StrokeWidthPicker picker)
    {
        var sample = (FrameworkElement)typeof(StrokeWidthPicker).GetField("_sample", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(picker)!;
        sample.Measure(new Size(320, 62)); sample.Arrange(new Rect(0, 0, 320, 62)); sample.UpdateLayout();
        var bitmap = new RenderTargetBitmap(320, 62, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(sample);
        var pixels = new byte[320 * 62 * 4]; bitmap.CopyPixels(pixels, 320 * 4, 0);
        return pixels;
    }

    private static Color Center(byte[] pixels)
    {
        var offset = (32 * 320 + 160) * 4;
        return Color.FromArgb(pixels[offset + 3], pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }

    private static int CountBlue(byte[] pixels)
    {
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i] > 180 && pixels[i + 1] < 160 && pixels[i + 2] < 160) count++;
        return count;
    }

    private static void Pump()
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
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Stroke width picker test exceeded 30 seconds.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
