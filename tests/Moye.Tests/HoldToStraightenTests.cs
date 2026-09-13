using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using Moye.Controls;

namespace Moye.Tests;

public sealed class HoldToStraightenTests
{
    [Fact]
    public void LineOnlyConvertsAfterFullEndpointHoldAndSignalsOnce()
    {
        var session = NewSession();
        session.Add(Points((30, 2, .4f), (80, 0, .8f)), 100);

        Assert.Null(session.CreateStraightStroke());
        Assert.False(session.TryStraighten(749));
        Assert.True(session.TryStraighten(750));
        Assert.True(session.IsStraightened);
        Assert.False(session.TryStraighten(1500));
        AssertLine(session.CreateStraightStroke()!, new(0, 0), new(80, 0));
    }

    [Fact]
    public void StationaryEndpointJitterNeitherRestartsHoldNorAccumulatesFalseHandwritingDistance()
    {
        var session = NewSession();
        session.Add(Points((80, 0, .6f)), 100);
        var jitterCount = 0;
        for (var time = 110; time < 750; time += 10)
        {
            var jitter = time % 20 == 0 ? 1.2 : -1.2;
            session.Add(Points((80 + jitter, jitter, .7f)), time);
            jitterCount++;
        }

        Assert.True(session.TryStraighten(750));
        var stroke = session.CreateStraightStroke()!;
        Assert.Equal(2 + jitterCount, stroke.StylusPoints.Count);
        AssertLine(stroke, new(0, 0), session.EndPoint);
    }

    [Fact]
    public void SlowEndpointDriftResetsHoldAgainstAStableAnchor()
    {
        var session = NewSession();
        session.Add(Points((80, 0, .5f)), 100);
        for (var offset = 1; offset <= 4; offset++)
            session.Add(Points((80 + offset, 0, .5f)), 100 + offset * 100);

        Assert.False(session.TryStraighten(750));
        Assert.False(session.TryStraighten(1149));
        Assert.True(session.TryStraighten(1150));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(23.9)]
    public void DotsAndShortStrokesStayNative(double length)
    {
        var session = NewSession();
        session.Add(Points((length, 0, .9f)), 100);

        Assert.False(session.TryStraighten(1000));
        Assert.False(session.IsStraightened);
        Assert.Null(session.CreateStraightStroke());
    }

    [Theory]
    [InlineData("loop")]
    [InlineData("handwriting")]
    [InlineData("arc")]
    public void ClosedLoopsDenseHandwritingAndLargeArcsDoNotConvert(string shape)
    {
        var session = NewSession();
        var samples = shape switch
        {
            "loop" => Points((60, 0, .5f), (60, 60, .5f), (0, 60, .5f), (0, 0, .5f), (50, 0, .5f)),
            "handwriting" => Points((10, 18, .5f), (20, -18, .5f), (30, 18, .5f), (40, -18, .5f), (50, 18, .5f), (60, -18, .5f), (70, 18, .5f), (80, 0, .5f)),
            _ => Points((10, 0, .5f), (50, 35, .5f), (90, 0, .5f), (100, 0, .5f))
        };
        session.Add(samples, 100);

        Assert.False(session.TryStraighten(1000));
    }

    [Theory]
    [InlineData(.5, 47, false)]
    [InlineData(.5, 48, true)]
    [InlineData(2, 11.9, false)]
    [InlineData(2, 12, true)]
    public void MinimumLengthIsMeasuredInScreenDipAtCurrentZoom(double zoom, double length, bool shouldConvert)
    {
        var session = NewSession(zoom);
        session.Add(Points((length, 0, .5f)), 100);

        Assert.Equal(shouldConvert, session.TryStraighten(750));
    }

    [Fact]
    public void HoldMovementToleranceAlsoUsesScreenDip()
    {
        var session = NewSession(zoom: 2);
        session.Add(Points((30, 0, .5f)), 100);
        session.Add(Points((30, 1.6, .5f)), 300);

        Assert.False(session.TryStraighten(750));
        Assert.True(session.TryStraighten(950));
    }

