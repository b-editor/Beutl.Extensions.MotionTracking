using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Avalonia.Headless.NUnit;
using Beutl.Extensions.MotionTracking.Editing;
using Beutl.Extensions.MotionTracking.Properties;
using Beutl.Extensions.MotionTracking.Tracking;
using Beutl.Graphics.Shapes;
using Beutl.Graphics.Transformation;
using Beutl.Media;

namespace Beutl.Extensions.MotionTracking.Tests;

public class LocalizationTests
{
    [TestCase("ja")]
    [TestCase("zh-CN")]
    [TestCase("ko-KR")]
    [TestCase("es")]
    public void TranslationsCoverAllKeysAndPreserveFormatArguments(string language)
    {
        ResourceSet neutral = Strings.ResourceManager.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        ResourceSet translated = Strings.ResourceManager.GetResourceSet(CultureInfo.GetCultureInfo(language), true, false)!;
        Assert.That(translated, Is.Not.Null, $"Missing satellite resources for {language}");
        Assert.That(translated.Cast<DictionaryEntry>().Select(entry => entry.Key),
            Is.EquivalentTo(neutral.Cast<DictionaryEntry>().Select(entry => entry.Key)));

        foreach (DictionaryEntry entry in neutral)
        {
            string key = (string)entry.Key;
            string value = translated.GetString(key)!;
            Assert.That(value, Is.Not.Null.And.Not.Empty, key);
            string[] arguments = Arguments((string)entry.Value!);
            Assert.That(Arguments(value), Is.EquivalentTo(arguments), key);
            object[] values = arguments.Select(_ => (object)42).ToArray();
            Assert.DoesNotThrow(() => string.Format(value, values), key);
        }

        static string[] Arguments(string text) =>
            Regex.Matches(text, @"\{(\d+)\}").Select(match => match.Groups[1].Value).ToArray();
    }

    [TestCase("en-US", "Motion tracking")]
    [TestCase("ja-JP", "モーショントラッキング")]
    [TestCase("zh-CN", "运动跟踪")]
    [TestCase("ko-KR", "모션 트래킹")]
    [TestCase("es-ES", "Seguimiento de movimiento")]
    [TestCase("es-MX", "Seguimiento de movimiento")]
    [TestCase("fr-FR", "Motion tracking")]
    public void UsesTheUiCultureAndFallsBackToEnglish(string language, string expected)
    {
        using var culture = new CultureScope(language);
        Assert.That(new MotionTrackingTabExtension().DisplayName, Is.EqualTo(expected));
        // UI language is independent of the user's number/date formatting culture.
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        Assert.That(Strings.MotionTracking, Is.EqualTo(expected));
    }

    [AvaloniaTest]
    public void AppliedGroupsSurviveLanguageChangesAndRecognizeLegacyProjects()
    {
        using var culture = new CultureScope("en-US");
        var legacy = new TransformGroup { Name = { Value = "モーショントラッキング" } };
        var userGroup = new TransformGroup { Name = { Value = "User transform" } };
        var root = new TransformGroup { Children = { legacy, userGroup } };
        var drawable = new RectShape { Transform = { Value = root } };
        (TransformKey, MediaTime)[] keys =
        [
            (new TransformKey(0, 1, 2, 0, 100), MediaTime.Zero),
            (new TransformKey(1, 3, 4, 0, 100), EditorFixture.Frame(1)),
        ];

        TransformGroup previous = legacy;
        foreach (string language in new[] { "en-US", "es", "ja-JP", "zh-CN", "ko-KR" })
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            Assert.That(TrackingKeyframes.FindGroup(drawable), Is.SameAs(previous));
            Assert.That(TrackingKeyframes.Write(drawable, keys, MotionChannels.Position, 0), Is.GreaterThan(0));
            previous = TrackingKeyframes.FindGroup(drawable)!;
            Assert.That(previous.Name.Value, Is.EqualTo(Strings.MotionTracking));
            Assert.That(root.Children, Has.Count.EqualTo(2));
            Assert.That(root.Children[1], Is.SameAs(userGroup));
        }

        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
        Assert.That(TrackingKeyframes.Remove(drawable), Is.True);
        Assert.That(root.Children.Single(), Is.SameAs(userGroup));
    }
}

internal sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;

    public CultureScope(string language)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
    }
}
