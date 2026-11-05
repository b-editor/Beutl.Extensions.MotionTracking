namespace Beutl.Extensions.MotionTracking.Tracking;

/// <summary>A tracked pose in a frame and how well it matched. A pose placed by hand has a confidence of 1.</summary>
public readonly record struct TrackSample(TrackPose Pose, float Confidence);

/// <summary>A tracker's samples in frame order, by frame counted from the start of the source element.</summary>
public sealed class TrackSamples
{
    private readonly SortedList<long, TrackSample> _samples = [];

    public int Count => _samples.Count;

    public IList<long> Frames => _samples.Keys;

    public IList<TrackSample> Values => _samples.Values;

    public long? FirstFrame => _samples.Count > 0 ? _samples.Keys[0] : null;

    public long? LastFrame => _samples.Count > 0 ? _samples.Keys[^1] : null;

    public bool TryGet(long frame, out TrackSample sample) => _samples.TryGetValue(frame, out sample);

    public void Set(long frame, TrackSample sample) => _samples[frame] = sample;

    public void Clear() => _samples.Clear();

    /// <summary>Removes the samples between two frames, inclusive.</summary>
    public void RemoveRange(long first, long last)
    {
        for (int i = _samples.Count - 1; i >= 0; i--)
        {
            long frame = _samples.Keys[i];
            if (frame < first)
                break;
            if (frame <= last)
                _samples.RemoveAt(i);
        }
    }

    /// <summary>The sample nearest to a frame, or null when there are none.</summary>
    public TrackSample? Nearest(long frame)
    {
        if (_samples.Count == 0)
            return null;

        int index = LowerBound(frame);
        if (index >= _samples.Count)
            return _samples.Values[^1];
        if (index == 0 || _samples.Keys[index] == frame)
            return _samples.Values[index];

        long after = _samples.Keys[index] - frame;
        long before = frame - _samples.Keys[index - 1];
        return _samples.Values[before <= after ? index - 1 : index];
    }

    public IReadOnlyDictionary<long, TrackPose> ToPoses()
    {
        var poses = new Dictionary<long, TrackPose>(_samples.Count);
        foreach (KeyValuePair<long, TrackSample> pair in _samples)
            poses[pair.Key] = pair.Value.Pose;
        return poses;
    }

    private int LowerBound(long frame)
    {
        IList<long> keys = _samples.Keys;
        int low = 0;
        int high = keys.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (keys[middle] < frame)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }
}
