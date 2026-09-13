using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;

namespace Moye.Controls;

/// <summary>
/// Recognizes a line-shaped pen gesture held at its endpoint. This model owns no
/// timer, capture or InkCanvas collection; its caller keeps native ink active
/// until TryStraighten succeeds and decides when to commit or abort the stroke.
/// Feed one monotonic 64-bit millisecond clock, such as Environment.TickCount64,
/// throughout a session, rather than the wrapping StylusEventArgs.Timestamp.
/// </summary>
public sealed class HoldToStraightenSession
{
    public const long HoldDurationMilliseconds = 650;
    public const double MinimumLengthScreenDip = 24;
    public const double StationaryToleranceScreenDip = 3;

    private readonly List<Point> _motionAnchors = [];
    private StylusPointCollection? _samples;
    private DrawingAttributes? _attributes;
    private double _zoom = 1;
    private double _motionLength;
    private long _lastMovementAt, _latestTimestamp;

    public bool IsActive => _samples is not null;
    public bool IsStraightened { get; private set; }
    /// <summary>The fixed first point, in page coordinates; valid while IsActive.</summary>
    public Point StartPoint => _samples is { Count: > 0 } ? PointOf(_samples[0]) : default;
    /// <summary>The latest endpoint, in page coordinates; valid while IsActive.</summary>
    public Point EndPoint => _samples is { Count: > 0 } ? PointOf(_samples[^1]) : default;

    public void Begin(StylusPointCollection samples, DrawingAttributes attributes, long nowMilliseconds, double zoom = 1)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(attributes);
        if (samples.Count == 0) throw new ArgumentException("A stroke needs its first sample.", nameof(samples));
        if (!double.IsFinite(zoom) || zoom <= 0) throw new ArgumentOutOfRangeException(nameof(zoom));

        Abort();
        _samples = samples.Clone();
        _attributes = attributes.Clone();
        _zoom = zoom;
        _latestTimestamp = _lastMovementAt = nowMilliseconds;
        _motionAnchors.Add(StartPoint);
        for (var i = 1; i < _samples.Count; i++) TrackMovement(PointOf(_samples[i]), nowMilliseconds);
    }

    public void Add(StylusPointCollection samples, long nowMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (_samples is null || samples.Count == 0) return;
        // A delayed input packet or a clock regression must never make a hold
        // appear older. With a 64-bit monotonic source there is no 32-bit wrap.
        _latestTimestamp = Math.Max(_latestTimestamp, nowMilliseconds);
        var snapshot = samples.Clone();
        _samples.Add(snapshot);
        if (IsStraightened) return;
        foreach (var sample in snapshot) TrackMovement(PointOf(sample), _latestTimestamp);
    }

    /// <summary>Returns true exactly once, when this gesture first becomes a line.</summary>
    public bool TryStraighten(long nowMilliseconds)
    {
        if (_samples is null || IsStraightened || nowMilliseconds < _latestTimestamp ||
            (decimal)nowMilliseconds - _lastMovementAt < HoldDurationMilliseconds || !IsLineCandidate()) return false;
        IsStraightened = true;
        return true;
    }

    /// <summary>Builds an independent preview snapshot, or null before recognition.</summary>
    public Stroke? CreateStraightStroke()
    {
        if (!IsStraightened || _samples is null || _attributes is null) return null;
        var stroke = new Stroke(_samples.Clone(), _attributes.Clone());
        ProjectOntoLine(stroke, StartPoint, EndPoint);
        return stroke;
    }

    /// <summary>
    /// Projects a native committed stroke onto the recognized segment. Retains
    /// all samples, pressure, point description and custom stroke/attribute data.
    /// The source stays untouched, so the caller can replace its native points
    /// before exposing one completed stroke to history and persistence.
    /// </summary>
    public static Stroke CreateStraightStroke(Stroke source, Point start, Point end)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!double.IsFinite(start.X) || !double.IsFinite(start.Y)) throw new ArgumentOutOfRangeException(nameof(start));
        if (!double.IsFinite(end.X) || !double.IsFinite(end.Y)) throw new ArgumentOutOfRangeException(nameof(end));
        var stroke = source.Clone();
        ProjectOntoLine(stroke, start, end);
        return stroke;
    }

    public void Abort()
    {
        _samples = null;
        _attributes = null;
        _motionAnchors.Clear();
        _motionLength = 0;
        _lastMovementAt = _latestTimestamp = 0;
        IsStraightened = false;
    }

    private void TrackMovement(Point point, long timestamp)
    {
        var distance = (point - _motionAnchors[^1]).Length;
        // Compare against a stable anchor, not the preceding packet: slow drift
        // must reset the hold, while repeated sub-pixel endpoint jitter must not.
        if (distance <= StationaryToleranceScreenDip / _zoom) return;
        _motionLength += distance;
        _motionAnchors.Add(point);
        _lastMovementAt = timestamp;
    }

    private bool IsLineCandidate()
    {
        if (_samples is null || _samples.Count < 2) return false;
        var chord = EndPoint - StartPoint;
        var length = chord.Length;
        if (length * _zoom < MinimumLengthScreenDip) return false;
        var pathLength = _motionLength + (EndPoint - _motionAnchors[^1]).Length;
        if (pathLength <= 0 || length / pathLength < .65) return false;
        var maximumDeviation = Math.Max(12 / _zoom, length * .2);
        foreach (var anchor in _motionAnchors)
        {
            var offset = anchor - StartPoint;
            var deviation = Math.Abs(Vector.CrossProduct(chord, offset)) / length;
            if (deviation > maximumDeviation) return false;
        }
        return true;
    }

    private static void ProjectOntoLine(Stroke stroke, Point start, Point end)
    {
        var points = stroke.StylusPoints;
        var progress = new double[points.Count];
        for (var i = 1; i < points.Count; i++)
            progress[i] = progress[i - 1] + (PointOf(points[i]) - PointOf(points[i - 1])).Length;
        var length = progress[^1];
        for (var i = 0; i < points.Count; i++)
        {
            var fraction = i == points.Count - 1 ? 1 : length > 0 ? progress[i] / length : 0;
            var point = points[i];
            point.X = start.X + (end.X - start.X) * fraction;
            point.Y = start.Y + (end.Y - start.Y) * fraction;
            points[i] = point;
        }
        stroke.DrawingAttributes.FitToCurve = false;
    }

    private static Point PointOf(StylusPoint point) => new(point.X, point.Y);
}
