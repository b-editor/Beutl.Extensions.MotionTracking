namespace Beutl.Extensions.MotionTracking.Tracking;

/// <summary>A frame to track in, valid until it is disposed.</summary>
internal interface ITrackingFrame : IDisposable
{
    int Width { get; }

    int Height { get; }

    void LoadInto(ImagePyramid pyramid);

    /// <summary>Copies the frame's premultiplied 8-bit BGRA pixels, with <see cref="Width"/> * 4 bytes per row.</summary>
    void CopyBgra(byte[] destination);
}

internal interface ITrackingFrameSource
{
    /// <summary>How many frames may be held in memory at once for reading backwards.</summary>
    int BackwardBufferFrames { get; }

    ValueTask<ITrackingFrame> ReadAsync(long frame, CancellationToken cancellationToken);
}

/// <summary>A tracker to run, from its pose in the first frame. Poses are in scene coordinates.</summary>
internal sealed record TrackerStart(TrackerOptions Options, TrackPose Pose);

/// <summary>The results for one frame, by tracker. A tracker that already stopped has none.</summary>
internal sealed record TrackingUpdate(long Frame, TrackResult?[] Results, ITrackingFrame Image);

internal sealed record TrackingOutcome(long LastFrame, TrackStatus?[] Stops);

/// <summary>
/// Runs trackers through a range of frames. Poses are in scene coordinates, where a pixel covers x to x + 1; the
/// trackers work with pixel centres at integers.
/// </summary>
internal static class TrackingSession
{
    private const float PixelCenter = 0.5f;

    /// <param name="firstFrame">The frame the start poses are in.</param>
    /// <param name="lastFrame">The frame to stop after. Before <paramref name="firstFrame"/> it tracks backwards.</param>
    /// <param name="report">Called for each frame, on a background thread, with a frame that is only valid during the call.</param>
    public static async Task<TrackingOutcome> RunAsync(
        ITrackingFrameSource source,
        IReadOnlyList<TrackerStart> starts,
        long firstFrame,
        long lastFrame,
        Action<TrackingUpdate> report,
        CancellationToken cancellationToken)
    {
        await using var reader = new SequentialFrameReader(source, firstFrame, lastFrame);
        ImagePyramid pyramid;
        var trackers = new PatchTracker?[starts.Count];
        using (ITrackingFrame first = await reader.NextAsync(cancellationToken).ConfigureAwait(false))
        {
            pyramid = new ImagePyramid(first.Width, first.Height);
            first.LoadInto(pyramid);
            for (int i = 0; i < trackers.Length; i++)
            {
                var tracker = new PatchTracker(starts[i].Options, pyramid.LevelCount);
                TrackPose pose = starts[i].Pose;
                tracker.Start(pyramid, pose with { X = pose.X - PixelCenter, Y = pose.Y - PixelCenter });
                trackers[i] = tracker;
            }
        }

        var stops = new TrackStatus?[trackers.Length];
        long last = firstFrame;
        int direction = lastFrame >= firstFrame ? 1 : -1;
        for (long frame = firstFrame + direction; direction > 0 ? frame <= lastFrame : frame >= lastFrame; frame += direction)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using ITrackingFrame image = await reader.NextAsync(cancellationToken).ConfigureAwait(false);
            image.LoadInto(pyramid);

            var results = new TrackResult?[trackers.Length];
            Parallel.For(0, trackers.Length, i =>
            {
                if (trackers[i] is { } tracker)
                    results[i] = tracker.Step(pyramid);
            });

            bool anyTracked = false;
            for (int i = 0; i < results.Length; i++)
            {
                if (results[i] is not { } result)
                    continue;

                TrackPose pose = result.Pose;
                results[i] = result with { Pose = pose with { X = pose.X + PixelCenter, Y = pose.Y + PixelCenter } };
                if (result.Status == TrackStatus.Tracked)
                {
                    anyTracked = true;
                }
                else
                {
                    stops[i] = result.Status;
                    trackers[i] = null;
                }
            }

            if (!anyTracked)
                break;

            last = frame;
            report(new TrackingUpdate(frame, results, image));
        }

        return new TrackingOutcome(last, stops);
    }

    /// <summary>
    /// Reads frames in tracking order. Going forwards the next frame is read while the current one is worked on.
    /// Going backwards frames are read in runs from earlier to later, because stepping a video decoder backwards
    /// frame by frame means seeking back to a keyframe for every single frame.
    /// </summary>
    private sealed class SequentialFrameReader(ITrackingFrameSource source, long first, long last) : IAsyncDisposable
    {
        private readonly int _direction = last >= first ? 1 : -1;
        private readonly Stack<ITrackingFrame> _buffer = new();
        private long _next = first;
        private Task<ITrackingFrame>? _pending;

        public async Task<ITrackingFrame> NextAsync(CancellationToken cancellationToken)
        {
            return _direction > 0
                ? await NextForwardAsync(cancellationToken).ConfigureAwait(false)
                : await NextBackwardAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<ITrackingFrame> NextForwardAsync(CancellationToken cancellationToken)
        {
            Task<ITrackingFrame> read = _pending ?? source.ReadAsync(_next, cancellationToken).AsTask();
            _pending = null;
            ITrackingFrame frame = await read.ConfigureAwait(false);
            _next++;
            if (_next <= last)
                _pending = source.ReadAsync(_next, cancellationToken).AsTask();
            return frame;
        }

        private async Task<ITrackingFrame> NextBackwardAsync(CancellationToken cancellationToken)
        {
            if (_buffer.Count == 0)
            {
                long oldest = Math.Max(last, _next - Math.Max(source.BackwardBufferFrames, 1) + 1);
                for (long frame = oldest; frame <= _next; frame++)
                    _buffer.Push(await source.ReadAsync(frame, cancellationToken).ConfigureAwait(false));
            }

            _next--;
            return _buffer.Pop();
        }

        public async ValueTask DisposeAsync()
        {
            while (_buffer.TryPop(out ITrackingFrame? frame))
                frame.Dispose();

            if (_pending is { } pending)
            {
                try
                {
                    (await pending.ConfigureAwait(false)).Dispose();
                }
                catch (Exception)
                {
                    // Whatever stopped the session is already being reported; a frame nobody wants cannot add to it.
                }
            }
        }
    }
}
