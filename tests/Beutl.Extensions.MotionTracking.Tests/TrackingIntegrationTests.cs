using System.Text.Json.Nodes;
using Avalonia.Headless.NUnit;
using Beutl.Animation;
using Beutl.Composition;
using Beutl.Extensions.MotionTracking.Editing;
using Beutl.Extensions.MotionTracking.Properties;
using Beutl.Extensions.MotionTracking.Tracking;
using Beutl.Graphics;
using Beutl.Graphics.Shapes;
using Beutl.Graphics.Transformation;
using Beutl.Media;
using Beutl.ProjectSystem;

namespace Beutl.Extensions.MotionTracking.Tests;

// Runs the tab against scenes rendered by Beutl's own pipeline: what the trackers see is what a video element would give.
[SetUICulture("ja-JP")]
public class TrackingIntegrationTests
{
    private const float CenterX = EditorFixture.Width / 2f;
    private const float CenterY = EditorFixture.Height / 2f;

    private static Matrix MatrixAt(Drawable drawable, long frame)
        => drawable.Transform.Value!.CreateMatrix(new CompositionContext(EditorFixture.Frame(frame)));

    private static Drawable DrawableOf(Element element) => element.Objects.OfType<Drawable>().Single();

    private static TransformGroup? GroupOf(Element element) => TrackingKeyframes.FindGroup(DrawableOf(element));

    [AvaloniaTest]
    public async Task FollowsAMovingPictureThroughTheRenderer()
    {
        HeadlessScene.EnsureAvailable();
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(1.5f, -0.75f, 30);
        var tab = f.OpenTab(source);
        TrackerViewModel tracker = tab.Trackers.Single();

        await tab.TrackAsync(1);

        Assert.That(tab.IsTracking.Value, Is.False);
        Assert.That(tracker.Samples.Count, Is.EqualTo(31), tab.Status.Value);
        for (int frame = 0; frame <= 30; frame++)
        {
            Assert.That(tracker.Samples.TryGet(frame, out TrackSample sample), Is.True);
            Assert.That(sample.Pose.X, Is.EqualTo(CenterX + 1.5f * frame).Within(0.25f), $"x at frame {frame}");
            Assert.That(sample.Pose.Y, Is.EqualTo(CenterY - 0.75f * frame).Within(0.25f), $"y at frame {frame}");
            Assert.That(sample.Confidence, Is.GreaterThan(0.9f));
        }

        Assert.That(tab.CurrentFrame.Value, Is.EqualTo(30), "The playhead goes to where the track ended.");
        Assert.That(tab.Status.Value, Does.Contain("30 フレーム"));
    }

    [AvaloniaTest]
    public async Task TracksFromThePlayheadInEitherDirection()
    {
        HeadlessScene.EnsureAvailable();
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(1, 0.5f, 30);
        var tab = f.OpenTab(source);
        TrackerViewModel tracker = tab.Trackers.Single();
        f.Clock.Seek(EditorFixture.Frame(15));
        tab.MoveTracker(tracker, CenterX, CenterY);

        await tab.TrackAsync(-1);

        Assert.That(tracker.Samples.FirstFrame, Is.EqualTo(0));
        Assert.That(tracker.Samples.LastFrame, Is.EqualTo(15));
        Assert.That(tracker.PoseAt(0).X, Is.EqualTo(CenterX - 15).Within(0.25f));
        Assert.That(tab.CurrentFrame.Value, Is.EqualTo(0));

        // Forwards from the frame it started from, it joins up with what it found on the way back.
        f.Clock.Seek(EditorFixture.Frame(15));
        await tab.TrackAsync(1, singleFrame: true);

        Assert.That(tracker.Samples.LastFrame, Is.EqualTo(16));
        Assert.That(tracker.PoseAt(16).X, Is.EqualTo(CenterX + 1).Within(0.25f));
        Assert.That(tab.CurrentFrame.Value, Is.EqualTo(16));
    }

    [AvaloniaTest]
    public async Task ObjectsOnTopOfTheSourceDoNotDisturbTracking()
    {
        HeadlessScene.EnsureAvailable();
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(1.5f, 0, 30);

        // A big flat object right over the tracker, which would hide the pattern completely in the composite.
        f.AddMarker(EditorFixture.Frame(60), layer: 1, size: 400);
        var tab = f.OpenTab(source);
        TrackerViewModel tracker = tab.Trackers.Single();

        await tab.TrackAsync(1);

        Assert.That(tracker.Samples.Count, Is.EqualTo(31), tab.Status.Value);
        Assert.That(tracker.PoseAt(30).X, Is.EqualTo(CenterX + 45).Within(0.25f));
    }

