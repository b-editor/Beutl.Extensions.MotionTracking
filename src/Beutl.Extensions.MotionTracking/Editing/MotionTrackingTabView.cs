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
/// The view of the "モーショントラッキング" tool tab, laid out like Beutl's own tool tabs: the frame with the trackers
/// over it, the frames bar under that, and the panel of trackers and what to do with the result beside it, or under
/// it when the tab is narrow. Its DataContext is a <see cref="MotionTrackingTabViewModel"/>.
/// </summary>
public sealed class MotionTrackingTabView : UserControl, IDisposable
{
    private const double PanelWidth = 340;
    private const double WideLayoutMinWidth = 760;

    private readonly TrackerCanvas _canvas = new();
    private readonly TrackRangeBar _rangeBar = new() { Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
    private readonly Grid _body = new();
    private readonly Control _viewerPane;
    private readonly ScrollViewer _panelPane;
    private readonly ListBox _trackerList;
    private readonly StackPanel _trackerSettings;
    private readonly NumericUpDown _patternWidth = SizeEditor();
    private readonly NumericUpDown _patternHeight = SizeEditor();
    private readonly NumericUpDown _searchWidth = SizeEditor();
    private readonly NumericUpDown _searchHeight = SizeEditor();
    private readonly ComboBox _model = Choices("位置・回転・拡大", "位置のみ");
    private readonly ComboBox _template = Choices("自動で更新", "最初のまま");
    private readonly Slider _confidence = new() { Minimum = 0.3, Maximum = 0.98, SmallChange = 0.01, LargeChange = 0.05 };
    private readonly TextBlock _confidenceText = Secondary();
    private readonly TextBlock _featureText = Secondary();
    private readonly TextBlock _frameText = Secondary();
    private readonly ComboBox _use = Choices("オブジェクトを動きに合わせる", "手ブレを補正（固定する）", "手ブレを補正（滑らかにする）");
    private readonly ComboBox _density = Choices("すべてのフレーム", "標準（誤差 0.1px）", "少なめ（誤差 0.5px）");
    private readonly NumericUpDown _smooth = new() { Minimum = 0, Maximum = 120, Increment = 1, FormatString = "0", ClipValueToMinMax = true };
    private readonly Control _targetRow;
    private readonly TextBlock _useHint = Secondary();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly List<IDisposable> _trackerSubscriptions = [];
    private MotionTrackingTabViewModel? _viewModel;
    private bool _wide = true;
    private bool _layoutApplied;
    private bool _updating;

    public MotionTrackingTabView()
    {
        // The frame takes all the room it can; the bar under it takes what its rows need.
        var viewer = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_rangeBar, Dock.Bottom);
        viewer.Children.Add(_rangeBar);

        // Outside the source there is no frame to show, and nothing to place a tracker on.
        var noFrameText = new TextBlock { Text = "再生ヘッドが追跡元の範囲の外にあります", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        var noFrame = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { noFrameText, TextButton("追跡元の先頭へ移動", () => ViewModel?.SeekToFrame(0)) },
            [!IsVisibleProperty] = new Binding("CurrentFrame.Value") { Converter = ObjectConverters.IsNull },
        };
        viewer.Children.Add(new Panel { Children = { _canvas, noFrame } });
        _viewerPane = viewer;

        (_trackerList, _trackerSettings) = BuildTrackerControls();
        _targetRow = BuildTargetRow();
        _panelPane = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = BuildPanel(),
        };

        _body.Children.Add(_viewerPane);
        _body.Children.Add(_panelPane);
        _body[!IsVisibleProperty] = new Binding("Source.Value") { Converter = ObjectConverters.IsNotNull };

        TextBlock noSourceTitle = EmptyTitle();
        noSourceTitle[!TextBlock.TextProperty] = new Binding("SourceMessage.Value");
        StackPanel noSource = EmptyState(Icon.Target, noSourceTitle, TextButton("選択中の要素を使う", () => ViewModel?.UseSelectionAsSource(), accent: true));
        noSource[!IsVisibleProperty] = new Binding("Source.Value") { Converter = ObjectConverters.IsNull };

        var page = new DockPanel { LastChildFill = true };
        Border bar = BuildToolBar();
        DockPanel.SetDock(bar, Dock.Top);
        page.Children.Add(bar);
        Control status = BuildStatusLine();
        DockPanel.SetDock(status, Dock.Bottom);
        page.Children.Add(status);
        page.Children.Add(new Panel { Children = { _body, noSource } });
        Content = page;

