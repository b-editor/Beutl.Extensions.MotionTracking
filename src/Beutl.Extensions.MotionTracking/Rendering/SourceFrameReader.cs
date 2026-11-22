using System.Text.Json.Nodes;
using Beutl.Composition;
using Beutl.Extensions.MotionTracking.Editing;
using Beutl.Extensions.MotionTracking.Properties;
using Beutl.Extensions.MotionTracking.Tracking;
using Beutl.Graphics;
using Beutl.Graphics.Rendering;
using Beutl.Graphics.Transformation;
using Beutl.Media;
using Beutl.Media.Decoding;
using Beutl.Models;
using Beutl.ProjectSystem;
using Beutl.Serialization;

namespace Beutl.Extensions.MotionTracking.Rendering;

/// <summary>What a reader is opened from: a copy of the scene taken on the thread that owns it.</summary>
internal sealed record SourceCapture(
    JsonObject Scene, Uri? Uri, Project Project, Guid SourceId, MediaTime Start, Rational Rate, FontManager Fonts, DecoderRegistry Decoders);

/// <summary>
/// Renders the frames of one element alone, the way it appears in the scene. It works on a detached copy of the
/// scene, so edits made during tracking cannot reach it, and objects placed on top of the video, such as the one
/// that is to follow the track, stay out of what the trackers see. Frames are counted from the start of the element.
/// </summary>
internal sealed class SourceFrameReader : ITrackingFrameSource, IDisposable
{
    private const long BackwardBufferBytes = 256 * 1024 * 1024;
    private const int MaxBackwardFrames = 32;
    private const int RetainedTargetReleaseInterval = 30;

    private readonly Scene _scene;
    private readonly MediaTime _start;
    private readonly Rational _rate;
    private readonly FontManager _fonts;
    private readonly DecoderRegistry _decoders;
    private readonly PixelSize _size;
    private SceneRenderer? _renderer;
    private int _renderedFrames;

    private SourceFrameReader(Scene scene, MediaTime start, Rational rate, FontManager fonts, DecoderRegistry decoders)
    {
        _scene = scene;
        _start = start;
        _rate = rate;
        _fonts = fonts;
        _decoders = decoders;
        _size = scene.FrameSize.Value;
    }

    public int BackwardBufferFrames
        => (int)Math.Clamp(BackwardBufferBytes / Math.Max(_size.Width * _size.Height * 4L, 1), 2, MaxBackwardFrames);

    /// <summary>Reads what the copy is made from. Call on the thread that owns the scene.</summary>
    public static SourceCapture Capture(Scene scene, Element source, FontManager fonts, DecoderRegistry decoders)
    {
        JsonObject json = CoreSerializer.SerializeToJsonObject(scene, new CoreSerializerOptions
        {
            BaseUri = scene.Uri,
            Mode = CoreSerializationMode.EmbedReferencedObjects,
        });

        // A project of its own gives the copy the same frame rate and settings, apart from the open project.
        var project = new Project();
        Rational rate = scene.FindHierarchicalParent<Project>().GetFrameRate();
        project.FrameRate.Value = rate;
        if (scene.FindHierarchicalParent<Project>() is { } original)
        {
            project.SampleRate.Value = original.SampleRate.Value;
            project.ChannelLayout.Value = original.ChannelLayout.Value;
            foreach ((string key, string value) in original.Variables.Value)
                project.Variables.Value[key] = value;
        }

        return new SourceCapture(json, scene.Uri, project, source.Id.Value, source.Start.Value, rate, fonts, decoders);
    }

