using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Threading;
using Moye.Controls;

namespace Moye.Tests;

/// <summary>
/// Real WPF layout and routed BringIntoView requests in a detached visual tree.
/// These reproduce scrolling and coordinate changes, not hardware pen delivery.
/// </summary>
public sealed class InputViewportStabilityTests
{
    [Fact]
    public void TouchdownGuardKeepsPartiallyVisiblePageAndInkCoordinatesStationary()
    {
        Sta(() =>
        {
            var fixture = new PagesFixture();
            fixture.SetOffset(650);
            var original = fixture.PageOrigin;
            Assert.InRange(original.Y, 100, 220);
            Assert.True(original.Y + fixture.Ink.Height > fixture.Scroll.ViewportHeight);

            // Same request made when the selected ListBoxItem receives focus.
            // Establish that this fixture reproduces the original page jump.
            fixture.SecondContainer.BringIntoView();
            fixture.Layout();
            Assert.True(Math.Abs(original.Y - fixture.PageOrigin.Y) > 80);

            fixture.SetOffset(650);
            fixture.InstallGuard(inputActive: true);
            var inkPoint = new Point(75, 60);
            var screenPoint = fixture.Ink.TranslatePoint(inkPoint, fixture.List);
            var offset = fixture.Scroll.VerticalOffset;
            fixture.List.SelectedIndex = 1;
            fixture.SecondContainer.BringIntoView();
            fixture.Layout();
            Assert.Equal(offset, fixture.Scroll.VerticalOffset, 6);
            Assert.Equal(screenPoint, fixture.Ink.TranslatePoint(inkPoint, fixture.List));

            // Packets in page coordinates retain their displayed location and
            // pressure after the selection/focus request has been processed.
            var stroke = new Stroke(new StylusPointCollection
            {
                new StylusPoint(inkPoint.X, inkPoint.Y, .25f), new StylusPoint(125, 80, .8f)
            });
            fixture.Ink.Strokes.Add(stroke);
            fixture.Layout();
            Assert.Equal(screenPoint, fixture.Ink.TranslatePoint(
                new Point(stroke.StylusPoints[0].X, stroke.StylusPoints[0].Y), fixture.List));
            Assert.InRange(stroke.StylusPoints[0].PressureFactor, .249f, .251f);
        });
    }

    [Fact]
    public void NativeInkCanvasFocusRequestDoesNotMoveThePageEvenBeforeContactState()
    {
        Sta(() =>
        {
            var fixture = new PagesFixture();
            fixture.SetOffset(650);
            var offset = fixture.Scroll.VerticalOffset;
            var origin = fixture.PageOrigin;
            // InkCanvas's native focus request routes through its own handler,
            // independently of the shell's early stylus-contact flag.
            fixture.Ink.BringIntoView();
            fixture.Layout();
            Assert.Equal(offset, fixture.Scroll.VerticalOffset, 6);
            Assert.Equal(origin, fixture.PageOrigin);
        });
    }

    [Fact]
    public void ExplicitSidebarNavigationStillBringsTheSelectedPageIntoView()
    {
        Sta(() =>
        {
            var fixture = new PagesFixture();
            fixture.InstallGuard(inputActive: false);
            fixture.SetOffset(0);
            fixture.List.SelectedIndex = 1;
            fixture.List.ScrollIntoView(fixture.SecondPage);
            fixture.Layout();
            Assert.True(fixture.Scroll.VerticalOffset > 500);
            Assert.InRange(fixture.PageOrigin.Y, -1, 1);
        });
    }

    [Fact]
    public void TextCaretCanScrollWhilePenContactAndSelectionGuardsAreActive()
    {
        Sta(() =>
        {
            var fixture = new PagesFixture();
            fixture.InstallGuard(inputActive: true, selectionChanging: true);
            fixture.SetOffset(650);
            var offset = fixture.Scroll.VerticalOffset;
            fixture.Text.BringIntoView(new Rect(0, 0, 2, 20));
            fixture.Layout();
            Assert.True(fixture.Scroll.VerticalOffset > offset + 300);
            var caret = fixture.Text.TranslatePoint(new Point(0, 0), fixture.List);
            Assert.InRange(caret.Y, 0, fixture.Scroll.ViewportHeight - 18);
        });
    }

