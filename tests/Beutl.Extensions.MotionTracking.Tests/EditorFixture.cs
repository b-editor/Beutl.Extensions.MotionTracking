using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Beutl.Animation;
using Beutl.Collections;
using Beutl.Composition;
using Beutl.Configuration;
using Beutl.Editor;
using Beutl.Editor.Observers;
using Beutl.Editor.Services;
using Beutl.Engine;
using Beutl.Extensibility;
using Beutl.Extensions.MotionTracking.Editing;
using Beutl.Graphics;
using Beutl.Graphics.Backend;
using Beutl.Graphics.Rendering;
using Beutl.Graphics.Shapes;
using Beutl.Graphics.Transformation;
using Beutl.Media;
using Beutl.Media.Decoding;
using Beutl.Media.Source;
using Beutl.ProjectSystem;
using FluentAvalonia.UI.Controls;
using Reactive.Bindings;

namespace Beutl.Extensions.MotionTracking.Tests;

/// <summary>A scene with the editor services the tool tab reads, wired like the editor so that edits reach the history.</summary>
internal sealed class EditorFixture : IDisposable
{
    public const int Width = 640;
    public const int Height = 360;
    public const double FrameSeconds = 1.0 / 30;

    private static readonly Lazy<string> s_texture = new(() =>
    {
        string path = Path.Combine(Path.GetTempPath(), $"motion-tracking-texture-{Guid.NewGuid():N}.png");
        new SyntheticVideo().SaveTexture(path);
        return path;
    });

    // The installed fonts, read from the folders Beutl's font settings list by default, as the editor provides them.
    private static readonly Lazy<FontManager> s_fonts = new(() => new FontManager(new FontConfig()));

    // The decoders Beutl brings itself, which open the animated pictures that stand in for video here.
    private static readonly Lazy<DecoderRegistry> s_decoders = new(() =>
    {
        var decoders = new DecoderRegistry(new ExtensionConfig());
        BuiltInDecoders.Register(decoders);
        return decoders;
    });

    private readonly BeutlApplication _application;
    private readonly Dictionary<Type, object> _services = [];
    private readonly CoreObjectOperationObserver _observer;
    private readonly IDisposable _historySubscription;
    private readonly List<MotionTrackingTabViewModel> _tabs = [];
    private readonly List<string> _files = [];

    public EditorFixture()
    {
        Scene = new Scene(Width, Height, "Fixture");

        // Rooted like an open project: animations only count from the start of their element inside a hierarchy.
        var project = new Project { FrameRate = { Value = new Rational(30) } };
        project.Items.Add(Scene);
        _application = new BeutlApplication { Project = { Value = project } };
        var sequence = new OperationSequenceGenerator();
        _observer = new CoreObjectOperationObserver(null, Scene, sequence);
        History = new HistoryManager(sequence);
        _historySubscription = History.Subscribe(_observer);
        _services[typeof(IEditorSelection)] = Selection;
        _services[typeof(IEditorClock)] = Clock;
        _services[typeof(HistoryManager)] = History;
        _services[typeof(Scene)] = Scene;
        _services[typeof(FontManager)] = Fonts;
        _services[typeof(DecoderRegistry)] = Decoders;
        Context = new FakeEditorContext(Scene, this);
    }

    public static FontManager Fonts => s_fonts.Value;

    public static DecoderRegistry Decoders => s_decoders.Value;

    public Scene Scene { get; }

    public HistoryManager History { get; }

    public FakeSelection Selection { get; } = new();

    public FakeClock Clock { get; } = new();

    public FakeEditorContext Context { get; }

    public object? GetService(Type type) => _services.GetValueOrDefault(type);

    public static MediaTime Frame(long frame) => MediaTime.FromFrames(frame, new Rational(30));

    public Element Add(EngineObject obj, MediaTime start, MediaTime length, int layer)
    {
        var element = new Element { Start = { Value = start }, Length = { Value = length }, ZIndex = { Value = layer } };
        element.AddObject(obj);
        Scene.AddChild(element);
        return element;
    }

