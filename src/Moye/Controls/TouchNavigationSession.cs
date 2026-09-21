using System.Windows;

namespace Moye.Controls;

/// <summary>A viewport movement in screen DIP; positive ScrollDelta advances the scroll offset.</summary>
public readonly record struct TouchNavigationFrame(
    Point PreviousCenter, Point Center, double Scale, Vector ScrollDelta, bool IsInertial);

/// <summary>
/// Coalesces touch packets into one movement per displayed frame. Owns no WPF
/// capture, scroll offsets, timers or layout. Supply one monotonic millisecond
/// clock, consume a pending frame before changing the contact count, and feed
/// the final release position through MoveContact before EndContact.
/// </summary>
public sealed class TouchNavigationSession
{
    private const double MinimumPinchDistance = 20;
    private const double VelocityWindowMilliseconds = 100;
    private const double MaximumReleasePauseMilliseconds = 80;
    private const double MinimumVelocitySampleMilliseconds = 16;
    private const double MinimumFlingDistance = 4;
    private const double MaximumVelocity = 3; // Screen DIP per millisecond.
    private const double InertiaFriction = .006;
    private const double MinimumInertiaVelocity = .02;
    private const double MaximumFrameGapMilliseconds = 120;
    private readonly Dictionary<int, Point> _contacts = [];
    private readonly MotionSample[] _samples = new MotionSample[128];
    private int _sampleStart, _sampleCount;
    private double _lastMovementAt;
    private Point _baselineCenter;
    private double _baselineDistance;
    private bool _pendingFrame;
    private Vector _inertiaVelocity;
    private Point _inertiaCenter;
    private double _inertiaAt;

    private readonly record struct MotionSample(Point Position, double At);

    public int Count => _contacts.Count;
    public bool IsInertiaActive { get; private set; }
    public bool HasPendingFrame => _pendingFrame || IsInertiaActive;

    public void BeginContact(int id, Point position, double nowMs)
    {
        if (!IsFinite(position) || !double.IsFinite(nowMs)) return;
        StopInertia();
        _contacts[id] = position;
        Rebaseline(nowMs);
    }

    public void MoveContact(int id, Point position, double nowMs)
    {
        if (!IsFinite(position) || !double.IsFinite(nowMs) || !_contacts.TryGetValue(id, out var previous)) return;
        _contacts[id] = position;
        if (_contacts.Count == 1) AddVelocitySample(position, nowMs, previous != position);
        if (_contacts.Count <= 2 && previous != position) _pendingFrame = true;
    }

    public void EndContact(int id, double nowMs, bool allowInertia = true)
    {
        if (!_contacts.TryGetValue(id, out var position)) return;
        var velocity = default(Vector);
        var fling = _contacts.Count == 1 && allowInertia && double.IsFinite(nowMs) &&
                    TryGetReleaseVelocity(position, nowMs, out velocity);
        _contacts.Remove(id);
        StopInertia();
        Rebaseline(double.IsFinite(nowMs) ? nowMs : 0);
        if (!fling) return;
        _inertiaVelocity = velocity;
        _inertiaCenter = position;
        _inertiaAt = nowMs;
        IsInertiaActive = true;
    }

    public void Cancel()
    {
        _contacts.Clear();
        _sampleStart = _sampleCount = 0;
        _pendingFrame = false;
        _baselineCenter = default;
        _baselineDistance = 0;
        StopInertia();
    }

    public bool TryTakeFrame(double nowMs, out TouchNavigationFrame frame)
    {
        frame = default;
        if (!double.IsFinite(nowMs)) return false;
        if (_pendingFrame && _contacts.Count is 1 or 2)
        {
            GetGeometry(out var center, out var distance);
            var scale = _contacts.Count == 2 && _baselineDistance > MinimumPinchDistance && distance > MinimumPinchDistance
                ? distance / _baselineDistance : 1;
            var delta = _baselineCenter - center;
            frame = new(_baselineCenter, center, scale, delta, false);
            _baselineCenter = center;
            _baselineDistance = distance;
            _pendingFrame = false;
            return delta.LengthSquared > 0 || Math.Abs(scale - 1) > .000000001;
        }
        if (!IsInertiaActive) return false;
        var elapsed = nowMs - _inertiaAt;
        if (elapsed <= 0) return false;
        if (elapsed > MaximumFrameGapMilliseconds) { StopInertia(); return false; }
        var speed = _inertiaVelocity.Length;
        if (speed <= MinimumInertiaVelocity) { StopInertia(); return false; }
        // Integrate the exponential analytically, including the partial final
        // frame. Travel therefore does not depend on 60/120 Hz frame frequency.
        var remaining = Math.Log(speed / MinimumInertiaVelocity) / InertiaFriction;
        var duration = Math.Min(elapsed, remaining);
        var decay = Math.Exp(-InertiaFriction * duration);
        var scrollDelta = _inertiaVelocity * ((1 - decay) / InertiaFriction);
        var previousCenter = _inertiaCenter;
        _inertiaCenter -= scrollDelta;
        _inertiaVelocity *= decay;
        _inertiaAt = nowMs;
        if (elapsed >= remaining) StopInertia();
        frame = new(previousCenter, _inertiaCenter, 1, scrollDelta, true);
        return scrollDelta.LengthSquared > 0;
    }

