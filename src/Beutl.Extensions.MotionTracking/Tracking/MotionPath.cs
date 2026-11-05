namespace Beutl.Extensions.MotionTracking.Tracking;

/// <summary>
/// The motion at one frame, as a map of the scene: a point x goes to C + s R(θ) (x - A), where A is the path's anchor.
/// </summary>
/// <param name="Rotation">θ in degrees. It keeps counting past a full turn.</param>
/// <param name="Scale">s, where 1 is unchanged.</param>
public readonly record struct MotionSample(long Frame, double X, double Y, double Rotation, double Scale);

/// <summary>Which parts of the motion a result carries.</summary>
[Flags]
public enum MotionChannels
{
    None = 0,
    Position = 1,
    Rotation = 2,
    Scale = 4,
    All = Position | Rotation | Scale,
}

/// <summary>How the motion is put to use.</summary>
public enum MotionUse
{
    /// <summary>The object moves along with what was tracked.</summary>
    Follow,

    /// <summary>The motion is removed: what was tracked stays where it was at the reference frame.</summary>
    Lock,

    /// <summary>The shake is removed: what was tracked keeps only the smoothed motion.</summary>
    Steady,
}

/// <summary>The transform values for one frame.</summary>
public readonly record struct TransformKey(long Frame, float TranslateX, float TranslateY, float Rotation, float ScalePercent);

/// <summary>The motion the trackers describe, relative to a reference frame where it is the identity.</summary>
public sealed class MotionPath(double anchorX, double anchorY, long referenceFrame, IReadOnlyList<MotionSample> samples)
{
    public double AnchorX { get; } = anchorX;

    public double AnchorY { get; } = anchorY;

    public long ReferenceFrame { get; } = referenceFrame;

    public IReadOnlyList<MotionSample> Samples { get; } = samples;

    /// <summary>
    /// Works out the motion from tracked poses by frame. One tracker gives its own position, rotation and scale.
    /// Several give the rotation, scale and shift that best fit them all, for the frames all of them have.
    /// </summary>
    /// <param name="referenceFrame">The frame the motion is relative to; the nearest usable frame is taken.</param>
    /// <returns>The motion, or null when the trackers have no frame in common, or too little between them to fit.</returns>
    public static MotionPath? Solve(IReadOnlyList<IReadOnlyDictionary<long, TrackPose>> tracks, long referenceFrame)
    {
        if (tracks.Count == 0)
            return null;

        IEnumerable<long> common = tracks[0].Keys;
        for (int i = 1; i < tracks.Count; i++)
        {
            IReadOnlyDictionary<long, TrackPose> track = tracks[i];
            common = common.Where(track.ContainsKey);
        }

        long[] frames = [.. common.Order()];
        if (frames.Length == 0)
            return null;

        long reference = frames.MinBy(frame => Math.Abs(frame - referenceFrame));
        return tracks.Count == 1
            ? SolveOne(tracks[0], frames, reference)
            : SolveMany(tracks, frames, reference);
    }

    private static MotionPath SolveOne(IReadOnlyDictionary<long, TrackPose> track, long[] frames, long reference)
    {
        TrackPose start = track[reference];
        var samples = new MotionSample[frames.Length];
        for (int i = 0; i < frames.Length; i++)
        {
            TrackPose pose = track[frames[i]];
            samples[i] = new MotionSample(
                frames[i],
                pose.X,
                pose.Y,
                (pose.Rotation - start.Rotation) * 180.0 / Math.PI,
                pose.Scale / (double)start.Scale);
        }

        return new MotionPath(start.X, start.Y, reference, samples);
    }

