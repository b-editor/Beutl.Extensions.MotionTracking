namespace Beutl.Extensions.MotionTracking.Tracking;

/// <summary>What a tracker follows besides the position.</summary>
public enum TrackerMotionModel
{
    /// <summary>The position, the rotation and the scale of the pattern, as far as the pattern shows them.</summary>
    PositionRotationScale,

    /// <summary>Only the position. On a pattern that turns or changes size, the template lags behind and the position wanders.</summary>
    Position,
}

/// <summary>What a tracker compares each frame with.</summary>
public enum TrackerTemplateMode
{
    /// <summary>The pattern as it looks now, which follows changes of appearance and is corrected against the first look.</summary>
    Adaptive,

    /// <summary>The pattern as it looked when tracking started. It does not drift, but stops when the look changes.</summary>
    Fixed,
}

/// <summary>Why a tracking step ended the way it did.</summary>
public enum TrackStatus
{
    Tracked,
    LowConfidence,
    OutOfFrame,
}

/// <summary>Where a pattern is in a frame. The rotation is in radians; 1 is the scale it was first tracked at.</summary>
public readonly record struct TrackPose(float X, float Y, float Rotation = 0f, float Scale = 1f);

/// <summary>The outcome of tracking a pattern into one frame.</summary>
public readonly record struct TrackResult(TrackPose Pose, float Confidence, TrackStatus Status);

/// <summary>The sizes and behaviour of a tracker. Half sizes exclude the centre pixel.</summary>
public sealed record TrackerOptions
{
    public int PatternHalfWidth { get; init; } = 16;

    public int PatternHalfHeight { get; init; } = 16;

    /// <summary>The half width of the area searched for the pattern, which includes the pattern itself.</summary>
    public int SearchHalfWidth { get; init; } = 48;

    public int SearchHalfHeight { get; init; } = 48;

    public TrackerMotionModel Model { get; init; } = TrackerMotionModel.PositionRotationScale;

    public TrackerTemplateMode Template { get; init; } = TrackerTemplateMode.Adaptive;

    /// <summary>The similarity to the pattern, from 0 to 1, below which the tracker counts as lost.</summary>
    public float MinConfidence { get; init; } = 0.75f;
}

/// <summary>
/// Follows a rectangular pattern from frame to frame: a coarse-to-fine search by normalized cross-correlation finds
/// the area it moved to, and an inverse compositional Lucas-Kanade alignment refines that to a fraction of a pixel.
/// Correlation and alignment both compensate for brightness and contrast, so lighting changes do not disturb them.
/// </summary>
internal sealed class PatchTracker
{
    // Below this size a pyramid level holds too little of the pattern to search reliably.
    private const int MinCoarseHalfSize = 4;
    private const int MaxCandidates = 3;
    private const int MaxIterations = 24;
    private const float ConvergedStep = 0.01f;

    // The alignment works on the level where the pattern is at most this big; larger patterns gain nothing from more samples.
    private const int MaxAlignmentHalfSize = 48;

    // Below this the pattern's look no longer feeds the adaptive template, which keeps occluders out of it.
    private const float MinUpdateConfidence = 0.75f;
    private const float TemplateBlend = 0.2f;
    private const float MaxConfidenceDrop = 0.1f;
    private const float ConfidenceSmoothing = 0.1f;

    // How far a pattern may move on a frame where the match suddenly got worse: several times its usual speed, or a share of its size.
    private const float MaxJumpSpeeds = 3;
    private const float MinJumpShare = 0.5f;
    private const float SpeedSmoothing = 0.2f;

    // How well the first look must still match, and how close its alignment must land, to correct the drift.
    private const float MinDriftCorrectionConfidence = 0.7f;
    private const float MaxDriftCorrection = 1.5f;
    private const float VelocitySmoothing = 0.5f;
    private const float MinSearchMargin = 4;

    // On a detailed image a wrong place can correlate moderately, so only a clearly good match settles the search.
    private const float MinSureSearchScore = 0.85f;

    // A nearly flat image, as under an occluder, says nothing the alignment can follow; nor should it be amplified to look like more.
    private const float MinWarpDeviation = 0.25f;
    private const float MaxGain = 4;

    // What rotation or scale must add to the information about the pattern, as a share of what the translation has.
    private const double MinObservableShare = 0.03;

    private readonly TrackerOptions _options;
    private readonly int _parameterCount;
    private readonly int _alignmentLevel;
    private readonly float _levelScale;

    private Patch? _reference;
    private Patch? _current;
    private CoarseTemplate?[] _coarse = [];
    private float[] _warped = [];

    // The pose in level 0 coordinates, and its rotation and scale as the complex number c + di.
    private float _x;
    private float _y;
    private float _c = 1;
    private float _d;
    private float _rotation;
    private float _velocityX;
    private float _velocityY;
    private float _typicalConfidence = 1;
    private float _typicalSpeed;

