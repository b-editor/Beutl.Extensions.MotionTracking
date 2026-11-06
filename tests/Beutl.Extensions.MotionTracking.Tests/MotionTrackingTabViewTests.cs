using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Beutl.Extensions.MotionTracking.Editing;
using Beutl.Extensions.MotionTracking.Tracking;
using Beutl.ProjectSystem;

namespace Beutl.Extensions.MotionTracking.Tests;

// Drives the tool tab's view in a headless Avalonia session.
public class MotionTrackingTabViewTests
{
    private const float CenterX = EditorFixture.Width / 2f;
    private const float CenterY = EditorFixture.Height / 2f;

    private static T[] Find<T>(Control root)
        where T : Control => root.GetVisualDescendants().OfType<T>().Where(c => c.IsEffectivelyVisible).ToArray();

    private static (Window Window, MotionTrackingTabView View) Show(MotionTrackingTabViewModel tab, double width = 1280, double height = 720, ThemeVariant? theme = null)
    {
        var view = new MotionTrackingTabView { DataContext = tab };
        var window = new Window { Width = width, Height = height, Content = view, RequestedThemeVariant = theme ?? ThemeVariant.Dark };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    private static void Render(Window window, string name)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        using var capture = window.CaptureRenderedFrame();
        Assert.That(capture, Is.Not.Null);
        string directory = Environment.GetEnvironmentVariable("MOTION_TRACKING_UI_RENDER_DIR")
                           ?? Path.Combine(TestContext.CurrentContext.WorkDirectory, "rendered");
        Directory.CreateDirectory(directory);
        capture!.Save(Path.Combine(directory, name), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    // The canvas fits the frame in with a margin of 8 and centres it.
    private static Point ToWindow(Window window, TrackerCanvas canvas, double x, double y)
    {
        double scale = Math.Min((canvas.Bounds.Width - 16) / EditorFixture.Width, (canvas.Bounds.Height - 16) / EditorFixture.Height);
        var inCanvas = new Point(
            (canvas.Bounds.Width - EditorFixture.Width * scale) / 2 + x * scale,
            (canvas.Bounds.Height - EditorFixture.Height * scale) / 2 + y * scale);
        return canvas.TranslatePoint(inCanvas, window)!.Value;
    }

    private static void Drag(Window window, Point from, Point to)
    {
        window.MouseMove(from);
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2));
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitForFrame(MotionTrackingTabViewModel tab)
    {
        for (int i = 0; i < 400 && tab.FrameImage == null; i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.That(tab.FrameImage, Is.Not.Null, tab.Status.Value);
    }

    [AvaloniaTest]
    public void TheToolIsOfferedInTheToolPickers()
    {
        using var f = new EditorFixture();
        var extension = new MotionTrackingTabExtension();

        Assert.That(extension.Header, Is.EqualTo("モーショントラッキング"));
        Assert.That(extension.GetType().GetCustomAttributes(typeof(Beutl.Extensibility.ExportAttribute), false), Is.Not.Empty);
        Assert.That(extension.GetIcon(), Is.Not.Null);
        Assert.That(extension.TryCreateContent(f.Context, out Control? content), Is.True);
        Assert.That(content, Is.TypeOf<MotionTrackingTabView>());
        Assert.That(extension.TryCreateContext(f.Context, out Beutl.Extensibility.IToolContext? context), Is.True);
        Assert.That(context, Is.TypeOf<MotionTrackingTabViewModel>());
        Assert.That(context!.Extension, Is.SameAs(extension));
        context.Dispose();
        ((MotionTrackingTabView)content!).Dispose();
    }

    [AvaloniaTest]
    public void TheEmptyTabAsksForAVideo()
    {
        using var f = new EditorFixture();
        var tab = f.OpenTab();
        (Window window, MotionTrackingTabView view) = Show(tab, 900, 500);

        Assert.That(Find<TextBlock>(view).Any(t => t.Text?.Contains("追跡する動画") == true), Is.True);
        Assert.That(Find<TrackerCanvas>(view), Is.Empty);
        Render(window, "empty.png");

        // The button there uses the selection, as the message says.
        Element source = f.AddMovingPicture(1, 0, 10);
        f.Selection.Select(source);
        Find<Button>(view).Single(b => b.Content as string == "選択中の要素を使う").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.That(tab.Source.Value, Is.SameAs(source));
        Assert.That(Find<TrackerCanvas>(view), Is.Not.Empty);
    }

    [AvaloniaTest]
    public async Task ShowsTheFrameWithTrackersAndTheirPaths()
    {
        HeadlessScene.EnsureAvailable();
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(4, 2f, 30);
        Element target = f.AddMarker(EditorFixture.Frame(40));
        var tab = f.OpenTab(source);
        tab.SetTarget(target);
        tab.MoveTracker(tab.Trackers[0], 180, 120);
        tab.AddTracker(420, 90);
        (Window window, MotionTrackingTabView view) = Show(tab);
        await WaitForFrame(tab);
        Render(window, "placed.png");

        await tab.TrackAsync(1);
        f.Clock.Seek(EditorFixture.Frame(15));
        for (int i = 0; i < 100 && tab.CurrentFrame.Value != 15; i++)
            await Task.Delay(10);
        await Task.Delay(300);
        Dispatcher.UIThread.RunJobs();
        Assert.That(tab.Trackers.All(t => t.Samples.Count == 31), Is.True, tab.Status.Value);
        Render(window, "tracked.png");

        // Everything open, as far as it fits.
        Find<Expander>(view).Single().IsExpanded = true;
        tab.Use.Value = MotionUse.Steady;
        tab.SelectedTracker.Value = tab.Trackers[0];
        Dispatcher.UIThread.RunJobs();
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(16);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        Render(window, "details.png");

        window.Close();
    }

    [AvaloniaTest]
    public async Task ShowsTheLayoutStackedWhenNarrow()
    {
        HeadlessScene.EnsureAvailable();
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(4, 2f, 30);
        var tab = f.OpenTab(source);
        (Window window, MotionTrackingTabView view) = Show(tab, 560, 900);
        await WaitForFrame(tab);

        TrackerCanvas canvas = Find<TrackerCanvas>(view).Single();
        Assert.That(canvas.Bounds.Width, Is.GreaterThan(500));
        Render(window, "narrow.png");
        window.Close();
    }

    [AvaloniaTest]
    public async Task TheBarButtonsSayWhatTheyDo()
    {
        HeadlessScene.EnsureAvailable();
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(4, 2f, 30);
        var tab = f.OpenTab(source);
        (Window window, MotionTrackingTabView view) = Show(tab);
        await WaitForFrame(tab);
        Settle();

        // With room for them, the bar's buttons say what they do.
        string[] labeled = ["変更", "全体を表示"];
        Assert.That(labeled.Where(name => !ShowsLabel(view, name)), Is.Empty);

        // In a very narrow tab they keep their icons and tooltips, and drop the labels that no longer fit.
        window.Width = 340;
        Settle();
        Assert.That(labeled.Where(name => ShowsLabel(view, name)), Is.Empty);
        Assert.That(labeled.Select(name => ToolTip.GetTip(ButtonNamed(view, name))), Has.All.Not.Null);
        Render(window, "bar-narrow.png");

        window.Width = 1280;
        Settle();
        Assert.That(labeled.Where(name => !ShowsLabel(view, name)), Is.Empty, "The labels come back when there is room again.");
        window.Close();

        static bool ShowsLabel(Control view, string name) => Find<TextBlock>(ButtonNamed(view, name)).Any(t => t.Text == name);
    }

    [AvaloniaTest]
    public async Task TheTrackButtonsComeBetweenTheTrackersAndApplying()
    {
        HeadlessScene.EnsureAvailable();
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(4, 2f, 30);
        var tab = f.OpenTab(source);
        (Window window, MotionTrackingTabView view) = Show(tab);
        await WaitForFrame(tab);
        Settle();

        double trackers = TopOf(view, "トラッカー");
        double tracking = TopOf(view, "追跡");
        double applying = TopOf(view, "適用");
        Assert.That(trackers, Is.LessThan(tracking));
        Assert.That(tracking, Is.LessThan(applying));
        string[] names = ["最初まで追跡", "最後まで追跡", "1 フレーム戻って追跡", "1 フレーム進んで追跡"];
        Assert.That(names.Select(name => ButtonNamed(view, name).TranslatePoint(default, view)!.Value.Y), Has.All.InRange(tracking, applying));
        Assert.That(Find<Button>(view).Select(AutomationProperties.GetName), Has.None.EqualTo("停止"), "Stop shows only while tracking.");
        Render(window, "track.png");

        // While tracking, the button that stops it takes the place of those that start it.
        tab.IsTracking.Value = true;
        Settle();
        Assert.That(Find<Button>(view).Select(AutomationProperties.GetName), Has.Member("停止").And.None.EqualTo("最後まで追跡"));
        tab.IsTracking.Value = false;
        Settle();

        // The button tracks the way its label says.
        Click(ButtonNamed(view, "最後まで追跡"));
        for (int i = 0; i < 500 && (tab.IsTracking.Value || tab.Trackers[0].Samples.Count == 0); i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.That(tab.Trackers[0].Samples.Count, Is.GreaterThan(1), tab.Status.Value);
        window.Close();

        // The top of the highest text that reads exactly so, such as a section's title.
        static double TopOf(Control view, string text) =>
            Find<TextBlock>(view).Where(t => t.Text == text).Min(t => t.TranslatePoint(default, view)!.Value.Y);
    }

    [AvaloniaTest]
    public void RemovingATrackerTakesItsRowAway()
    {
        HeadlessScene.EnsureAvailable();
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(4, 2f, 30);
        var tab = f.OpenTab(source);
        tab.AddTracker(420, 90);
        (Window window, MotionTrackingTabView view) = Show(tab);
        Settle();
        Assert.That(Find<ListBoxItem>(view), Has.Length.EqualTo(2));

        tab.SelectedTracker.Value = tab.Trackers[0];
        tab.RemoveSelectedTracker();
        Settle();

        // The row of the removed tracker must not stay behind, over the row that took its place.
        ListBoxItem[] rows = Find<ListBoxItem>(view);
        Assert.That(rows, Has.Length.EqualTo(1));
        Assert.That(Find<TextBlock>(rows[0]).Select(t => t.Text), Has.Member("トラッカー 2"));
        Render(window, "removed.png");
        window.Close();
    }

    private static Button ButtonNamed(Control view, string name) =>
        Find<Button>(view).Single(b => AutomationProperties.GetName(b) == name);

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void Settle()
    {
        for (int i = 0; i < 5; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaTest]
    public void DraggingATrackerMovesIt()
    {
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(1, 0, 10);
        var tab = f.OpenTab(source);
        (Window window, MotionTrackingTabView view) = Show(tab);
        TrackerCanvas canvas = Find<TrackerCanvas>(view).Single();
        TrackerViewModel tracker = tab.Trackers.Single();

        Drag(window, ToWindow(window, canvas, CenterX, CenterY), ToWindow(window, canvas, CenterX + 50, CenterY - 20));

        Assert.That(tracker.Anchor.X, Is.EqualTo(CenterX + 50).Within(0.5f));
        Assert.That(tracker.Anchor.Y, Is.EqualTo(CenterY - 20).Within(0.5f));
        Assert.That(tracker.Samples.Count, Is.Zero);
        window.Close();
    }

    [AvaloniaTest]
    public void DraggingOnATrackedFrameCorrectsThatFrame()
    {
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(1, 0, 10);
        var tab = f.OpenTab(source);
        (Window window, MotionTrackingTabView view) = Show(tab);
        TrackerCanvas canvas = Find<TrackerCanvas>(view).Single();
        TrackerViewModel tracker = tab.Trackers.Single();
        tracker.Samples.Set(0, new TrackSample(new TrackPose(CenterX, CenterY), 0.8f));
        tracker.Samples.Set(5, new TrackSample(new TrackPose(CenterX + 5, CenterY), 0.8f));

        f.Clock.Seek(EditorFixture.Frame(5));
        Drag(window, ToWindow(window, canvas, CenterX + 5, CenterY), ToWindow(window, canvas, CenterX + 15, CenterY + 10));

        Assert.That(tracker.Samples.TryGet(5, out TrackSample corrected), Is.True);
        Assert.That(corrected.Pose.X, Is.EqualTo(CenterX + 15).Within(0.5f));
        Assert.That(corrected.Confidence, Is.EqualTo(1));
        Assert.That(tracker.Samples.TryGet(0, out TrackSample other), Is.True);
        Assert.That(other.Pose.X, Is.EqualTo(CenterX));
        window.Close();
    }

    [AvaloniaTest]
    public void DraggingCornersResizesThePatternAndTheSearchArea()
    {
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(1, 0, 10);
        var tab = f.OpenTab(source);
        (Window window, MotionTrackingTabView view) = Show(tab);
        TrackerCanvas canvas = Find<TrackerCanvas>(view).Single();
        TrackerViewModel tracker = tab.Trackers.Single();
        tracker.PatternHalfWidth.Value = 10;
        tracker.PatternHalfHeight.Value = 10;
        tracker.SearchHalfWidth.Value = 60;
        tracker.SearchHalfHeight.Value = 60;

        Drag(window, ToWindow(window, canvas, CenterX + 10, CenterY + 10), ToWindow(window, canvas, CenterX + 25, CenterY + 18));

        Assert.That(tracker.PatternHalfWidth.Value, Is.EqualTo(25).Within(1));
        Assert.That(tracker.PatternHalfHeight.Value, Is.EqualTo(18).Within(1));

        Drag(window, ToWindow(window, canvas, CenterX - 60, CenterY - 60), ToWindow(window, canvas, CenterX - 80, CenterY - 45));

        Assert.That(tracker.SearchHalfWidth.Value, Is.EqualTo(80).Within(1));
        Assert.That(tracker.SearchHalfHeight.Value, Is.EqualTo(45).Within(1));

        // The search area cannot end up inside the pattern; growing the pattern takes it along.
        tab.SetPatternSize(tracker, 100, 100);
        Assert.That(tracker.SearchHalfWidth.Value, Is.GreaterThan(100));
        Assert.That(tracker.SearchHalfHeight.Value, Is.GreaterThan(100));
        window.Close();
    }

    [AvaloniaTest]
    public void DoubleClickingAnEmptySpotAddsATracker()
    {
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(1, 0, 10);
        var tab = f.OpenTab(source);
        (Window window, MotionTrackingTabView view) = Show(tab);
        TrackerCanvas canvas = Find<TrackerCanvas>(view).Single();
        Point spot = ToWindow(window, canvas, 100, 100);

        window.MouseMove(spot);
        window.MouseDown(spot, MouseButton.Left);
        window.MouseUp(spot, MouseButton.Left);
        window.MouseDown(spot, MouseButton.Left);
        window.MouseUp(spot, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.That(tab.Trackers, Has.Count.EqualTo(2));
        Assert.That(tab.Trackers[1].Anchor.X, Is.EqualTo(100).Within(0.5f));
        Assert.That(tab.SelectedTracker.Value, Is.SameAs(tab.Trackers[1]));
        window.Close();
    }

    [AvaloniaTest]
    public void TheWheelZoomsAroundThePointer()
    {
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(1, 0, 10);
        var tab = f.OpenTab(source);
        (Window window, MotionTrackingTabView view) = Show(tab);
        TrackerCanvas canvas = Find<TrackerCanvas>(view).Single();
        TrackerViewModel tracker = tab.Trackers.Single();

        // After zooming in on the tracker it is still under the pointer and can be dragged from there.
        Point point = ToWindow(window, canvas, CenterX, CenterY);
        window.MouseWheel(point, new Vector(0, 6));
        Dispatcher.UIThread.RunJobs();
        Drag(window, point, new Point(point.X + 40, point.Y));

        Assert.That(tracker.Anchor.X, Is.GreaterThan(CenterX));
        Assert.That(tracker.Anchor.X, Is.LessThan(CenterX + 25), "Zoomed in, the same drag moves less of the frame.");
        Assert.That(tracker.Anchor.Y, Is.EqualTo(CenterY).Within(0.5f));
        window.Close();
    }

    [AvaloniaTest]
    public void TheEditorsFollowTheSelectedTracker()
    {
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(1, 0, 10);
        var tab = f.OpenTab(source);
        (Window window, MotionTrackingTabView view) = Show(tab);
        TrackerViewModel first = tab.Trackers.Single();
        first.PatternHalfWidth.Value = 11;
        TrackerViewModel second = tab.AddTracker(100, 100);
        tab.SetPatternSize(second, 22, 22);
        Dispatcher.UIThread.RunJobs();

        Find<Expander>(view).Single().IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
        NumericUpDown[] numbers = Find<NumericUpDown>(view);
        Assert.That(numbers.Any(n => n.Value == 44), Is.True);

        tab.SelectedTracker.Value = first;
        Dispatcher.UIThread.RunJobs();
        Assert.That(Find<NumericUpDown>(view).Any(n => n.Value == 22), Is.True);
        Assert.That(second.PatternHalfWidth.Value, Is.EqualTo(22), "Showing another tracker does not write to it.");

        Find<Expander>(view).Single().IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
        Find<ComboBox>(view).First().SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.That(first.Model.Value, Is.EqualTo(TrackerMotionModel.Position));
        Assert.That(second.Model.Value, Is.EqualTo(TrackerMotionModel.PositionRotationScale));
        window.Close();
    }

    [AvaloniaTest]
    public void StabilizingHidesTheTargetRow()
    {
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(1, 0, 10);
        var tab = f.OpenTab(source);
        (Window window, MotionTrackingTabView view) = Show(tab);
        Assert.That(Find<TextBlock>(view).Any(t => t.Text == "適用先"), Is.True);

        tab.Use.Value = MotionUse.Lock;
        Dispatcher.UIThread.RunJobs();

        Assert.That(Find<TextBlock>(view).Any(t => t.Text == "適用先"), Is.False);
        window.Close();
    }

    [AvaloniaTest]
    public void TheFramesBarMovesThePlayhead()
    {
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(1, 0, 99);
        var tab = f.OpenTab(source);
        (Window window, MotionTrackingTabView view) = Show(tab);
        TrackRangeBar bar = Find<TrackRangeBar>(view).Single();
        Point point = bar.TranslatePoint(new Point(bar.Bounds.Width / 2, bar.Bounds.Height / 2), window)!.Value;

        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.That(tab.CurrentFrame.Value, Is.EqualTo(50).Within(1));
        window.Close();
    }
}