    [Fact]
    public void ConvertedLineTracksRotatedEndpointWithoutAnotherHoldOrReclassification()
    {
        var session = NewSession();
        session.Add(Points((30, 3, .4f), (80, 0, .8f)), 100);
        Assert.True(session.TryStraighten(750));
        var oldPreview = session.CreateStraightStroke()!;

        session.Add(Points((0, 100, .9f)), 760);

        Assert.True(session.IsStraightened);
        var newPreview = session.CreateStraightStroke()!;
        AssertLine(newPreview, new(0, 0), new(0, 100));
        Assert.Equal(new[] { .2f, .4f, .8f, .9f }, newPreview.StylusPoints.Select(p => p.PressureFactor));
        AssertLine(oldPreview, new(0, 0), new(80, 0));
    }

    [Fact]
    public void BuilderRetainsHighlighterPressureAttributesAndCustomMetadataWithoutMutatingSource()
    {
        var points = Points((0, 0, .2f), (30, 4, .4f), (55, -3, .7f), (80, 0, .9f));
        var attributes = new DrawingAttributes
        {
            Color = Colors.Gold, IsHighlighter = true, Width = 9, Height = 5,
            IgnorePressure = false, FitToCurve = true, StylusTip = StylusTip.Rectangle,
            StylusTipTransform = new Matrix(1, 0, .2, 1, 0, 0)
        };
        var source = new Stroke(points, attributes);
        var strokeProperty = Guid.NewGuid();
        var attributeProperty = Guid.NewGuid();
        source.AddPropertyData(strokeProperty, "native stroke metadata");
        source.DrawingAttributes.AddPropertyData(attributeProperty, 17);

        var projected = HoldToStraightenSession.CreateStraightStroke(source, new(20, 30), new(100, 90));

        AssertLine(projected, new(20, 30), new(100, 90));
        Assert.Equal(source.StylusPoints.Count, projected.StylusPoints.Count);
        Assert.Equal(source.StylusPoints.Select(p => p.PressureFactor), projected.StylusPoints.Select(p => p.PressureFactor));
        Assert.Equal(source.DrawingAttributes.Color, projected.DrawingAttributes.Color);
        Assert.Equal(source.DrawingAttributes.IsHighlighter, projected.DrawingAttributes.IsHighlighter);
        Assert.Equal(source.DrawingAttributes.Width, projected.DrawingAttributes.Width);
        Assert.Equal(source.DrawingAttributes.Height, projected.DrawingAttributes.Height);
        Assert.Equal(source.DrawingAttributes.IgnorePressure, projected.DrawingAttributes.IgnorePressure);
        Assert.Equal(source.DrawingAttributes.StylusTip, projected.DrawingAttributes.StylusTip);
        Assert.Equal(source.DrawingAttributes.StylusTipTransform, projected.DrawingAttributes.StylusTipTransform);
        Assert.Equal("native stroke metadata", projected.GetPropertyData(strokeProperty));
        Assert.Equal(17, projected.DrawingAttributes.GetPropertyData(attributeProperty));
        Assert.True(source.DrawingAttributes.FitToCurve);
        Assert.False(projected.DrawingAttributes.FitToCurve);
        Assert.Equal(4, source.StylusPoints[1].Y);
        projected.DrawingAttributes.Color = Colors.Blue;
        Assert.Equal(Colors.Gold, source.DrawingAttributes.Color);
    }

    [Fact]
    public void BuilderRetainsAdditionalTabletPointProperties()
    {
        var description = new StylusPointDescription(
        [
            new StylusPointPropertyInfo(StylusPointProperties.X),
            new StylusPointPropertyInfo(StylusPointProperties.Y),
            new StylusPointPropertyInfo(StylusPointProperties.NormalPressure),
            new StylusPointPropertyInfo(StylusPointProperties.XTiltOrientation)
        ]);
        var points = new StylusPointCollection(description)
        {
            new StylusPoint(0, 0, .25f, description, [10]),
            new StylusPoint(40, 5, .6f, description, [20]),
            new StylusPoint(80, 0, .9f, description, [30])
        };
        var source = new Stroke(points);

        var projected = HoldToStraightenSession.CreateStraightStroke(source, new(0, 0), new(80, 0));

        Assert.Equal(4, projected.StylusPoints.Description.GetStylusPointProperties().Count);
        Assert.Equal(new[] { 10, 20, 30 }, projected.StylusPoints.Select(p => p.GetPropertyValue(StylusPointProperties.XTiltOrientation)));
        Assert.Equal(source.StylusPoints.Select(p => p.PressureFactor), projected.StylusPoints.Select(p => p.PressureFactor));
    }

