using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Beutl.Editor;
using Beutl.Editor.Services;
using Beutl.Extensibility;
using Beutl.Extensions.MotionTracking.Rendering;
using Beutl.Extensions.MotionTracking.Tracking;
using Beutl.Graphics;
using Beutl.Logging;
using Beutl.Media;
using Beutl.ProjectSystem;
using Microsoft.Extensions.Logging;
using Reactive.Bindings;
using Color = Avalonia.Media.Color;
using PixelFormat = Avalonia.Platform.PixelFormat;

namespace Beutl.Extensions.MotionTracking.Editing;

/// <summary>Which samples to clear.</summary>
public enum ClearRange
{
    All,
    BeforeCurrentFrame,
    AfterCurrentFrame,
}

/// <summary>How many keyframes applying writes.</summary>
public enum KeyframeDensity
{
    /// <summary>A keyframe for every frame.</summary>
    EveryFrame,

    /// <summary>Only as many as it takes to stay within a tenth of a pixel.</summary>
    Precise,

    /// <summary>Few enough to edit by hand, staying within half a pixel.</summary>
    Few,
}

/// <summary>
/// The "モーショントラッキング" tool tab: trackers placed on the frames of one element, the tracking runs that follow
/// them through the video, and applying the result as keyframes. Everything happens on the UI thread except the
/// tracking run, whose results come back here in batches. Frames in this class are counted from the start of the
/// source element, so that moving the element does not move the tracks.
/// </summary>
public sealed class MotionTrackingTabViewModel : IToolContext
{
    private const string NoSourceMessage = "追跡する動画をタイムラインで選択してください";
    private const string MissingSourceMessage = "追跡元の要素が削除されました";
    private const string NoFrameHere = "再生ヘッドが追跡元の範囲の外にあります";

    // Measured on real footage: below the first, a tracker slides along an edge or loses a flat area at once.
    private const float WeakFeature = 1.5f;
    private const float GoodFeature = 8f;

    // How often the picture follows a running track. The samples are drawn as they come in.
    private static readonly TimeSpan s_displayInterval = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan s_playbackFrameInterval = TimeSpan.FromMilliseconds(100);

    private static readonly Color[] s_colors =
    [
        Color.FromRgb(255, 214, 64),
        Color.FromRgb(90, 200, 250),
        Color.FromRgb(255, 122, 168),
        Color.FromRgb(124, 227, 139),
        Color.FromRgb(255, 169, 77),
        Color.FromRgb(180, 157, 255),
    ];

    private readonly ILogger _logger = Log.CreateLogger<MotionTrackingTabViewModel>();
    private readonly IServiceProvider _services;
    private readonly Scene? _scene;
    private readonly IEditorSelection? _selection;
    private readonly IEditorClock? _clock;
    private readonly IPreviewPlayer? _player;
    private readonly HistoryManager? _history;
    private readonly IDisposable? _clockSubscription;
    private readonly IDisposable? _selectedSubscription;
    private readonly IDisposable? _trackerSubscription;
    private readonly CancellationTokenSource _lifetime = new();

    private Element? _boundSource;
    private Element? _boundTarget;
    private SourceFrameReader? _reader;
    private bool _readerStale = true;
    private long? _requestedFrame;
    private Task? _pump;
    private Task? _run;
    private CancellationTokenSource? _tracking;
    private int _trackerNumber;

    // The picture being shown: its pixels, and its luma for judging how good a spot is for a tracker.
    private byte[] _displayPixels = [];
    private GrayImage? _displayLuma;
    private int _displayState;
    private long? _shownFrame;
    private int _displayWidth;
    private int _displayHeight;
    private long _lastDisplayTicks;

    // Results of a running track on their way to the UI thread.
    private readonly ConcurrentQueue<(long Frame, TrackResult?[] Results)> _results = new();
    private TrackerViewModel[] _runningTrackers = [];
    private long _runFirstFrame;
    private long _runLastFrame;
    private int _flushPosted;

    public MotionTrackingTabViewModel(ToolTabExtension extension, IServiceProvider services)
    {
        Extension = extension;
        _services = services;
        _scene = services.GetService(typeof(Scene)) as Scene;
        _selection = services.GetService(typeof(IEditorSelection)) as IEditorSelection;
        _clock = services.GetService(typeof(IEditorClock)) as IEditorClock;
        _player = services.GetService(typeof(IPreviewPlayer)) as IPreviewPlayer;
        _history = services.GetService(typeof(HistoryManager)) as HistoryManager;
        if (_scene != null)
            _scene.Children.CollectionChanged += OnSceneChildrenChanged;

        _clockSubscription = _clock?.CurrentTime.Subscribe(_ => OnPlayheadMoved());
        _selectedSubscription = IsSelected.Subscribe(selected =>
        {
            if (selected)
                RequestFrame();
        });
        _trackerSubscription = SelectedTracker.Subscribe(_ =>
        {
            UpdateFeature();
            NotifyOverlay();
        });

        // Opening the tab with a video selected is the usual way in.
        if (SelectedElement() is { } selected && HasPicture(selected))
            SetSource(selected);
    }

