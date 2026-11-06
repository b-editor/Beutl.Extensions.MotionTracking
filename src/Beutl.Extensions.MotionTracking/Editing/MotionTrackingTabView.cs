using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Beutl.Extensions.MotionTracking.Tracking;
using FluentIcons.Common;
using Reactive.Bindings;
using static Beutl.Extensions.MotionTracking.Editing.BeutlUi;

namespace Beutl.Extensions.MotionTracking.Editing;

/// <summary>
/// The view of the "モーショントラッキング" tool tab, laid out like Beutl's "無音区間の分割" tab: the frame with the
/// trackers over it, with the bar of tracked frames and the zoom buttons under it; the settings beside them, or under
/// them when the tab is narrow; and a footer with what happened and the button that applies the motion. Its
/// DataContext is a <see cref="MotionTrackingTabViewModel"/>.
/// </summary>
public sealed class MotionTrackingTabView : UserControl, IDisposable
{
    // Where the "無音区間の分割" tab puts its settings beside the preview, and how wide they are then.
    private const double PanelWidth = 360;
    private const double WideLayoutMinWidth = 800;
    private const double ZoomStep = 1.5;

    private readonly TrackerCanvas _canvas = new();
    private readonly TrackRangeBar _rangeBar = new() { Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
    private readonly Grid _body = new();
    private readonly Grid _viewerPane;
    private readonly ScrollViewer _panelPane;
    private readonly StackPanel _panelStack = new() { Spacing = 12 };
    private readonly Border[] _groups;
    private readonly Grid _footerLayout = new() { RowSpacing = 8, ColumnSpacing = 16 };
    private readonly Grid _footerButtons = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
    private readonly Button _applyButton;
    private readonly Button _zoomOut;
    private readonly Button _zoomIn;
    private readonly Button _resetZoom;
    private readonly ListBox _trackerList;
    private readonly StackPanel _trackerSettings;
    private readonly NumericUpDown _patternWidth = SizeEditor();
    private readonly NumericUpDown _patternHeight = SizeEditor();
    private readonly NumericUpDown _searchWidth = SizeEditor();
    private readonly NumericUpDown _searchHeight = SizeEditor();
    private readonly NumericUpDown _confidence = new() { Minimum = 30, Maximum = 98, Increment = 1, FormatString = "0", ClipValueToMinMax = true };
    private readonly CheckBox _model = new() { Content = "回転と拡大縮小も追跡する" };
    private readonly CheckBox _template = new() { Content = "見本を自動で更新する" };
    private readonly TextBlock _featureText = Secondary();
    private readonly TextBlock _frameText = Secondary();
    private readonly TextBlock _zoomText = Secondary();
    private readonly ComboBox _use = Choices("オブジェクトを動きに合わせる", "手ブレを補正（固定する）", "手ブレを補正（滑らかにする）");
    private readonly ComboBox _density = Choices("毎フレーム", "標準", "少なめ");
    private readonly NumericUpDown _smooth = new() { Minimum = 0, Maximum = 120, Increment = 1, FormatString = "0", ClipValueToMinMax = true };
    private readonly Control _targetRow;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly List<IDisposable> _trackerSubscriptions = [];
    private MotionTrackingTabViewModel? _viewModel;
    private bool _wide = true;
    private bool _layoutApplied;
    private bool _updating;

    public MotionTrackingTabView()
    {
        (_viewerPane, _zoomOut, _zoomIn, _resetZoom) = BuildViewer();
        (_trackerList, _trackerSettings) = BuildTrackerControls();
        _targetRow = BuildTargetRow();
        _groups = [Group(BuildTrackerGroup()), Group(BuildTrackSection()), Group(BuildApplyGroup())];
        foreach (Border group in _groups)
            _panelStack.Children.Add(group);
        _panelPane = new ScrollViewer
        {
            Margin = new Thickness(8),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _panelStack,
        };

        _body.Children.Add(_viewerPane);
        _body.Children.Add(_panelPane);
        _body[!IsVisibleProperty] = new Binding("Source.Value") { Converter = ObjectConverters.IsNotNull };

        Border bar = BuildToolBar();
        var content = new Panel { Children = { _body, BuildEmptyState() } };
        (Border footer, _applyButton) = BuildFooter();
        Grid.SetRow(content, 1);
        Grid.SetRow(footer, 2);
        Content = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Children = { bar, content, footer } };

        ApplyLayout(wide: true);
        UpdateZoom();
    }