    [AvaloniaTest]
    public async Task TheElementsTimeOnTheTimelineDoesNotMatterToTheTrack()
    {
        HeadlessScene.EnsureAvailable();
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(1, 0, 20, startFrame: 40);
        var tab = f.OpenTab(source);
        TrackerViewModel tracker = tab.Trackers.Single();

        Assert.That(tab.CurrentFrame.Value, Is.Null, "The playhead is before the element.");
        f.Clock.Seek(EditorFixture.Frame(40));
        Assert.That(tab.CurrentFrame.Value, Is.EqualTo(0));

        await tab.TrackAsync(1);

        Assert.That(tracker.Samples.Count, Is.EqualTo(21), tab.Status.Value);
        Assert.That(tracker.PoseAt(20).X, Is.EqualTo(CenterX + 20).Within(0.25f));

        // Moving the element takes the track along.
        source.Start.Value = EditorFixture.Frame(100);
        Assert.That(tab.CurrentFrame.Value, Is.Null);
        f.Clock.Seek(EditorFixture.Frame(120));
        Assert.That(tab.CurrentFrame.Value, Is.EqualTo(20));
    }

    [AvaloniaTest]
    public async Task AppliedKeyframesMoveTheTargetWithTheTrack()
    {
        HeadlessScene.EnsureAvailable();
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(2, -1, 30);
        Element target = f.AddMarker(EditorFixture.Frame(40));
        var tab = f.OpenTab(source);
        tab.SetTarget(target);
        await tab.TrackAsync(1);

        // The target stays where it is at the frame on the playhead.
        f.Clock.Seek(EditorFixture.Frame(10));
        tab.Apply();

        Assert.That(tab.Status.Value, Does.Contain("キーフレーム"), tab.Status.Value);
        source.IsEnabled.Value = false;
        foreach ((long frame, double x, double y) in new (long, double, double)[] { (0, -20, 10), (10, 0, 0), (25, 30, -15), (30, 40, -20) })
        {
            using Bitmap rendered = HeadlessScene.Render(f.Scene, EditorFixture.Frame(frame));
            (double X, double Y)? found = HeadlessScene.FindBright(rendered);
            Assert.That(found, Is.Not.Null);
            Assert.That(found!.Value.X, Is.EqualTo(CenterX + x).Within(0.6), $"x at frame {frame}");
            Assert.That(found!.Value.Y, Is.EqualTo(CenterY + y).Within(0.6), $"y at frame {frame}");
        }
    }

    [AvaloniaTest]
    public async Task ApplyingIsOneStepInTheHistory()
    {
        HeadlessScene.EnsureAvailable();
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(2, 0, 10);
        Element target = f.AddMarker(EditorFixture.Frame(20));
        var tab = f.OpenTab(source);
        tab.SetTarget(target);
        await tab.TrackAsync(1);
        f.Clock.Seek(EditorFixture.Frame(0));
        int before = f.History.UndoCount;

        tab.Apply();

        Assert.That(f.History.UndoCount, Is.EqualTo(before + 1));
        Assert.That(GroupOf(target), Is.Not.Null);
        f.History.Undo();
        Assert.That(GroupOf(target), Is.Null);
        f.History.Redo();
        Assert.That(GroupOf(target), Is.Not.Null);
        Assert.That(MatrixAt(DrawableOf(target), 10).M31, Is.EqualTo(20).Within(0.3f));
    }

    [AvaloniaTest]
    public async Task ApplyingAgainReplacesTheKeyframesOfBefore()
    {
        HeadlessScene.EnsureAvailable();
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(2, 0, 10);
        Element target = f.AddMarker(EditorFixture.Frame(20));
        var tab = f.OpenTab(source);
        tab.SetTarget(target);
        await tab.TrackAsync(1);
        f.Clock.Seek(EditorFixture.Frame(0));

        tab.Apply();
        tab.Density.Value = KeyframeDensity.EveryFrame;
        tab.Apply();

        var root = (TransformGroup)DrawableOf(target).Transform.Value!;
        Assert.That(root.Children.Count(child => child.Name.Value == Strings.MotionTracking), Is.EqualTo(1));
        Assert.That(MatrixAt(DrawableOf(target), 10).M31, Is.EqualTo(20).Within(0.3f), "The motion is not applied twice.");
    }