    /// <summary>
    /// A detailed picture filling the frame, which moves in a straight line. A point at (x, y) in the first frame is at
    /// (x + speedX * frame, y + speedY * frame) later on.
    /// </summary>
    public Element AddMovingPicture(float speedX, float speedY, int frames, long startFrame = 0, int layer = 0)
    {
        var translate = new TranslateTransform();
        translate.X.SetKeyFrameAnimation(Line(0, speedX * frames, frames));
        translate.Y.SetKeyFrameAnimation(Line(0, speedY * frames, frames));
        SourceImage picture = Picture();
        ((TransformGroup)picture.Transform.Value!).Children.Add(translate);
        return Add(picture, Frame(startFrame), Frame(frames + 1), layer);
    }

    /// <summary>The same picture turning and growing about the middle of the frame, by these amounts per frame.</summary>
    public Element AddTurningPicture(float degrees, float scalePercent, int frames, int layer = 0)
    {
        var rotation = new RotationTransform();
        rotation.Rotation.SetKeyFrameAnimation(Line(0, degrees * frames, frames));
        var scale = new ScaleTransform();
        scale.Scale.SetKeyFrameAnimation(Line(100, 100 + scalePercent * frames, frames));
        SourceImage picture = Picture();
        var group = (TransformGroup)picture.Transform.Value!;
        group.Children.Add(rotation);
        group.Children.Add(scale);
        return Add(picture, Frame(0), Frame(frames + 1), layer);
    }

    // The picture moving by whole pixels in a video element, which only opens through the editor's decoders.
    public Element AddMovingVideo(int speedX, int speedY, int frames, int layer = 0)
    {
        string path = Path.Combine(Path.GetTempPath(), $"motion-tracking-video-{Guid.NewGuid():N}.apng");
        _files.Add(path);
        new SyntheticVideo(Width, Height).SaveMovingVideo(path, speedX, speedY, frames);
        var source = new VideoSource();
        source.ReadFrom(new Uri(path));
        var video = new SourceVideo();
        video.Source.Value = source;
        return Add(video, MediaTime.Zero, Frame(frames + 1), layer);
    }

    /// <summary>A small white rectangle in the middle of the frame, to be moved by what is tracked.</summary>
    public Element AddMarker(MediaTime length, int layer = 1, int size = 20)
    {
        var marker = new RectShape { Width = { Value = size }, Height = { Value = size } };
        marker.Fill.Value = new SolidColorBrush(Colors.White);
        return Add(marker, MediaTime.Zero, length, layer);
    }

    private static SourceImage Picture()
    {
        var image = new ImageSource();
        image.ReadFrom(new Uri(s_texture.Value));
        var picture = new SourceImage();
        picture.Source.Value = image;
        return picture;
    }

    private static KeyFrameAnimation<float> Line(float from, float to, int frames)
    {
        var animation = new KeyFrameAnimation<float>();
        animation.KeyFrames.Add(new KeyFrame<float> { Value = { Value = from }, KeyTime = { Value = MediaTime.Zero } });
        animation.KeyFrames.Add(new KeyFrame<float> { Value = { Value = to }, KeyTime = { Value = Frame(frames) } });
        return animation;
    }

    /// <summary>Opens the tab on a source element, showing frames like a selected tab does.</summary>
    public MotionTrackingTabViewModel OpenTab(Element? source = null)
    {
        var tab = new MotionTrackingTabViewModel(new MotionTrackingTabExtension(), Context);
        tab.IsSelected.Value = true;
        if (source != null)
            tab.SetSource(source);
        _tabs.Add(tab);
        return tab;
    }

    public void Dispose()
    {
        foreach (MotionTrackingTabViewModel tab in _tabs)
            tab.Dispose();
        _historySubscription.Dispose();
        History.Dispose();
        _observer.Dispose();
        _application.Project.Value = null;

        // The decoder reads an animated PNG whole when it opens it, so nothing holds the files any more.
        foreach (string file in _files)
            File.Delete(file);
    }
}

internal sealed class FakeSelection : IEditorSelection
{
    private readonly ReactivePropertySlim<CoreObject?> _selectedObject = new();

    public IReadOnlyReactiveProperty<CoreObject?> SelectedObject => _selectedObject;

    public IReadOnlyReactiveProperty<int?> SelectedLayerNumber { get; } = new ReactivePropertySlim<int?>();

    public ICoreList<Element> SelectedElements { get; } = new CoreList<Element>();

    public void Select(CoreObject? obj, bool notifyIfSelected = false)
    {
        _selectedObject.Value = obj;
        if (notifyIfSelected)
            _selectedObject.ForceNotify();
        SelectedElements.Clear();
        if ((obj as Element ?? (obj as IHierarchical)?.FindHierarchicalParent<Element>()) is { } element)
            SelectedElements.Add(element);
    }
}

