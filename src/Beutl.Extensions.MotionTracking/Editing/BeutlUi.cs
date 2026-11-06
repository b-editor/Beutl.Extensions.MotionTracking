using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia.Fluent;
using FluentIcons.Common;

namespace Beutl.Extensions.MotionTracking.Editing;

/// <summary>
/// Builds controls that look like Beutl's own tool tabs. Extensions cannot reference Beutl.Controls, so its
/// ToolTabBar and styles are rebuilt here from the same theme resources and style classes, which Beutl provides
/// at run time.
/// </summary>
internal static class BeutlUi
{
    /// <summary>The bar at the top of a tool tab: a title, then actions on the right.</summary>
    public static Border ToolBar(Control content)
    {
        var bar = new Border
        {
            MinHeight = 38,
            Padding = new Thickness(4, 2),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = content,
        };
        return WithResource(bar, Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
    }

    public static TextBlock BarTitle(string? text = null) => new()
    {
        Text = text,
        Margin = new Thickness(8, 0),
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    /// <summary>The space between the icon and the label of a <see cref="LabeledButton"/>.</summary>
    public const double LabelSpacing = 6;

    /// <summary>An icon-only button with its name as the tooltip, like the buttons in Beutl's tool bars.</summary>
    public static Button IconButton(Icon icon, string name, Action onClick)
    {
        Button button = BarButton(new FluentIcon { Icon = icon, FontSize = 16 }, name, onClick);
        AutomationProperties.SetName(button, name);
        return button;
    }

    /// <summary>
    /// A tool bar button with an icon and a label, for actions that are hard to tell apart by their icons alone. The
    /// label is handed back so that a narrow bar can hide it and leave the icon, whose tooltip still describes it.
    /// </summary>
    public static Button LabeledButton(
        Icon icon, string label, string description, Action onClick, out TextBlock labelText, bool iconAfterLabel = false)
    {
        Button button = BarButton(IconAndLabel(icon, label, iconAfterLabel, out labelText), description, onClick);
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetHelpText(button, description);
        return button;
    }

    /// <summary>A button with an icon and a label that fills the width it is given, for the main actions of a panel.</summary>
    public static Button IconTextButton(Icon icon, string label, Action onClick, bool iconAfterLabel = false, string? name = null)
    {
        var button = new Button
        {
            Content = IconAndLabel(icon, label, iconAfterLabel, out _),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        AutomationProperties.SetName(button, name ?? label);
        if (name != null)
            ToolTip.SetTip(button, name);
        button.Click += (_, _) => onClick();
        return button;
    }

    private static StackPanel IconAndLabel(Icon icon, string label, bool iconAfterLabel, out TextBlock labelText)
    {
        var glyph = new FluentIcon { Icon = icon, FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        labelText = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = LabelSpacing };
        content.Children.Add(iconAfterLabel ? labelText : glyph);
        content.Children.Add(iconAfterLabel ? glyph : labelText);
        return content;
    }

    private static Button BarButton(Control content, string tip, Action onClick)
    {
        var button = new Button
        {
            Padding = new Thickness(6),
            VerticalAlignment = VerticalAlignment.Center,
            Content = content,
        };
        WithResource(button, StyledElement.ThemeProperty, "ToolTabBarIconButtonTheme");
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => onClick();
        return button;
    }

    public static Button TextButton(string text, Action onClick, bool accent = false)
    {
        var button = new Button { Content = text, VerticalAlignment = VerticalAlignment.Center };
        if (accent)
            button.Classes.Add("accent");
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>Small text in the theme's secondary text colour.</summary>
    public static TextBlock Secondary(string? text = null)
    {
        var block = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        return WithResource(block, TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
    }

    /// <summary>The heading of a section of a side panel.</summary>
    public static TextBlock SectionTitle(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold };

    /// <summary>A bordered group, like the settings groups and lists in Beutl.</summary>
    public static Border Group(Control child, Thickness? padding = null)
    {
        var group = new Border { Child = child, Padding = padding ?? new Thickness(12) };
        group.Classes.Add("options-group");
        return group;
    }

    /// <summary>The empty state Beutl's tool tabs show: an icon in a rounded tile, a title, and an optional action.</summary>
    public static StackPanel EmptyState(Icon icon, Control title, Control? action = null)
    {
        var tile = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(16),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = WithResource(new FluentIcon { Icon = icon, FontSize = 24 }, TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush"),
        };
        WithResource(tile, Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");

        var panel = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 360,
            Margin = new Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { tile, title },
        };
        if (action != null)
        {
            action.HorizontalAlignment = HorizontalAlignment.Center;
            panel.Children.Add(action);
        }

        return panel;
    }

    public static TextBlock EmptyTitle() => new() { FontSize = 16, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };

    /// <summary>A label and its editor on one row, like a row of Beutl's property panel.</summary>
    public static void AddRow(Grid grid, string label, Control editor)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        TextBlock text = new() { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(text, row);
        Grid.SetRow(editor, row);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(text);
        grid.Children.Add(editor);
    }

    public static Grid Rows() => new()
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*"),
        ColumnSpacing = 12,
        RowSpacing = 8,
    };

    /// <summary>Binds a property to a theme resource, so the control follows Beutl's theme and its light/dark switch.</summary>
    public static T WithResource<T>(T control, AvaloniaProperty property, string key)
        where T : Control
    {
        control.Bind(property, control.GetResourceObservable(key));
        return control;
    }
}
