using Beutl.Extensions.MotionTracking.Tracking;

namespace Beutl.Extensions.MotionTracking.Tests;

public class TrackingSessionTests
{
    /// <summary>Frames of a pattern moving at a known speed, recording the order they are asked for and how many remain undisposed.</summary>
    private sealed class FakeSource(Func<long, TrackPose> motion, int backwardBuffer = 4) : ITrackingFrameSource
    {
        private readonly SyntheticVideo _video = new();
        private int _open;

        public List<long> Requested { get; } = [];

        public int OpenFrames => Volatile.Read(ref _open);

        public int BackwardBufferFrames => backwardBuffer;

        public Func<long, Exception?> Fail { get; set; } = _ => null;

        public async ValueTask<ITrackingFrame> ReadAsync(long frame, CancellationToken cancellationToken)
        {
            lock (Requested)
                Requested.Add(frame);

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (Fail(frame) is { } exception)
                throw exception;

            Interlocked.Increment(ref _open);
            return new Frame(this, _video.Render(200, 200, motion(frame)));
        }

        private sealed class Frame(FakeSource source, ImagePyramid image) : ITrackingFrame
        {
            public int Width => image.Width;

            public int Height => image.Height;

            public void LoadInto(ImagePyramid pyramid)
            {
                image[0].Pixels.CopyTo(pyramid[0].Pixels, 0);
                pyramid.BuildLevels();
            }

            public void CopyBgra(byte[] destination) => throw new NotSupportedException();

            public void Dispose() => Interlocked.Decrement(ref source._open);
        }
    }

    private static TrackerStart Start(float x, float y) => new(new TrackerOptions(), new TrackPose(x, y));

    [Test]
    public async Task TracksForwardsInSceneCoordinates()
    {
        // The source's poses say where the pattern's pixel centre is; in scene coordinates that is half a pixel on.
        var source = new FakeSource(frame => new TrackPose(100 + frame * 2f, 120));
        var updates = new List<TrackingUpdate>();

        TrackingOutcome outcome = await TrackingSession.RunAsync(source, [Start(100.5f, 120.5f)], 0, 10, updates.Add, default);

        Assert.That(outcome.LastFrame, Is.EqualTo(10));
        Assert.That(outcome.Stops, Is.EqualTo(new TrackStatus?[] { null }));
        Assert.That(updates.Select(u => u.Frame), Is.EqualTo(Enumerable.Range(1, 10).Select(i => (long)i)));
        TrackPose last = updates[^1].Results[0]!.Value.Pose;
        Assert.That(last.X, Is.EqualTo(120.5f).Within(0.1f));
        Assert.That(last.Y, Is.EqualTo(120.5f).Within(0.1f));
        Assert.That(source.OpenFrames, Is.Zero);
    }

    [Test]
    public async Task TracksBackwardsReadingRunsFromEarlierToLater()
    {
        var source = new FakeSource(frame => new TrackPose(100 + frame * 2f, 120), backwardBuffer: 4);
        var updates = new List<TrackingUpdate>();

        TrackingOutcome outcome = await TrackingSession.RunAsync(source, [Start(120.5f, 120.5f)], 10, 0, updates.Add, default);

        Assert.That(outcome.LastFrame, Is.EqualTo(0));
        Assert.That(updates.Select(u => u.Frame), Is.EqualTo(Enumerable.Range(0, 10).Reverse().Select(i => (long)i)));
        Assert.That(updates[^1].Results[0]!.Value.Pose.X, Is.EqualTo(100.5f).Within(0.1f));

        // A decoder only has to seek once for each run, not once for each frame.
        Assert.That(source.Requested, Is.EqualTo(new long[] { 7, 8, 9, 10, 3, 4, 5, 6, 0, 1, 2 }));
        Assert.That(source.OpenFrames, Is.Zero);
    }

    [Test]
    public async Task EachTrackerStopsOnItsOwn()
    {
        // The second tracker's pattern leaves the frame on the way; the first's stays inside.
        var source = new FakeSource(frame => new TrackPose(160 + frame * 4f, 120));
        var updates = new List<TrackingUpdate>();

        TrackingOutcome outcome = await TrackingSession.RunAsync(
            source,
            [Start(120, 120), Start(280, 120)],
            0,
            20,
            updates.Add,
            default);

        Assert.That(outcome.Stops[0], Is.Null);
        Assert.That(outcome.Stops[1], Is.EqualTo(TrackStatus.OutOfFrame));
        Assert.That(outcome.LastFrame, Is.EqualTo(20));
        Assert.That(updates.Count(u => u.Results[1] != null), Is.LessThan(20));
        Assert.That(updates[^1].Results[0], Is.Not.Null);
        Assert.That(updates[^1].Results[1], Is.Null);
    }

    [Test]
    public async Task EndsWhenEveryTrackerHasStopped()
    {
        var source = new FakeSource(frame => new TrackPose(300 + frame * 8f, 120));
        var updates = new List<TrackingUpdate>();

        TrackingOutcome outcome = await TrackingSession.RunAsync(source, [Start(300, 120)], 0, 100, updates.Add, default);

        Assert.That(outcome.Stops[0], Is.EqualTo(TrackStatus.OutOfFrame));
        Assert.That(outcome.LastFrame, Is.LessThan(10));
        Assert.That(source.OpenFrames, Is.Zero);
    }

    [Test]
    public void CancellingLeavesNoFrameOpen()
    {
        var source = new FakeSource(frame => new TrackPose(100 + frame, 120));
        using var cancellation = new CancellationTokenSource();

        Assert.CatchAsync<OperationCanceledException>(() => TrackingSession.RunAsync(
            source,
            [Start(100, 120)],
            0,
            100,
            update =>
            {
                if (update.Frame == 3)
                    cancellation.Cancel();
            },
            cancellation.Token));

        Assert.That(source.OpenFrames, Is.Zero);
    }

    [Test]
    public void AFrameThatCannotBeReadEndsTheRun()
    {
        var source = new FakeSource(frame => new TrackPose(100 + frame, 120))
        {
            Fail = frame => frame == 4 ? new IOException("decoder failed") : null,
        };

        IOException? exception = Assert.CatchAsync<IOException>(() => TrackingSession.RunAsync(
            source,
            [Start(100, 120)],
            0,
            100,
            _ => { },
            default));

        Assert.That(exception!.Message, Is.EqualTo("decoder failed"));
        Assert.That(source.OpenFrames, Is.Zero);
    }
}