internal sealed class FakeClock : IEditorClock
{
    private readonly ReactivePropertySlim<MediaTime> _currentTime = new();

    public IReadOnlyReactiveProperty<MediaTime> CurrentTime => _currentTime;

    public IReadOnlyReactiveProperty<MediaTime> MaximumTime { get; } = new ReactivePropertySlim<MediaTime>(MediaTime.FromSeconds(300));

    public void Seek(MediaTime time) => _currentTime.Value = time;
}

internal sealed class FakeEditorContext(CoreObject obj, EditorFixture fixture) : IEditorContext
{
    public CoreObject Object => obj;

    public EditorExtension Extension { get; } = new FakeEditorExtension();

    public IReactiveProperty<bool> IsEnabled { get; } = new ReactivePropertySlim<bool>(true);

    public object? GetService(Type serviceType) => fixture.GetService(serviceType);

    public T? FindToolTab<T>(Func<T, bool> condition)
        where T : IToolContext => default;

    public T? FindToolTab<T>()
        where T : IToolContext => default;

    public bool OpenToolTab(IToolContext item) => true;

    public void CloseToolTab(IToolContext item)
    {
    }
}

internal sealed class FakeEditorExtension : EditorExtension
{
    public override string Name => "Test editor";

    public override string DisplayName => Name;

    public override FilePickerFileType GetFilePickerFileType() => throw new NotSupportedException();

    public override FAIconSource? GetIcon() => null;

    public override bool TryCreateEditor(CoreObject obj, out Control editor) => throw new NotSupportedException();

    public override bool TryCreateContext(CoreObject obj, IEditorContextServices services, out IEditorContext context) => throw new NotSupportedException();

    public override bool MatchFileExtension(string ext) => false;
}

/// <summary>Renders a scene through Beutl's own pipeline without starting the editor.</summary>
internal static class HeadlessScene
{
    private static readonly Lazy<bool> s_isAvailable = new(() =>
        RenderThread.Dispatcher.Invoke(() => GraphicsContextFactory.GetOrCreateShared() is not null));

    public static void EnsureAvailable()
    {
        if (!s_isAvailable.Value)
            Assert.Ignore("No graphics context is available on this host.");
    }

    /// <summary>Renders one frame and returns it as 8-bit sRGB BGRA.</summary>
    public static Bitmap Render(Scene scene, MediaTime time)
    {
        EnsureAvailable();
        return RenderThread.Dispatcher.Invoke(() =>
        {
            using var renderer = new SceneRenderer(scene, EditorFixture.Fonts, RenderIntent.Delivery, decoders: EditorFixture.Decoders);
            CompositionFrame frame = renderer.Compositor.EvaluateGraphics(time);
            renderer.Render(frame);
            using Bitmap snapshot = renderer.Snapshot();
            return snapshot.Convert(BitmapColorType.Bgra8888, BitmapAlphaType.Premul, BitmapColorSpace.Srgb);
        });
    }

    /// <summary>The middle of the bright pixels of a frame, or null when it has none.</summary>
    public static (double X, double Y)? FindBright(Bitmap frame, byte threshold = 128)
    {
        double sumX = 0, sumY = 0;
        long count = 0;
        for (int y = 0; y < frame.Height; y++)
        {
            ReadOnlySpan<byte> row = frame.GetRow(y);
            for (int x = 0; x < frame.Width; x++)
            {
                if (row[x * 4 + 1] >= threshold)
                {
                    sumX += x + 0.5;
                    sumY += y + 0.5;
                    count++;
                }
            }
        }

        return count == 0 ? null : (sumX / count, sumY / count);
    }

    /// <summary>The average difference in brightness between two frames over an area.</summary>
    public static double Difference(Bitmap a, Bitmap b, int left, int top, int width, int height)
    {
        double total = 0;
        for (int y = top; y < top + height; y++)
        {
            ReadOnlySpan<byte> rowA = a.GetRow(y);
            ReadOnlySpan<byte> rowB = b.GetRow(y);
            for (int x = left; x < left + width; x++)
                total += Math.Abs(rowA[x * 4 + 1] - rowB[x * 4 + 1]);
        }

        return total / (width * height);
    }
}