    /// <summary>Raised when what the canvas draws over the picture changed.</summary>
    public event EventHandler? OverlayChanged;

    /// <summary>Raised when the picture changed.</summary>
    public event EventHandler? FrameChanged;

    public ToolTabExtension Extension { get; }

    public IReactiveProperty<bool> IsSelected { get; } = new ReactivePropertySlim<bool>();

    public IReadOnlyReactiveProperty<string> Header { get; } = new ReactivePropertySlim<string>("モーショントラッキング");

    /// <summary>The element being tracked, or null when there is none or it left the scene.</summary>
    public ReactivePropertySlim<Element?> Source { get; } = new();

    public ReactivePropertySlim<string> SourceName { get; } = new("");

    /// <summary>Why nothing can be tracked, or null while <see cref="Source"/> is set.</summary>
    public ReactivePropertySlim<string?> SourceMessage { get; } = new(NoSourceMessage);

    /// <summary>The element that follows the motion, or null when there is none or it left the scene.</summary>
    public ReactivePropertySlim<Element?> Target { get; } = new();

    public ReactivePropertySlim<string> TargetName { get; } = new("（未設定）");

    public ObservableCollection<TrackerViewModel> Trackers { get; } = [];

    public ReactivePropertySlim<TrackerViewModel?> SelectedTracker { get; } = new();

    /// <summary>The number of frames of the source element.</summary>
    public ReactivePropertySlim<long> FrameCount { get; } = new();

    /// <summary>The frame the playhead is on, or null when it is outside the source element.</summary>
    public ReactivePropertySlim<long?> CurrentFrame { get; } = new();

    /// <summary>The frame the overlay shows: the playhead's, or the one a running track has reached.</summary>
    public ReactivePropertySlim<long?> DisplayFrame { get; } = new();

    /// <summary>The picture of the source element. It is rewritten in place; <see cref="FrameChanged"/> says when.</summary>
    public WriteableBitmap? FrameImage { get; private set; }

    /// <summary>The size of the scene, which poses are in.</summary>
    public PixelSize SceneSize => _scene?.FrameSize.Value ?? new PixelSize(1920, 1080);

    public ReactivePropertySlim<bool> IsTracking { get; } = new();

    /// <summary>How far a running track has come, from 0 to 1.</summary>
    public ReactivePropertySlim<double> Progress { get; } = new();

    public ReactivePropertySlim<string?> Status { get; } = new();

    /// <summary>How good the spot under the selected tracker is for tracking, as words; null when not known.</summary>
    public ReactivePropertySlim<string?> FeatureStrength { get; } = new();

    /// <summary>What applying does with the motion.</summary>
    public ReactivePropertySlim<MotionUse> Use { get; } = new(MotionUse.Follow);

    public ReactivePropertySlim<bool> UsePosition { get; } = new(true);

    public ReactivePropertySlim<bool> UseRotation { get; } = new(false);

    public ReactivePropertySlim<bool> UseScale { get; } = new(false);

    /// <summary>How many frames on each side count in smoothing the motion.</summary>
    public ReactivePropertySlim<int> SmoothRadius { get; } = new(0);

    public ReactivePropertySlim<KeyframeDensity> Density { get; } = new(KeyframeDensity.Precise);

    private Rational Rate => _scene?.FindHierarchicalParent<Project>().GetFrameRate() ?? new Rational(30);

    /// <summary>The tracker in a frame, for drawing: its pose, and whether it was actually found there.</summary>
    public (TrackPose Pose, bool IsInFrame) PoseForDisplay(TrackerViewModel tracker)
    {
        long frame = DisplayFrame.Value ?? tracker.Samples.FirstFrame ?? 0;
        return (tracker.PoseAt(frame), tracker.Samples.Count == 0 || tracker.Samples.TryGet(frame, out _));
    }

    /// <summary>Makes the element under the timeline's selection the one to track.</summary>
    public void UseSelectionAsSource()
    {
        Element? element = SelectedElement();
        if (element == null || !HasPicture(element))
        {
            Status.Value = "タイムラインで動画や画像の要素を選択してください";
            return;
        }

        SetSource(element);
        Status.Value = null;
    }

    /// <summary>Makes the element under the timeline's selection the one that follows the motion.</summary>
    public void UseSelectionAsTarget()
    {
        Element? element = SelectedElement();
        if (element == null || !HasPicture(element))
        {
            Status.Value = "タイムラインで動きを付けたい要素を選択してください";
            return;
        }

        SetTarget(element);
        Status.Value = null;
    }