    [AvaloniaTest]
    public async Task TheTrackAppliesOutsideExistingTransforms()
    {
        HeadlessScene.EnsureAvailable();
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(2, 0, 10);
        Element target = f.AddMarker(EditorFixture.Frame(20));
        var root = (TransformGroup)DrawableOf(target).Transform.Value!;
        root.Children.Add(new TranslateTransform(100, 50));
        root.Children.Add(new ScaleTransform(100, 100, 200));
        var tab = f.OpenTab(source);
        tab.SetTarget(target);
        await tab.TrackAsync(1);

        tab.Apply();

        // Scene-space motion: the object's own scale of 200 % does not double it.
        Matrix start = MatrixAt(DrawableOf(target), 0);
        Matrix end = MatrixAt(DrawableOf(target), 10);
        Assert.That(end.M31 - start.M31, Is.EqualTo(20).Within(0.3f));

        // What the preview's handles edit is still the object's own translate.
        Assert.That(root.Children[0], Is.TypeOf<TransformGroup>());
        Assert.That(root.Children[1], Is.TypeOf<TranslateTransform>());
    }

    [AvaloniaTest]
    public async Task KeyframesAreCountedFromTheStartOfTheTarget()
    {
        HeadlessScene.EnsureAvailable();
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(2, 0, 30);
        Element target = f.AddMarker(EditorFixture.Frame(10));
        target.Start.Value = EditorFixture.Frame(10);
        var tab = f.OpenTab(source);
        tab.SetTarget(target);
        tab.Density.Value = KeyframeDensity.EveryFrame;
        await tab.TrackAsync(1);
        f.Clock.Seek(EditorFixture.Frame(10));

        tab.Apply();

        Drawable drawable = DrawableOf(target);
        Assert.That(MatrixAt(drawable, 10).M31, Is.EqualTo(0).Within(0.3f));
        Assert.That(MatrixAt(drawable, 15).M31, Is.EqualTo(10).Within(0.3f));

        // Only the frames the target is there for get keyframes.
        var translate = (TranslateTransform)GroupOf(target)!.Children[0];
        Assert.That(translate.X.GetKeyFrameAnimation()!.KeyFrames, Has.Count.EqualTo(11));
    }

    [AvaloniaTest]
    public async Task TwoTrackersDriveRotationAndScale()
    {
        HeadlessScene.EnsureAvailable();
        using var f = new EditorFixture();
        Element source = f.AddTurningPicture(degrees: 2f, scalePercent: 1f, frames: 20);
        Element target = f.AddMarker(EditorFixture.Frame(30));
        var tab = f.OpenTab(source);
        tab.SetTarget(target);
        TrackerViewModel left = tab.Trackers.Single();
        tab.MoveTracker(left, CenterX - 120, CenterY);
        TrackerViewModel right = tab.AddTracker(CenterX + 120, CenterY);

        await tab.TrackAsync(1);

        Assert.That(left.Samples.Count, Is.EqualTo(21), tab.Status.Value);
        Assert.That(right.Samples.Count, Is.EqualTo(21), tab.Status.Value);
        f.Clock.Seek(EditorFixture.Frame(0));
        tab.UseRotation.Value = true;
        tab.UseScale.Value = true;
        tab.Apply();

        TransformGroup group = GroupOf(target)!;
        var rotation = group.Children.OfType<RotationTransform>().Single();
        var scale = group.Children.OfType<ScaleTransform>().Single();
        var context = new CompositionContext(EditorFixture.Frame(20));
        Assert.That(context.Get(rotation.Rotation), Is.EqualTo(40).Within(0.5f));
        Assert.That(context.Get(scale.Scale), Is.EqualTo(120).Within(0.7f));

        // Everything turns about the middle of the frame, which is where the target is too: it stays there.
        Assert.That(MatrixAt(DrawableOf(target), 20).M31, Is.EqualTo(0).Within(1f));
        Assert.That(MatrixAt(DrawableOf(target), 20).M32, Is.EqualTo(0).Within(1f));
    }

