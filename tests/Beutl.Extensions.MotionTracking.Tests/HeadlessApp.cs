using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Beutl.Extensions.MotionTracking.Tests;
using FluentAvalonia.Styling;

[assembly: AvaloniaTestApplication(typeof(HeadlessApp))]

namespace Beutl.Extensions.MotionTracking.Tests;

/// <summary>A headless Avalonia application for exercising the views without a display.</summary>
public sealed class HeadlessApp : Application
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessApp>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public override void Initialize()
    {
        // The same themes as the editor, so that what the tests render looks like the real thing.
        var baseUri = new Uri("avares://Beutl.Extensions.MotionTracking.Tests/");
        Styles.Add(new FluentAvaloniaTheme { PreferUserAccentColor = false });
        Styles.Add(new StyleInclude(baseUri) { Source = new Uri("avares://Beutl.Controls/Styles.axaml") });
    }
}