    /// <summary>Tracks another element. Tracks are positions in the frames of one element, so they start over.</summary>
    public void SetSource(Element? element)
    {
        if (ReferenceEquals(_boundSource, element) || IsTracking.Value)
            return;

        if (_boundSource != null)
            _boundSource.Edited -= OnSourceEdited;

        _boundSource = element;
        _readerStale = true;
        if (element != null)
            element.Edited += OnSourceEdited;

        foreach (TrackerViewModel tracker in Trackers)
        {
            tracker.Samples.Clear();
            tracker.NotifySamplesChanged();
        }

        UpdatePresence();
        OnPlayheadMoved();
        if (Source.Value != null && Trackers.Count == 0)
            AddTracker();
    }

    public void SetTarget(Element? element)
    {
        _boundTarget = element;
        UpdatePresence();
    }

    /// <summary>Selects the source element on the timeline, to show which one the tab works on.</summary>
    public void SelectSourceInTimeline()
    {
        if (Source.Value is { } element)
            _selection?.Select(element);
    }

    public void SelectTargetInTimeline()
    {
        if (Target.Value is { } element)
            _selection?.Select(element);
    }

    /// <summary>Adds a tracker, at a place in the scene or in the middle of it.</summary>
    public TrackerViewModel AddTracker(double? x = null, double? y = null)
    {
        PixelSize size = SceneSize;
        int number = ++_trackerNumber;
        int halfSize = Math.Clamp(size.Width / 120, 8, 64);

        // A second tracker in the middle would land exactly on the first.
        float offset = (Trackers.Count % 4) * halfSize * 4;
        var pose = new TrackPose((float)(x ?? size.Width / 2.0 + offset), (float)(y ?? size.Height / 2.0 + offset));
        var tracker = new TrackerViewModel($"トラッカー {number}", s_colors[(number - 1) % s_colors.Length], halfSize, pose);

        // A new tracker uses the settings of the one being worked with.
        if (SelectedTracker.Value is { } selected)
        {
            tracker.Model.Value = selected.Model.Value;
            tracker.Template.Value = selected.Template.Value;
            tracker.MinConfidence.Value = selected.MinConfidence.Value;
            tracker.PatternHalfWidth.Value = selected.PatternHalfWidth.Value;
            tracker.PatternHalfHeight.Value = selected.PatternHalfHeight.Value;
            tracker.SearchHalfWidth.Value = selected.SearchHalfWidth.Value;
            tracker.SearchHalfHeight.Value = selected.SearchHalfHeight.Value;
        }

        Trackers.Add(tracker);
        SelectedTracker.Value = tracker;
        NotifyOverlay();
        return tracker;
    }

    public void RemoveSelectedTracker()
    {
        if (SelectedTracker.Value is not { } tracker || IsTracking.Value)
            return;

        int index = Trackers.IndexOf(tracker);
        Trackers.RemoveAt(index);
        SelectedTracker.Value = Trackers.Count == 0 ? null : Trackers[Math.Min(index, Trackers.Count - 1)];
        NotifyOverlay();
    }

    /// <summary>Clears samples of the selected tracker. It stays where it is now.</summary>
    public void ClearSamples(ClearRange range)
    {
        if (SelectedTracker.Value is not { } tracker || IsTracking.Value)
            return;

        long frame = CurrentFrame.Value ?? 0;
        tracker.Anchor = tracker.PoseAt(frame);
        switch (range)
        {
            case ClearRange.All:
                tracker.Samples.Clear();
                break;
            case ClearRange.BeforeCurrentFrame:
                tracker.Samples.RemoveRange(long.MinValue, frame - 1);
                break;
            case ClearRange.AfterCurrentFrame:
                tracker.Samples.RemoveRange(frame + 1, long.MaxValue);
                break;
        }

        tracker.NotifySamplesChanged();
        NotifyOverlay();
    }

    /// <summary>
    /// Moves a tracker to a place in the scene, at the frame on display. A tracker that has samples gets one here: the
    /// correction to continue tracking from.
    /// </summary>
    public void MoveTracker(TrackerViewModel tracker, double x, double y)
    {
        // Outside the source there is no frame for a tracked pose to go with.
        if (IsTracking.Value || (DisplayFrame.Value == null && tracker.Samples.Count > 0))
            return;

        var pose = tracker.PoseAt(DisplayFrame.Value ?? 0) with { X = (float)x, Y = (float)y };
        if (tracker.Samples.Count > 0 && DisplayFrame.Value is { } frame)
        {
            tracker.Samples.Set(frame, new TrackSample(pose, 1));
            tracker.NotifySamplesChanged();
        }
        else
        {
            tracker.Anchor = pose;
        }

        NotifyOverlay();
    }

    /// <summary>Changes the size of the pattern. The search area grows along if it would no longer hold it.</summary>
    public void SetPatternSize(TrackerViewModel tracker, int halfWidth, int halfHeight)
    {
        if (IsTracking.Value)
            return;

        tracker.PatternHalfWidth.Value = Math.Clamp(halfWidth, TrackerViewModel.MinPatternHalfSize, TrackerViewModel.MaxHalfSize);
        tracker.PatternHalfHeight.Value = Math.Clamp(halfHeight, TrackerViewModel.MinPatternHalfSize, TrackerViewModel.MaxHalfSize);
        SetSearchSize(tracker, tracker.SearchHalfWidth.Value, tracker.SearchHalfHeight.Value);
        UpdateFeature();
    }