    private static MotionPath? SolveMany(IReadOnlyList<IReadOnlyDictionary<long, TrackPose>> tracks, long[] frames, long reference)
    {
        int n = tracks.Count;
        var fromX = new double[n];
        var fromY = new double[n];
        double anchorX = 0, anchorY = 0;
        for (int i = 0; i < n; i++)
        {
            TrackPose pose = tracks[i][reference];
            fromX[i] = pose.X;
            fromY[i] = pose.Y;
            anchorX += pose.X;
            anchorY += pose.Y;
        }

        anchorX /= n;
        anchorY /= n;
        double spread = 0;
        for (int i = 0; i < n; i++)
        {
            fromX[i] -= anchorX;
            fromY[i] -= anchorY;
            spread += fromX[i] * fromX[i] + fromY[i] * fromY[i];
        }

        // Trackers on top of each other say nothing about rotation or scale.
        if (spread < 1)
            return null;

        var samples = new MotionSample[frames.Length];
        for (int f = 0; f < frames.Length; f++)
        {
            double toX = 0, toY = 0;
            for (int i = 0; i < n; i++)
            {
                TrackPose pose = tracks[i][frames[f]];
                toX += pose.X;
                toY += pose.Y;
            }

            toX /= n;
            toY /= n;

            // The least squares similarity between the two point sets, about their centres.
            double dot = 0, cross = 0;
            for (int i = 0; i < n; i++)
            {
                TrackPose pose = tracks[i][frames[f]];
                double x = pose.X - toX;
                double y = pose.Y - toY;
                dot += fromX[i] * x + fromY[i] * y;
                cross += fromX[i] * y - fromY[i] * x;
            }

            samples[f] = new MotionSample(
                frames[f],
                toX,
                toY,
                Math.Atan2(cross, dot) * 180.0 / Math.PI,
                Math.Sqrt(dot * dot + cross * cross) / spread);
        }

        UnwrapRotation(samples, reference);
        return new MotionPath(anchorX, anchorY, reference, samples);
    }

    // Atan2 jumps by a full turn when the angle passes a half turn.
    private static void UnwrapRotation(MotionSample[] samples, long reference)
    {
        for (int i = 1; i < samples.Length; i++)
        {
            double previous = samples[i - 1].Rotation;
            double rotation = samples[i].Rotation;
            rotation += 360 * Math.Round((previous - rotation) / 360);
            samples[i] = samples[i] with { Rotation = rotation };
        }

        // Counting from the first frame may leave whole turns at the reference frame, where the rotation must be zero.
        double offset = samples.First(sample => sample.Frame == reference).Rotation;
        for (int i = 0; i < samples.Length; i++)
            samples[i] = samples[i] with { Rotation = samples[i].Rotation - offset };
    }

    /// <summary>
    /// A copy with the jitter smoothed out. Each frame is replaced by a line fitted through the frames around it,
    /// which unlike a moving average does not pull the path inwards at its ends or in steady motion.
    /// </summary>
    /// <param name="radius">How many frames on each side count. Zero returns this path.</param>
    public MotionPath Smooth(int radius)
    {
        if (radius <= 0 || Samples.Count < 3)
            return this;

        var result = new MotionSample[Samples.Count];
        int start = 0;
        for (int i = 0; i < Samples.Count; i++)
        {
            long frame = Samples[i].Frame;
            while (Samples[start].Frame < frame - radius)
                start++;

            double sumWeight = 0, sumT = 0, sumTT = 0;
            double x = 0, xt = 0, y = 0, yt = 0, r = 0, rt = 0, s = 0, st = 0;
            for (int j = start; j < Samples.Count && Samples[j].Frame <= frame + radius; j++)
            {
                MotionSample sample = Samples[j];
                double t = sample.Frame - frame;
                double u = t / (radius + 1);
                double weight = (1 - u * u) * (1 - u * u);
                double logScale = Math.Log(Math.Max(sample.Scale, 1e-6));
                sumWeight += weight;
                sumT += weight * t;
                sumTT += weight * t * t;
                x += weight * sample.X;
                xt += weight * t * sample.X;
                y += weight * sample.Y;
                yt += weight * t * sample.Y;
                r += weight * sample.Rotation;
                rt += weight * t * sample.Rotation;
                s += weight * logScale;
                st += weight * t * logScale;
            }

            // The line's value at t = 0.
            double determinant = sumWeight * sumTT - sumT * sumT;
            result[i] = determinant < 1e-9
                ? Samples[i]
                : new MotionSample(
                    frame,
                    (sumTT * x - sumT * xt) / determinant,
                    (sumTT * y - sumT * yt) / determinant,
                    (sumTT * r - sumT * rt) / determinant,
                    Math.Exp((sumTT * s - sumT * st) / determinant));
        }

        return new MotionPath(AnchorX, AnchorY, ReferenceFrame, result);
    }