    [AvaloniaTest]
    public async Task OneTrackerCanMeasureRotationAndScaleItself()
    {
        HeadlessScene.EnsureAvailable();
        using var f = new EditorFixture();
        Element source = f.AddTurningPicture(degrees: 2f, scalePercent: 1f, frames: 20);
        Element target = f.AddMarker(EditorFixture.Frame(30));
        var tab = f.OpenTab(source);
        tab.SetTarget(target);
        TrackerViewModel tracker = tab.Trackers.Single();
        tab.MoveTracker(tracker, CenterX + 100, CenterY - 50);

        await tab.TrackAsync(1);
        f.Clock.Seek(EditorFixture.Frame(0));
        tab.UseRotation.Value = true;
        tab.UseScale.Value = true;
        tab.Apply();

        Assert.That(tracker.Samples.Count, Is.EqualTo(21), tab.Status.Value);
        TransformGroup group = GroupOf(target)!;
        var context = new CompositionContext(EditorFixture.Frame(20));
        Assert.That(context.Get(group.Children.OfType<RotationTransform>().Single().Rotation), Is.EqualTo(40).Within(1f));
        Assert.That(context.Get(group.Children.OfType<ScaleTransform>().Single().Scale), Is.EqualTo(120).Within(1.5f));
    }

    [AvaloniaTest]
    public async Task OnlyThePositionIsAppliedUnlessAskedForMore()
    {
        HeadlessScene.EnsureAvailable();
        using var f = new EditorFixture();
        Element source = f.AddTurningPicture(degrees: 2f, scalePercent: 1f, frames: 10);
        Element target = f.AddMarker(EditorFixture.Frame(20));
        var tab = f.OpenTab(source);
        tab.SetTarget(target);
        await tab.TrackAsync(1);

        tab.Apply();

        Assert.That(GroupOf(target)!.Children.Single(), Is.TypeOf<TranslateTransform>());
    }

    [AvaloniaTest]
    public async Task APositionOnlyTrackerHasNoRotationOrScaleToWrite()
    {
        HeadlessScene.EnsureAvailable();
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(2, 0, 10);
        Element target = f.AddMarker(EditorFixture.Frame(20));
        var tab = f.OpenTab(source);
        tab.SetTarget(target);
        tab.Trackers[0].Model.Value = TrackerMotionModel.Position;
        await tab.TrackAsync(1);
        tab.UseRotation.Value = true;
        tab.UseScale.Value = true;

        tab.Apply();

        Assert.That(GroupOf(target)!.Children.Single(), Is.TypeOf<TranslateTransform>());
    }

    [AvaloniaTest]
    public async Task TextTurningAboutTheCornerOfItsBoxFollowsThePicture()
    {
        HeadlessScene.EnsureAvailable();
        using var f = new EditorFixture();
        Element source = f.AddTurningPicture(degrees: 2f, scalePercent: 1f, frames: 20);

        // Its transforms turn it about the corner of its box, which only laying the text out finds.
        var text = new TextBlock { Text = { Value = "Beutl" }, Size = { Value = 48 }, TransformOrigin = { Value = RelativePoint.TopLeft } };
        text.Fill.Value = new SolidColorBrush(Colors.White);
        ((TransformGroup)text.Transform.Value!).Children.Add(new TranslateTransform(-80, -30));
        Element target = f.Add(text, MediaTime.Zero, EditorFixture.Frame(30), layer: 1);
        var tab = f.OpenTab(source);
        tab.SetTarget(target);
        tab.MoveTracker(tab.Trackers.Single(), CenterX - 120, CenterY);
        tab.AddTracker(CenterX + 120, CenterY);
        await tab.TrackAsync(1);
        f.Clock.Seek(EditorFixture.Frame(0));
        tab.UseRotation.Value = true;
        tab.UseScale.Value = true;

        tab.Apply();

        // Like everything in the picture, it turns and grows about the middle of the frame.
        Assert.That(GroupOf(target), Is.Not.Null, tab.Status.Value);
        source.IsEnabled.Value = false;
        using Bitmap first = HeadlessScene.Render(f.Scene, EditorFixture.Frame(0));
        using Bitmap last = HeadlessScene.Render(f.Scene, EditorFixture.Frame(20));
        (double X, double Y) start = HeadlessScene.FindBright(first)!.Value;
        (double X, double Y) end = HeadlessScene.FindBright(last)!.Value;
        Matrix turn = MatrixAt(DrawableOf(source), 20);
        double x = start.X - CenterX, y = start.Y - CenterY;
        Assert.That(end.X, Is.EqualTo(CenterX + x * turn.M11 + y * turn.M21).Within(1.5));
        Assert.That(end.Y, Is.EqualTo(CenterY + x * turn.M12 + y * turn.M22).Within(1.5));
    }