    public PatchTracker(TrackerOptions options, int levelCount)
    {
        _options = options;
        _parameterCount = options.Model == TrackerMotionModel.Position ? 2 : 4;
        int level = 0;
        int largest = Math.Max(options.PatternHalfWidth, options.PatternHalfHeight);
        while (level < levelCount - 1 && largest >> level > MaxAlignmentHalfSize)
            level++;
        _alignmentLevel = level;
        _levelScale = 1f / (1 << level);
    }

    public TrackPose Pose => new(_x, _y, _rotation, MathF.Sqrt(_c * _c + _d * _d));

    /// <summary>Takes the pattern at <paramref name="pose"/> in <paramref name="frame"/> as the look to follow.</summary>
    public void Start(ImagePyramid frame, TrackPose pose)
    {
        _x = pose.X;
        _y = pose.Y;
        _rotation = pose.Rotation;
        _c = pose.Scale * MathF.Cos(pose.Rotation);
        _d = pose.Scale * MathF.Sin(pose.Rotation);
        _velocityX = 0;
        _velocityY = 0;
        _typicalConfidence = 1;
        _typicalSpeed = 0;

        int halfWidth = Math.Max(_options.PatternHalfWidth >> _alignmentLevel, 2);
        int halfHeight = Math.Max(_options.PatternHalfHeight >> _alignmentLevel, 2);
        _reference = new Patch(halfWidth, halfHeight, _parameterCount);
        Capture(_reference, frame);
        _reference.Prepare();
        _current = _reference;
        _warped = new float[_reference.Count];
        _coarse = new CoarseTemplate?[frame.LevelCount];
        CaptureCoarse(frame);
    }

    /// <summary>Finds the pattern in the next frame. After a result that is not <see cref="TrackStatus.Tracked"/> the pose is unchanged.</summary>
    public TrackResult Step(ImagePyramid frame)
    {
        Patch current = _current ?? throw new InvalidOperationException("Call Start first.");
        (float searchX, float searchY, float searchScore) = Search(frame, _x + _velocityX, _y + _velocityY);
        if (searchScore < MinSureSearchScore && (MathF.Abs(_velocityX) > 0.5f || MathF.Abs(_velocityY) > 0.5f))
        {
            // The motion may have just changed direction: try again around the last position.
            (float x2, float y2, float score2) = Search(frame, _x, _y);
            if (score2 > searchScore)
                (searchX, searchY, searchScore) = (x2, y2, score2);
        }

        GrayImage image = frame[_alignmentLevel];
        var aligned = new Alignment(searchX * _levelScale, searchY * _levelScale, _c, _d);
        float confidence = Align(current, image, ref aligned);

        // A failed alignment must not lose what the search found.
        var searched = new Alignment(searchX * _levelScale, searchY * _levelScale, _c, _d);
        float searchedConfidence = Evaluate(current, image, searched);
        if (searchedConfidence > confidence + 0.02f)
        {
            aligned = searched;
            confidence = searchedConfidence;
        }

        if (_options.Template == TrackerTemplateMode.Adaptive && !ReferenceEquals(current, _reference))
        {
            // Aligning to the first look removes the error each update of the template adds, while it still matches.
            Alignment corrected = aligned;
            float correctedConfidence = Align(_reference!, image, ref corrected);
            float moved = MathF.Abs(corrected.X - aligned.X) + MathF.Abs(corrected.Y - aligned.Y);
            if (correctedConfidence >= MinDriftCorrectionConfidence && moved / _levelScale <= MaxDriftCorrection)
            {
                aligned = corrected;
                confidence = MathF.Max(confidence, correctedConfidence);
            }
        }

        var proposed = new TrackPose(aligned.X / _levelScale, aligned.Y / _levelScale, ToRotation(aligned), aligned.Scale);
        if (VisibleShare(current, image, aligned) < 0.5f)
            return new TrackResult(proposed, confidence, TrackStatus.OutOfFrame);

        if (confidence < _options.MinConfidence)
            return new TrackResult(proposed, confidence, TrackStatus.LowConfidence);

        // A sudden drop means something else is in the pattern, such as an occluder sliding over it.
        bool steady = confidence >= _typicalConfidence - MaxConfidenceDrop;
        float travelled = MathF.Abs(proposed.X - _x) + MathF.Abs(proposed.Y - _y);
        float jump = MathF.Max(MaxJumpSpeeds * _typicalSpeed, MinJumpShare * Math.Max(_options.PatternHalfWidth, _options.PatternHalfHeight));
        if (!steady && travelled > jump)
        {
            // The match got worse and landed far from where the pattern was heading: it is somewhere that merely looks alike.
            return new TrackResult(proposed, confidence, TrackStatus.LowConfidence);
        }

        _typicalSpeed += (travelled - _typicalSpeed) * SpeedSmoothing;
        _velocityX = _velocityX * VelocitySmoothing + (proposed.X - _x) * (1 - VelocitySmoothing);
        _velocityY = _velocityY * VelocitySmoothing + (proposed.Y - _y) * (1 - VelocitySmoothing);
        _x = proposed.X;
        _y = proposed.Y;
        _rotation = proposed.Rotation;
        _c = aligned.C;
        _d = aligned.D;

        // Learning the look of an occluder would make the tracker follow its edge, so the templates wait until the
        // match is steady again.
        _typicalConfidence += (confidence - _typicalConfidence) * ConfidenceSmoothing;
        if (steady)
        {
            if (_options.Template == TrackerTemplateMode.Adaptive && confidence >= MinUpdateConfidence)
            {
                if (ReferenceEquals(_current, _reference))
                    _current = _reference!.Copy();
                _current!.Blend(image, aligned, TemplateBlend);
                _current.Prepare();
            }

            CaptureCoarse(frame);
        }

        return new TrackResult(proposed, confidence, TrackStatus.Tracked);
    }

