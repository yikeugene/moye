using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Ink;
using System.Windows.Media;
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
    private bool _mouseDown, _finishingInput, _rendererSuspended;
    private bool _holdToStraightenEnabled = true;
    private int _inputRevision;
    private StylusDevice? _writingDevice;
    private readonly HoldToStraightenSession _straightening = new();
    private readonly DispatcherTimer _holdTimer;
    public bool IsPenDown => _isPenDown;
    public Stroke? StraightLinePreview { get; private set; }
    public event EventHandler? StraightLinePreviewChanged;
    public bool HoldToStraightenEnabled
    {
        get => _holdToStraightenEnabled;
        set { if (_holdToStraightenEnabled == value) return; FinishInput(); _holdToStraightenEnabled = value; }
    }
    public event EventHandler? PenContactChanged;
    public event EventHandler? InputCompleted;

    public PenInkCanvas()
    {
        _holdTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(40) };
        _holdTimer.Tick += (_, _) => CheckHold();
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
            EndStraightening();
            SetPenContact(false);
            InputCompleted?.Invoke(this, EventArgs.Empty);
        }), true);
        AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left || IsTouch(e.StylusDevice)) return;
            if (e.StylusDevice is null) { _mouseDown = false; EndStraightening(); }
            InputCompleted?.Invoke(this, EventArgs.Empty);
        }), true);
        LostStylusCapture += (_, _) => CompleteLostCapture();
        LostMouseCapture += (_, _) => CompleteLostCapture();
        StylusOutOfRange += (_, e) => { if (!IsTouch(e.StylusDevice)) FinishInput(); };
        Unloaded += (_, _) => FinishInput();
    }

    public void SetRequestedMode(InkCanvasEditingMode mode)
    {
        if (_requestedMode != mode)
        {
            FinishInput();
            _requestedMode = mode;
        }
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
            _inputRevision++;
            EditingMode = _requestedMode;
            SetPenContact(true);
            if (!e.StylusDevice.Inverted && _requestedMode == InkCanvasEditingMode.Ink)
                BeginStraightening(e.GetStylusPoints(this), e.StylusDevice);
        }
        base.OnPreviewStylusDown(e);
    }

    protected override void OnPreviewStylusMove(StylusEventArgs e)
    {
        if (IsTouch(e.StylusDevice) && _isPenDown) e.Handled = true;
        else if (_writingDevice == e.StylusDevice && !e.InAir) TrackStraightening(e.GetStylusPoints(this));
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
        else if (_writingDevice == e.StylusDevice)
        {
            // Take the release position before the native class handler commits.
            TrackStraightening(e.GetStylusPoints(this));
            _holdTimer.Stop();
        }
        base.OnPreviewStylusUp(e);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        // Touch-to-mouse promotion must never feed InkCanvas's editing coordinator.
        if (IsTouch(e.StylusDevice)) e.Handled = true;
        else if (e.StylusDevice is null && !_isPenDown && e.ChangedButton == MouseButton.Left)
        {
            _inputRevision++;
            EditingMode = _requestedMode;
            _mouseDown = true;
            if (_requestedMode == InkCanvasEditingMode.Ink) BeginStraightening(MousePoints(e), null);
        }
        base.OnPreviewMouseDown(e);
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        if (IsTouch(e.StylusDevice)) e.Handled = true;
        else if (e.StylusDevice is null && _mouseDown && e.LeftButton == MouseButtonState.Pressed) TrackStraightening(MousePoints(e));
        base.OnPreviewMouseMove(e);
    }

    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        if (IsTouch(e.StylusDevice)) e.Handled = true;
        else if (e.StylusDevice is null && _mouseDown && e.ChangedButton == MouseButton.Left)
        {
            TrackStraightening(MousePoints(e));
            _holdTimer.Stop();
        }
        base.OnPreviewMouseUp(e);
    }

    private StylusPointCollection MousePoints(MouseEventArgs e)
    {
        var point = e.GetPosition(this);
        return new StylusPointCollection { new StylusPoint(point.X, point.Y, .5f) };
    }

    private void BeginStraightening(StylusPointCollection points, StylusDevice? device)
    {
        EndStraightening();
        if (!HoldToStraightenEnabled || points.Count == 0) return;
        _writingDevice = device;
        // UI movement thresholds are display DIP, while strokes remain page DIP.
        double scale = 1;
        if (VisualTreeHelper.GetParent(this) is FrameworkElement parent)
        {
            var matrix = parent.LayoutTransform.Value;
            scale = Math.Sqrt(matrix.M11 * matrix.M11 + matrix.M12 * matrix.M12);
        }
        _straightening.Begin(points, DefaultDrawingAttributes, Environment.TickCount64, scale);
        _holdTimer.Start();
    }

    private void TrackStraightening(StylusPointCollection points)
    {
        if (!_straightening.IsActive || points.Count == 0) return;
        _straightening.Add(points, Environment.TickCount64);
        if (_straightening.IsStraightened) UpdateStraightPreview();
    }

    private void CheckHold()
    {
        if (!_straightening.IsActive) { _holdTimer.Stop(); return; }
        // A queued timer must not snap a stroke after its button/pen was released,
        // captured by another control, inverted, or interrupted by navigation.
        bool pressed = _writingDevice is { } pen
            ? _isPenDown && !pen.InAir && !pen.Inverted && pen.Captured == this
            : _mouseDown && Mouse.LeftButton == MouseButtonState.Pressed && IsMouseCaptured;
        if (!pressed || !IsLoaded || !IsEnabled || ActiveEditingMode != InkCanvasEditingMode.Ink) return;
        if (!_straightening.TryStraighten(Environment.TickCount64)) return;
        // Keep InkCanvas collecting raw packets and owning capture/history. Only
        // replace its live rendering; the final native stroke is straightened once.
        if (DynamicRenderer is not null) { DynamicRenderer.Enabled = false; _rendererSuspended = true; }
        _holdTimer.Stop();
        UpdateStraightPreview();
    }

    private void UpdateStraightPreview()
    {
        StraightLinePreview = _straightening.CreateStraightStroke();
        StraightLinePreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnStrokeCollected(InkCanvasStrokeCollectedEventArgs e)
    {
        if (_straightening.IsStraightened)
        {
            // Mutate the just-collected instance before subscribers serialize it.
            // This avoids adding a second stroke or recording a rough intermediate.
            var line = HoldToStraightenSession.CreateStraightStroke(e.Stroke,
                _straightening.StartPoint, _straightening.EndPoint);
            e.Stroke.DrawingAttributes = line.DrawingAttributes;
            e.Stroke.StylusPoints = line.StylusPoints;
        }
        EndStraightening();
        base.OnStrokeCollected(e);
    }

    private void EndStraightening()
    {
        _holdTimer.Stop();
        _straightening.Abort();
        _writingDevice = null;
        if (_rendererSuspended)
        {
            _rendererSuspended = false;
            if (DynamicRenderer is not null) DynamicRenderer.Enabled = ActiveEditingMode == InkCanvasEditingMode.Ink;
        }
        if (StraightLinePreview is not null)
        {
            StraightLinePreview = null;
            StraightLinePreviewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CompleteLostCapture()
    {
        var revision = _inputRevision;
        // Native capture-loss handlers get the first opportunity to commit.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (revision != _inputRevision || IsStylusCaptureWithin || IsMouseCaptureWithin) return;
            EndStraightening(); _mouseDown = false;
            SetPenContact(false);
            InputCompleted?.Invoke(this, EventArgs.Empty);
        }));
    }

    /// <summary>Commit a partial native stroke before virtualization or deactivation.</summary>
    public void FinishInput()
    {
        if (_finishingInput) return;
        _finishingInput = true;
        _holdTimer.Stop();
        try
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
            EndStraightening();
            _mouseDown = false;
            _touchDevices.Clear();
            SetPenContact(false);
            EditingMode = _requestedMode;
            InputCompleted?.Invoke(this, EventArgs.Empty);
        }
        finally { _finishingInput = false; }
    }

    private void SetPenContact(bool value)
    {
        if (_isPenDown == value) return;
        _isPenDown = value;
        PenContactChanged?.Invoke(this, EventArgs.Empty);
    }
}
