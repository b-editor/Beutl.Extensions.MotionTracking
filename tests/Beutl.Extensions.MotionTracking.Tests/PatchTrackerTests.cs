using Beutl.Extensions.MotionTracking.Tracking;

namespace Beutl.Extensions.MotionTracking.Tests;

// Each test moves a known pattern and compares the result with the truth.
public class PatchTrackerTests
{
    private const double Origin = 200;

    private static TrackerOptions Options(
        TrackerMotionModel model = TrackerMotionModel.Position,
        TrackerTemplateMode template = TrackerTemplateMode.Adaptive,
        int pattern = 16,
        int search = 48,
        float minConfidence = 0.75f) => new()
    {
        PatternHalfWidth = pattern,
        PatternHalfHeight = pattern,
        SearchHalfWidth = search,
        SearchHalfHeight = search,
        Model = model,
        Template = template,
        MinConfidence = minConfidence,
    };

    private static (List<TrackResult> Results, TrackPose[] Truth) Run(
        TrackerOptions options,
        int frames,
        Func<int, TrackPose> motion,
        Func<int, (float Gain, float Bias)>? lighting = null,
        float noise = 0,
        Func<int, Func<int, int, float?>?>? overlay = null,
        SyntheticVideo? video = null)
    {
        video ??= new SyntheticVideo();
        var truth = new TrackPose[frames];
        var pyramids = new ImagePyramid[frames];
        for (int i = 0; i < frames; i++)
        {
            truth[i] = motion(i);
            (float gain, float bias) = lighting?.Invoke(i) ?? (1, 0);
            pyramids[i] = video.Render(Origin, Origin, truth[i], gain, bias, noise, overlay?.Invoke(i));
        }

        var tracker = new PatchTracker(options, pyramids[0].LevelCount);
        return (SyntheticVideo.Track(tracker, pyramids, truth[0]), truth);
    }