    /// <summary>
    /// How distinct a pattern is, as the smallest eigenvalue of its gradient matrix per pixel. Small values mean it
    /// looks the same when moved along some direction: a flat area, or the straight edge of something.
    /// </summary>
    public static float MeasureFeature(GrayImage image, float x, float y, int halfWidth, int halfHeight)
    {
        double xx = 0, xy = 0, yy = 0;
        int count = 0;
        for (int v = -halfHeight; v <= halfHeight; v++)
        {
            for (int u = -halfWidth; u <= halfWidth; u++)
            {
                float gx = (image.Sample(x + u + 1, y + v) - image.Sample(x + u - 1, y + v)) / 2;
                float gy = (image.Sample(x + u, y + v + 1) - image.Sample(x + u, y + v - 1)) / 2;
                xx += gx * gx;
                xy += gx * gy;
                yy += gy * gy;
                count++;
            }
        }

        double trace = xx + yy;
        double difference = xx - yy;
        double smallest = (trace - Math.Sqrt(difference * difference + 4 * xy * xy)) / 2;
        return (float)(smallest / count);
    }

    private float ToRotation(in Alignment alignment)
    {
        if (_parameterCount == 2)
            return _rotation;

        // The turn since the last pose, added on, so that the angle keeps counting past a half turn.
        float re = alignment.C * _c + alignment.D * _d;
        float im = alignment.D * _c - alignment.C * _d;
        return _rotation + MathF.Atan2(im, re);
    }

    private void Capture(Patch patch, ImagePyramid frame)
    {
        patch.Capture(frame[_alignmentLevel], new Alignment(_x * _levelScale, _y * _levelScale, _c, _d));
    }

    // The search compares frame to frame, so the template is the upright look of the pattern in the last frame.
    private void CaptureCoarse(ImagePyramid frame)
    {
        float scale = MathF.Sqrt(_c * _c + _d * _d);
        float halfWidth = _options.PatternHalfWidth * scale;
        float halfHeight = _options.PatternHalfHeight * scale;
        for (int level = _alignmentLevel; level < frame.LevelCount; level++)
        {
            float factor = 1f / (1 << level);
            int hw = (int)MathF.Round(halfWidth * factor);
            int hh = (int)MathF.Round(halfHeight * factor);
            if (level > _alignmentLevel && Math.Min(hw, hh) < MinCoarseHalfSize)
            {
                for (int rest = level; rest < frame.LevelCount; rest++)
                    _coarse[rest] = null;
                break;
            }

            hw = Math.Max(hw, 2);
            hh = Math.Max(hh, 2);
            CoarseTemplate? template = _coarse[level];
            if (template == null || template.HalfWidth != hw || template.HalfHeight != hh)
                _coarse[level] = template = new CoarseTemplate(hw, hh);
            template.Capture(frame[level], _x * factor, _y * factor);
        }
    }