    [AvaloniaTest]
    public async Task StabilizingHoldsThePictureStill()
    {
        HeadlessScene.EnsureAvailable();
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(3, -1.5f, 20);
        var tab = f.OpenTab(source);
        await tab.TrackAsync(1);
        tab.Use.Value = MotionUse.Lock;
        f.Clock.Seek(EditorFixture.Frame(0));

        tab.Apply();

        using Bitmap first = HeadlessScene.Render(f.Scene, EditorFixture.Frame(0));
        using Bitmap later = HeadlessScene.Render(f.Scene, EditorFixture.Frame(20));
        double difference = HeadlessScene.Difference(first, later, EditorFixture.Width / 2 - 40, EditorFixture.Height / 2 - 40, 80, 80);
        Assert.That(difference, Is.LessThan(2), "The tracked area does not move from frame to frame.");
    }

    [AvaloniaTest]
    public async Task TrackingAgainSeesThePictureWithoutItsStabilization()
    {
        HeadlessScene.EnsureAvailable();
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(3, 0, 20);
        var tab = f.OpenTab(source);
        TrackerViewModel tracker = tab.Trackers.Single();
        await tab.TrackAsync(1);
        tab.Use.Value = MotionUse.Lock;
        f.Clock.Seek(EditorFixture.Frame(0));
        tab.Apply();

        await tab.TrackAsync(1);

        // Were the correction in the picture, the track would stand still.
        Assert.That(tracker.PoseAt(20).X, Is.EqualTo(CenterX + 60).Within(0.25f));
    }

    [AvaloniaTest]
    public async Task RemovingTakesTheKeyframesOffAgain()
    {
        HeadlessScene.EnsureAvailable();
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(2, 0, 10);
        Element target = f.AddMarker(EditorFixture.Frame(20));
        var tab = f.OpenTab(source);
        tab.SetTarget(target);
        await tab.TrackAsync(1);
        tab.Apply();

        tab.RemoveApplied();

        Assert.That(GroupOf(target), Is.Null);
        f.History.Undo();
        Assert.That(GroupOf(target), Is.Not.Null);
    }

    [AvaloniaTest]
    public async Task AManualCorrectionIsWhatTrackingContinuesFrom()
    {
        HeadlessScene.EnsureAvailable();
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(1, 0, 20);
        var tab = f.OpenTab(source);
        TrackerViewModel tracker = tab.Trackers.Single();
        await tab.TrackAsync(1, singleFrame: true);
        Assert.That(tracker.PoseAt(1).X, Is.EqualTo(CenterX + 1).Within(0.25f));

        // Moved to another spot on the picture, the next frames follow that spot instead.
        tab.MoveTracker(tracker, CenterX - 40, CenterY + 30);
        await tab.TrackAsync(1);

        Assert.That(tracker.PoseAt(20).X, Is.EqualTo(CenterX - 40 + 19).Within(0.25f));
        Assert.That(tracker.PoseAt(20).Y, Is.EqualTo(CenterY + 30).Within(0.25f));
    }

    [AvaloniaTest]
    public async Task StoppingKeepsWhatWasFound()
    {
        HeadlessScene.EnsureAvailable();
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(1, 0, 400);
        var tab = f.OpenTab(source);
        TrackerViewModel tracker = tab.Trackers.Single();

        Task running = tab.TrackAsync(1);
        Assert.That(tab.IsTracking.Value, Is.True);
        tab.Stop();
        await running;

        Assert.That(tab.IsTracking.Value, Is.False);
        Assert.That(tab.Status.Value, Does.Contain("停止"));
        Assert.That(tracker.Samples.Count, Is.LessThan(401));
        Assert.That(tracker.Samples.TryGet(0, out _), Is.True);
    }

