using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Beutl.Extensibility;
using Beutl.Extensions.MotionTracking.Properties;
using FluentAvalonia.UI.Controls;
using FluentIcons.Avalonia.Fluent;
using FluentIcons.Common;

namespace Beutl.Extensions.MotionTracking.Editing;

[Export]
public sealed class MotionTrackingTabExtension : ToolTabExtension
{
    public override string Name => "Motion tracking";

    public override string DisplayName => Strings.MotionTracking;

    public override string? Header => Strings.MotionTracking;

    public override bool CanMultiple => false;

    // The zoom of the frame and what is open in the panel survive switching tabs.
    public override bool ReuseContentAcrossActivation => true;

    // It shows the frames and places trackers on them, so it needs the room the preview has, next to which it opens.
    public override DockAnchor DefaultAnchor => DockAnchor.Player;

    public override FAIconSource? GetIcon() => new FluentIconSource { Icon = Icon.Target };

    public override bool TryCreateContent(IEditorContext editorContext, [NotNullWhen(true)] out Control? control)
    {
        control = new MotionTrackingTabView();
        return true;
    }

    public override bool TryCreateContext(IEditorContext editorContext, [NotNullWhen(true)] out IToolContext? context)
    {
        context = new MotionTrackingTabViewModel(this, editorContext);
        return true;
    }
}
