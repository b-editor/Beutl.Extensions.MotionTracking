using Beutl.Extensions.MotionTracking.Tracking;

namespace Beutl.Extensions.MotionTracking.Tests;

public class MotionPathTests
{
    private static Dictionary<long, TrackPose> Track(int frames, Func<int, TrackPose> pose)
    {
        var track = new Dictionary<long, TrackPose>();
        for (int i = 0; i < frames; i++)
            track[i] = pose(i);
        return track;
    }

    // What the transform group in TrackingKeyframes does to a point, as the drawable applies it: rotate and scale about the pivot, then shift.
    private static (double X, double Y) Map(TransformKey key, double pivotX, double pivotY, double x, double y)
    {
        double radians = key.Rotation * Math.PI / 180;
        double scale = key.ScalePercent / 100.0;
        double dx = x - pivotX;
        double dy = y - pivotY;
        return (pivotX + key.TranslateX + scale * (Math.Cos(radians) * dx - Math.Sin(radians) * dy),
            pivotY + key.TranslateY + scale * (Math.Sin(radians) * dx + Math.Cos(radians) * dy));
    }

    private static void AssertPoint((double X, double Y) actual, double x, double y, double tolerance = 1e-3)
    {
        Assert.That(actual.X, Is.EqualTo(x).Within(tolerance));
        Assert.That(actual.Y, Is.EqualTo(y).Within(tolerance));
    }

    [Test]
    public void OneTrackerMovesThePointItFollowed()
    {
        MotionPath path = MotionPath.Solve([Track(10, i => new TrackPose(100 + i * 2, 50 - i))], referenceFrame: 4)!;

        Assert.That(path.ReferenceFrame, Is.EqualTo(4));
        Assert.That(path.AnchorX, Is.EqualTo(108));
        Assert.That(path.AnchorY, Is.EqualTo(46));

        TransformKey[] keys = path.ToTransformKeys(MotionUse.Follow, MotionChannels.All, 960, 540);
        Assert.That(keys[4].TranslateX, Is.Zero);
        Assert.That(keys[4].TranslateY, Is.Zero);
        Assert.That(keys[9].TranslateX, Is.EqualTo(10));
        Assert.That(keys[9].TranslateY, Is.EqualTo(-5));
        Assert.That(keys[0].TranslateX, Is.EqualTo(-8));
    }

    [Test]
    public void AReferenceOutsideTheTrackTakesTheNearestFrame()
    {
        MotionPath path = MotionPath.Solve([Track(10, i => new TrackPose(i, 0))], referenceFrame: 100)!;

        Assert.That(path.ReferenceFrame, Is.EqualTo(9));
    }

    [Test]
    public void OneTrackerSuppliesItsOwnRotationAndScale()
    {
        // The pattern turns and grows about its own centre, which moves.
        var track = Track(20, i => new TrackPose(300 + i * 3, 200, i * 0.1f, 1 + i * 0.05f));

        MotionPath path = MotionPath.Solve([track], referenceFrame: 5)!;
        TransformKey[] keys = path.ToTransformKeys(MotionUse.Follow, MotionChannels.All, 960, 540);

        TrackPose reference = track[5];
        foreach (long frame in new long[] { 0, 5, 12, 19 })
        {
            TrackPose pose = track[frame];
            double turn = pose.Rotation - reference.Rotation;
            double scale = pose.Scale / reference.Scale;

            // A point on the object, some way from the tracked point, moves rigidly with it.
            const double offsetX = 40, offsetY = -25;
            double x = pose.X + scale * (Math.Cos(turn) * offsetX - Math.Sin(turn) * offsetY);
            double y = pose.Y + scale * (Math.Sin(turn) * offsetX + Math.Cos(turn) * offsetY);

            AssertPoint(Map(keys[frame], 960, 540, reference.X + offsetX, reference.Y + offsetY), x, y);
            Assert.That(keys[frame].Rotation, Is.EqualTo(turn * 180 / Math.PI).Within(1e-3));
            Assert.That(keys[frame].ScalePercent, Is.EqualTo(scale * 100).Within(1e-3));
        }
    }