    /// <summary>
    /// The transform that does the motion for an object, frame by frame. The transform is applied in the object's
    /// transform group, which rotates and scales about <paramref name="pivotX"/>, <paramref name="pivotY"/>: where the
    /// object's transform origin lands in the scene before any transform. The translation makes up for that pivot
    /// not being the anchor of the motion.
    /// </summary>
    /// <param name="smoothed">For <see cref="MotionUse.Steady"/>, the path to keep. For the others the motion comes from this path alone.</param>
    public TransformKey[] ToTransformKeys(MotionUse use, MotionChannels channels, double pivotX, double pivotY, MotionPath? smoothed = null)
    {
        var keys = new TransformKey[Samples.Count];
        bool useRotation = channels.HasFlag(MotionChannels.Rotation);
        bool useScale = channels.HasFlag(MotionChannels.Scale);
        bool usePosition = channels.HasFlag(MotionChannels.Position);
        for (int i = 0; i < keys.Length; i++)
        {
            MotionSample sample = Samples[i];

            // In every use the map is x -> to + m (x - from), where m is the rotation and scale.
            double fromX, fromY, toX, toY, rotation, scale;
            switch (use)
            {
                case MotionUse.Lock:
                    (fromX, fromY) = (sample.X, sample.Y);
                    (toX, toY) = (AnchorX, AnchorY);
                    rotation = -sample.Rotation;
                    scale = 1 / sample.Scale;
                    break;
                case MotionUse.Steady:
                    MotionSample keep = (smoothed ?? this).Samples[i];
                    (fromX, fromY) = (sample.X, sample.Y);
                    (toX, toY) = (keep.X, keep.Y);
                    rotation = keep.Rotation - sample.Rotation;
                    scale = keep.Scale / sample.Scale;
                    break;
                default:
                    (fromX, fromY) = (AnchorX, AnchorY);
                    (toX, toY) = (sample.X, sample.Y);
                    rotation = sample.Rotation;
                    scale = sample.Scale;
                    break;
            }

            if (!useRotation)
                rotation = 0;
            if (!useScale)
                scale = 1;

            double translateX = 0, translateY = 0;
            if (usePosition)
            {
                double radians = rotation * Math.PI / 180;
                double c = scale * Math.Cos(radians);
                double d = scale * Math.Sin(radians);
                double offsetX = pivotX - fromX;
                double offsetY = pivotY - fromY;
                translateX = toX - pivotX + c * offsetX - d * offsetY;
                translateY = toY - pivotY + d * offsetX + c * offsetY;
            }

            keys[i] = new TransformKey(sample.Frame, (float)translateX, (float)translateY, (float)rotation, (float)(scale * 100));
        }

        return keys;
    }
}

/// <summary>Thins out a curve of keyframes, keeping it within a tolerance of the original.</summary>
internal static class KeyframeSimplifier
{
    /// <summary>The indices of the samples to keep, by the Ramer-Douglas-Peucker algorithm on the value at each frame.</summary>
    /// <param name="tolerance">How far the straight lines between kept samples may be from a dropped sample.</param>
    public static int[] Simplify(IReadOnlyList<long> frames, IReadOnlyList<float> values, float tolerance)
    {
        int count = frames.Count;
        if (count <= 2 || tolerance <= 0)
            return [.. Enumerable.Range(0, count)];

        var keep = new bool[count];
        keep[0] = true;
        keep[count - 1] = true;
        var ranges = new Stack<(int First, int Last)>();
        ranges.Push((0, count - 1));
        while (ranges.TryPop(out (int First, int Last) range))
        {
            (int first, int last) = range;
            double span = frames[last] - frames[first];
            int worst = -1;
            double worstError = tolerance;
            for (int i = first + 1; i < last; i++)
            {
                double position = (frames[i] - frames[first]) / span;
                double expected = values[first] + (values[last] - values[first]) * position;
                double error = Math.Abs(values[i] - expected);
                if (error > worstError)
                {
                    worstError = error;
                    worst = i;
                }
            }

            if (worst < 0)
                continue;

            keep[worst] = true;
            if (worst - first > 1)
                ranges.Push((first, worst));
            if (last - worst > 1)
                ranges.Push((worst, last));
        }

        return [.. Enumerable.Range(0, count).Where(i => keep[i])];
    }
}