    [Fact]
    public void BeginSnapshotsInputPointsAndDrawingAttributes()
    {
        var session = new HoldToStraightenSession();
        var points = Points((0, 0, .2f), (80, 0, .8f));
        var attributes = new DrawingAttributes { Color = Colors.Gold, IsHighlighter = true };
        session.Begin(points, attributes, 0);
        attributes.Color = Colors.Red;
        points[1] = new StylusPoint(0, 0, 1);

        Assert.True(session.TryStraighten(650));
        var preview = session.CreateStraightStroke()!;
        Assert.Equal(Colors.Gold, preview.DrawingAttributes.Color);
        Assert.True(preview.DrawingAttributes.IsHighlighter);
        Assert.Equal(.8f, preview.StylusPoints[^1].PressureFactor);
        AssertLine(preview, new(0, 0), new(80, 0));
    }

    [Theory]
    [InlineData((long)int.MaxValue - 100)]
    [InlineData(long.MaxValue - 1000)]
    public void HoldClockSafelyCrosses32BitTimestampBoundaryAndHandlesLarge64BitValues(long start)
    {
        var session = new HoldToStraightenSession();
        session.Begin(Points((0, 0, .2f), (80, 0, .8f)), new DrawingAttributes(), start);

        Assert.False(session.TryStraighten(start - 1));
        Assert.False(session.TryStraighten(start + 649));
        Assert.True(session.TryStraighten(start + 650));
    }

    [Fact]
    public void DelayedPacketsCannotMoveTheHoldClockBackwards()
    {
        var session = NewSession();
        session.Add(Points((80, 0, .5f)), 1000);
        session.Add(Points((90, 0, .5f)), 100);

        Assert.False(session.TryStraighten(750));
        Assert.False(session.TryStraighten(1649));
        Assert.True(session.TryStraighten(1650));
    }

    [Fact]
    public void AbortPreventsDelayedTimerOrMoveFromRevivingGestureAndNextStrokeStartsClean()
    {
        var session = NewSession();
        session.Add(Points((80, 0, .8f)), 100);
        Assert.True(session.TryStraighten(750));

        session.Abort();
        session.Add(Points((140, 0, .7f)), 800);

        Assert.False(session.IsActive);
        Assert.False(session.IsStraightened);
        Assert.False(session.TryStraighten(2000));
        Assert.Null(session.CreateStraightStroke());
        session.Begin(Points((20, 30, .3f), (60, 30, .6f)), new DrawingAttributes(), 3000);
        Assert.False(session.TryStraighten(3649));
        Assert.True(session.TryStraighten(3650));
        var preview = session.CreateStraightStroke()!;
        Assert.Equal(2, preview.StylusPoints.Count);
        AssertLine(preview, new(20, 30), new(60, 30));
    }

    private static HoldToStraightenSession NewSession(double zoom = 1)
    {
        var session = new HoldToStraightenSession();
        session.Begin(Points((0, 0, .2f)), new DrawingAttributes(), 0, zoom);
        return session;
    }

    private static StylusPointCollection Points(params (double X, double Y, float Pressure)[] points)
        => new(points.Select(p => new StylusPoint(p.X, p.Y, p.Pressure)));

    private static void AssertLine(Stroke stroke, Point start, Point end)
    {
        var points = stroke.StylusPoints;
        Assert.Equal(start.X, points[0].X, 8);
        Assert.Equal(start.Y, points[0].Y, 8);
        Assert.Equal(end.X, points[^1].X, 8);
        Assert.Equal(end.Y, points[^1].Y, 8);
        var segment = end - start;
        double previousProgress = -1;
        foreach (var point in points)
        {
            var offset = new Point(point.X, point.Y) - start;
            Assert.InRange(Math.Abs(Vector.CrossProduct(segment, offset)), 0, .000001);
            var progress = Vector.Multiply(segment, offset) / segment.LengthSquared;
            Assert.InRange(progress, 0, 1.000001);
            Assert.True(progress >= previousProgress - .000001);
            previousProgress = progress;
        }
    }
}