    [Test]
    public void TwoTrackersGiveRotationAndScaleFromTheirSpan()
    {
        // Two points of something that turns and grows about (400, 300), while it moves away.
        static TrackPose At(int i, double u, double v)
        {
            double turn = i * 0.05;
            double scale = 1 + i * 0.02;
            return new TrackPose(
                (float)(400 + i * 2 + scale * (Math.Cos(turn) * u - Math.Sin(turn) * v)),
                (float)(300 - i + scale * (Math.Sin(turn) * u + Math.Cos(turn) * v)));
        }

        var a = Track(30, i => At(i, -100, 20));
        var b = Track(30, i => At(i, 120, -40));

        MotionPath path = MotionPath.Solve([a, b], referenceFrame: 0)!;
        TransformKey[] keys = path.ToTransformKeys(MotionUse.Follow, MotionChannels.All, 960, 540);

        Assert.That(keys[0].Rotation, Is.Zero.Within(1e-4));
        Assert.That(keys[0].ScalePercent, Is.EqualTo(100).Within(1e-3));
        Assert.That(keys[29].Rotation, Is.EqualTo(29 * 0.05 * 180 / Math.PI).Within(0.01));
        Assert.That(keys[29].ScalePercent, Is.EqualTo((1 + 29 * 0.02) * 100).Within(0.01));

        // Both tracked points land where they were tracked, and so does anything else on the object.
        TrackPose start = At(0, 60, 75);
        TrackPose end = At(29, 60, 75);
        AssertPoint(Map(keys[29], 960, 540, a[0].X, a[0].Y), a[29].X, a[29].Y, 0.01);
        AssertPoint(Map(keys[29], 960, 540, b[0].X, b[0].Y), b[29].X, b[29].Y, 0.01);
        AssertPoint(Map(keys[29], 960, 540, start.X, start.Y), end.X, end.Y, 0.01);
    }

    [Test]
    public void RotationKeepsCountingPastAHalfTurnWithTwoTrackers()
    {
        var a = Track(100, i => new TrackPose(500 + 100 * MathF.Cos(i * 0.1f), 300 + 100 * MathF.Sin(i * 0.1f)));
        var b = Track(100, i => new TrackPose(500 - 100 * MathF.Cos(i * 0.1f), 300 - 100 * MathF.Sin(i * 0.1f)));

        MotionPath path = MotionPath.Solve([a, b], referenceFrame: 50)!;

        Assert.That(path.Samples[50].Rotation, Is.Zero.Within(1e-6));
        Assert.That(path.Samples[99].Rotation, Is.EqualTo(49 * 0.1 * 180 / Math.PI).Within(0.01));
        Assert.That(path.Samples[0].Rotation, Is.EqualTo(-50 * 0.1 * 180 / Math.PI).Within(0.01));
    }

    [Test]
    public void SeveralTrackersUseOnlyTheFramesTheyHaveInCommon()
    {
        var a = Track(10, i => new TrackPose(i, 0));
        var b = new Dictionary<long, TrackPose>();
        for (int i = 5; i < 15; i++)
            b[i] = new TrackPose(100 + i, 0);

        MotionPath path = MotionPath.Solve([a, b], referenceFrame: 0)!;

        Assert.That(path.Samples.Select(s => s.Frame), Is.EqualTo(new long[] { 5, 6, 7, 8, 9 }));
        Assert.That(path.ReferenceFrame, Is.EqualTo(5));
    }

    [Test]
    public void NoCommonFramesGiveNoPath()
    {
        var a = Track(3, i => new TrackPose(i, 0));
        var b = new Dictionary<long, TrackPose> { [10] = new(0, 0) };

        Assert.That(MotionPath.Solve([a, b], 0), Is.Null);
        Assert.That(MotionPath.Solve([], 0), Is.Null);
    }