    /// <summary>Stop only a direction that reached a document boundary.</summary>
    public void StopInertiaAxes(bool horizontal, bool vertical)
    {
        if (horizontal) _inertiaVelocity.X = 0;
        if (vertical) _inertiaVelocity.Y = 0;
        if (_inertiaVelocity.Length <= MinimumInertiaVelocity) StopInertia();
    }

    private void Rebaseline(double nowMs)
    {
        _pendingFrame = false;
        GetGeometry(out _baselineCenter, out _baselineDistance);
        _sampleStart = _sampleCount = 0;
        _lastMovementAt = nowMs;
        if (_contacts.Count == 1) AddVelocitySample(_baselineCenter, nowMs, false);
    }

    private void GetGeometry(out Point center, out double distance)
    {
        center = default;
        distance = 0;
        if (_contacts.Count is not (1 or 2)) return;
        // Avoid allocating arrays or enumerators on every native touch packet.
        var points = _contacts.Values.GetEnumerator();
        points.MoveNext();
        var first = points.Current;
        center = first;
        if (_contacts.Count == 1) return;
        points.MoveNext();
        var second = points.Current;
        center = new((first.X + second.X) / 2, (first.Y + second.Y) / 2);
        distance = (first - second).Length;
    }

    private void AddVelocitySample(Point position, double nowMs, bool moved)
    {
        if (_sampleCount > 0 && nowMs < Sample(_sampleCount - 1).At)
        {
            // A regressing clock cannot produce a huge or reversed fling.
            _sampleStart = _sampleCount = 0;
            _lastMovementAt = nowMs;
        }
        if (moved) _lastMovementAt = nowMs;
        while (_sampleCount > 0 && nowMs - Sample(0).At > VelocityWindowMilliseconds)
        {
            _sampleStart = (_sampleStart + 1) % _samples.Length;
            _sampleCount--;
        }
        if (_sampleCount > 0 && nowMs == Sample(_sampleCount - 1).At)
        {
            _samples[(_sampleStart + _sampleCount - 1) % _samples.Length] = new(position, nowMs);
            return;
        }
        if (_sampleCount == _samples.Length)
        {
            _sampleStart = (_sampleStart + 1) % _samples.Length;
            _sampleCount--;
        }
        _samples[(_sampleStart + _sampleCount) % _samples.Length] = new(position, nowMs);
        _sampleCount++;
    }

    private bool TryGetReleaseVelocity(Point position, double nowMs, out Vector velocity)
    {
        velocity = default;
        if (_sampleCount < 2 || nowMs < Sample(_sampleCount - 1).At ||
            nowMs - _lastMovementAt > MaximumReleasePauseMilliseconds) return false;
        AddVelocitySample(position, nowMs, false);
        if (_sampleCount < 2) return false;
        var earliest = Sample(0);
        var duration = nowMs - earliest.At;
        var delta = earliest.Position - position;
        if (duration < MinimumVelocitySampleMilliseconds || delta.Length < MinimumFlingDistance) return false;
        velocity = delta / duration;
        var speed = velocity.Length;
        if (speed > MaximumVelocity) velocity *= MaximumVelocity / speed;
        return speed > MinimumInertiaVelocity;
    }

    private MotionSample Sample(int index) => _samples[(_sampleStart + index) % _samples.Length];
    private void StopInertia() { IsInertiaActive = false; _inertiaVelocity = default; }
    private static bool IsFinite(Point point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
}