    private static double PositionError(TrackPose found, TrackPose truth)
    {
        double dx = found.X - truth.X;
        double dy = found.Y - truth.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    [Test]
    public void FollowsSlowMotionToAFractionOfAPixel()
    {
        (List<TrackResult> results, TrackPose[] truth) = Run(Options(), 30, i => new TrackPose(100 + i * 1.37f, 90 + i * 0.61f));

        Assert.That(results, Has.Count.EqualTo(29));
        for (int i = 0; i < results.Count; i++)
        {
            Assert.That(results[i].Status, Is.EqualTo(TrackStatus.Tracked));
            Assert.That(PositionError(results[i].Pose, truth[i + 1]), Is.LessThan(0.1), $"frame {i + 1}");
            Assert.That(results[i].Confidence, Is.GreaterThan(0.95f));
        }
    }

    [Test]
    public void FollowsFastMotionWithinTheSearchArea()
    {
        // About 25 pixels a frame, well beyond what alignment alone reaches.
        (List<TrackResult> results, TrackPose[] truth) = Run(
            Options(),
            12,
            i => new TrackPose(40 + i * 21.3f, 40 + i * 14.7f));

        Assert.That(results, Has.Count.EqualTo(11));
        Assert.That(results.All(r => r.Status == TrackStatus.Tracked));
        for (int i = 0; i < results.Count; i++)
            Assert.That(PositionError(results[i].Pose, truth[i + 1]), Is.LessThan(0.15), $"frame {i + 1}");
    }

    [Test]
    public void SurvivesSuddenChangesOfDirection()
    {
        // A shaking camera: the velocity prediction is wrong on every frame.
        (List<TrackResult> results, TrackPose[] truth) = Run(
            Options(),
            30,
            i => new TrackPose(160 + (i % 2 == 0 ? 16 : -16), 120 + (i % 4 < 2 ? 9 : -9)));

        Assert.That(results.All(r => r.Status == TrackStatus.Tracked));
        for (int i = 0; i < results.Count; i++)
            Assert.That(PositionError(results[i].Pose, truth[i + 1]), Is.LessThan(0.15), $"frame {i + 1}");
    }

    [Test]
    public void IgnoresChangesOfBrightnessAndContrast()
    {
        (List<TrackResult> results, TrackPose[] truth) = Run(
            Options(),
            40,
            i => new TrackPose(100 + i, 100),
            lighting: i => (1 - i * 0.012f, i * 1.5f));

        Assert.That(results, Has.Count.EqualTo(39));
        Assert.That(results.All(r => r.Status == TrackStatus.Tracked));
        for (int i = 0; i < results.Count; i++)
            Assert.That(PositionError(results[i].Pose, truth[i + 1]), Is.LessThan(0.15), $"frame {i + 1}");
    }

    [Test]
    public void StaysAccurateThroughSensorNoise()
    {
        (List<TrackResult> results, TrackPose[] truth) = Run(
            Options(),
            60,
            i => new TrackPose(80 + i * 2.3f, 100 + i * 0.7f),
            noise: 6);

        Assert.That(results, Has.Count.EqualTo(59));
        Assert.That(results.All(r => r.Status == TrackStatus.Tracked));
        double worst = results.Select((r, i) => PositionError(r.Pose, truth[i + 1])).Max();
        Assert.That(worst, Is.LessThan(0.5), "The error does not build up over the frames.");
    }

    [Test]
    public void AdaptiveTemplateDoesNotDriftOverManyFrames()
    {
        (List<TrackResult> results, TrackPose[] truth) = Run(
            Options(),
            300,
            i => new TrackPose(160 + 60 * MathF.Sin(i * 0.05f), 120 + 40 * MathF.Cos(i * 0.037f)),
            noise: 2);

        Assert.That(results, Has.Count.EqualTo(299));
        Assert.That(PositionError(results[^1].Pose, truth[^1]), Is.LessThan(0.25));
    }

    [Test]
    public void FixedTemplateStopsWhenThePatternTurnsAway()
    {
        Func<int, TrackPose> turning = i => new TrackPose(160, 120, i * 0.06f);

        (List<TrackResult> results, _) = Run(Options(template: TrackerTemplateMode.Fixed), 60, turning);

        Assert.That(results[^1].Status, Is.EqualTo(TrackStatus.LowConfidence));
        Assert.That(results, Has.Count.LessThan(40));
    }

    [Test]
    public void AdaptiveTemplateFollowsAGradualTurnThatAFixedOneLoses()
    {
        Func<int, TrackPose> turning = i => new TrackPose(160, 120, i * 0.06f);

        (List<TrackResult> results, TrackPose[] truth) = Run(Options(), 60, turning);

        Assert.That(results, Has.Count.EqualTo(59));
        Assert.That(results.All(r => r.Status == TrackStatus.Tracked));
        // A position-only template cannot tell turning from shifting, so it wanders, but it stays on the pattern.
        Assert.That(PositionError(results[^1].Pose, truth[^1]), Is.LessThan(16));
    }

    [Test]
    public void SimilarityModelMeasuresRotationAndScale()
    {
        (List<TrackResult> results, TrackPose[] truth) = Run(
            Options(TrackerMotionModel.PositionRotationScale),
            60,
            i => new TrackPose(160 + i * 0.4f, 120 - i * 0.3f, i * 0.05f, 1 + i * 0.008f));

        Assert.That(results, Has.Count.EqualTo(59));
        for (int i = 0; i < results.Count; i++)
        {
            TrackPose found = results[i].Pose;
            TrackPose expected = truth[i + 1];
            Assert.That(PositionError(found, expected), Is.LessThan(0.2), $"position at frame {i + 1}");
            Assert.That(found.Rotation, Is.EqualTo(expected.Rotation).Within(0.01f), $"rotation at frame {i + 1}");
            Assert.That(found.Scale, Is.EqualTo(expected.Scale).Within(0.01f), $"scale at frame {i + 1}");
        }
    }

    [Test]
    public void SimilarityModelStaysExactlyOnAPatternThatKeepsTurning()
    {
        (List<TrackResult> results, TrackPose[] truth) = Run(
            Options(TrackerMotionModel.PositionRotationScale),
            60,
            i => new TrackPose(160, 120, i * 0.06f));

        Assert.That(results, Has.Count.EqualTo(59));
        for (int i = 0; i < results.Count; i++)
            Assert.That(PositionError(results[i].Pose, truth[i + 1]), Is.LessThan(0.2), $"frame {i + 1}");
    }

    [Test]
    public void SimilarityModelCountsPastAHalfTurn()
    {
        (List<TrackResult> results, TrackPose[] truth) = Run(
            Options(TrackerMotionModel.PositionRotationScale),
            100,
            i => new TrackPose(160, 120, i * 0.05f));

        Assert.That(results, Has.Count.EqualTo(99));
        Assert.That(results[^1].Pose.Rotation, Is.EqualTo(truth[^1].Rotation).Within(0.03f));
        Assert.That(results[^1].Pose.Rotation, Is.GreaterThan(MathF.PI));
    }

    [Test]
    public void StopsWhenThePatternIsCovered()
    {
        // A flat object slides over the pattern from frame 20.
        (List<TrackResult> results, _) = Run(
            Options(),
            40,
            i => new TrackPose(160, 120),
            overlay: i => i < 20 ? null : (x, y) => x > 320 - (i - 20) * 40 ? 30f : null);

        Assert.That(results[^1].Status, Is.EqualTo(TrackStatus.LowConfidence));
        Assert.That(results, Has.Count.LessThan(30));
    }

    [Test]
    public void StopsWhenThePatternLeavesTheFrame()
    {
        (List<TrackResult> results, TrackPose[] truth) = Run(
            Options(),
            60,
            i => new TrackPose(280 + i * 4, 120));

        Assert.That(results[^1].Status, Is.EqualTo(TrackStatus.OutOfFrame));
        Assert.That(results[^1].Pose.X, Is.GreaterThan(315));
        // Right up to the edge the position stays good.
        Assert.That(PositionError(results[^3].Pose, truth[results.Count - 2]), Is.LessThan(1));
    }

    [Test]
    public void KeepsThePoseAfterALostStep()
    {
        var video = new SyntheticVideo();
        ImagePyramid start = video.Render(Origin, Origin, new TrackPose(160, 120));
        ImagePyramid lost = video.Render(Origin, Origin, new TrackPose(160, 120), overlay: (_, _) => 40f);
        var tracker = new PatchTracker(Options(), start.LevelCount);
        tracker.Start(start, new TrackPose(160, 120));

        TrackResult result = tracker.Step(lost);

        Assert.That(result.Status, Is.EqualTo(TrackStatus.LowConfidence));
        Assert.That(tracker.Pose, Is.EqualTo(new TrackPose(160, 120)));
    }

    [Test]
    public void TracksPatternsMuchLargerThanTheAlignmentGrid()
    {
        var video = new SyntheticVideo(960, 720);
        (List<TrackResult> results, TrackPose[] truth) = Run(
            Options(pattern: 120, search: 160),
            20,
            i => new TrackPose(400 + i * 3.4f, 360 - i * 1.3f),
            video: video);

        Assert.That(results, Has.Count.EqualTo(19));
        Assert.That(results.All(r => r.Status == TrackStatus.Tracked));
        for (int i = 0; i < results.Count; i++)
            Assert.That(PositionError(results[i].Pose, truth[i + 1]), Is.LessThan(0.3), $"frame {i + 1}");
    }

    [Test]
    public void WorksWithVerySmallPatterns()
    {
        (List<TrackResult> results, TrackPose[] truth) = Run(
            Options(pattern: 6, search: 16),
            30,
            i => new TrackPose(100 + i * 1.7f, 100 + i * 0.9f));

        Assert.That(results, Has.Count.EqualTo(29));
        for (int i = 0; i < results.Count; i++)
            Assert.That(PositionError(results[i].Pose, truth[i + 1]), Is.LessThan(0.3), $"frame {i + 1}");
    }

    [Test]
    public void StartsFromAPoseThatIsAlreadyTurned()
    {
        // Resuming a track: the first pose carries the rotation and scale tracked so far.
        (List<TrackResult> results, TrackPose[] truth) = Run(
            Options(TrackerMotionModel.PositionRotationScale),
            20,
            i => new TrackPose(160, 120, 1.2f + i * 0.03f, 1.3f));

        Assert.That(results, Has.Count.EqualTo(19));
        Assert.That(results[^1].Pose.Rotation, Is.EqualTo(truth[^1].Rotation).Within(0.01f));
        Assert.That(results[^1].Pose.Scale, Is.EqualTo(1.3f).Within(0.01f));
    }

    [Test]
    public void AFlatAreaIsNotAFeature()
    {
        var flat = new GrayImage(100, 100);
        Array.Fill(flat.Pixels, 128f);

        Assert.That(PatchTracker.MeasureFeature(flat, 50, 50, 10, 10), Is.EqualTo(0).Within(0.001f));
    }

    [Test]
    public void AStraightEdgeScoresLowerThanACorner()
    {
        var edge = new GrayImage(100, 100);
        var corner = new GrayImage(100, 100);
        for (int y = 0; y < 100; y++)
        {
            for (int x = 0; x < 100; x++)
            {
                edge.Pixels[y * 100 + x] = x < 50 ? 0 : 255;
                corner.Pixels[y * 100 + x] = x < 50 && y < 50 ? 0 : 255;
            }
        }

        float edgeScore = PatchTracker.MeasureFeature(edge, 50, 50, 10, 10);
        float cornerScore = PatchTracker.MeasureFeature(corner, 50, 50, 10, 10);

        Assert.That(edgeScore, Is.EqualTo(0).Within(0.001f));
        Assert.That(cornerScore, Is.GreaterThan(1));
    }

    [Test]
    public void LumaFollowsTheColorWeights()
    {
        var image = new GrayImage(2, 1);
        byte[] bgra = [255, 0, 0, 255, /* blue */ 0, 255, 0, 255 /* green */];

        image.LoadBgra(bgra, 8);

        Assert.That(image.Pixels[0], Is.EqualTo(255 * 0.0722f).Within(0.01f));
        Assert.That(image.Pixels[1], Is.EqualTo(255 * 0.7152f).Within(0.01f));
    }

    [Test]
    public void PyramidLevelsAreAlignedAtTheirCoordinates()
    {
        // A bright dot stays where its level 0 position divided by the level's scale says.
        var pyramid = new ImagePyramid(256, 256);
        for (int y = 0; y < 256; y++)
        {
            for (int x = 0; x < 256; x++)
            {
                double dx = x - 96.0;
                double dy = y - 64.0;
                pyramid[0].Pixels[y * 256 + x] = (float)(255 * Math.Exp(-(dx * dx + dy * dy) / (2 * 8 * 8)));
            }
        }

        pyramid.BuildLevels();

        GrayImage level2 = pyramid[2];
        int best = Array.IndexOf(level2.Pixels, level2.Pixels.Max());
        Assert.That(best % level2.Width, Is.EqualTo(24));
        Assert.That(best / level2.Width, Is.EqualTo(16));
    }
}