    [AvaloniaTest]
    public async Task AWeakSpotIsPointedOut()
    {
        HeadlessScene.EnsureAvailable();
        using var f = new EditorFixture();

        // On the straight edge of something big and flat, nothing tells the tracker where along the edge it is.
        Element source = f.AddMarker(EditorFixture.Frame(20), layer: 0, size: 300);
        var tab = f.OpenTab(source);
        TrackerViewModel tracker = tab.Trackers.Single();
        tab.MoveTracker(tracker, CenterX + 150, CenterY);
        for (int i = 0; i < 400 && tab.FrameImage == null; i++)
            await Task.Delay(10);
        await Task.Delay(50);
        tab.UpdateFeature();
        Assert.That(tab.FeatureStrength.Value, Does.StartWith("弱い"));

        await tab.TrackAsync(1, singleFrame: true);

        Assert.That(tab.Status.Value, Does.Contain("模様の少ない場所"));

        // On its corner there is no doubt.
        f.Clock.Seek(EditorFixture.Frame(0));
        tab.MoveTracker(tracker, CenterX + 150, CenterY + 150);
        await Task.Delay(50);
        tab.UpdateFeature();
        Assert.That(tab.FeatureStrength.Value, Does.Not.StartWith("弱い"));
    }

    [AvaloniaTest]
    public void AnElementCannotFollowItself()
    {
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(1, 0, 10);
        var tab = f.OpenTab(source);
        tab.SetTarget(source);
        tab.Trackers[0].Samples.Set(0, new TrackSample(new TrackPose(1, 1), 1));

        tab.Apply();

        Assert.That(tab.Status.Value, Does.Contain("同じ要素"));
        Assert.That(GroupOf(source), Is.Null);
    }

    [AvaloniaTest]
    public void TheSourceGoesAndComesBackWithItsElement()
    {
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(1, 0, 10);
        var tab = f.OpenTab(source);
        Assert.That(tab.Source.Value, Is.SameAs(source));

        f.Scene.RemoveChild(source);
        Assert.That(tab.Source.Value, Is.Null);
        Assert.That(tab.SourceMessage.Value, Does.Contain("削除"));

        f.Scene.AddChild(source);
        Assert.That(tab.Source.Value, Is.SameAs(source));
        Assert.That(tab.SourceMessage.Value, Is.Null);
    }

    [AvaloniaTest]
    public void TheSelectionPicksTheElements()
    {
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(1, 0, 10);
        Element target = f.AddMarker(EditorFixture.Frame(10));

        // Opening with a selected element starts on it.
        f.Selection.Select(source);
        var tab = f.OpenTab();
        Assert.That(tab.Source.Value, Is.SameAs(source));
        Assert.That(tab.Trackers, Has.Count.EqualTo(1));

        f.Selection.Select(target);
        tab.UseSelectionAsTarget();
        Assert.That(tab.Target.Value, Is.SameAs(target));

        tab.SelectSourceInTimeline();
        Assert.That(f.Selection.SelectedObject.Value, Is.SameAs(source));
    }

    [AvaloniaTest]
    public void ApplyingExplainsWhatIsMissing()
    {
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(1, 0, 10);
        var tab = f.OpenTab(source);

        tab.Apply();
        Assert.That(tab.Status.Value, Does.Contain("適用先"));

        tab.SetTarget(f.AddMarker(EditorFixture.Frame(10)));
        tab.Apply();
        Assert.That(tab.Status.Value, Does.Contain("追跡"));

        tab.UsePosition.Value = false;
        tab.Trackers[0].Samples.Set(0, new TrackSample(new TrackPose(1, 1), 1));
        tab.Apply();
        Assert.That(tab.Status.Value, Does.Contain("オン"));
    }

    [AvaloniaTest]
    public void ALockedTargetIsLeftAlone()
    {
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(1, 0, 10);
        Element target = f.AddMarker(EditorFixture.Frame(10));
        target.IsLocked.Value = true;
        var tab = f.OpenTab(source);
        tab.SetTarget(target);
        tab.Trackers[0].Samples.Set(0, new TrackSample(new TrackPose(1, 1), 1));

        tab.Apply();

        Assert.That(tab.Status.Value, Does.Contain("ロック"));
        Assert.That(GroupOf(target), Is.Null);
    }

