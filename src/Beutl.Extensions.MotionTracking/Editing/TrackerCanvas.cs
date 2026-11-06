using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.VisualTree;
using Beutl.Extensions.MotionTracking.Tracking;

namespace Beutl.Extensions.MotionTracking.Editing;

/// <summary>
/// Shows the frame of the source element with the trackers over it. A tracker is dragged by its inside, and resized
/// by the corners of its pattern and of its search area. The wheel zooms around the pointer, and the right or middle
/// button drags the view. Double-clicking an empty spot adds a tracker there.
/// </summary>
internal sealed class TrackerCanvas : Control
{
    /// <summary>How far the view zooms in at most.</summary>
    public const double MaxZoom = 32;

    private const double HandleRadius = 4;
    private const double GrabDistance = 8;
    private const double LoupeSize = 160;
    private const double LoupeZoom = 5;

    private static readonly IBrush s_frameBackground = new ImmutableSolidColorBrush(Colors.Black);
    private static readonly ImmutableSolidColorBrush s_shadow = new(Color.FromArgb(170, 0, 0, 0));
    private static readonly IBrush s_lowConfidence = new ImmutableSolidColorBrush(Color.FromRgb(255, 92, 72));
    private static readonly IBrush s_handleFill = new ImmutableSolidColorBrush(Colors.White);
    private static readonly IPen s_shadowPen = new ImmutablePen(s_shadow, 3);
    private static readonly IPen s_loupeBorder = new ImmutablePen(new ImmutableSolidColorBrush(Colors.White), 1);
    private static readonly DashStyle s_dash = new([4, 3], 0);
    private static readonly Cursor s_moveCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor s_resizeCursor = new(StandardCursorType.BottomRightCorner);

    private MotionTrackingTabViewModel? _viewModel;
    private double _zoom = 1;
    private Vector _pan;

    private DragKind _drag;
    private TrackerViewModel? _dragTracker;
    private Point _dragStart;
    private Vector _dragPan;
    private TrackPose _dragPose;
    private Vector _dragGrabOffset;