    // Finds the area most like the template around a predicted position, on the alignment level's pixel grid.
    private (float X, float Y, float Score) Search(ImagePyramid frame, float predictedX, float predictedY)
    {
        float marginX = MathF.Max(_options.SearchHalfWidth - _options.PatternHalfWidth, MinSearchMargin);
        float marginY = MathF.Max(_options.SearchHalfHeight - _options.PatternHalfHeight, MinSearchMargin);

        // Start at the coarsest level that still holds the pattern and leaves something to search.
        int top = _alignmentLevel;
        for (int level = _alignmentLevel + 1; level < frame.LevelCount; level++)
        {
            if (_coarse[level] is not { IsFlat: false })
                break;
            top = level;
            if (MathF.Max(marginX, marginY) / (1 << level) < 2)
                break;
        }

        while (top > _alignmentLevel && _coarse[top] is not { IsFlat: false })
            top--;

        CoarseTemplate? template = _coarse[top];
        if (template == null || template.IsFlat)
            return (predictedX, predictedY, 0);

        float factor = 1f / (1 << top);
        int centerX = (int)MathF.Round(predictedX * factor);
        int centerY = (int)MathF.Round(predictedY * factor);
        int radiusX = (int)MathF.Ceiling(marginX * factor);
        int radiusY = (int)MathF.Ceiling(marginY * factor);
        Span<Candidate> candidates = stackalloc Candidate[MaxCandidates];
        int count = FindCandidates(frame[top], template, centerX, centerY, radiusX, radiusY, candidates);

        float alignmentScale = _levelScale;
        float bestScore = float.NegativeInfinity;
        float bestX = predictedX;
        float bestY = predictedY;
        float bestDistance = float.PositiveInfinity;
        for (int i = 0; i < count; i++)
        {
            Candidate candidate = candidates[i];
            for (int level = top - 1; level >= _alignmentLevel; level--)
            {
                candidate = Refine(frame[level], _coarse[level]!, candidate.X * 2, candidate.Y * 2);
            }

            float x = candidate.X / alignmentScale;
            float y = candidate.Y / alignmentScale;
            float distance = MathF.Abs(x - predictedX) + MathF.Abs(y - predictedY);

            // On a repeating texture several places match about as well: the one the motion predicts is the right one.
            bool clearlyBetter = candidate.Score > bestScore + 0.02f;
            bool asGoodAndCloser = candidate.Score > bestScore - 0.02f && distance < bestDistance;
            if (clearlyBetter || asGoodAndCloser)
            {
                bestScore = MathF.Max(candidate.Score, bestScore);
                bestX = x;
                bestY = y;
                bestDistance = distance;
            }
        }

        return (bestX, bestY, bestScore);
    }