    [AvaloniaTest]
    public void TheTabRemembersItsWork()
    {
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(1, 0, 10);
        Element target = f.AddMarker(EditorFixture.Frame(10));
        var tab = f.OpenTab(source);
        tab.SetTarget(target);
        tab.Use.Value = MotionUse.Steady;
        tab.SmoothRadius.Value = 7;
        tab.Density.Value = KeyframeDensity.Few;
        tab.UseRotation.Value = true;
        TrackerViewModel tracker = tab.Trackers.Single();
        tracker.Name.Value = "左の角";
        tracker.Model.Value = TrackerMotionModel.Position;
        tracker.Template.Value = TrackerTemplateMode.Fixed;
        tracker.MinConfidence.Value = 0.5f;
        tracker.IsActive.Value = false;
        tracker.PatternHalfWidth.Value = 20;
        tracker.SearchHalfHeight.Value = 77;
        tracker.Samples.Set(3, new TrackSample(new TrackPose(1, 2, 0.5f, 1.5f), 0.9f));
        tracker.Samples.Set(4, new TrackSample(new TrackPose(5, 6, 0.6f, 1.6f), 0.8f));

        var json = new JsonObject();
        tab.WriteToJson(json);
        var restored = f.OpenTab();
        restored.ReadFromJson(JsonNode.Parse(json.ToJsonString())!.AsObject());

        Assert.That(restored.Source.Value, Is.SameAs(source));
        Assert.That(restored.Target.Value, Is.SameAs(target));
        Assert.That(restored.Use.Value, Is.EqualTo(MotionUse.Steady));
        Assert.That(restored.SmoothRadius.Value, Is.EqualTo(7));
        Assert.That(restored.Density.Value, Is.EqualTo(KeyframeDensity.Few));
        Assert.That(restored.UseRotation.Value, Is.True);
        TrackerViewModel back = restored.Trackers.Single();
        Assert.That(back.Name.Value, Is.EqualTo("左の角"));
        Assert.That(back.Color, Is.EqualTo(tracker.Color));
        Assert.That(back.Model.Value, Is.EqualTo(TrackerMotionModel.Position));
        Assert.That(back.Template.Value, Is.EqualTo(TrackerTemplateMode.Fixed));
        Assert.That(back.MinConfidence.Value, Is.EqualTo(0.5f));
        Assert.That(back.IsActive.Value, Is.False);
        Assert.That(back.PatternHalfWidth.Value, Is.EqualTo(20));
        Assert.That(back.SearchHalfHeight.Value, Is.EqualTo(77));
        Assert.That(back.Samples.Count, Is.EqualTo(2));
        Assert.That(back.Samples.TryGet(4, out TrackSample sample), Is.True);
        Assert.That(sample.Pose, Is.EqualTo(new TrackPose(5, 6, 0.6f, 1.6f)));
        Assert.That(sample.Confidence, Is.EqualTo(0.8f));
    }

    [AvaloniaTest]
    public void TracksSavedAtAnotherFrameRateKeepTheirTimes()
    {
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(1, 0, 10);
        var tab = f.OpenTab(source);
        tab.Trackers[0].Samples.Set(30, new TrackSample(new TrackPose(1, 2), 1));

        var json = new JsonObject();
        tab.WriteToJson(json);
        json["FrameRateNumerator"] = 60L;
        json["FrameRateDenominator"] = 1L;
        var restored = f.OpenTab();
        restored.ReadFromJson(JsonNode.Parse(json.ToJsonString())!.AsObject());

        Assert.That(restored.Trackers.Single().Samples.Frames, Is.EqualTo(new long[] { 15 }));
    }

    [AvaloniaTest]
    public void BrokenTabStateIsIgnored()
    {
        using var f = new EditorFixture();
        Element source = f.AddMovingPicture(1, 0, 10);
        var tab = f.OpenTab(source);

        tab.ReadFromJson(new JsonObject
        {
            ["Use"] = "Nonsense",
            ["Source"] = "not a guid",
            ["SmoothRadius"] = -3,
            ["Trackers"] = new JsonArray(new JsonObject { ["Name"] = "色がない" }, 3, new JsonObject { ["Name"] = "壊れた", ["Color"] = "#ff00ff00", ["Samples"] = "%%%" }),
        });

        Assert.That(tab.Source.Value, Is.SameAs(source));
        Assert.That(tab.Use.Value, Is.EqualTo(MotionUse.Follow));
        Assert.That(tab.SmoothRadius.Value, Is.Zero);
        Assert.That(tab.Trackers.Single().Name.Value, Is.EqualTo("壊れた"));
        Assert.That(tab.Trackers.Single().Samples.Count, Is.Zero);
    }
}