    [Fact]
    public void TouchScrollQueuedBeforeTouchdownCannotMoveTheNextStroke()
    {
        Sta(() =>
        {
            var fixture = new PagesFixture();
            fixture.SetOffset(650);
            var originalOffset = fixture.Scroll.VerticalOffset;
            var originalOrigin = fixture.PageOrigin;
            fixture.Scroll.ScrollToVerticalOffset(730);
            fixture.Scroll.ScrollToHorizontalOffset(30);
            InvokeShell("FreezePendingViewportScroll", fixture.Scroll);
            fixture.Layout();
            Assert.Equal(originalOffset, fixture.Scroll.VerticalOffset, 6);
            Assert.Equal(originalOrigin, fixture.PageOrigin);
        });
    }

    private sealed class PagesFixture
    {
        public ListBox List { get; }
        public ScrollViewer Scroll { get; }
        public Border SecondPage { get; }
        public ListBoxItem SecondContainer { get; }
        public PenInkCanvas Ink { get; } = new() { Width = 400, Height = 800 };
        public TextBox Text { get; } = new() { Width = 180, Height = 30, Text = "Unicode 中文 caret" };
        public Point PageOrigin => Ink.TranslatePoint(new Point(), List);

        public PagesFixture()
        {
            var scroll = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ScrollViewer");
            scroll.SetValue(ScrollViewer.CanContentScrollProperty, false);
            scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
            List = new ListBox
            {
                Width = 420, Height = 320,
                Template = new ControlTemplate(typeof(ListBox)) { VisualTree = scroll },
                ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(StackPanel))),
                ItemContainerStyle = new Style(typeof(ListBoxItem))
                {
                    Setters = { new Setter(Control.PaddingProperty, new Thickness(0)),
                        new Setter(Control.BorderThicknessProperty, new Thickness(0)) }
                }
            };
            List.Items.Add(new Border { Width = 400, Height = 800 });
            var content = new Grid { Width = 400, Height = 800 };
            content.Children.Add(Ink);
            var objects = new Canvas();
            Canvas.SetTop(Text, 740); Canvas.SetLeft(Text, 40);
            objects.Children.Add(Text); content.Children.Add(objects);
            SecondPage = new Border { Child = content, Width = 400, Height = 800 };
            List.Items.Add(SecondPage);
            Layout();
            Scroll = Descendants<ScrollViewer>(List).First();
            SecondContainer = (ListBoxItem)List.ItemContainerGenerator.ContainerFromIndex(1);
            Assert.NotNull(SecondContainer);
            Assert.True(Scroll.ExtentHeight > Scroll.ViewportHeight);
        }

        public void InstallGuard(bool inputActive, bool selectionChanging = false)
        {
            // The production hook sits below ScrollViewer, on ItemsPresenter.
            // A hook on ListBox is too late: ScrollViewer already handles it.
            Descendants<ItemsPresenter>(List).First().RequestBringIntoView += (_, e) =>
            {
                if ((bool)InvokeShell("ShouldSuppressPageBringIntoView", e.TargetObject, inputActive, selectionChanging)!)
                    e.Handled = true;
            };
        }

        public void SetOffset(double value) { Scroll.ScrollToVerticalOffset(value); Layout(); }
        public void Layout()
        {
            List.Measure(new Size(420, 320));
            List.Arrange(new Rect(0, 0, 420, 320));
            List.UpdateLayout();
            PumpDispatcher();
            List.UpdateLayout();
        }
    }

    private static object? InvokeShell(string method, params object?[] args) =>
        typeof(MainWindow).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

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
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Viewport regression test exceeded 30 seconds.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