    /// <summary>Changes the size of the search area, which always holds the pattern and some room to move in.</summary>
    public void SetSearchSize(TrackerViewModel tracker, int halfWidth, int halfHeight)
    {
        if (IsTracking.Value)
            return;

        tracker.SearchHalfWidth.Value = Math.Clamp(halfWidth, tracker.PatternHalfWidth.Value + TrackerViewModel.MinSearchMargin, TrackerViewModel.MaxHalfSize);
        tracker.SearchHalfHeight.Value = Math.Clamp(halfHeight, tracker.PatternHalfHeight.Value + TrackerViewModel.MinSearchMargin, TrackerViewModel.MaxHalfSize);
        NotifyOverlay();
    }

    /// <summary>Judges the spot under the selected tracker again, after it was moved or resized.</summary>
    public void UpdateFeature()
    {
        if (SelectedTracker.Value is not { } tracker || _displayLuma == null || DisplayFrame.Value is not { } frame)
        {
            FeatureStrength.Value = null;
            return;
        }

        FeatureStrength.Value = MeasureFeature(tracker, frame) switch
        {
            < WeakFeature => "弱い（模様の少ない場所や直線の上では、追跡が不安定になります）",
            < GoodFeature => "普通",
            _ => "良い",
        };
    }

    private float MeasureFeature(TrackerViewModel tracker, long frame)
    {
        TrackPose pose = tracker.PoseAt(frame);
        return PatchTracker.MeasureFeature(
            _displayLuma!,
            pose.X - 0.5f,
            pose.Y - 0.5f,
            Math.Min(tracker.PatternHalfWidth.Value, 256),
            Math.Min(tracker.PatternHalfHeight.Value, 256));
    }

    /// <summary>Moves the playhead to a frame of the source element.</summary>
    public void SeekToFrame(long frame)
    {
        if (_clock != null && Source.Value is { } source)
            _clock.Seek(source.Start.Value + MediaTime.FromFrames(Math.Clamp(frame, 0, Math.Max(FrameCount.Value - 1, 0)), Rate));
    }

    /// <summary>Stops a running track. What was found so far stays.</summary>
    public void Stop() => _tracking?.Cancel();

    /// <summary>
    /// Tracks the active trackers from the frame on the playhead, each from where it is there, until the end of the
    /// source element or until all of them stop. Later samples are overwritten as it goes.
    /// </summary>
    /// <param name="direction">1 to go forwards, -1 to go backwards.</param>
    /// <param name="singleFrame">Whether to stop after one frame.</param>
    public Task TrackAsync(int direction, bool singleFrame = false)
    {
        if (IsTracking.Value || Source.Value == null)
            return Task.CompletedTask;

        if (CurrentFrame.Value is not { } start)
        {
            Status.Value = NoFrameHere;
            return Task.CompletedTask;
        }

        TrackerViewModel[] trackers = [.. Trackers.Where(tracker => tracker.IsActive.Value)];
        if (trackers.Length == 0)
        {
            Status.Value = "追跡するトラッカーがありません。トラッカーを追加するか、一覧でオンにしてください";
            return Task.CompletedTask;
        }

        long last = direction > 0 ? FrameCount.Value - 1 : 0;
        if (singleFrame)
            last = Math.Clamp(start + direction, 0, FrameCount.Value - 1);
        if (last == start)
        {
            Status.Value = direction > 0 ? "ここが追跡元の最後のフレームです" : "ここが追跡元の最初のフレームです";
            return Task.CompletedTask;
        }

        _run = RunTrackingAsync(trackers, start, last);
        return _run;
    }

    private async Task RunTrackingAsync(TrackerViewModel[] trackers, long start, long last)
    {
        using var tracking = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _tracking = tracking;
        IsTracking.Value = true;
        Progress.Value = 0;
        Status.Value = "追跡しています…";
        _requestedFrame = null;
        _runningTrackers = trackers;
        _runFirstFrame = start;
        _runLastFrame = last;

        // A tracker on a weak spot can slide along it with full confidence, so its result alone says nothing is wrong.
        string[] weak = _displayLuma != null && _shownFrame == start
            ? [.. trackers.Where(tracker => MeasureFeature(tracker, start) < WeakFeature).Select(tracker => tracker.Name.Value)]
            : [];

        var starts = new TrackerStart[trackers.Length];
        for (int i = 0; i < trackers.Length; i++)
        {
            TrackerViewModel tracker = trackers[i];
            TrackPose pose = tracker.PoseAt(start);
            starts[i] = new TrackerStart(tracker.ToOptions(), pose);
            if (!tracker.Samples.TryGet(start, out _))
                tracker.Samples.Set(start, new TrackSample(pose, 1));
            tracker.NotifySamplesChanged();
        }

        NotifyOverlay();
        long reached = start;
        string message;
        try
        {
            if (_pump is { } pump)
                await pump;

            SourceFrameReader reader = await EnsureReaderAsync();
            TrackingOutcome outcome = await Task.Run(
                () => TrackingSession.RunAsync(reader, starts, start, last, OnTrackingUpdate, tracking.Token),
                tracking.Token);
            reached = outcome.LastFrame;
            message = DescribeOutcome(trackers, outcome, start);
            if (weak.Length > 0)
                message += $"。{string.Join("、", weak)}は模様の少ない場所にあったので、結果がずれているかもしれません";
        }
        catch (OperationCanceledException)
        {
            message = $"停止しました。ここまでの {Math.Abs(LastReported(start) - start)} フレームは残っています";
            reached = LastReported(start);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tracking failed");
            message = $"追跡できませんでした: {ex.Message}";
            reached = LastReported(start);
        }

        if (_lifetime.IsCancellationRequested)
            return;

        FlushResults();
        _tracking = null;
        IsTracking.Value = false;
        Progress.Value = 0;
        Status.Value = message;
        OnPlayheadMoved();
        SeekToFrame(reached);
        RequestFrame();
    }