    [Test]
    public void TrackersOnTheSameSpotGiveNoPath()
    {
        var a = Track(3, i => new TrackPose(50, 50));

        Assert.That(MotionPath.Solve([a, a], 0), Is.Null);
    }

    [Test]
    public void LockHoldsTheTrackedPointStill()
    {
        var track = Track(20, i => new TrackPose(300 + i * 3, 200 + MathF.Sin(i) * 10, i * 0.02f, 1 + i * 0.01f));
        MotionPath path = MotionPath.Solve([track], referenceFrame: 3)!;

        TransformKey[] keys = path.ToTransformKeys(MotionUse.Lock, MotionChannels.All, 960, 540);

        for (int frame = 0; frame < 20; frame++)
            AssertPoint(Map(keys[frame], 960, 540, track[frame].X, track[frame].Y), track[3].X, track[3].Y, 0.01);
    }

    [Test]
    public void LockUndoesWhatFollowDoes()
    {
        var track = Track(20, i => new TrackPose(300 + i * 3, 200, i * 0.02f, 1 + i * 0.01f));
        MotionPath path = MotionPath.Solve([track], referenceFrame: 0)!;
        TransformKey[] follow = path.ToTransformKeys(MotionUse.Follow, MotionChannels.All, 960, 540);
        TransformKey[] locked = path.ToTransformKeys(MotionUse.Lock, MotionChannels.All, 960, 540);

        (double x, double y) = Map(follow[15], 960, 540, 123, 456);
        AssertPoint(Map(locked[15], 960, 540, x, y), 123, 456, 0.01);
    }

    [Test]
    public void SteadyMovesTheTrackedPointOntoTheSmoothedPath()
    {
        // A steady pan with a shake on top.
        var track = Track(60, i => new TrackPose(100 + i * 4 + (i % 2 == 0 ? 3 : -3), 200));
        MotionPath path = MotionPath.Solve([track], referenceFrame: 0)!;
        MotionPath smooth = path.Smooth(5);

        TransformKey[] keys = path.ToTransformKeys(MotionUse.Steady, MotionChannels.All, 960, 540, smooth);

        // The result of the shaking position plus the correction runs in nearly even steps.
        double[] steadied = [.. Enumerable.Range(0, 60).Select(i => track[i].X + (double)keys[i].TranslateX)];
        for (int i = 10; i < 50; i++)
            Assert.That(steadied[i + 1] - steadied[i], Is.EqualTo(4).Within(0.5), $"frame {i}");
    }

    [TestCase(MotionChannels.Position, true, false, false)]
    [TestCase(MotionChannels.Rotation, false, true, false)]
    [TestCase(MotionChannels.Scale, false, false, true)]
    public void ChannelsThatAreOffStayAtRest(MotionChannels channels, bool moves, bool turns, bool scales)
    {
        var track = Track(5, i => new TrackPose(100 + i * 10, 100, i * 0.1f, 1 + i * 0.1f));

        TransformKey key = MotionPath.Solve([track], 0)!.ToTransformKeys(MotionUse.Follow, channels, 960, 540)[4];

        Assert.That(key.TranslateX != 0, Is.EqualTo(moves));
        Assert.That(key.Rotation != 0, Is.EqualTo(turns));
        Assert.That(Math.Abs(key.ScalePercent - 100) > 1e-3, Is.EqualTo(scales));
    }

    [Test]
    public void SmoothingRemovesJitterButKeepsSteadyMotionWhereItIs()
    {
        var random = new Random(3);
        var steady = Track(50, i => new TrackPose(i * 5, 100));
        var jittery = Track(50, i => new TrackPose(i * 5 + (float)(random.NextDouble() - 0.5) * 4, 100));

        MotionPath cleanSmooth = MotionPath.Solve([steady], 0)!.Smooth(4);
        MotionPath jitterSmooth = MotionPath.Solve([jittery], 0)!.Smooth(4);

        for (int i = 0; i < 50; i++)
        {
            // Motion in a straight line is untouched, right up to the ends.
            Assert.That(cleanSmooth.Samples[i].X, Is.EqualTo(i * 5).Within(1e-3), $"frame {i}");
            Assert.That(jitterSmooth.Samples[i].X, Is.EqualTo(i * 5).Within(i is > 4 and < 45 ? 1.0 : 2.0), $"frame {i}");
        }
    }

