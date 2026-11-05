using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Beutl.Extensions.MotionTracking.Tracking;
using Reactive.Bindings;

namespace Beutl.Extensions.MotionTracking.Editing;

/// <summary>A tracker: the area it follows, how, and where it was found in each frame so far.</summary>
public sealed class TrackerViewModel
{
    public const int MinPatternHalfSize = 3;
    public const int MaxHalfSize = 2048;
    public const int MinSearchMargin = 4;

    public TrackerViewModel(string name, Color color, int patternHalfSize, TrackPose anchor)
    {
        Name.Value = name;
        Color = color;
        Brush = new ImmutableSolidColorBrush(color);
        PatternHalfWidth.Value = patternHalfSize;
        PatternHalfHeight.Value = patternHalfSize;
        SearchHalfWidth.Value = patternHalfSize * 3;
        SearchHalfHeight.Value = patternHalfSize * 3;
        Anchor = anchor;
    }

    public ReactivePropertySlim<string> Name { get; } = new("");

    public Color Color { get; }

    public IBrush Brush { get; }

    /// <summary>Whether the tracker takes part in tracking and in what is applied.</summary>
    public ReactivePropertySlim<bool> IsActive { get; } = new(true);

    public ReactivePropertySlim<int> PatternHalfWidth { get; } = new();

    public ReactivePropertySlim<int> PatternHalfHeight { get; } = new();

    public ReactivePropertySlim<int> SearchHalfWidth { get; } = new();

    public ReactivePropertySlim<int> SearchHalfHeight { get; } = new();

    public ReactivePropertySlim<TrackerMotionModel> Model { get; } = new(TrackerMotionModel.PositionRotationScale);

    public ReactivePropertySlim<TrackerTemplateMode> Template { get; } = new(TrackerTemplateMode.Adaptive);

    /// <summary>The similarity to the pattern below which tracking stops, from 0 to 1.</summary>
    public ReactivePropertySlim<float> MinConfidence { get; } = new(0.75f);

    /// <summary>Where the tracker is while it has no samples.</summary>
    public TrackPose Anchor { get; set; }

    public TrackSamples Samples { get; } = new();

    /// <summary>The number of samples, for showing in the list.</summary>
    public ReactivePropertySlim<int> SampleCount { get; } = new();

    public void NotifySamplesChanged() => SampleCount.Value = Samples.Count;

    /// <summary>The pose in a frame: its own, or the nearest one tracked, or where the tracker was placed.</summary>
    public TrackPose PoseAt(long frame)
    {
        if (Samples.TryGet(frame, out TrackSample sample))
            return sample.Pose;

        return Samples.Nearest(frame)?.Pose ?? Anchor;
    }

    public TrackerOptions ToOptions() => new()
    {
        PatternHalfWidth = PatternHalfWidth.Value,
        PatternHalfHeight = PatternHalfHeight.Value,
        SearchHalfWidth = SearchHalfWidth.Value,
        SearchHalfHeight = SearchHalfHeight.Value,
        Model = Model.Value,
        Template = Template.Value,
        MinConfidence = MinConfidence.Value,
    };

    public JsonObject ToJson()
    {
        const int size = sizeof(long) + 5 * sizeof(float);
        var bytes = new byte[Samples.Count * size];
        for (int i = 0; i < Samples.Count; i++)
        {
            Span<byte> span = bytes.AsSpan(i * size, size);
            TrackSample sample = Samples.Values[i];
            BinaryPrimitives.WriteInt64LittleEndian(span, Samples.Frames[i]);
            BinaryPrimitives.WriteSingleLittleEndian(span[8..], sample.Pose.X);
            BinaryPrimitives.WriteSingleLittleEndian(span[12..], sample.Pose.Y);
            BinaryPrimitives.WriteSingleLittleEndian(span[16..], sample.Pose.Rotation);
            BinaryPrimitives.WriteSingleLittleEndian(span[20..], sample.Pose.Scale);
            BinaryPrimitives.WriteSingleLittleEndian(span[24..], sample.Confidence);
        }

        return new JsonObject
        {
            ["Name"] = Name.Value,
            ["Color"] = Color.ToString(),
            ["IsActive"] = IsActive.Value,
            ["PatternHalfWidth"] = PatternHalfWidth.Value,
            ["PatternHalfHeight"] = PatternHalfHeight.Value,
            ["SearchHalfWidth"] = SearchHalfWidth.Value,
            ["SearchHalfHeight"] = SearchHalfHeight.Value,
            ["Model"] = Model.Value.ToString(),
            ["Template"] = Template.Value.ToString(),
            ["MinConfidence"] = MinConfidence.Value,
            ["AnchorX"] = Anchor.X,
            ["AnchorY"] = Anchor.Y,
            ["Samples"] = Convert.ToBase64String(bytes),
        };
    }