    private long LastReported(long start)
    {
        FlushResults();
        return DisplayFrame.Value ?? start;
    }

    private static string DescribeOutcome(TrackerViewModel[] trackers, TrackingOutcome outcome, long start)
    {
        long frames = Math.Abs(outcome.LastFrame - start);
        var stops = new List<string>();
        for (int i = 0; i < trackers.Length; i++)
        {
            switch (outcome.Stops[i])
            {
                case TrackStatus.LowConfidence:
                    stops.Add($"{trackers[i].Name.Value}は見失いました（信頼度の低下）");
                    break;
                case TrackStatus.OutOfFrame:
                    stops.Add($"{trackers[i].Name.Value}は画面の外に出ました");
                    break;
            }
        }

        string result = $"{frames} フレーム追跡しました";
        return stops.Count == 0 ? result : $"{result}。{string.Join("、", stops)}";
    }

    // Runs on the tracking thread; the frame is only valid during the call.
    private void OnTrackingUpdate(TrackingUpdate update)
    {
        _results.Enqueue((update.Frame, update.Results));

        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref _lastDisplayTicks) >= s_displayInterval.TotalMilliseconds
            && Interlocked.CompareExchange(ref _displayState, 1, 0) == 0)
        {
            Volatile.Write(ref _lastDisplayTicks, now);
            CopyToDisplay(update.Image, withLuma: false);
            Volatile.Write(ref _displayState, 2);
        }

        if (Interlocked.Exchange(ref _flushPosted, 1) == 0)
            Dispatcher.UIThread.Post(FlushResults);
    }

    private void FlushResults()
    {
        Volatile.Write(ref _flushPosted, 0);
        if (_lifetime.IsCancellationRequested)
            return;

        bool any = false;
        long frame = 0;
        while (_results.TryDequeue(out (long Frame, TrackResult?[] Results) item))
        {
            frame = item.Frame;
            for (int i = 0; i < item.Results.Length; i++)
            {
                if (item.Results[i] is { Status: TrackStatus.Tracked } result)
                    _runningTrackers[i].Samples.Set(frame, new TrackSample(result.Pose, result.Confidence));
            }

            any = true;
        }

        if (any)
        {
            foreach (TrackerViewModel tracker in _runningTrackers)
                tracker.NotifySamplesChanged();

            DisplayFrame.Value = frame;
            long total = Math.Abs(_runLastFrame - _runFirstFrame);
            Progress.Value = total == 0 ? 1 : Math.Abs(frame - _runFirstFrame) / (double)total;
        }

        if (Volatile.Read(ref _displayState) == 2)
        {
            ShowDisplay();
            Volatile.Write(ref _displayState, 0);
        }

        if (any)
            NotifyOverlay();
    }

    /// <summary>Writes the motion to the target as keyframes, as one edit in the history.</summary>
    public void Apply()
    {
        if (IsTracking.Value)
            return;

        Element? element = Use.Value == MotionUse.Follow ? Target.Value : Source.Value;
        if (element == null)
        {
            Status.Value = Use.Value == MotionUse.Follow
                ? "適用先の要素を設定してください"
                : "追跡元の要素を設定してください";
            return;
        }

        if (_scene != null && _scene.IsElementLocked(element))
        {
            Status.Value = "適用先の要素はロックされています";
            return;
        }

        if (Use.Value == MotionUse.Follow && ReferenceEquals(element, Source.Value))
        {
            Status.Value = "適用先が追跡元と同じ要素です。別の要素を設定するか、手ブレの補正を選んでください";
            return;
        }

        TrackerViewModel[] trackers = [.. Trackers.Where(tracker => tracker.IsActive.Value && tracker.Samples.Count > 0)];
        if (trackers.Length == 0)
        {
            Status.Value = "まだ追跡していません。先にトラッカーを追跡してください";
            return;
        }

        MotionChannels channels = MotionChannels.None;
        if (UsePosition.Value)
            channels |= MotionChannels.Position;
        if (UseRotation.Value)
            channels |= MotionChannels.Rotation;
        if (UseScale.Value)
            channels |= MotionChannels.Scale;
        if (channels == MotionChannels.None)
        {
            Status.Value = "位置・回転・拡大縮小のどれかをオンにしてください";
            return;
        }

        MotionPath? path = MotionPath.Solve([.. trackers.Select(tracker => tracker.Samples.ToPoses())], CurrentFrame.Value ?? 0);
        if (path == null)
        {
            Status.Value = trackers.Length > 1
                ? "トラッカー同士に共通のフレームがないか、位置が近すぎます"
                : "追跡結果がありません";
            return;
        }

        int written = WriteKeyframes(element, path, channels);
        if (written == 0)
        {
            Status.Value = "適用先の要素と追跡結果の時間が重なっていません";
            return;
        }

        _history?.Commit("モーショントラッキングを適用");
        string name = Describe(element);
        Status.Value = Use.Value == MotionUse.Follow
            ? $"「{name}」に {written} 個のキーフレームを書き込みました。再生ヘッドの位置（フレーム {path.ReferenceFrame + 1}）で今の配置のままになります"
            : $"「{name}」に {written} 個のキーフレームを書き込みました";
    }

    private int WriteKeyframes(Element element, MotionPath path, MotionChannels channels)
    {
        int smooth = SmoothRadius.Value;
        MotionPath moving = Use.Value == MotionUse.Follow ? path.Smooth(smooth) : path;
        MotionPath? smoothed = Use.Value == MotionUse.Steady ? path.Smooth(Math.Max(smooth, 1)) : null;
        Rational rate = Rate;
        MediaTime referenceTime = (Source.Value?.Start.Value ?? MediaTime.Zero) + MediaTime.FromFrames(path.ReferenceFrame, rate);
        float tolerance = Density.Value switch
        {
            KeyframeDensity.Precise => 0.1f,
            KeyframeDensity.Few => 0.5f,
            _ => 0,
        };

        int written = 0;
        foreach (Drawable drawable in element.Objects.OfType<Drawable>())
        {
            (double pivotX, double pivotY) = TrackingKeyframes.MeasurePivot(drawable, SceneSize, referenceTime);
            TransformKey[] keys = moving.ToTransformKeys(Use.Value, channels, pivotX, pivotY, smoothed);
            List<(TransformKey Key, MediaTime Time)> timed = ToKeyTimes(keys, element);
            if (timed.Count > 0)
                written += TrackingKeyframes.Write(drawable, timed, channels, tolerance);
        }

        return written;
    }

    // Key times count from the start of the element holding the animation; a key outside the element would never play.
    private List<(TransformKey Key, MediaTime Time)> ToKeyTimes(TransformKey[] keys, Element element)
    {
        Rational rate = Rate;
        MediaTime sourceStart = Source.Value?.Start.Value ?? MediaTime.Zero;
        MediaTime elementStart = element.Start.Value;
        MediaTime length = element.Length.Value;
        var result = new List<(TransformKey, MediaTime)>(keys.Length);
        foreach (TransformKey key in keys)
        {
            MediaTime time = (sourceStart + MediaTime.FromFrames(key.Frame, rate) - elementStart)
                .RoundToFrame(rate, MidpointRounding.AwayFromZero);
            if (time >= MediaTime.Zero && time <= length)
                result.Add((key, time));
        }

        return result;
    }

    /// <summary>Removes the keyframes an earlier apply wrote to the element it would write to now.</summary>
    public void RemoveApplied()
    {
        Element? element = Use.Value == MotionUse.Follow ? Target.Value : Source.Value;
        if (element == null || IsTracking.Value)
            return;

        bool removed = false;
        foreach (Drawable drawable in element.Objects.OfType<Drawable>())
            removed |= TrackingKeyframes.Remove(drawable);

        if (removed)
        {
            _history?.Commit("モーショントラッキングを解除");
            Status.Value = $"「{Describe(element)}」のトラッキングのキーフレームを削除しました";
        }
        else
        {
            Status.Value = "このトラッキングのキーフレームはまだありません";
        }
    }

    private Element? SelectedElement()
    {
        if (_selection == null)
            return null;

        return _selection.SelectedObject.Value switch
        {
            Element element => element,
            IHierarchical hierarchical when hierarchical.FindHierarchicalParent<Element>() is { } parent => parent,
            _ => _selection.SelectedElements.FirstOrDefault(),
        };
    }

    private static bool HasPicture(Element element) => element.Objects.OfType<Drawable>().Any();

    private static string Describe(Element element)
    {
        if (!string.IsNullOrWhiteSpace(element.Name.Value))
            return element.Name.Value;

        return element.Objects.OfType<Drawable>().FirstOrDefault() is { } drawable
            ? TypeDisplayHelpers.GetLocalizedName(drawable.GetType())
            : TypeDisplayHelpers.GetLocalizedName(typeof(Element));
    }

    private void UpdatePresence()
    {
        bool sourcePresent = _boundSource != null && _scene != null && _scene.Children.Contains(_boundSource);
        Source.Value = sourcePresent ? _boundSource : null;
        SourceName.Value = sourcePresent ? Describe(_boundSource!) : "";
        SourceMessage.Value = sourcePresent ? null : _boundSource == null ? NoSourceMessage : MissingSourceMessage;
        UpdateRange();

        bool targetPresent = _boundTarget != null && _scene != null && _scene.Children.Contains(_boundTarget);
        Target.Value = targetPresent ? _boundTarget : null;
        TargetName.Value = targetPresent ? Describe(_boundTarget!) : _boundTarget == null ? "（未設定）" : "（削除されました）";
    }

    private void UpdateRange()
    {
        FrameCount.Value = Source.Value is { } source
            ? Math.Max(source.Length.Value.ToFrame(Rate, MidpointRounding.AwayFromZero), 0)
            : 0;
    }

    private void OnSceneChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdatePresence();
        OnPlayheadMoved();
    }

    private void OnSourceEdited(object? sender, EventArgs e)
    {
        // A running track keeps its copy of the scene; the next frame shown after it uses the edited one.
        _readerStale = true;
        SourceName.Value = Source.Value is { } source ? Describe(source) : "";
        UpdateRange();
        OnPlayheadMoved();
        RequestFrame();
    }

    private void OnPlayheadMoved()
    {
        long? frame = null;
        if (Source.Value is { } source && _clock != null)
        {
            long at = (_clock.CurrentTime.Value - source.Start.Value).ToFrame(Rate, MidpointRounding.AwayFromZero);
            if (at >= 0 && at < FrameCount.Value)
                frame = at;
        }

        CurrentFrame.Value = frame;
        if (IsTracking.Value)
            return;

        DisplayFrame.Value = frame;
        RequestFrame();
        UpdateFeature();
        NotifyOverlay();
    }

    private void NotifyOverlay() => OverlayChanged?.Invoke(this, EventArgs.Empty);

    private void RequestFrame()
    {
        if (!IsSelected.Value || IsTracking.Value || Source.Value == null || CurrentFrame.Value is not { } frame)
            return;

        if (frame == _shownFrame && !_readerStale)
            return;

        _requestedFrame = frame;
        if (_pump is null or { IsCompleted: true })
            _pump = PumpFramesAsync();
    }

    // Renders the frame asked for last; while one is being rendered, later requests replace each other.
    private async Task PumpFramesAsync()
    {
        while (_requestedFrame is { } frame && !_lifetime.IsCancellationRequested && !IsTracking.Value && Source.Value != null)
        {
            _requestedFrame = null;
            try
            {
                SourceFrameReader reader = await EnsureReaderAsync();
                using ITrackingFrame image = await reader.ReadAsync(frame, _lifetime.Token);
                await Task.Run(() => CopyToDisplay(image, withLuma: true));
                if (_lifetime.IsCancellationRequested)
                    return;

                ShowDisplay();
                _shownFrame = frame;
                UpdateFeature();
                NotifyOverlay();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to render a frame of the source element");
                Status.Value = $"フレームを描画できませんでした: {ex.Message}";
                return;
            }

            // Playback has the preview to show; this picture just keeps up as far as it does not get in the way.
            if (_player?.IsPlaying.Value == true)
                await Task.Delay(s_playbackFrameInterval);
        }
    }

    private async Task<SourceFrameReader> EnsureReaderAsync()
    {
        if (_reader != null && !_readerStale)
            return _reader;

        _reader?.Dispose();
        _reader = null;
        _readerStale = false;
        _shownFrame = null;
        SourceCapture capture = SourceFrameReader.Capture(_scene!, Source.Value ?? _boundSource!);
        SourceFrameReader reader = await Task.Run(() => SourceFrameReader.Open(capture));
        if (_lifetime.IsCancellationRequested)
        {
            reader.Dispose();
            throw new OperationCanceledException();
        }

        _reader = reader;
        return reader;
    }

    private void CopyToDisplay(ITrackingFrame image, bool withLuma)
    {
        int size = image.Width * image.Height * 4;
        if (_displayPixels.Length != size)
            _displayPixels = new byte[size];

        image.CopyBgra(_displayPixels);
        _displayWidth = image.Width;
        _displayHeight = image.Height;
        if (withLuma)
        {
            if (_displayLuma == null || _displayLuma.Width != image.Width || _displayLuma.Height != image.Height)
                _displayLuma = new GrayImage(image.Width, image.Height);
            _displayLuma.LoadBgra(_displayPixels, image.Width * 4);
        }
    }

    private void ShowDisplay()
    {
        if (_displayWidth <= 0 || _displayHeight <= 0)
            return;

        var size = new Avalonia.PixelSize(_displayWidth, _displayHeight);
        if (FrameImage == null || FrameImage.PixelSize != size)
        {
            FrameImage?.Dispose();
            FrameImage = new WriteableBitmap(size, new Avalonia.Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        }

        using (ILockedFramebuffer buffer = FrameImage.Lock())
        {
            int rowBytes = _displayWidth * 4;
            for (int y = 0; y < _displayHeight; y++)
                Marshal.Copy(_displayPixels, y * rowBytes, buffer.Address + y * buffer.RowBytes, rowBytes);
        }

        FrameChanged?.Invoke(this, EventArgs.Empty);
    }

    public void WriteToJson(JsonObject json)
    {
        json[nameof(Use)] = Use.Value.ToString();
        json[nameof(UsePosition)] = UsePosition.Value;
        json[nameof(UseRotation)] = UseRotation.Value;
        json[nameof(UseScale)] = UseScale.Value;
        json[nameof(SmoothRadius)] = SmoothRadius.Value;
        json[nameof(Density)] = Density.Value.ToString();

        // A tab restored with the layout finds its elements again by id.
        if (_boundSource != null)
            json["Source"] = _boundSource.Id.Value.ToString();
        if (_boundTarget != null)
            json["Target"] = _boundTarget.Id.Value.ToString();

        Rational rate = Rate;
        json["FrameRateNumerator"] = rate.Numerator;
        json["FrameRateDenominator"] = rate.Denominator;
        var trackers = new JsonArray();
        foreach (TrackerViewModel tracker in Trackers)
            trackers.Add(tracker.ToJson());
        json["Trackers"] = trackers;
    }

    public void ReadFromJson(JsonObject json)
    {
        if (TryGet(json, nameof(Use), out string? use) && Enum.TryParse(use, out MotionUse parsedUse))
            Use.Value = parsedUse;
        if (TryGet(json, nameof(UsePosition), out bool position))
            UsePosition.Value = position;
        if (TryGet(json, nameof(UseRotation), out bool rotation))
            UseRotation.Value = rotation;
        if (TryGet(json, nameof(UseScale), out bool scale))
            UseScale.Value = scale;
        if (TryGet(json, nameof(SmoothRadius), out int smooth) && smooth >= 0)
            SmoothRadius.Value = smooth;
        if (TryGet(json, nameof(Density), out string? density) && Enum.TryParse(density, out KeyframeDensity parsedDensity))
            Density.Value = parsedDensity;

        // The trackers are restored last, because choosing a source clears samples.
        if (_scene != null && TryGet(json, "Source", out string? sourceId) && Guid.TryParse(sourceId, out Guid source))
            SetSource(_scene.Children.FirstOrDefault(element => element.Id.Value == source));
        if (_scene != null && TryGet(json, "Target", out string? targetId) && Guid.TryParse(targetId, out Guid target))
            SetTarget(_scene.Children.FirstOrDefault(element => element.Id.Value == target));

        if (json.TryGetPropertyValue("Trackers", out JsonNode? node) && node is JsonArray array)
        {
            Rational rate = Rate;
            Func<long, long> convert = frame => frame;
            if (TryGet(json, "FrameRateNumerator", out long numerator) && TryGet(json, "FrameRateDenominator", out long denominator)
                && numerator > 0 && denominator > 0 && (numerator != rate.Numerator || denominator != rate.Denominator))
            {
                var saved = new Rational(numerator, denominator);
                convert = frame => MediaTime.FromFrames(frame, saved).ToFrame(rate, MidpointRounding.AwayFromZero);
            }

            Trackers.Clear();
            _trackerNumber = 0;
            foreach (JsonNode? item in array)
            {
                if (item is JsonObject trackerJson && TrackerViewModel.FromJson(trackerJson, convert) is { } tracker)
                {
                    Trackers.Add(tracker);
                    _trackerNumber++;
                }
            }

            SelectedTracker.Value = Trackers.FirstOrDefault();
            NotifyOverlay();
        }
    }

    private static bool TryGet<T>(JsonObject json, string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? value)
    {
        value = default;
        return json.TryGetPropertyValue(key, out JsonNode? node)
               && node is JsonValue jsonValue
               && jsonValue.TryGetValue(out value)
               && value != null;
    }

    public object? GetService(Type serviceType) => _services.GetService(serviceType);

    public void Dispose()
    {
        _lifetime.Cancel();
        _clockSubscription?.Dispose();
        _selectedSubscription?.Dispose();
        _trackerSubscription?.Dispose();
        if (_scene != null)
            _scene.Children.CollectionChanged -= OnSceneChildrenChanged;
        if (_boundSource != null)
            _boundSource.Edited -= OnSourceEdited;

        // A frame may still be rendering with the reader; it goes when that is done.
        Task? pump = _pump;
        Task? run = _run;
        SourceFrameReader? reader = _reader;
        _reader = null;
        Task.WhenAll(pump ?? Task.CompletedTask, run ?? Task.CompletedTask)
            .ContinueWith(_ => reader?.Dispose(), TaskScheduler.Default);
    }
}