    [Test]
    public void SmoothingWithNoRadiusChangesNothing()
    {
        MotionPath path = MotionPath.Solve([Track(5, i => new TrackPose(i, i))], 0)!;

        Assert.That(path.Smooth(0), Is.SameAs(path));
    }

    [Test]
    public void SimplifyingKeepsTheEndsAndDropsWhatLiesOnALine()
    {
        long[] frames = [.. Enumerable.Range(0, 100).Select(i => (long)i)];
        float[] values = [.. frames.Select(frame => frame * 2f)];

        Assert.That(KeyframeSimplifier.Simplify(frames, values, 0.1f), Is.EqualTo(new[] { 0, 99 }));
    }

    [Test]
    public void SimplifyingStaysWithinTheTolerance()
    {
        long[] frames = [.. Enumerable.Range(0, 300).Select(i => (long)i)];
        float[] values = [.. frames.Select(frame => 40 * MathF.Sin(frame * 0.05f))];

        int[] kept = KeyframeSimplifier.Simplify(frames, values, 0.2f);

        Assert.That(kept.Length, Is.LessThan(100));
        for (int k = 0; k + 1 < kept.Length; k++)
        {
            int first = kept[k];
            int last = kept[k + 1];
            for (int i = first; i <= last; i++)
            {
                float expected = values[first] + (values[last] - values[first]) * (i - first) / (last - first);
                Assert.That(values[i], Is.EqualTo(expected).Within(0.2001f), $"frame {i}");
            }
        }
    }

    [Test]
    public void NoToleranceKeepsEverything()
    {
        long[] frames = [0, 1, 2, 3];
        float[] values = [0, 0, 0, 0];

        Assert.That(KeyframeSimplifier.Simplify(frames, values, 0), Is.EqualTo(new[] { 0, 1, 2, 3 }));
    }

    [Test]
    public void GapsCountInTheDistanceBetweenKeys()
    {
        long[] frames = [0, 10, 100];
        float[] values = [0, 1, 10];

        Assert.That(KeyframeSimplifier.Simplify(frames, values, 0.01f), Is.EqualTo(new[] { 0, 2 }));
    }

    [Test]
    public void SamplesFindTheNearestFrame()
    {
        var samples = new TrackSamples();
        samples.Set(10, new TrackSample(new TrackPose(1, 0), 1));
        samples.Set(20, new TrackSample(new TrackPose(2, 0), 1));
        samples.Set(5, new TrackSample(new TrackPose(0, 0), 1));

        Assert.That(samples.FirstFrame, Is.EqualTo(5));
        Assert.That(samples.LastFrame, Is.EqualTo(20));
        Assert.That(samples.Nearest(0)!.Value.Pose.X, Is.EqualTo(0));
        Assert.That(samples.Nearest(10)!.Value.Pose.X, Is.EqualTo(1));
        Assert.That(samples.Nearest(14)!.Value.Pose.X, Is.EqualTo(1));
        Assert.That(samples.Nearest(16)!.Value.Pose.X, Is.EqualTo(2));
        Assert.That(samples.Nearest(99)!.Value.Pose.X, Is.EqualTo(2));
    }

    [Test]
    public void SamplesRemoveARange()
    {
        var samples = new TrackSamples();
        for (int i = 0; i < 10; i++)
            samples.Set(i, new TrackSample(default, 1));

        samples.RemoveRange(3, 6);

        Assert.That(samples.Frames, Is.EqualTo(new long[] { 0, 1, 2, 7, 8, 9 }));
    }
}
