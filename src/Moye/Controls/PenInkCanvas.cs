using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Moye.Controls;

/// <summary>
/// Keeps WPF's native pressure-aware DynamicRenderer while separating touch from
/// pen/mouse. Touch is left unhandled for the owning viewport's pan/zoom handler.
/// </summary>
public sealed class PenInkCanvas : InkCanvas
{
    private InkCanvasEditingMode _requestedMode = InkCanvasEditingMode.Ink;
    private readonly HashSet<int> _touchDevices = [];
    private bool _isPenDown;
    public bool IsPenDown => _isPenDown;
    public event EventHandler? PenContactChanged;
    public event EventHandler? InputCompleted;

    public PenInkCanvas()
    {
        Background = System.Windows.Media.Brushes.Transparent;
        ClipToBounds = true;
        Focusable = true;
        EditingModeInverted = InkCanvasEditingMode.EraseByPoint;
        Stylus.SetIsPressAndHoldEnabled(this, false);
        Stylus.SetIsFlicksEnabled(this, false);
        Stylus.SetIsTapFeedbackEnabled(this, false);
        Stylus.SetIsTouchFeedbackEnabled(this, false);
        AddHandler(Stylus.StylusUpEvent, new StylusEventHandler((_, e) =>
        {
            if (IsTouch(e.StylusDevice)) return;
            // Native InkCanvas's handler commits the stroke before this runs.
            SetPenContact(false);
            InputCompleted?.Invoke(this, EventArgs.Empty);
        }), true);
        AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (!IsTouch(e.StylusDevice)) InputCompleted?.Invoke(this, EventArgs.Empty);
        }), true);
        LostStylusCapture += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!IsStylusCaptured)
            {
                SetPenContact(false);
                InputCompleted?.Invoke(this, EventArgs.Empty);
            }
        }));
        LostMouseCapture += (_, _) => InputCompleted?.Invoke(this, EventArgs.Empty);
        StylusOutOfRange += (_, e) => { if (!IsTouch(e.StylusDevice)) FinishInput(); };
        Unloaded += (_, _) => FinishInput();
    }

    public void SetRequestedMode(InkCanvasEditingMode mode)
    {
        if (_requestedMode == mode) return;
        FinishInput();
        _requestedMode = mode;
        EditingMode = _touchDevices.Count == 0 ? mode : InkCanvasEditingMode.None;
    }

    public static bool IsTouch(StylusDevice? device) => device?.TabletDevice.Type == TabletDeviceType.Touch;

    protected override void OnPreviewStylusDown(StylusDownEventArgs e)
    {
        if (IsTouch(e.StylusDevice))
        {
            _touchDevices.Add(e.StylusDevice.Id);
            if (_isPenDown || IsMouseCaptureWithin) e.Handled = true;
            else EditingMode = InkCanvasEditingMode.None;
        }
        else
        {
            EditingMode = _requestedMode;
            SetPenContact(true);
        }
        base.OnPreviewStylusDown(e);
    }

    protected override void OnPreviewStylusMove(StylusEventArgs e)
    {
        if (IsTouch(e.StylusDevice) && _isPenDown) e.Handled = true;
        base.OnPreviewStylusMove(e);
    }

    protected override void OnPreviewStylusUp(StylusEventArgs e)
    {
        if (IsTouch(e.StylusDevice))
        {
            _touchDevices.Remove(e.StylusDevice.Id);
            if (_isPenDown) e.Handled = true;
            else Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_touchDevices.Count == 0 && !_isPenDown) EditingMode = _requestedMode;
            }));
        }
        base.OnPreviewStylusUp(e);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        // Touch-to-mouse promotion must never feed InkCanvas's editing coordinator.
        if (IsTouch(e.StylusDevice)) e.Handled = true;
        else if (e.StylusDevice is null && !_isPenDown) EditingMode = _requestedMode;
        base.OnPreviewMouseDown(e);
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        if (IsTouch(e.StylusDevice)) e.Handled = true;
        base.OnPreviewMouseMove(e);
    }

    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        if (IsTouch(e.StylusDevice)) e.Handled = true;
        base.OnPreviewMouseUp(e);
    }

    /// <summary>Commit a partial native stroke before virtualization or deactivation.</summary>
    public void FinishInput()
    {
        if (IsStylusCaptured || IsMouseCaptured || IsMouseCaptureWithin || _isPenDown)
        {
            // WPF commits ink on LostDeviceCapture. Switching Ink -> None before
            // releasing capture DISCARDs its unfinished stroke (InkCollectionBehavior).
            // Always release first, then reset editing/selection behavior.
            ReleaseStylusCapture();
            ReleaseMouseCapture();
            if (IsMouseCaptureWithin) Mouse.Capture(null);
            EditingMode = InkCanvasEditingMode.None;
        }
        _touchDevices.Clear();
        SetPenContact(false);
        EditingMode = _requestedMode;
        InputCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void SetPenContact(bool value)
    {
        if (_isPenDown == value) return;
        _isPenDown = value;
        PenContactChanged?.Invoke(this, EventArgs.Empty);
    }
}
