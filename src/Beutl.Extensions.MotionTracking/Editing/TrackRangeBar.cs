using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.VisualTree;
using Beutl.Extensions.MotionTracking.Tracking;

namespace Beutl.Extensions.MotionTracking.Editing;

/// <summary>
/// The frames of the source element from left to right, with a row for each tracker that shows which frames it
/// has and how well they matched. Clicking or dragging moves the playhead.
/// </summary>
internal sealed class TrackRangeBar : Control
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<TrackRangeBar, IBrush?>(nameof(Background), new ImmutableSolidColorBrush(Color.FromRgb(32, 32, 36)));

    public static readonly StyledProperty<IBrush?> PlayheadBrushProperty =
        AvaloniaProperty.Register<TrackRangeBar, IBrush?>(nameof(PlayheadBrush), Brushes.White);

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<TrackRangeBar, CornerRadius>(nameof(CornerRadius));

    private const double RowHeight = 6;
    private const double RowGap = 2;
    private const double Padding = 4;
    private const double PlayheadWidth = 2;

    private static readonly IBrush s_lowConfidence = new ImmutableSolidColorBrush(Color.FromRgb(255, 92, 72));

    private MotionTrackingTabViewModel? _viewModel;
    private bool _seeking;

    static TrackRangeBar()
    {
        AffectsRender<TrackRangeBar>(BackgroundProperty, PlayheadBrushProperty, CornerRadiusProperty);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <summary>The line at the playhead's frame.</summary>
    public IBrush? PlayheadBrush
    {
        get => GetValue(PlayheadBrushProperty);
        set => SetValue(PlayheadBrushProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public MotionTrackingTabViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            Listen(false);
            _viewModel = value;
            Listen(true);
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    private void Listen(bool listen)
    {
        if (_viewModel is not { } viewModel || !this.IsAttachedToVisualTree())
            return;

        viewModel.OverlayChanged -= OnChanged;
        if (listen)
            viewModel.OverlayChanged += OnChanged;
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

    private void OnChanged(object? sender, EventArgs e)
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int rows = Math.Max(_viewModel?.Trackers.Count ?? 1, 1);
        return new Size(0, Padding * 2 + rows * RowHeight + (rows - 1) * RowGap);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var shape = new RoundedRect(new Rect(Bounds.Size), CornerRadius);
        context.DrawRectangle(Background, null, shape);
        if (_viewModel is not { FrameCount.Value: > 0 } viewModel)
            return;

        // The rows run to the ends, where the corners round them off.
        using DrawingContext.PushedState clip = context.PushClip(shape);
        long count = viewModel.FrameCount.Value;
        double width = Bounds.Width;
        double y = Padding;
        foreach (TrackerViewModel tracker in viewModel.Trackers)
        {
            DrawRow(context, tracker, count, width, y);
            y += RowHeight + RowGap;
        }

        if (viewModel.DisplayFrame.Value is { } frame && PlayheadBrush is { } playhead)
        {
            double x = (frame + 0.5) / count * width;
            context.FillRectangle(playhead, new Rect(x - PlayheadWidth / 2, 0, PlayheadWidth, Bounds.Height));
        }
    }

    // A long clip has several frames to a pixel; a run of them is drawn as one, in the colour of the worst among them.
    private static void DrawRow(DrawingContext context, TrackerViewModel tracker, long count, double width, double y)
    {
        TrackSamples samples = tracker.Samples;
        float lowBelow = tracker.MinConfidence.Value + 0.1f;
        double runStart = -1;
        double runEnd = -1;
        bool runLow = false;
        for (int i = 0; i < samples.Count; i++)
        {
            long frame = samples.Frames[i];
            double left = frame / (double)count * width;
            double right = (frame + 1) / (double)count * width;
            bool low = samples.Values[i].Confidence < lowBelow;
            if (runStart >= 0 && left - runEnd < 0.5 && low == runLow)
            {
                runEnd = right;
                continue;
            }

            Flush();
            runStart = left;
            runEnd = right;
            runLow = low;
        }

        Flush();

        void Flush()
        {
            if (runStart < 0)
                return;

            IBrush brush = runLow ? s_lowConfidence : tracker.Brush;
            context.FillRectangle(brush, new Rect(runStart, y, Math.Max(runEnd - runStart, 1), RowHeight));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _seeking = true;
        e.Pointer.Capture(this);
        Seek(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_seeking)
            Seek(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_seeking)
        {
            _seeking = false;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _seeking = false;
    }

    private void Seek(double x)
    {
        if (_viewModel is { FrameCount.Value: > 0 } viewModel && Bounds.Width > 0)
            viewModel.SeekToFrame((long)(x / Bounds.Width * viewModel.FrameCount.Value));
    }
}