    private MotionTrackingTabViewModel? ViewModel => DataContext as MotionTrackingTabViewModel;

    private static NumericUpDown SizeEditor() => new()
    {
        Minimum = TrackerViewModel.MinPatternHalfSize * 2,
        Maximum = TrackerViewModel.MaxHalfSize * 2,
        Increment = 2,
        FormatString = "0",
        ClipValueToMinMax = true,
    };

    private static ComboBox Choices(params string[] items)
    {
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 0 };
        foreach (string item in items)
            combo.Items.Add(item);
        return combo;
    }

    // The name of the video being tracked, and the buttons that act on it, as the "無音区間の分割" tab shows its audio.
    private Border BuildToolBar()
    {
        TextBlock title = BarTitle();
        title[!TextBlock.TextProperty] = new Binding("SourceName.Value");
        title[!ToolTip.TipProperty] = new Binding("SourceName.Value");
        title[!IsVisibleProperty] = new Binding("Source.Value") { Converter = ObjectConverters.IsNotNull };
        Button locate = IconButton(Icon.Search, "追跡元をタイムラインで選択", () => ViewModel?.SelectSourceInTimeline());
        locate[!IsVisibleProperty] = new Binding("Source.Value") { Converter = ObjectConverters.IsNotNull };
        Button change = IconButton(Icon.ArrowSwap, "選択中の要素を追跡元にする（追跡結果は消えます）", () => ViewModel?.UseSelectionAsSource());
        change[!IsEnabledProperty] = new Binding("IsTracking.Value") { Converter = BoolConverters.Not };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { locate, change } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { title, actions } };
        Grid.SetColumn(actions, 1);
        return ToolBar(grid);
    }

    // The frame on a card like the "無音区間の分割" tab's waveform, the bar of tracked frames under it, and the frame
    // number beside the zoom buttons.
    private (Grid Pane, Button ZoomOut, Button ZoomIn, Button ResetZoom) BuildViewer()
    {
        // Outside the source there is no frame to show, and nothing to place a tracker on.
        var noFrame = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 12, 0, 0),
            Children =
            {
                new TextBlock { Text = "再生ヘッドが追跡元の範囲の外にあります", VerticalAlignment = VerticalAlignment.Center },
                TextButton("追跡元の先頭へ移動", () => ViewModel?.SeekToFrame(0)),
            },
            [!IsVisibleProperty] = new Binding("CurrentFrame.Value") { Converter = ObjectConverters.IsNull },
        };
        var card = new Border
        {
            Padding = new Thickness(8),
            Child = new Panel { ClipToBounds = true, Children = { _canvas, noFrame } },
        };
        WithResource(card, Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
        WithResource(card, Border.CornerRadiusProperty, "ControlCornerRadius");

        WithResource(_rangeBar, TrackRangeBar.BackgroundProperty, "ControlFillColorSecondaryBrush");
        WithResource(_rangeBar, TrackRangeBar.CornerRadiusProperty, "ControlCornerRadius");
        WithResource(_rangeBar, TrackRangeBar.PlayheadBrushProperty, "AccentFillColorDefaultBrush");
        ToolTip.SetTip(_rangeBar, "トラッカーごとの追跡済みのフレームです。赤い所は一致度が低いフレームです。押すとそのフレームへ移動します");

        Button zoomOut = IconButton(Icon.ZoomOut, "縮小", () => _canvas.ZoomBy(1 / ZoomStep));
        Button zoomIn = IconButton(Icon.ZoomIn, "拡大", () => _canvas.ZoomBy(ZoomStep));
        Button resetZoom = IconButton(Icon.ArrowAutofitContent, "拡大率をリセット", () => _canvas.ResetView());
        _canvas.ZoomChanged += (_, _) => UpdateZoom();
        _frameText.VerticalAlignment = VerticalAlignment.Center;
        _frameText.TextWrapping = TextWrapping.NoWrap;
        _zoomText.VerticalAlignment = VerticalAlignment.Center;
        var zoom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _zoomText, zoomOut, zoomIn, resetZoom } };
        var under = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { _frameText, zoom } };
        Grid.SetColumn(zoom, 1);

        var pane = new Grid
        {
            Margin = new Thickness(8),
            RowDefinitions = new RowDefinitions("*,Auto,Auto"),
            RowSpacing = 8,
            Children = { card, _rangeBar, under },
        };
        Grid.SetRow(_rangeBar, 1);
        Grid.SetRow(under, 2);
        return (pane, zoomOut, zoomIn, resetZoom);
    }

    private void UpdateZoom()
    {
        double zoom = _canvas.Zoom;
        _zoomText.Text = $"{zoom:0.#}×";
        _zoomIn.IsEnabled = zoom < TrackerCanvas.MaxZoom;
        _zoomOut.IsEnabled = _resetZoom.IsEnabled = zoom > 1;
    }

    // Like the "無音区間の分割" tab before it has audio: a line that says what to do, here with the button that does it.
    private Control BuildEmptyState()
    {
        TextBlock message = Secondary();
        message[!TextBlock.TextProperty] = new Binding("SourceMessage.Value");
        Button use = TextButton("選択中の要素を使う", () => ViewModel?.UseSelectionAsSource(), accent: true);
        use.HorizontalAlignment = HorizontalAlignment.Left;
        return new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children = { message, use },
            [!IsVisibleProperty] = new Binding("Source.Value") { Converter = ObjectConverters.IsNull },
        };
    }

    // Like the footer of the "無音区間の分割" tab: what happened, how far tracking has got, and the button that applies.
    private (Border Footer, Button Apply) BuildFooter()
    {
        TextBlock status = Secondary();
        status[!TextBlock.TextProperty] = new Binding("Status.Value");
        status[!IsVisibleProperty] = new Binding("Status.Value") { Converter = StringConverters.IsNotNullOrEmpty };
        var progress = new ProgressBar
        {
            Height = 4,
            Minimum = 0,
            Maximum = 1,
            [!RangeBase.ValueProperty] = new Binding("Progress.Value"),
            [!IsVisibleProperty] = new Binding("IsTracking.Value"),
        };
        var state = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { status, progress } };

        // Removing what was applied sits beside applying it, which keeps the room it has in the "無音区間の分割" tab.
        Button remove = TextButton("解除", () => ViewModel?.RemoveApplied());
        ToolTip.SetTip(remove, "適用したキーフレームを取り除きます");
        Button apply = TextButton("適用", () => ViewModel?.Apply(), accent: true);
        apply.HorizontalAlignment = HorizontalAlignment.Stretch;
        _footerButtons.Children.Add(remove);
        _footerButtons.Children.Add(apply);
        Grid.SetColumn(apply, 1);
        _footerButtons[!IsEnabledProperty] = new Binding("IsTracking.Value") { Converter = BoolConverters.Not };
        _footerButtons[!IsVisibleProperty] = new Binding("Source.Value") { Converter = ObjectConverters.IsNotNull };
        _footerLayout.Children.Add(state);
        _footerLayout.Children.Add(_footerButtons);

        var footer = new Border { Padding = new Thickness(12), BorderThickness = new Thickness(0, 1, 0, 0), Child = _footerLayout };
        WithResource(footer, Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        // Before there is a video, it still says why the button did nothing.
        footer[!IsVisibleProperty] = new MultiBinding
        {
            Converter = BoolConverters.Or,
            Bindings =
            {
                new Binding("Source.Value") { Converter = ObjectConverters.IsNotNull },
                new Binding("Status.Value") { Converter = StringConverters.IsNotNullOrEmpty },
            },
        };
        return (footer, apply);
    }

    private (ListBox List, StackPanel Settings) BuildTrackerControls()
    {
        var list = new ListBox
        {
            MaxHeight = 170,
            Background = Brushes.Transparent,
            ItemTemplate = new FuncDataTemplate<TrackerViewModel>(BuildTrackerRow, supportsRecycling: false),
            [!ItemsControl.ItemsSourceProperty] = new Binding("Trackers"),
            [!SelectingItemsControl.SelectedItemProperty] = new Binding("SelectedTracker.Value") { Mode = BindingMode.TwoWay },
        };

        Grid sizes = Rows();
        AddRow(sizes, "パターン幅（px）", _patternWidth);
        AddRow(sizes, "パターン高さ（px）", _patternHeight);
        AddRow(sizes, "検索の幅（px）", _searchWidth);
        AddRow(sizes, "検索の高さ（px）", _searchHeight);
        AddRow(sizes, "止める信頼度（%）", _confidence);
        ToolTip.SetTip(_confidence, "パターンとの一致度がこれを下回ると、そのトラッカーは止まります");
        ToolTip.SetTip(_model, "オフにすると位置だけを追跡します");
        ToolTip.SetTip(_template, "オフにすると、追跡を始めたときの見た目と比べ続けます");

        var clear = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                TextButton("結果を消す", () => ViewModel?.ClearSamples(ClearRange.All)),
                TextButton("ここより前を消す", () => ViewModel?.ClearSamples(ClearRange.BeforeCurrentFrame)),
                TextButton("ここより後を消す", () => ViewModel?.ClearSamples(ClearRange.AfterCurrentFrame)),
            },
        };
        foreach (Control button in clear.Children)
            button.Margin = new Thickness(0, 0, 6, 6);

        var details = new Expander
        {
            Header = "詳細設定",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel { Spacing = 8, Children = { sizes, _model, _template, clear } },
        };

        _patternWidth.ValueChanged += (_, e) => Edit(tracker => ViewModel!.SetPatternSize(tracker, ToHalf(e.NewValue), tracker.PatternHalfHeight.Value));
        _patternHeight.ValueChanged += (_, e) => Edit(tracker => ViewModel!.SetPatternSize(tracker, tracker.PatternHalfWidth.Value, ToHalf(e.NewValue)));
        _searchWidth.ValueChanged += (_, e) => Edit(tracker => ViewModel!.SetSearchSize(tracker, ToHalf(e.NewValue), tracker.SearchHalfHeight.Value));
        _searchHeight.ValueChanged += (_, e) => Edit(tracker => ViewModel!.SetSearchSize(tracker, tracker.SearchHalfWidth.Value, ToHalf(e.NewValue)));
        _confidence.ValueChanged += (_, e) => Edit(tracker => tracker.MinConfidence.Value = (float)((e.NewValue ?? 75) / 100));
        _model.IsCheckedChanged += (_, _) => Edit(tracker =>
            tracker.Model.Value = _model.IsChecked == true ? TrackerMotionModel.PositionRotationScale : TrackerMotionModel.Position);
        _template.IsCheckedChanged += (_, _) => Edit(tracker =>
            tracker.Template.Value = _template.IsChecked == true ? TrackerTemplateMode.Adaptive : TrackerTemplateMode.Fixed);

        var settings = new StackPanel
        {
            Spacing = 8,
            Children = { _featureText, details },
            [!IsVisibleProperty] = new Binding("SelectedTracker.Value") { Converter = ObjectConverters.IsNotNull },
        };
        return (list, settings);

        static int ToHalf(decimal? value) => (int)((value ?? 0) / 2);
    }

    // Built from bindings only. The row of a removed tracker is built once more without a tracker, and a row that
    // threw there would leave the list unsure of its rows, drawing the removed one over the one that took its place.
    private static Control BuildTrackerRow(TrackerViewModel? tracker, INameScope scope)
    {
        var active = new CheckBox
        {
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
            [!ToggleButton.IsCheckedProperty] = new Binding("IsActive.Value") { Mode = BindingMode.TwoWay },
        };
        ToolTip.SetTip(active, "追跡と適用に使う");
        var swatch = new Rectangle
        {
            Width = 12,
            Height = 12,
            RadiusX = 2,
            RadiusY = 2,
            VerticalAlignment = VerticalAlignment.Center,
            [!Shape.FillProperty] = new Binding(nameof(TrackerViewModel.Brush)),
        };
        var name = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            [!TextBlock.TextProperty] = new Binding("Name.Value"),
        };
        TextBlock count = Secondary();
        count.VerticalAlignment = VerticalAlignment.Center;
        count[!TextBlock.TextProperty] = new Binding("SampleCount.Value") { StringFormat = "{0} フレーム" };
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
            ColumnSpacing = 6,
            Children = { active, swatch, name, count },
        };
        Grid.SetColumn(swatch, 1);
        Grid.SetColumn(name, 2);
        Grid.SetColumn(count, 3);
        return row;
    }

    private Control BuildTargetRow()
    {
        TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        name[!TextBlock.TextProperty] = new Binding("TargetName.Value");
        Button use = TextButton("選択中の要素を使う", () => ViewModel?.UseSelectionAsTarget());
        Button locate = IconButton(Icon.Search, "適用先の要素をタイムラインで選択", () => ViewModel?.SelectTargetInTimeline());
        locate[!IsEnabledProperty] = new Binding("Target.Value") { Converter = ObjectConverters.IsNotNull };
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        var label = new TextBlock { Text = "適用先", VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(label);
        line.Children.Add(name);
        line.Children.Add(locate);
        Grid.SetColumn(name, 1);
        Grid.SetColumn(locate, 2);
        return new StackPanel { Spacing = 6, Children = { line, use } };
    }

    private Control BuildTrackerGroup()
    {
        var trackerButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 2,
            Children =
            {
                IconButton(Icon.Add, "トラッカーを追加（フレームをダブルクリックでも追加できます）", () =>
                {
                    ViewModel?.AddTracker();
                    ViewModel?.UpdateFeature();
                }),
                IconButton(Icon.Delete, "選択中のトラッカーを削除 (Delete)", () => ViewModel?.RemoveSelectedTracker()),
            },
            [!IsEnabledProperty] = new Binding("IsTracking.Value") { Converter = BoolConverters.Not },
        };
        var trackerHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        TextBlock trackerTitle = SectionTitle("トラッカー");
        trackerTitle.VerticalAlignment = VerticalAlignment.Center;
        trackerHeader.Children.Add(trackerTitle);
        trackerHeader.Children.Add(trackerButtons);
        Grid.SetColumn(trackerButtons, 1);

        _trackerSettings[!IsEnabledProperty] = new Binding("IsTracking.Value") { Converter = BoolConverters.Not };
        return new StackPanel { Spacing = 8, Children = { trackerHeader, _trackerList, _trackerSettings } };
    }

    // Between placing the trackers and using what they found. The buttons track from the playhead, and while they do,
    // the button that stops them takes their place.
    private Control BuildTrackSection()
    {
        Button toStart = IconTextButton(Icon.Previous, "最初まで追跡", () => ViewModel?.TrackAsync(-1));
        Button toEnd = IconTextButton(Icon.Next, "最後まで追跡", () => ViewModel?.TrackAsync(1), iconAfterLabel: true);
        Button previous = IconTextButton(Icon.PreviousFrame, "1 フレーム", () => ViewModel?.TrackAsync(-1, singleFrame: true), name: "1 フレーム戻って追跡");
        Button next = IconTextButton(Icon.NextFrame, "1 フレーム", () => ViewModel?.TrackAsync(1, singleFrame: true), iconAfterLabel: true, name: "1 フレーム進んで追跡");
        var buttons = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = 6,
            RowSpacing = 6,
            Children = { toStart, toEnd, previous, next },
            [!IsVisibleProperty] = new Binding("IsTracking.Value") { Converter = BoolConverters.Not },
        };
        Grid.SetColumn(toEnd, 1);
        Grid.SetRow(previous, 1);
        Grid.SetRow(next, 1);
        Grid.SetColumn(next, 1);

        Button stop = IconTextButton(Icon.Stop, "停止", () => ViewModel?.Stop());
        stop[!IsVisibleProperty] = new Binding("IsTracking.Value");
        return new StackPanel { Spacing = 8, Children = { SectionTitle("追跡"), buttons, stop } };
    }

    // How the motion is used. The button that applies it is in the footer, as in the "無音区間の分割" tab.
    private Control BuildApplyGroup()
    {
        var channels = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children =
            {
                Check("位置", "UsePosition.Value"),
                Check("回転", "UseRotation.Value"),
                Check("拡大縮小", "UseScale.Value"),
            },
        };

        Grid options = Rows();
        AddRow(options, "滑らかさ（フレーム）", _smooth);
        AddRow(options, "キーフレーム", _density);
        ToolTip.SetTip(_density, "「標準」は誤差 0.1px、「少なめ」は誤差 0.5px までに収まるように間引きます");
        _smooth.ValueChanged += (_, e) =>
        {
            if (!_updating && ViewModel is { } viewModel)
                viewModel.SmoothRadius.Value = (int)(e.NewValue ?? 0);
        };
        _use.SelectionChanged += (_, _) =>
        {
            if (!_updating && ViewModel is { } viewModel)
                viewModel.Use.Value = (MotionUse)_use.SelectedIndex;
            UpdateUseHint();
        };
        _density.SelectionChanged += (_, _) =>
        {
            if (!_updating && ViewModel is { } viewModel)
                viewModel.Density.Value = (KeyframeDensity)_density.SelectedIndex;
        };

        UpdateUseHint();
        return new StackPanel
        {
            Spacing = 8,
            Children = { SectionTitle("適用"), _use, _targetRow, channels, options },
            [!IsEnabledProperty] = new Binding("IsTracking.Value") { Converter = BoolConverters.Not },
        };

        static Control Check(string text, string path) => new CheckBox
        {
            Content = text,
            MinWidth = 0,
            [!ToggleButton.IsCheckedProperty] = new Binding(path) { Mode = BindingMode.TwoWay },
        };
    }

    private void UpdateUseHint()
    {
        var use = (MotionUse)Math.Max(_use.SelectedIndex, 0);
        _targetRow.IsVisible = use == MotionUse.Follow;
        ToolTip.SetTip(_use, use switch
        {
            MotionUse.Lock => "追跡元を逆向きに動かして、追跡した点を固定します",
            MotionUse.Steady => "追跡元の細かい揺れだけを取り除きます",
            _ => "適用先の要素が、追跡した動きについて行きます",
        });
    }

    private void Edit(Action<TrackerViewModel> edit)
    {
        if (!_updating && ViewModel?.SelectedTracker.Value is { } tracker)
            edit(tracker);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        foreach (IDisposable subscription in _subscriptions)
            subscription.Dispose();
        _subscriptions.Clear();

        _viewModel = ViewModel;
        _canvas.ViewModel = _viewModel;
        _rangeBar.ViewModel = _viewModel;
        if (_viewModel is not { } viewModel)
            return;

        _subscriptions.Add(viewModel.SelectedTracker.Subscribe(ShowTracker));
        _subscriptions.Add(viewModel.FeatureStrength.Subscribe(text =>
        {
            _featureText.Text = $"この場所の特徴: {text}";
            _featureText.IsVisible = text != null;
        }));
        _subscriptions.Add(viewModel.DisplayFrame.Subscribe(_ => UpdateFrameText()));
        _subscriptions.Add(viewModel.FrameCount.Subscribe(_ => UpdateFrameText()));
        _subscriptions.Add(viewModel.Use.Subscribe(use => Sync(() => _use.SelectedIndex = (int)use)));
        _subscriptions.Add(viewModel.Density.Subscribe(density => Sync(() => _density.SelectedIndex = (int)density)));
        _subscriptions.Add(viewModel.SmoothRadius.Subscribe(radius => Sync(() => _smooth.Value = radius)));
        UpdateUseHint();
    }

    private void UpdateFrameText()
    {
        _frameText.Text = _viewModel is { Source.Value: not null, DisplayFrame.Value: { } frame } viewModel
            ? $"フレーム {frame + 1} / {viewModel.FrameCount.Value}"
            : "";
    }

    // Keeps the editors on the selected tracker's values, whether changed here or by dragging on the canvas.
    private void ShowTracker(TrackerViewModel? tracker)
    {
        foreach (IDisposable subscription in _trackerSubscriptions)
            subscription.Dispose();
        _trackerSubscriptions.Clear();
        if (tracker == null)
            return;

        _trackerSubscriptions.Add(tracker.PatternHalfWidth.Subscribe(value => Sync(() => _patternWidth.Value = value * 2)));
        _trackerSubscriptions.Add(tracker.PatternHalfHeight.Subscribe(value => Sync(() => _patternHeight.Value = value * 2)));
        _trackerSubscriptions.Add(tracker.SearchHalfWidth.Subscribe(value => Sync(() => _searchWidth.Value = value * 2)));
        _trackerSubscriptions.Add(tracker.SearchHalfHeight.Subscribe(value => Sync(() => _searchHeight.Value = value * 2)));
        _trackerSubscriptions.Add(tracker.Model.Subscribe(value => Sync(() => _model.IsChecked = value == TrackerMotionModel.PositionRotationScale)));
        _trackerSubscriptions.Add(tracker.Template.Subscribe(value => Sync(() => _template.IsChecked = value == TrackerTemplateMode.Adaptive)));
        _trackerSubscriptions.Add(tracker.MinConfidence.Subscribe(value => Sync(() => _confidence.Value = (decimal)Math.Round(value * 100))));
    }

    private void Sync(Action update)
    {
        bool updating = _updating;
        _updating = true;
        update();
        _updating = updating;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyLayout(e.NewSize.Width >= WideLayoutMinWidth);
    }

    public void Dispose()
    {
        foreach (IDisposable subscription in _trackerSubscriptions)
            subscription.Dispose();
        _trackerSubscriptions.Clear();
        DataContext = null;
    }

    // Side by side when there is room, stacked when narrow, where the frame keeps at least half of the height. Side by
    // side everything is a little tighter, as in the "無音区間の分割" tab.
    private void ApplyLayout(bool wide)
    {
        if (_layoutApplied && _wide == wide)
            return;

        _layoutApplied = true;
        _wide = wide;
        if (wide)
        {
            _body.RowDefinitions = [];
            _body.ColumnDefinitions = new ColumnDefinitions($"*,{PanelWidth}");
            Grid.SetRow(_panelPane, 0);
            Grid.SetColumn(_panelPane, 1);
        }
        else
        {
            _body.ColumnDefinitions = [];
            _body.RowDefinitions = new RowDefinitions("*,*");
            Grid.SetColumn(_panelPane, 0);
            Grid.SetRow(_panelPane, 1);
        }

        _viewerPane.RowSpacing = wide ? 4 : 8;
        _panelStack.Spacing = wide ? 8 : 12;
        foreach (Border group in _groups)
            group.Padding = new Thickness(wide ? 8 : 12);

        _footerLayout.ColumnDefinitions = new ColumnDefinitions(wide ? "*,Auto" : "*");
        _footerLayout.RowDefinitions = new RowDefinitions(wide ? "Auto" : "Auto,Auto");
        Grid.SetColumn(_footerButtons, wide ? 1 : 0);
        Grid.SetRow(_footerButtons, wide ? 0 : 1);
        _applyButton.MinWidth = wide ? 128 : 0;
    }
}