    /// <summary>Restores a tracker, or returns null when the json does not describe one.</summary>
    /// <param name="convertFrame">Converts a frame number from the rate it was saved in to the rate of the project now.</param>
    public static TrackerViewModel? FromJson(JsonObject json, Func<long, long> convertFrame)
    {
        if (!TryGet(json, "Name", out string? name)
            || !TryGet(json, "Color", out string? colorText) || !Color.TryParse(colorText, out Color color))
        {
            return null;
        }

        TryGet(json, "AnchorX", out float anchorX);
        TryGet(json, "AnchorY", out float anchorY);
        var tracker = new TrackerViewModel(name, color, MinPatternHalfSize, new TrackPose(anchorX, anchorY));
        if (TryGet(json, "IsActive", out bool isActive))
            tracker.IsActive.Value = isActive;
        if (TryGet(json, "Model", out string? model) && Enum.TryParse(model, out TrackerMotionModel parsedModel))
            tracker.Model.Value = parsedModel;
        if (TryGet(json, "Template", out string? template) && Enum.TryParse(template, out TrackerTemplateMode parsedTemplate))
            tracker.Template.Value = parsedTemplate;
        if (TryGet(json, "MinConfidence", out float confidence) && confidence is >= 0 and <= 1)
            tracker.MinConfidence.Value = confidence;

        RestoreSize(json, "PatternHalfWidth", tracker.PatternHalfWidth);
        RestoreSize(json, "PatternHalfHeight", tracker.PatternHalfHeight);
        RestoreSize(json, "SearchHalfWidth", tracker.SearchHalfWidth);
        RestoreSize(json, "SearchHalfHeight", tracker.SearchHalfHeight);

        if (TryGet(json, "Samples", out string? samples))
        {
            try
            {
                const int size = sizeof(long) + 5 * sizeof(float);
                byte[] bytes = Convert.FromBase64String(samples);
                for (int i = 0; i + size <= bytes.Length; i += size)
                {
                    ReadOnlySpan<byte> span = bytes.AsSpan(i, size);
                    var pose = new TrackPose(
                        BinaryPrimitives.ReadSingleLittleEndian(span[8..]),
                        BinaryPrimitives.ReadSingleLittleEndian(span[12..]),
                        BinaryPrimitives.ReadSingleLittleEndian(span[16..]),
                        BinaryPrimitives.ReadSingleLittleEndian(span[20..]));
                    tracker.Samples.Set(
                        convertFrame(BinaryPrimitives.ReadInt64LittleEndian(span)),
                        new TrackSample(pose, BinaryPrimitives.ReadSingleLittleEndian(span[24..])));
                }
            }
            catch (FormatException)
            {
                // Damaged samples are lost, not the tracker.
            }
        }

        tracker.NotifySamplesChanged();
        return tracker;
    }

    private static void RestoreSize(JsonObject json, string key, ReactivePropertySlim<int> property)
    {
        if (TryGet(json, key, out int value) && value is >= MinPatternHalfSize and <= MaxHalfSize)
            property.Value = value;
    }

    private static bool TryGet<T>(JsonObject json, string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? value)
    {
        value = default;
        return json.TryGetPropertyValue(key, out JsonNode? node)
               && node is JsonValue jsonValue
               && jsonValue.TryGetValue(out value)
               && value != null;
    }
}
