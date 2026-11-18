using System.Globalization;
using Beutl.Animation;
using Beutl.Composition;
using Beutl.Engine;
using Beutl.Extensions.MotionTracking.Properties;
using Beutl.Extensions.MotionTracking.Tracking;
using Beutl.Graphics;
using Beutl.Graphics.Transformation;
using Beutl.Media;

namespace Beutl.Extensions.MotionTracking.Editing;

/// <summary>
/// Writes motion to a drawable as ordinary keyframes, so that the result needs nothing from this extension to play
/// and can be edited like any other animation. The keyframes live in a transform group of their own at the head of
/// the drawable's transforms. As the first of the list it applies last, in scene space, after the transforms the
/// user placed the object with. Being nested, it is also not what the preview's handles pick up as the object's
/// translate, rotate and scale, so dragging the object afterwards adjusts its place relative to the track instead of
/// bending a few keyframes.
/// </summary>
internal static class TrackingKeyframes
{
    // Names are saved in projects. Recognize all shipped translations and the original Japanese name so that
    // reapplying or removing motion still finds the group after the editor's language changes.
    private const string LegacyGroupName = "モーショントラッキング";
    private static readonly HashSet<string> s_groupNames = new(
        new[] { "en", "ja", "zh-CN", "ko-KR", "es" }
            .Select(culture => Strings.ResourceManager.GetString(nameof(Strings.MotionTracking), CultureInfo.GetCultureInfo(culture))!)
            .Append(LegacyGroupName),
        StringComparer.Ordinal);

    // Below these a rotation or scale curve does nothing that could be seen.
    private const float IdentityRotation = 0.001f;
    private const float IdentityScale = 0.001f;

    /// <summary>The group an earlier apply left on the drawable, if any.</summary>
    public static TransformGroup? FindGroup(Drawable drawable)
        => drawable.Transform.Value is TransformGroup root ? FindGroup(root) : null;

    private static TransformGroup? FindGroup(TransformGroup root)
        => root.Children.OfType<TransformGroup>().FirstOrDefault(group => s_groupNames.Contains(group.Name.Value));

    /// <summary>Where the transforms of the drawable rotate and scale about, in scene coordinates.</summary>
    public static (double X, double Y) MeasurePivot(Drawable drawable, PixelSize frameSize, MediaTime time, FontManager fonts)
    {
        var canvas = new Size(frameSize.Width, frameSize.Height);
        if (drawable.AlignmentX.Value == AlignmentX.Center
            && drawable.AlignmentY.Value == AlignmentY.Center
            && drawable.TransformOrigin.Value == RelativePoint.Center
            && !IsDynamic(drawable.AlignmentX) && !IsDynamic(drawable.AlignmentY) && !IsDynamic(drawable.TransformOrigin))
        {
            // Whatever the object's size, its centre starts at the centre of the frame.
            return (canvas.Width / 2.0, canvas.Height / 2.0);
        }

        // Text is measured with the editor's fonts, as the renderer lays it out.
        var context = new CompositionContext(time) { TargetDomain = new Rect(default, canvas), Fonts = fonts };
        using var resource = (Drawable.Resource)drawable.ToResource(context);
        Size size = resource.Measure(canvas);
        Point origin = resource.TransformOrigin.ToPixels(size);
        return (Align(resource.AlignmentX, size.Width, canvas.Width) + origin.X,
            Align(resource.AlignmentY, size.Height, canvas.Height) + origin.Y);
    }

    private static bool IsDynamic(IExpressionProperty property) => property.Expression != null;

    private static double Align(AlignmentX alignment, float size, float canvas) => alignment switch
    {
        AlignmentX.Center => canvas / 2.0 - size / 2.0,
        AlignmentX.Right => canvas - size,
        _ => 0,
    };

    private static double Align(AlignmentY alignment, float size, float canvas) => alignment switch
    {
        AlignmentY.Center => canvas / 2.0 - size / 2.0,
        AlignmentY.Bottom => canvas - size,
        _ => 0,
    };

    /// <summary>Replaces the drawable's tracking keyframes. Returns the number of keyframes written.</summary>
    /// <param name="keys">The transform values by frame, with the key time of each.</param>
    /// <param name="tolerance">How far, in scene pixels, the simplified curves may be from the values.</param>
    public static int Write(
        Drawable drawable,
        IReadOnlyList<(TransformKey Key, MediaTime Time)> keys,
        MotionChannels channels,
        float tolerance)
    {
        var group = new TransformGroup { Name = { Value = Strings.MotionTracking } };
        int count = 0;
        if (channels.HasFlag(MotionChannels.Position))
        {
            var translate = new TranslateTransform();
            count += SetCurve(translate.X, keys, key => key.TranslateX, tolerance);
            count += SetCurve(translate.Y, keys, key => key.TranslateY, tolerance);
            group.Children.Add(translate);
        }

        // A tracker that follows only the position gives nothing to write here, however the options are set.
        if (channels.HasFlag(MotionChannels.Rotation) && keys.Any(k => MathF.Abs(k.Key.Rotation) > IdentityRotation))
        {
            var rotation = new RotationTransform();
            count += SetCurve(rotation.Rotation, keys, key => key.Rotation, tolerance * 0.1f);
            group.Children.Add(rotation);
        }

        if (channels.HasFlag(MotionChannels.Scale) && keys.Any(k => MathF.Abs(k.Key.ScalePercent - 100) > IdentityScale))
        {
            var scale = new ScaleTransform();
            count += SetCurve(scale.Scale, keys, key => key.ScalePercent, tolerance * 0.1f);
            group.Children.Add(scale);
        }

        if (drawable.Transform.Value is not TransformGroup root)
        {
            Transform? existing = drawable.Transform.Value;
            root = new TransformGroup();

            // The group takes the transform's place first: added while the drawable still owned it, the transform
            // would stay in the group's children but lose its parent when the drawable released it.
            drawable.Transform.Value = root;
            if (existing != null)
                root.Children.Add(existing);
        }

        // The group is built before it joins the drawable, so the history records one change however many keyframes it holds.
        TransformGroup? old = FindGroup(root);
        int index = old == null ? 0 : root.Children.IndexOf(old);
        if (old != null)
            root.Children.Remove(old);
        root.Children.Insert(index, group);
        return count;
    }

    /// <summary>Removes the drawable's tracking keyframes. Returns whether it had any.</summary>
    public static bool Remove(Drawable drawable)
    {
        if (drawable.Transform.Value is TransformGroup root && FindGroup(root) is { } group)
        {
            root.Children.Remove(group);
            return true;
        }

        return false;
    }

    private static int SetCurve(
        IProperty<float> property,
        IReadOnlyList<(TransformKey Key, MediaTime Time)> keys,
        Func<TransformKey, float> value,
        float tolerance)
    {
        long[] frames = keys.Select(k => k.Key.Frame).ToArray();
        float[] values = keys.Select(k => value(k.Key)).ToArray();
        int[] kept = KeyframeSimplifier.Simplify(frames, values, tolerance);
        var animation = new KeyFrameAnimation<float>();
        foreach (int i in kept)
        {
            animation.KeyFrames.Add(new KeyFrame<float>
            {
                Value = { Value = values[i] },
                KeyTime = { Value = keys[i].Time },
            });
        }

        property.SetKeyFrameAnimation(animation);
        return kept.Length;
    }
}