        ApplyLayout(wide: true);
    }

    private MotionTrackingTabViewModel? ViewModel => DataContext as MotionTrackingTabViewModel;

    private static NumericUpDown SizeEditor() => new()
    {
        Minimum = TrackerViewModel.MinPatternHalfSize * 2,
        Maximum = TrackerViewModel.MaxHalfSize * 2,
        Increment = 2,
        FormatString = "0",
        MinWidth = 96,
        ClipValueToMinMax = true,
    };

    private static ComboBox Choices(params string[] items)
    {
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 0 };
        foreach (string item in items)
            combo.Items.Add(item);
        return combo;
    }

    private Border BuildToolBar()
    {
        TextBlock title = BarTitle();
        title[!TextBlock.TextProperty] = new Binding("SourceName.Value") { StringFormat = "追跡元: {0}" };
        title[!IsVisibleProperty] = new Binding("Source.Value") { Converter = ObjectConverters.IsNotNull };

        Button previous = IconButton(Icon.Previous, "1 フレーム戻って追跡", () => ViewModel?.TrackAsync(-1, singleFrame: true));
        Button backward = IconButton(Icon.Rewind, "後ろへ追跡（再生ヘッドから先頭まで）", () => ViewModel?.TrackAsync(-1));
        Button stop = IconButton(Icon.Stop, "追跡を止める", () => ViewModel?.Stop());
        Button forward = IconButton(Icon.FastForward, "前へ追跡（再生ヘッドから末尾まで）", () => ViewModel?.TrackAsync(1));
        Button next = IconButton(Icon.Next, "1 フレーム進んで追跡", () => ViewModel?.TrackAsync(1, singleFrame: true));
        foreach (Button button in new[] { previous, backward, forward, next })
        {
            button[!IsEnabledProperty] = new MultiBinding
            {
                Converter = BoolConverters.And,
                Bindings =
                {
                    new Binding("Source.Value") { Converter = ObjectConverters.IsNotNull },
                    new Binding("IsTracking.Value") { Converter = BoolConverters.Not },
                },
            };
        }

        stop[!IsEnabledProperty] = new Binding("IsTracking.Value");

        Button useSelection = IconButton(Icon.TargetArrow, "選択中の要素を追跡元にする", () => ViewModel?.UseSelectionAsSource());
        useSelection[!IsEnabledProperty] = new Binding("IsTracking.Value") { Converter = BoolConverters.Not };
        Button locate = IconButton(Icon.Search, "追跡元の要素をタイムラインで選択", () => ViewModel?.SelectSourceInTimeline());
        locate[!IsEnabledProperty] = new Binding("Source.Value") { Converter = ObjectConverters.IsNotNull };
        Button fit = IconButton(Icon.ArrowMaximize, "全体を表示", () => _canvas.ResetView());

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Children = { previous, backward, stop, forward, next, Separator(), useSelection, locate, fit },
        };
        _frameText.VerticalAlignment = VerticalAlignment.Center;
        _frameText.TextWrapping = TextWrapping.NoWrap;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { title, _frameText, actions } };
        title.MaxWidth = 280;
        Grid.SetColumn(_frameText, 1);
        Grid.SetColumn(actions, 2);
        return ToolBar(grid);

        static Control Separator() => WithResource(
            new Rectangle { Width = 1, Height = 16, Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center },
            Shape.FillProperty,
            "DividerStrokeColorDefaultBrush");
    }

    private Control BuildStatusLine()
    {
        var progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Width = 120,
            VerticalAlignment = VerticalAlignment.Center,
            [!RangeBase.ValueProperty] = new Binding("Progress.Value"),
            [!IsVisibleProperty] = new Binding("IsTracking.Value"),
        };
        TextBlock message = Secondary();
        message.VerticalAlignment = VerticalAlignment.Center;
        message[!TextBlock.TextProperty] = new Binding("Status.Value");
        var row = new DockPanel { Margin = new Thickness(8, 4), LastChildFill = true };
        DockPanel.SetDock(progress, Dock.Right);
        row.Children.Add(progress);
        row.Children.Add(message);
        row[!IsVisibleProperty] = new Binding("Status.Value") { Converter = StringConverters.IsNotNullOrEmpty };
        return row;
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
        AddRow(sizes, "パターン幅", _patternWidth);
        AddRow(sizes, "パターン高さ", _patternHeight);
        AddRow(sizes, "検索の幅", _searchWidth);
        AddRow(sizes, "検索の高さ", _searchHeight);
        AddRow(sizes, "追跡内容", _model);
        AddRow(sizes, "見本の更新", _template);
        ToolTip.SetTip(_model, "「位置のみ」では、回転や拡大縮小するパターンを追跡すると位置が少しずつずれます");
        ToolTip.SetTip(_template, "比べる見本を、見た目の変化に合わせて更新するか、追跡を始めたときの見た目のままにするかを選びます");

        _confidenceText.VerticalAlignment = VerticalAlignment.Center;
        _confidenceText.MinWidth = 36;
        _confidenceText.TextAlignment = TextAlignment.Right;
        var confidenceRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_confidenceText, Dock.Right);
        confidenceRow.Children.Add(_confidenceText);
        confidenceRow.Children.Add(_confidence);
        AddRow(sizes, "止める信頼度", confidenceRow);
        ToolTip.SetTip(confidenceRow, "パターンとの一致度がこれを下回ると、そのトラッカーは止まります。見失っても動き続けるときは上げ、すぐ止まるときは下げます");

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
            Content = new StackPanel { Spacing = 8, Children = { sizes, clear } },
        };

        _patternWidth.ValueChanged += (_, e) => Edit(tracker => ViewModel!.SetPatternSize(tracker, ToHalf(e.NewValue), tracker.PatternHalfHeight.Value));
        _patternHeight.ValueChanged += (_, e) => Edit(tracker => ViewModel!.SetPatternSize(tracker, tracker.PatternHalfWidth.Value, ToHalf(e.NewValue)));
        _searchWidth.ValueChanged += (_, e) => Edit(tracker => ViewModel!.SetSearchSize(tracker, ToHalf(e.NewValue), tracker.SearchHalfHeight.Value));
        _searchHeight.ValueChanged += (_, e) => Edit(tracker => ViewModel!.SetSearchSize(tracker, tracker.SearchHalfWidth.Value, ToHalf(e.NewValue)));
        _model.SelectionChanged += (_, _) => Edit(tracker => tracker.Model.Value = (TrackerMotionModel)_model.SelectedIndex);
        _template.SelectionChanged += (_, _) => Edit(tracker => tracker.Template.Value = (TrackerTemplateMode)_template.SelectedIndex);
        _confidence.ValueChanged += (_, e) =>
        {
            _confidenceText.Text = $"{e.NewValue:P0}";
            Edit(tracker => tracker.MinConfidence.Value = (float)e.NewValue);
        };

        var settings = new StackPanel
        {
            Spacing = 8,
            Children = { _featureText, details },
            [!IsVisibleProperty] = new Binding("SelectedTracker.Value") { Converter = ObjectConverters.IsNotNull },
        };
        return (list, settings);

        static int ToHalf(decimal? value) => (int)((value ?? 0) / 2);
    }

    private static Control BuildTrackerRow(TrackerViewModel tracker, INameScope scope)
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
            Fill = tracker.Brush,
            VerticalAlignment = VerticalAlignment.Center,
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

    private Control BuildPanel()
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

        var trackerGroup = new StackPanel
        {
            Spacing = 8,
            Children = { trackerHeader, _trackerList, _trackerSettings },
        };
        _trackerSettings[!IsEnabledProperty] = new Binding("IsTracking.Value") { Converter = BoolConverters.Not };

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
        AddRow(options, "滑らかさ", WithSuffix(_smooth, "フレーム"));
        AddRow(options, "キーフレーム", _density);
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

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                TextButton("適用", () => ViewModel?.Apply(), accent: true),
                TextButton("解除", () => ViewModel?.RemoveApplied()),
            },
        };

        var applyGroup = new StackPanel
        {
            Spacing = 8,
            Children = { SectionTitle("適用"), _use, _useHint, _targetRow, channels, options, buttons },
            [!IsEnabledProperty] = new Binding("IsTracking.Value") { Converter = BoolConverters.Not },
        };

        UpdateUseHint();
        return new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(8),
            Children = { Group(trackerGroup), Group(applyGroup) },
        };

        static Control Check(string text, string path) => new CheckBox
        {
            Content = text,
            MinWidth = 0,
            [!ToggleButton.IsCheckedProperty] = new Binding(path) { Mode = BindingMode.TwoWay },
        };

        static Control WithSuffix(Control editor, string suffix)
        {
            var row = new DockPanel { LastChildFill = true };
            TextBlock text = Secondary(suffix);
            text.VerticalAlignment = VerticalAlignment.Center;
            text.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(text, Dock.Right);
            row.Children.Add(text);
            row.Children.Add(editor);
            return row;
        }
    }

    private void UpdateUseHint()
    {
        var use = (MotionUse)Math.Max(_use.SelectedIndex, 0);
        _targetRow.IsVisible = use == MotionUse.Follow;
        _useHint.Text = use switch
        {
            MotionUse.Lock => "追跡元の要素を逆向きに動かして、追跡した点を再生ヘッドのフレームの位置に固定します。",
            MotionUse.Steady => "追跡元の要素の細かい揺れだけを取り除きます。「滑らかさ」が大きいほど大きな揺れまで取り除きます。",
            _ => "再生ヘッドのフレームでは今の配置のまま、適用先の要素が追跡した動きについて行きます。",
        };
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
        _trackerSubscriptions.Add(tracker.Model.Subscribe(value => Sync(() => _model.SelectedIndex = (int)value)));
        _trackerSubscriptions.Add(tracker.Template.Subscribe(value => Sync(() => _template.SelectedIndex = (int)value)));
        _trackerSubscriptions.Add(tracker.MinConfidence.Subscribe(value =>
        {
            Sync(() => _confidence.Value = value);
            _confidenceText.Text = $"{value:P0}";
        }));
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

    // Side by side when there is room, stacked when narrow, where the frame keeps at least half of the height.
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
    }
}