    /// <summary>Builds the copy and a renderer for it. It takes over the capture, and can run on any thread.</summary>
    public static SourceFrameReader Open(SourceCapture capture)
    {
        if (capture.Uri is { } uri)
            capture.Scene["Uri"] = uri.ToString();

        var scene = (Scene)CoreSerializer.DeserializeFromJsonObject(capture.Scene, typeof(Scene), new CoreSerializerOptions
        {
            BaseUri = capture.Uri,
            Mode = CoreSerializationMode.Read | CoreSerializationMode.EmbedReferencedObjects,
            PreserveUnchangedValues = true,
        });
        scene.Uri ??= capture.Uri;
        capture.Project.Items.Add(scene);

        // An application of its own keeps the copy's expressions from reaching the open project.
        _ = new BeutlApplication { Project = { Value = capture.Project } };

        Isolate(scene, capture.SourceId);
        return new SourceFrameReader(scene, capture.Start, capture.Rate, capture.Fonts, capture.Decoders);
    }

    // Leaves only the source in the copy, fully visible and without the motion earlier tracking results applied to it.
    private static void Isolate(Scene scene, Guid sourceId)
    {
        bool found = false;
        foreach (Element element in scene.Children)
        {
            if (element.Id.Value != sourceId)
            {
                element.IsEnabled.Value = false;
                continue;
            }

            found = true;
            element.IsEnabled.Value = true;
            element.IsVisibleInOutput.Value = true;
            element.MatteMode.Value = MatteMode.None;
            element.EnterTransition.Value = null;
            element.ExitTransition.Value = null;
            foreach (Drawable drawable in element.Objects.OfType<Drawable>())
            {
                if (TrackingKeyframes.FindGroup(drawable) is { } group)
                    group.IsEnabled.Value = false;
            }
        }

        if (!found)
            throw new InvalidOperationException(Strings.SourceNotInScene);

        foreach (TimelineLayer layer in scene.Layers)
        {
            layer.IsVideoMuted.Value = false;
            layer.IsSolo.Value = false;
        }
    }

    public async ValueTask<ITrackingFrame> ReadAsync(long frame, CancellationToken cancellationToken)
    {
        MediaTime time = _start + MediaTime.FromFrames(frame, _rate);
        using Bitmap snapshot = await RenderThread.Dispatcher
            .InvokeAsync(() => Render(time, cancellationToken), ct: cancellationToken)
            .ConfigureAwait(false);

        // Luma of the gamma-encoded picture: differences in dark areas count for as much as they appear to.
        return new BitmapFrame(snapshot.Convert(BitmapColorType.Bgra8888, BitmapAlphaType.Premul, BitmapColorSpace.Srgb));
    }

    private Bitmap Render(MediaTime time, CancellationToken cancellationToken)
    {
        // Video opens only through the editor's decoders.
        _renderer ??= ExportRendererFactory.Create(_scene, _fonts, decoders: _decoders);
        CompositionFrame frame = _renderer.Compositor.EvaluateGraphics(time, _rate, cancellationToken);
        _renderer.Render(frame);
        Bitmap snapshot = _renderer.Snapshot();
        if (++_renderedFrames % RetainedTargetReleaseInterval == 0)
            _renderer.ReleaseRetainedRenderTargets();
        return snapshot;
    }

    public void Dispose()
    {
        SceneRenderer? renderer = _renderer;
        _renderer = null;
        if (renderer == null)
            return;

        // What it holds belongs to the render thread, and nothing here should wait for a frame to finish.
        if (RenderThread.Dispatcher.HasShutdownStarted)
            renderer.Dispose();
        else
            RenderThread.Dispatcher.Dispatch(renderer.Dispose);
    }

    private sealed class BitmapFrame(Bitmap bitmap) : ITrackingFrame
    {
        public int Width => bitmap.Width;

        public int Height => bitmap.Height;

        public void LoadInto(ImagePyramid pyramid) => pyramid.LoadBgra(bitmap.GetPixelSpan(), bitmap.RowBytes);

        public void CopyBgra(byte[] destination)
        {
            int rowBytes = bitmap.Width * 4;
            for (int y = 0; y < bitmap.Height; y++)
                bitmap.GetRow(y)[..rowBytes].CopyTo(destination.AsSpan(y * rowBytes, rowBytes));
        }

        public void Dispose() => bitmap.Dispose();
    }
}