    public TrackerCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public MotionTrackingTabViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            Listen(false);
            _viewModel = value;
            Listen(true);
            InvalidateVisual();
        }
    }

    // The view model outlives the control, so the control only listens to it while it is on screen.
    private void Listen(bool listen)
    {
        if (_viewModel is not { } viewModel || !this.IsAttachedToVisualTree())
            return;

        viewModel.OverlayChanged -= OnRedrawRequested;
        viewModel.FrameChanged -= OnRedrawRequested;
        if (listen)
        {
            viewModel.OverlayChanged += OnRedrawRequested;
            viewModel.FrameChanged += OnRedrawRequested;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Listen(true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Listen(false);
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>How far the view is zoomed in, from 1 for the whole frame to <see cref="MaxZoom"/>.</summary>
    public double Zoom => _zoom;

    /// <summary>Raised when the view is zoomed in or out, or shows the whole frame again.</summary>
    public event EventHandler? ZoomChanged;

    /// <summary>Shows the whole frame again.</summary>
    public void ResetView()
    {
        _zoom = 1;
        _pan = default;
        InvalidateVisual();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Zooms in or out around the middle of the view.</summary>
    public void ZoomBy(double factor) => ZoomAround(new Point(Bounds.Width / 2, Bounds.Height / 2), factor);

    // What is under the point stays under it. Returns whether the zoom changed.
    private bool ZoomAround(Point point, double factor)
    {
        Point scene = ToScene(point);
        double zoom = Math.Clamp(_zoom * factor, 1, MaxZoom);
        if (zoom == _zoom)
            return false;

        _zoom = zoom;
        _pan = zoom == 1 ? default : _pan + (point - ToView(scene.X, scene.Y));
        InvalidateVisual();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void OnRedrawRequested(object? sender, EventArgs e) => InvalidateVisual();

    // The frame fills the view; the card around the view leaves the room beside it.
    private double FitScale
    {
        get
        {
            Beutl.Media.PixelSize size = _viewModel?.SceneSize ?? new Beutl.Media.PixelSize(1920, 1080);
            double width = Math.Max(Bounds.Width, 1);
            double height = Math.Max(Bounds.Height, 1);
            return Math.Min(width / Math.Max(size.Width, 1), height / Math.Max(size.Height, 1));
        }
    }

    private double Scale => FitScale * _zoom;

    private Point Origin
    {
        get
        {
            Beutl.Media.PixelSize size = _viewModel?.SceneSize ?? new Beutl.Media.PixelSize(1920, 1080);
            double scale = Scale;
            return new Point(
                (Bounds.Width - size.Width * scale) / 2 + _pan.X,
                (Bounds.Height - size.Height * scale) / 2 + _pan.Y);
        }
    }

    private Point ToView(double x, double y)
    {
        Point origin = Origin;
        double scale = Scale;
        return new Point(origin.X + x * scale, origin.Y + y * scale);
    }

    private Point ToScene(Point view)
    {
        Point origin = Origin;
        double scale = Scale;
        return new Point((view.X - origin.X) / scale, (view.Y - origin.Y) / scale);
    }

    /// <summary>The corners of a tracker's pattern in the scene, turned and scaled with its pose.</summary>
    private static Point[] PatternCorners(TrackerViewModel tracker, TrackPose pose)
    {
        double w = tracker.PatternHalfWidth.Value * pose.Scale;
        double h = tracker.PatternHalfHeight.Value * pose.Scale;
        double cos = Math.Cos(pose.Rotation);
        double sin = Math.Sin(pose.Rotation);
        Point Corner(double u, double v) => new(pose.X + cos * u - sin * v, pose.Y + sin * u + cos * v);
        return [Corner(-w, -h), Corner(w, -h), Corner(w, h), Corner(-w, h)];
    }

    private static Rect SearchRect(TrackerViewModel tracker, TrackPose pose)
    {
        double w = tracker.SearchHalfWidth.Value;
        double h = tracker.SearchHalfHeight.Value;
        return new Rect(pose.X - w, pose.Y - h, w * 2, h * 2);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        // Clear, so that the card behind shows, but still there for the pointer everywhere.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (_viewModel is not { } viewModel)
            return;

        Beutl.Media.PixelSize size = viewModel.SceneSize;
        double scale = Scale;
        Point origin = Origin;
        var frameRect = new Rect(origin.X, origin.Y, size.Width * scale, size.Height * scale);
        context.FillRectangle(s_frameBackground, frameRect);
        if (viewModel.FrameImage is { } image)
        {
            // Past a few times the size, the separate pixels are what one is placing a tracker on.
            var mode = scale >= 3 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality;
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = mode }))
                context.DrawImage(image, new Rect(image.Size), frameRect);
        }

        foreach (TrackerViewModel tracker in viewModel.Trackers)
        {
            if (!ReferenceEquals(tracker, viewModel.SelectedTracker.Value))
                DrawTracker(context, viewModel, tracker, selected: false);
        }

        if (viewModel.SelectedTracker.Value is { } selected)
            DrawTracker(context, viewModel, selected, selected: true);

        if (_drag != DragKind.None && _drag != DragKind.Pan && _dragTracker != null)
            DrawLoupe(context, viewModel, _dragTracker);
    }

    private void DrawTracker(DrawingContext context, MotionTrackingTabViewModel viewModel, TrackerViewModel tracker, bool selected)
    {
        (TrackPose pose, bool isInFrame) = viewModel.PoseForDisplay(tracker);
        double opacity = tracker.IsActive.Value ? isInFrame ? 1 : 0.55 : 0.35;
        using (context.PushOpacity(opacity))
        {
            DrawPath(context, tracker, selected);

            var pen = new Pen(tracker.Brush, selected ? 1.5 : 1);
            var searchPen = new Pen(tracker.Brush, 1, s_dash);
            Point[] corners = PatternCorners(tracker, pose);
            Point[] view = [.. corners.Select(corner => ToView(corner.X, corner.Y))];
            Rect search = SearchRect(tracker, pose);
            Point searchTopLeft = ToView(search.X, search.Y);
            Point searchBottomRight = ToView(search.Right, search.Bottom);
            var searchView = new Rect(searchTopLeft, searchBottomRight);

            if (selected)
            {
                context.DrawRectangle(s_shadowPen, searchView);
                context.DrawRectangle(searchPen, searchView);
            }

            DrawPolygon(context, s_shadowPen, view);
            DrawPolygon(context, pen, view);

            Point center = ToView(pose.X, pose.Y);
            DrawCross(context, s_shadowPen, center, 6);
            DrawCross(context, pen, center, 6);

            if (selected)
            {
                foreach (Point corner in view)
                    DrawHandle(context, corner, tracker.Brush);
                DrawHandle(context, searchView.TopLeft, tracker.Brush);
                DrawHandle(context, searchView.TopRight, tracker.Brush);
                DrawHandle(context, searchView.BottomRight, tracker.Brush);
                DrawHandle(context, searchView.BottomLeft, tracker.Brush);
            }

            var label = new FormattedText(
                tracker.Name.Value,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                12,
                tracker.Brush);
            double left = view.Min(point => point.X);
            double top = view.Min(point => point.Y);
            var labelAt = new Point(left, top - label.Height - 4);
            context.FillRectangle(s_shadow, new Rect(labelAt.X - 2, labelAt.Y, label.Width + 4, label.Height));
            context.DrawText(label, labelAt);
        }
    }

    private void DrawPath(DrawingContext context, TrackerViewModel tracker, bool selected)
    {
        TrackSamples samples = tracker.Samples;
        if (samples.Count < 2)
            return;

        var normal = new StreamGeometry();
        var low = new StreamGeometry();
        float lowBelow = tracker.MinConfidence.Value + 0.1f;
        using (StreamGeometryContext normalContext = normal.Open())
        using (StreamGeometryContext lowContext = low.Open())
        {
            long previousFrame = samples.Frames[0];
            Point previous = ToView(samples.Values[0].Pose.X, samples.Values[0].Pose.Y);
            for (int i = 1; i < samples.Count; i++)
            {
                TrackSample sample = samples.Values[i];
                long frame = samples.Frames[i];
                Point point = ToView(sample.Pose.X, sample.Pose.Y);
                if (frame == previousFrame + 1)
                {
                    StreamGeometryContext target = sample.Confidence < lowBelow ? lowContext : normalContext;
                    target.BeginFigure(previous, false);
                    target.LineTo(point);
                    target.EndFigure(false);
                }

                previous = point;
                previousFrame = frame;
            }
        }

        double thickness = selected ? 1.5 : 1;
        context.DrawGeometry(null, new Pen(s_shadow, thickness + 2), normal);
        context.DrawGeometry(null, new Pen(s_shadow, thickness + 2), low);
        context.DrawGeometry(null, new Pen(tracker.Brush, thickness), normal);
        context.DrawGeometry(null, new Pen(s_lowConfidence, thickness), low);
    }

    private static void DrawPolygon(DrawingContext context, IPen pen, Point[] points)
    {
        for (int i = 0; i < points.Length; i++)
            context.DrawLine(pen, points[i], points[(i + 1) % points.Length]);
    }

    private static void DrawCross(DrawingContext context, IPen pen, Point center, double size)
    {
        context.DrawLine(pen, new Point(center.X - size, center.Y), new Point(center.X + size, center.Y));
        context.DrawLine(pen, new Point(center.X, center.Y - size), new Point(center.X, center.Y + size));
    }

    private static void DrawHandle(DrawingContext context, Point center, IBrush brush)
    {
        var rect = new Rect(center.X - HandleRadius, center.Y - HandleRadius, HandleRadius * 2, HandleRadius * 2);
        context.DrawRectangle(s_handleFill, new Pen(brush, 1.5), rect);
    }

    // A magnified view of what is under the tracker being dragged, away from the pointer, so that the pointer does not hide it.
    private void DrawLoupe(DrawingContext context, MotionTrackingTabViewModel viewModel, TrackerViewModel tracker)
    {
        if (viewModel.FrameImage is not { } image)
            return;

        TrackPose pose = viewModel.PoseForDisplay(tracker).Pose;
        double pixels = LoupeSize / LoupeZoom;
        var source = new Rect(pose.X - pixels / 2, pose.Y - pixels / 2, pixels, pixels);
        const double margin = 12;
        bool pointerOnLeft = _dragStart.X < Bounds.Width / 2;
        var target = new Rect(pointerOnLeft ? Bounds.Width - LoupeSize - margin : margin, margin, LoupeSize, LoupeSize);
        context.FillRectangle(s_frameBackground, target);
        using (context.PushClip(target))
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
        {
            context.DrawImage(image, source, target);
        }

        double w = tracker.PatternHalfWidth.Value * pose.Scale * LoupeZoom;
        double h = tracker.PatternHalfHeight.Value * pose.Scale * LoupeZoom;
        Point center = target.Center;
        using (context.PushClip(target))
        {
            var pattern = new Rect(center.X - w, center.Y - h, w * 2, h * 2);
            var pen = new Pen(tracker.Brush, 1);
            context.DrawRectangle(s_shadowPen, pattern);
            context.DrawRectangle(pen, pattern);
            DrawCross(context, s_shadowPen, center, 10);
            DrawCross(context, pen, center, 10);
        }

        context.DrawRectangle(s_loupeBorder, target);
    }

    private (TrackerViewModel Tracker, DragKind Part)? HitTest(Point view)
    {
        if (_viewModel is not { } viewModel)
            return null;

        if (viewModel.SelectedTracker.Value is { } selected)
        {
            TrackPose pose = viewModel.PoseForDisplay(selected).Pose;
            foreach (Point corner in PatternCorners(selected, pose))
            {
                if (IsNear(view, ToView(corner.X, corner.Y)))
                    return (selected, DragKind.ResizePattern);
            }

            Rect search = SearchRect(selected, pose);
            foreach (Point corner in new[] { search.TopLeft, search.TopRight, search.BottomRight, search.BottomLeft })
            {
                if (IsNear(view, ToView(corner.X, corner.Y)))
                    return (selected, DragKind.ResizeSearch);
            }
        }

        // The last drawn is on top; the selected one is drawn last of all.
        if (viewModel.SelectedTracker.Value is { } first && IsInside(viewModel, first, view))
            return (first, DragKind.Move);

        for (int i = viewModel.Trackers.Count - 1; i >= 0; i--)
        {
            TrackerViewModel tracker = viewModel.Trackers[i];
            if (IsInside(viewModel, tracker, view))
                return (tracker, DragKind.Move);
        }

        return null;
    }

    private static bool IsNear(Point a, Point b)
        => Math.Abs(a.X - b.X) <= GrabDistance && Math.Abs(a.Y - b.Y) <= GrabDistance;

    private bool IsInside(MotionTrackingTabViewModel viewModel, TrackerViewModel tracker, Point view)
    {
        TrackPose pose = viewModel.PoseForDisplay(tracker).Pose;
        Point scene = ToScene(view);
        (double u, double v) = ToPatternAxes(pose, scene);
        return Math.Abs(u) <= tracker.PatternHalfWidth.Value * pose.Scale
               && Math.Abs(v) <= tracker.PatternHalfHeight.Value * pose.Scale;
    }

    private static (double U, double V) ToPatternAxes(TrackPose pose, Point scene)
    {
        double dx = scene.X - pose.X;
        double dy = scene.Y - pose.Y;
        double cos = Math.Cos(pose.Rotation);
        double sin = Math.Sin(pose.Rotation);
        return (cos * dx + sin * dy, -sin * dx + cos * dy);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_viewModel is not { } viewModel)
            return;

        Focus();
        PointerPointProperties properties = e.GetCurrentPoint(this).Properties;
        Point point = e.GetPosition(this);
        if (properties.IsRightButtonPressed || properties.IsMiddleButtonPressed)
        {
            BeginDrag(e, DragKind.Pan, null, point);
            return;
        }

        if (!properties.IsLeftButtonPressed)
            return;

        if (HitTest(point) is { } hit)
        {
            viewModel.SelectedTracker.Value = hit.Tracker;
            BeginDrag(e, viewModel.IsTracking.Value ? DragKind.None : hit.Part, hit.Tracker, point);
            return;
        }

        if (e.ClickCount == 2 && !viewModel.IsTracking.Value && viewModel.Source.Value != null)
        {
            Point scene = ToScene(point);
            Beutl.Media.PixelSize size = viewModel.SceneSize;
            if (scene.X >= 0 && scene.Y >= 0 && scene.X <= size.Width && scene.Y <= size.Height)
            {
                viewModel.AddTracker(scene.X, scene.Y);
                viewModel.UpdateFeature();
                e.Handled = true;
                return;
            }
        }

        // Without a tracker under it the left button drags the view too, once there is more to see than fits.
        if (_zoom > 1)
            BeginDrag(e, DragKind.Pan, null, point);
    }

    private void BeginDrag(PointerPressedEventArgs e, DragKind kind, TrackerViewModel? tracker, Point point)
    {
        if (kind == DragKind.None)
            return;

        _drag = kind;
        _dragTracker = tracker;
        _dragStart = point;
        _dragPan = _pan;
        if (tracker != null && _viewModel != null)
        {
            _dragPose = _viewModel.PoseForDisplay(tracker).Pose;
            Point scene = ToScene(point);
            _dragGrabOffset = new Vector(scene.X - _dragPose.X, scene.Y - _dragPose.Y);
        }

        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point point = e.GetPosition(this);
        if (_drag == DragKind.None)
        {
            UpdateCursor(point);
            return;
        }

        e.Handled = true;
        if (_drag == DragKind.Pan)
        {
            _pan = _dragPan + (point - _dragStart);
            InvalidateVisual();
            return;
        }

        if (_viewModel is not { } viewModel || _dragTracker is not { } tracker)
            return;

        Point scene = ToScene(point);
        switch (_drag)
        {
            case DragKind.Move:
                viewModel.MoveTracker(tracker, scene.X - _dragGrabOffset.X, scene.Y - _dragGrabOffset.Y);
                break;
            case DragKind.ResizePattern:
            {
                (double u, double v) = ToPatternAxes(_dragPose, scene);
                viewModel.SetPatternSize(
                    tracker,
                    (int)Math.Round(Math.Abs(u) / _dragPose.Scale),
                    (int)Math.Round(Math.Abs(v) / _dragPose.Scale));
                break;
            }
            case DragKind.ResizeSearch:
                viewModel.SetSearchSize(
                    tracker,
                    (int)Math.Round(Math.Abs(scene.X - _dragPose.X)),
                    (int)Math.Round(Math.Abs(scene.Y - _dragPose.Y)));
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag != DragKind.None)
        {
            e.Pointer.Capture(null);
            EndDrag();
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    private void EndDrag()
    {
        bool editedTracker = _drag is DragKind.Move or DragKind.ResizePattern or DragKind.ResizeSearch;
        _drag = DragKind.None;
        _dragTracker = null;
        if (editedTracker)
            _viewModel?.UpdateFeature();
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (ZoomAround(e.GetPosition(this), Math.Pow(1.2, e.Delta.Y)))
            e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Delete && _viewModel is { IsTracking.Value: false, SelectedTracker.Value: not null } viewModel)
        {
            viewModel.RemoveSelectedTracker();
            e.Handled = true;
        }
    }

    private void UpdateCursor(Point point)
    {
        if (HitTest(point) is not { } hit)
        {
            Cursor = null;
            return;
        }

        Cursor = hit.Part == DragKind.Move ? s_moveCursor : s_resizeCursor;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        InvalidateVisual();
    }

    private enum DragKind
    {
        None,
        Pan,
        Move,
        ResizePattern,
        ResizeSearch,
    }
}
