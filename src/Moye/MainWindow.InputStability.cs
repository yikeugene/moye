using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Moye.Controls;

namespace Moye;

public partial class MainWindow
{
    private int? _viewportPenDeviceId;
    private int _viewportContactRevision;
    private ItemsPresenter? _bringIntoViewGuardHost;
    private bool IsViewportPenContactActive => _viewportPenDeviceId.HasValue;

    private void InitializeInputStability()
    {
        PageList.Loaded += (_, _) => EnsurePageBringIntoViewGuard();
        // Use handled-events-too: InkCanvas must commit capture loss first, but
        // its handled event must not leave the viewport's earlier gate stuck.
        Viewport.AddHandler(Stylus.LostStylusCaptureEvent,
            new StylusEventHandler(ViewportPenInterrupted), true);
        Viewport.AddHandler(Stylus.StylusOutOfRangeEvent,
            new StylusEventHandler(ViewportPenInterrupted), true);
        Deactivated += (_, _) => ResetViewportInputStability();
        Closed += (_, _) => ResetViewportInputStability();
    }

    private void EnsurePageBringIntoViewGuard()
    {
        var presenter = Descendants<ItemsPresenter>(PageList).FirstOrDefault();
        if (presenter is null || presenter == _bringIntoViewGuardHost) return;
        if (_bringIntoViewGuardHost is not null)
            _bringIntoViewGuardHost.RequestBringIntoView -= PageListRequestBringIntoView;
        _bringIntoViewGuardHost = presenter;
        // PageList itself is outside its template's ScrollViewer. The request
        // must be handled inside that viewer, before it queues MakeVisible.
        presenter.RequestBringIntoView += PageListRequestBringIntoView;
    }

    private void ViewportStylusDown(object sender, StylusDownEventArgs e)
    {
        if (e.StylusDevice.TabletDevice.Type != TabletDeviceType.Stylus ||
            !HasInputAncestor<PageEditor>(e.OriginalSource as DependencyObject)) return;

        // Tunnelling reaches here before PageHost activation, ListBox selection,
        // or InkCanvas's native focus/capture handlers. Set the gate first.
        _viewportPenDeviceId = e.StylusDevice.Id;
        _viewportContactRevision++;
        _zoomNavigationRevision++;
        _zoomNavigationActive = false;
        _ignoreTouchUntil = Environment.TickCount64 + 200;
        EnsurePageBringIntoViewGuard();
        ClearTouches(true);
        if (GetScroll() is { } scroll) FreezePendingViewportScroll(scroll);
    }

    private static void FreezePendingViewportScroll(ScrollViewer scroll)
    {
        // ScrollTo* commands from the last touch packet can still be queued.
        // Drain them back to the currently displayed origin before InkCanvas
        // begins this stroke, not in a later callback while the pen is moving.
        var horizontal = scroll.HorizontalOffset;
        var vertical = scroll.VerticalOffset;
        scroll.ScrollToHorizontalOffset(horizontal);
        scroll.ScrollToVerticalOffset(vertical);
        scroll.UpdateLayout();
    }

    private void ViewportStylusUp(object sender, StylusEventArgs e) => QueueViewportContactEnd(e, false);

    private void ViewportPenInterrupted(object sender, StylusEventArgs e) => QueueViewportContactEnd(e, true);

    private void QueueViewportContactEnd(StylusEventArgs e, bool checkCapture)
    {
        if (e.StylusDevice.TabletDevice.Type != TabletDeviceType.Stylus ||
            _viewportPenDeviceId != e.StylusDevice.Id) return;
        var revision = _viewportContactRevision;
        var device = e.StylusDevice;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (revision != _viewportContactRevision) return;
            // A capture transfer inside the same editor is not an interruption.
            if (checkCapture && !device.InAir &&
                HasInputAncestor<PageEditor>(device.Captured as DependencyObject)) return;
            ResetViewportInputStability();
            if (!_closing) ScheduleFitWidth();
        }));
    }

    private void ResetViewportInputStability()
    {
        _viewportContactRevision++;
        _viewportPenDeviceId = null;
        _ignoreTouchUntil = Environment.TickCount64 + 120;
    }

    private void PageListRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (ShouldSuppressPageBringIntoView(e.TargetObject, AnyPenDown, _suppressPageSelection))
            e.Handled = true;
    }

    private static bool ShouldSuppressPageBringIntoView(DependencyObject? target, bool inputActive, bool selectionChanging)
    {
        // Caret rectangles are small, deliberate requests from TextBoxView.
        // They must remain scrollable when editing a partially visible text box.
        if (HasInputAncestor<TextBoxBase>(target)) return false;
        return inputActive || selectionChanging || HasInputAncestor<PenInkCanvas>(target);
    }

    private static bool HasInputAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T) return true;
            current = current switch
            {
                Visual or Visual3D => VisualTreeHelper.GetParent(current),
                FrameworkContentElement content => content.Parent,
                _ => LogicalTreeHelper.GetParent(current)
            };
        }
        return false;
    }
}