    private static int FindCandidates(
        GrayImage image, CoarseTemplate template, int centerX, int centerY, int radiusX, int radiusY, Span<Candidate> candidates)
    {
        int width = radiusX * 2 + 1;
        int height = radiusY * 2 + 1;
        var scores = new float[width * height];
        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++)
                scores[j * width + i] = template.Correlate(image, centerX + i - radiusX, centerY + j - radiusY);
        }

        int count = 0;
        for (int j = 0; j < height; j++)
        {
            for (int i = 0; i < width; i++)
            {
                float score = scores[j * width + i];
                if (!IsLocalMaximum(scores, width, height, i, j, score))
                    continue;

                int position = count;
                while (position > 0 && candidates[position - 1].Score < score)
                    position--;
                if (position >= candidates.Length)
                    continue;

                for (int k = Math.Min(count, candidates.Length - 1); k > position; k--)
                    candidates[k] = candidates[k - 1];
                candidates[position] = new Candidate(centerX + i - radiusX, centerY + j - radiusY, score);
                count = Math.Min(count + 1, candidates.Length);
            }
        }

        if (count == 0)
            candidates[count++] = new Candidate(centerX, centerY, 0);
        return count;
    }

    private static bool IsLocalMaximum(float[] scores, int width, int height, int i, int j, float score)
    {
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int x = i + dx;
                int y = j + dy;
                if ((dx != 0 || dy != 0) && x >= 0 && x < width && y >= 0 && y < height && scores[y * width + x] > score)
                    return false;
            }
        }

        return true;
    }

    private static Candidate Refine(GrayImage image, CoarseTemplate template, int centerX, int centerY)
    {
        var best = new Candidate(centerX, centerY, float.NegativeInfinity);
        for (int dy = -2; dy <= 2; dy++)
        {
            for (int dx = -2; dx <= 2; dx++)
            {
                float score = template.Correlate(image, centerX + dx, centerY + dy);
                if (score > best.Score)
                    best = new Candidate(centerX + dx, centerY + dy, score);
            }
        }

        return best;
    }

    // Inverse compositional Lucas-Kanade. Returns the similarity to the template at the final pose.
    private float Align(Patch patch, GrayImage image, ref Alignment alignment)
    {
        if (!patch.CanAlign)
            return Evaluate(patch, image, alignment);

        Span<double> update = stackalloc double[_parameterCount];
        Alignment start = alignment;
        for (int iteration = 0; iteration < MaxIterations; iteration++)
        {
            if (!patch.ComputeUpdate(image, alignment, _warped, update))
                break;

            float tx = (float)update[0];
            float ty = (float)update[1];
            float a = 0;
            float b = 0;
            if (_parameterCount == 4)
            {
                a = (float)update[2] / patch.Radius;
                b = (float)update[3] / patch.Radius;
            }

            // Compose the current warp with the inverse of the update, in complex numbers: z' = z / (1 + a + bi).
            float denominator = (1 + a) * (1 + a) + b * b;
            float c = (alignment.C * (1 + a) + alignment.D * b) / denominator;
            float d = (alignment.D * (1 + a) - alignment.C * b) / denominator;
            float x = alignment.X - (c * tx - d * ty);
            float y = alignment.Y - (d * tx + c * ty);
            alignment = new Alignment(x, y, c, d);

            // An alignment that runs away has met something other than the pattern, such as an occluder.
            float scale = alignment.Scale;
            if (!float.IsFinite(scale) || scale is < 0.2f or > 5f
                || !float.IsFinite(x) || !float.IsFinite(y)
                || MathF.Abs(x - start.X) + MathF.Abs(y - start.Y) > patch.Radius * 2)
            {
                alignment = start;
                break;
            }

            if (MathF.Abs(tx) + MathF.Abs(ty) + (MathF.Abs(a) + MathF.Abs(b)) * patch.Radius < ConvergedStep)
                break;
        }

        return Evaluate(patch, image, alignment);
    }

    private float Evaluate(Patch patch, GrayImage image, in Alignment alignment) => patch.Similarity(image, alignment, _warped);

    private static float VisibleShare(Patch patch, GrayImage image, in Alignment alignment) => patch.VisibleShare(image, alignment);

    private readonly record struct Candidate(int X, int Y, float Score);

    /// <summary>Where the pattern lies on the alignment level: its centre, and its rotation and scale as c + di.</summary>
    internal readonly record struct Alignment(float X, float Y, float C, float D)
    {
        public float Scale => MathF.Sqrt(C * C + D * D);

        public float MapX(int u, int v) => X + C * u - D * v;

        public float MapY(int u, int v) => Y + D * u + C * v;
    }

    /// <summary>A zero-mean template for the correlation search. Samples outside the frame are NaN and take no part.</summary>
    private sealed class CoarseTemplate(int halfWidth, int halfHeight)
    {
        private readonly float[] _values = new float[(halfWidth * 2 + 1) * (halfHeight * 2 + 1)];
        private float _norm;
        private bool _hasGaps;

        public int HalfWidth { get; } = halfWidth;

        public int HalfHeight { get; } = halfHeight;

        /// <summary>Whether the template holds so little contrast that correlating with it says nothing.</summary>
        public bool IsFlat => _norm < 1e-3f;

        public void Capture(GrayImage image, float x, float y)
        {
            float sum = 0;
            int valid = 0;
            int i = 0;
            for (int v = -HalfHeight; v <= HalfHeight; v++)
            {
                for (int u = -HalfWidth; u <= HalfWidth; u++)
                {
                    float px = x + u;
                    float py = y + v;
                    if (IsInside(image, px, py))
                    {
                        float value = image.Sample(px, py);
                        _values[i] = value;
                        sum += value;
                        valid++;
                    }
                    else
                    {
                        _values[i] = float.NaN;
                    }

                    i++;
                }
            }

            _hasGaps = valid < _values.Length;
            float mean = sum / Math.Max(valid, 1);
            float energy = 0;
            for (i = 0; i < _values.Length; i++)
            {
                if (float.IsNaN(_values[i]))
                    continue;
                _values[i] -= mean;
                energy += _values[i] * _values[i];
            }

            // Per pixel, so that a template only counts as flat by its contrast and not by its size.
            _norm = valid == 0 || energy / valid < 0.25f ? 0 : MathF.Sqrt(energy);
        }

        // Normalized cross-correlation with the area of the image centred on (x, y), from -1 to 1.
        public float Correlate(GrayImage image, int x, int y)
        {
            int left = x - HalfWidth;
            int right = x + HalfWidth;
            int top = y - HalfHeight;
            int bottom = y + HalfHeight;
            if (_hasGaps || left < 0 || top < 0 || right >= image.Width || bottom >= image.Height)
                return CorrelateWithGaps(image, left, right, top, bottom);

            float[] template = _values;
            float[] pixels = image.Pixels;
            double sum = 0, squares = 0, product = 0;
            int i = 0;
            for (int row = top; row <= bottom; row++)
            {
                int offset = row * image.Width;
                for (int column = left; column <= right; column++)
                {
                    float value = pixels[offset + column];
                    sum += value;
                    squares += value * value;
                    product += value * template[i++];
                }
            }

            double variance = squares - sum * sum / template.Length;
            return variance < 1e-3 ? 0 : (float)(product / (_norm * Math.Sqrt(variance)));
        }

        private float CorrelateWithGaps(GrayImage image, int left, int right, int top, int bottom)
        {
            float[] template = _values;
            float[] pixels = image.Pixels;
            double count = 0, sumI = 0, sumT = 0, squaresI = 0, squaresT = 0, product = 0;
            int i = 0;
            for (int row = top; row <= bottom; row++)
            {
                for (int column = left; column <= right; column++)
                {
                    float t = template[i++];
                    if (float.IsNaN(t) || row < 0 || row >= image.Height || column < 0 || column >= image.Width)
                        continue;

                    float value = pixels[row * image.Width + column];
                    count++;
                    sumI += value;
                    sumT += t;
                    squaresI += value * value;
                    squaresT += t * t;
                    product += value * t;
                }
            }

            // Too little overlap says nothing, however well it happens to match.
            if (count < template.Length / 3.0)
                return 0;

            double varianceI = squaresI - sumI * sumI / count;
            double varianceT = squaresT - sumT * sumT / count;
            if (varianceI < 1e-3 || varianceT < 1e-3)
                return 0;

            return (float)((product - sumI * sumT / count) / Math.Sqrt(varianceI * varianceT));
        }
    }

    private static bool IsInside(GrayImage image, float x, float y)
        => x >= 0 && y >= 0 && x <= image.Width - 1 && y <= image.Height - 1;

    /// <summary>
    /// The pattern on an upright grid, with what the alignment precomputes from it. The window fades towards the edges,
    /// so that the background behind an object moving across it counts for less than the object. Whatever lies outside
    /// the frame, in the template or in the frame it is compared with, takes no part, so that a pattern can be followed
    /// out of the picture until about half of it has gone.
    /// </summary>
    private sealed class Patch
    {
        private readonly int _halfWidth;
        private readonly int _halfHeight;
        private readonly int _parameterCount;
        private readonly int _stride;
        private readonly float[] _values;
        private readonly float[] _window;
        private readonly float[] _weights;
        private readonly float[] _steepest;
        private readonly double[] _inverseHessian;
        private readonly float _windowTotal;

        public Patch(int halfWidth, int halfHeight, int parameterCount)
        {
            _halfWidth = halfWidth;
            _halfHeight = halfHeight;
            _parameterCount = parameterCount;
            _stride = halfWidth * 2 + 3;
            Radius = Math.Max(halfWidth, halfHeight);
            Count = (halfWidth * 2 + 1) * (halfHeight * 2 + 1);
            _values = new float[_stride * (halfHeight * 2 + 3)];
            _window = new float[Count];
            _weights = new float[Count];
            _steepest = new float[Count * parameterCount];
            _inverseHessian = new double[parameterCount * parameterCount];
            float sigmaX = halfWidth * 0.75f;
            float sigmaY = halfHeight * 0.75f;
            int i = 0;
            float total = 0;
            for (int v = -halfHeight; v <= halfHeight; v++)
            {
                for (int u = -halfWidth; u <= halfWidth; u++)
                {
                    float nx = u / sigmaX;
                    float ny = v / sigmaY;
                    float weight = MathF.Exp(-0.5f * (nx * nx + ny * ny));
                    _window[i++] = weight;
                    total += weight;
                }
            }

            _windowTotal = total;
        }

        public int Count { get; }

        public float Radius { get; }

        /// <summary>Whether the pattern has enough contrast in two directions to align it.</summary>
        public bool CanAlign { get; private set; }

        public Patch Copy()
        {
            var copy = new Patch(_halfWidth, _halfHeight, _parameterCount);
            Array.Copy(_values, copy._values, _values.Length);
            copy.Prepare();
            return copy;
        }

        public void Capture(GrayImage image, in Alignment alignment)
        {
            int i = 0;
            for (int v = -_halfHeight - 1; v <= _halfHeight + 1; v++)
            {
                for (int u = -_halfWidth - 1; u <= _halfWidth + 1; u++)
                {
                    float x = alignment.MapX(u, v);
                    float y = alignment.MapY(u, v);
                    _values[i++] = IsInside(image, x, y) ? image.Sample(x, y) : float.NaN;
                }
            }
        }

        public void Blend(GrayImage image, in Alignment alignment, float amount)
        {
            int i = 0;
            for (int v = -_halfHeight - 1; v <= _halfHeight + 1; v++)
            {
                for (int u = -_halfWidth - 1; u <= _halfWidth + 1; u++)
                {
                    float x = alignment.MapX(u, v);
                    float y = alignment.MapY(u, v);
                    if (IsInside(image, x, y))
                    {
                        float value = image.Sample(x, y);
                        float old = _values[i];
                        _values[i] = float.IsNaN(old) ? value : old + (value - old) * amount;
                    }

                    i++;
                }
            }
        }

        public void Prepare()
        {
            int n = _parameterCount;
            Span<double> hessian = stackalloc double[n * n];
            hessian.Clear();
            Span<double> steepest = stackalloc double[n];
            double total = 0, sum = 0, squares = 0;
            int index = 0;
            for (int v = -_halfHeight; v <= _halfHeight; v++)
            {
                int row = (v + _halfHeight + 1) * _stride + 1;
                for (int u = -_halfWidth; u <= _halfWidth; u++, index++)
                {
                    int at = row + u + _halfWidth;
                    float left = _values[at - 1];
                    float right = _values[at + 1];
                    float above = _values[at - _stride];
                    float below = _values[at + _stride];
                    float value = _values[at];
                    if (float.IsNaN(left + right + above + below + value))
                    {
                        _weights[index] = 0;
                        for (int k = 0; k < n; k++)
                            _steepest[index * n + k] = 0;
                        continue;
                    }

                    float gx = (right - left) / 2;
                    float gy = (below - above) / 2;
                    float weight = _window[index];
                    _weights[index] = weight;
                    total += weight;
                    sum += weight * value;
                    squares += weight * value * value;
                    steepest[0] = gx;
                    steepest[1] = gy;
                    if (n == 4)
                    {
                        steepest[2] = (gx * u + gy * v) / Radius;
                        steepest[3] = (gy * u - gx * v) / Radius;
                    }

                    for (int k = 0; k < n; k++)
                    {
                        _steepest[index * n + k] = (float)steepest[k];
                        for (int l = k; l < n; l++)
                            hessian[k * n + l] += weight * steepest[k] * steepest[l];
                    }
                }
            }

            for (int k = 0; k < n; k++)
            {
                for (int l = 0; l < k; l++)
                    hessian[k * n + l] = hessian[l * n + k];
            }

            CanAlign = false;
            if (total <= 0)
                return;

            double mean = sum / total;
            double deviation = Math.Sqrt(Math.Max(squares / total - mean * mean, 0));

            // The translation must be determined in both directions.
            double xx = hessian[0], xy = hessian[1], yy = hessian[n + 1];
            double trace = xx + yy;
            double difference = xx - yy;
            double smallest = (trace - Math.Sqrt(difference * difference + 4 * xy * xy)) / 2;
            if (smallest / total > 0.05 && deviation > 0.5)
                CanAlign = PrepareInverse(hessian);
        }

        // Rotation and scale are only followed as far as the pattern says something about them that a shift could not
        // explain as well. A round dot looks the same however it turns, and a corner the same however it grows: there
        // the noise alone would decide, and the pose would wander off. So the part of the Hessian about them, with
        // what the translation can explain taken out (its Schur complement), is inverted only in the directions
        // it is sure about.
        private bool PrepareInverse(ReadOnlySpan<double> h)
        {
            double a = h[0], b = h[1], d = h[_parameterCount + 1];
            double determinant = a * d - b * b;
            if (Math.Abs(determinant) < 1e-9)
                return false;

            double a00 = d / determinant, a01 = -b / determinant, a11 = a / determinant;
            if (_parameterCount == 2)
            {
                _inverseHessian[0] = a00;
                _inverseHessian[1] = a01;
                _inverseHessian[2] = a01;
                _inverseHessian[3] = a11;
                return true;
            }

            // H = [A B; B' C]: A for the translation, C for rotation and scale.
            double b00 = h[2], b01 = h[3], b10 = h[6], b11 = h[7];
            double c00 = h[10], c01 = h[11], c11 = h[15];

            // A^-1 B
            double ab00 = a00 * b00 + a01 * b10, ab01 = a00 * b01 + a01 * b11;
            double ab10 = a01 * b00 + a11 * b10, ab11 = a01 * b01 + a11 * b11;

            // S = C - B' A^-1 B
            double s00 = c00 - (b00 * ab00 + b10 * ab10);
            double s01 = c01 - (b00 * ab01 + b10 * ab11);
            double s11 = c11 - (b01 * ab01 + b11 * ab11);

            // S^+ from its eigenvalues, leaving out the weak ones.
            double threshold = MinObservableShare * (a + d) / 2;
            double half = (s00 + s11) / 2;
            double root = Math.Sqrt((s00 - s11) * (s00 - s11) / 4 + s01 * s01);
            double l1 = half + root;
            double l2 = half - root;
            double v1x, v1y;
            if (Math.Abs(s01) > 1e-12)
            {
                v1x = l1 - s11;
                v1y = s01;
                double norm = Math.Sqrt(v1x * v1x + v1y * v1y);
                v1x /= norm;
                v1y /= norm;
            }
            else
            {
                (v1x, v1y) = s00 >= s11 ? (1.0, 0.0) : (0.0, 1.0);
            }

            double v2x = -v1y, v2y = v1x;
            double p00 = 0, p01 = 0, p11 = 0;
            if (l1 > threshold)
            {
                p00 += v1x * v1x / l1;
                p01 += v1x * v1y / l1;
                p11 += v1y * v1y / l1;
            }

            if (l2 > threshold)
            {
                p00 += v2x * v2x / l2;
                p01 += v2x * v2y / l2;
                p11 += v2y * v2y / l2;
            }

            // The whole inverse, from the block formula, with S^+ in the place of S^-1:
            // [A^-1 + A^-1 B S^+ B' A^-1, -A^-1 B S^+; -S^+ B' A^-1, S^+]
            double q00 = ab00 * p00 + ab01 * p01, q01 = ab00 * p01 + ab01 * p11;
            double q10 = ab10 * p00 + ab11 * p01, q11 = ab10 * p01 + ab11 * p11;
            _inverseHessian[0] = a00 + q00 * ab00 + q01 * ab01;
            _inverseHessian[1] = a01 + q00 * ab10 + q01 * ab11;
            _inverseHessian[4] = _inverseHessian[1];
            _inverseHessian[5] = a11 + q10 * ab10 + q11 * ab11;
            _inverseHessian[2] = -q00;
            _inverseHessian[3] = -q01;
            _inverseHessian[6] = -q10;
            _inverseHessian[7] = -q11;
            _inverseHessian[8] = -q00;
            _inverseHessian[9] = -q10;
            _inverseHessian[12] = -q01;
            _inverseHessian[13] = -q11;
            _inverseHessian[10] = p00;
            _inverseHessian[11] = p01;
            _inverseHessian[14] = p01;
            _inverseHessian[15] = p11;
            return true;
        }

        // One step of the alignment: the update that brings the warped image closer to the template.
        public bool ComputeUpdate(GrayImage image, in Alignment alignment, float[] warped, Span<double> update)
        {
            WarpStatistics statistics = Warp(image, alignment, warped);
            if (statistics.Total <= 0 || statistics.DeviationI < MinWarpDeviation)
                return false;

            int n = _parameterCount;
            Span<double> right = stackalloc double[n];
            right.Clear();
            float gain = MathF.Min(statistics.DeviationT / statistics.DeviationI, MaxGain);
            int index = 0;
            for (int v = -_halfHeight; v <= _halfHeight; v++)
            {
                int row = (v + _halfHeight + 1) * _stride + 1;
                for (int u = -_halfWidth; u <= _halfWidth; u++, index++)
                {
                    float value = warped[index];
                    if (float.IsNaN(value))
                        continue;

                    float error = (value - statistics.MeanI) * gain + statistics.MeanT - _values[row + u + _halfWidth];
                    float weighted = _weights[index] * error;
                    for (int k = 0; k < n; k++)
                        right[k] += weighted * _steepest[index * n + k];
                }
            }

            for (int k = 0; k < n; k++)
            {
                double value = 0;
                for (int l = 0; l < n; l++)
                    value += _inverseHessian[k * n + l] * right[l];
                update[k] = value;
            }

            return true;
        }

        // Weighted normalized cross-correlation of the warped image with the template, clamped to 0..1.
        public float Similarity(GrayImage image, in Alignment alignment, float[] warped)
        {
            WarpStatistics statistics = Warp(image, alignment, warped);
            if (statistics.Total <= 0 || statistics.DeviationI < MinWarpDeviation || statistics.DeviationT < MinWarpDeviation)
                return 0;

            return Math.Clamp(statistics.Covariance / (statistics.DeviationI * statistics.DeviationT), 0, 1);
        }

        public float VisibleShare(GrayImage image, in Alignment alignment)
        {
            float visible = 0;
            int index = 0;
            for (int v = -_halfHeight; v <= _halfHeight; v++)
            {
                for (int u = -_halfWidth; u <= _halfWidth; u++, index++)
                {
                    if (IsInside(image, alignment.MapX(u, v), alignment.MapY(u, v)))
                        visible += _window[index];
                }
            }

            return visible / _windowTotal;
        }

        // Samples the image under the template, leaving NaN where either of them has nothing.
        private WarpStatistics Warp(GrayImage image, in Alignment alignment, float[] warped)
        {
            double total = 0, sumI = 0, sumT = 0, squaresI = 0, squaresT = 0, product = 0;
            int index = 0;
            for (int v = -_halfHeight; v <= _halfHeight; v++)
            {
                int row = (v + _halfHeight + 1) * _stride + 1;
                for (int u = -_halfWidth; u <= _halfWidth; u++, index++)
                {
                    float x = alignment.MapX(u, v);
                    float y = alignment.MapY(u, v);
                    float weight = _weights[index];
                    if (weight <= 0 || !IsInside(image, x, y))
                    {
                        warped[index] = float.NaN;
                        continue;
                    }

                    float value = image.Sample(x, y);
                    float template = _values[row + u + _halfWidth];
                    warped[index] = value;
                    total += weight;
                    sumI += weight * value;
                    sumT += weight * template;
                    squaresI += weight * value * value;
                    squaresT += weight * template * template;
                    product += weight * value * template;
                }
            }

            if (total <= 0)
                return default;

            double meanI = sumI / total;
            double meanT = sumT / total;
            return new WarpStatistics(
                (float)total,
                (float)meanI,
                (float)meanT,
                (float)Math.Sqrt(Math.Max(squaresI / total - meanI * meanI, 0)),
                (float)Math.Sqrt(Math.Max(squaresT / total - meanT * meanT, 0)),
                (float)(product / total - meanI * meanT));
        }

        private readonly record struct WarpStatistics(float Total, float MeanI, float MeanT, float DeviationI, float DeviationT, float Covariance);
    }
}
