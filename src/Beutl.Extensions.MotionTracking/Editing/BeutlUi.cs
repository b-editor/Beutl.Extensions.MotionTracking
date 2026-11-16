using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
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

    /// <summary>The title of a tool bar, on one line, cut short with an ellipsis when it does not fit.</summary>
    public static TextBlock BarTitle()
    {
        var title = new TextBlock { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        return WithResource(title, StyledElement.ThemeProperty, "ToolTabBarTitleTextBlockTheme");
    }

    private const double LabelSpacing = 6;

    /// <summary>An icon-only button with its name as the tooltip, like the buttons in Beutl's tool bars.</summary>
    public static Button IconButton(Icon icon, string name, Action onClick)
    {
        Button button = BarButton(new FluentIcon { Icon = icon, FontSize = 16 }, name, onClick);
        AutomationProperties.SetName(button, name);
        return button;
    }

    /// <summary>A button with an icon and a label that fills the width it is given, for the main actions of a panel.</summary>
    public static Button IconTextButton(Icon icon, string label, Action onClick, bool iconAfterLabel = false, string? name = null)
    {
        var button = new Button
        {
            Content = IconAndLabel(icon, label, iconAfterLabel),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        AutomationProperties.SetName(button, name ?? label);
        if (name != null)
            ToolTip.SetTip(button, name);
        button.Click += (_, _) => onClick();
        return button;
    }

    private static Grid IconAndLabel(Icon icon, string label, bool iconAfterLabel)
    {
        var glyph = new FluentIcon { Icon = icon, FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        var labelText = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        };
        // A horizontal StackPanel measures labels without a width limit, clipping longer translations.
        var content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(iconAfterLabel ? "*,Auto" : "Auto,*"),
            ColumnSpacing = LabelSpacing,
            Children = { glyph, labelText },
        };
        Grid.SetColumn(glyph, iconAfterLabel ? 1 : 0);
        Grid.SetColumn(labelText, iconAfterLabel ? 0 : 1);
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
    public static Border Group(Control child)
    {
        var group = new Border { Child = child, Padding = new Thickness(12) };
        group.Classes.Add("options-group");
        return group;
    }

    /// <summary>
    /// A label and its editor on one row, like the settings of Beutl's "無音区間の分割" tab: the label takes what the
    /// editor leaves and wraps, with the unit in it, such as "閾値（dB）".
    /// </summary>
    public static void AddRow(Grid grid, string label, Control editor)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        TextBlock text = new() { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        editor.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(editor, label);
        Grid.SetRow(text, row);
        Grid.SetRow(editor, row);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(text);
        grid.Children.Add(editor);
    }

    /// <summary>The rows of labels and editors, whose editors are all as wide, as in the "無音区間の分割" tab.</summary>
    public static Grid Rows() => new()
    {
        ColumnDefinitions = new ColumnDefinitions("*,120"),
        ColumnSpacing = 8,
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
