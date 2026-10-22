using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using FrpDesk.Models;
using FrpDesk.Services;
using FrpDesk.Views;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.UI.ViewManagement;
using WinRT;
using WinRT.Interop;
using XamlButton = Microsoft.UI.Xaml.Controls.Button;
using XamlClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace FrpDesk;

public sealed class ActivityEntry(
    string message,
    string time,
    string mark,
    Microsoft.UI.Xaml.Media.Brush markBrush)
{
    public string Message { get; set; } = message;
    public string Time { get; set; } = time;
    public string Mark { get; set; } = mark;
    public Microsoft.UI.Xaml.Media.Brush MarkBrush { get; set; } = markBrush;
}

public sealed class LogEntry(
    string rawText,
    string time,
    string level,
    string message,
    Microsoft.UI.Xaml.Media.Brush levelBrush,
    Microsoft.UI.Xaml.Media.Brush rowBrush)
{
    public string RawText { get; set; } = rawText;
    public string Time { get; set; } = time;
    public string Level { get; set; } = level;
    public string Message { get; set; } = message;
    public Microsoft.UI.Xaml.Media.Brush LevelBrush { get; set; } = levelBrush;
    public Microsoft.UI.Xaml.Media.Brush RowBrush { get; set; } = rowBrush;
}

public sealed class ProfileRow : INotifyPropertyChanged
{
    private string _name;
    private string _serverText = "未配置";
    private string _tunnelCountText = "0";
    private string _stateText = "已停止";
    private Microsoft.UI.Xaml.Media.Brush _stateBrush;
    private string _actionText = "启动";
    private bool _canToggle;
    private bool _canEdit = true;
    private bool _canDelete;

    public ProfileRow(TunnelProfile profile, Microsoft.UI.Xaml.Media.Brush idleBrush)
    {
        Profile = profile;
        _name = profile.Name;
        _stateBrush = idleBrush;
    }

    public TunnelProfile Profile { get; }
    public string Name { get => _name; private set => Set(ref _name, value); }
    public string ServerText { get => _serverText; private set => Set(ref _serverText, value); }
    public string TunnelCountText { get => _tunnelCountText; private set => Set(ref _tunnelCountText, value); }
    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }
    public Microsoft.UI.Xaml.Media.Brush StateBrush { get => _stateBrush; private set => Set(ref _stateBrush, value); }
    public string ActionText { get => _actionText; private set => Set(ref _actionText, value); }
    public bool CanToggle { get => _canToggle; private set => Set(ref _canToggle, value); }
    public bool CanEdit { get => _canEdit; private set => Set(ref _canEdit, value); }
    public bool CanDelete { get => _canDelete; private set => Set(ref _canDelete, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh(TomlEndpointParser parser, Func<string, Microsoft.UI.Xaml.Media.Brush> brush)
    {
        Name = Profile.Name;
        var server = parser.GetServerEndpoint(Profile.Config);
        ServerText = string.IsNullOrWhiteSpace(server) ? "未配置" : server;
        TunnelCountText = parser.Parse(Profile.Config).Count.ToString(CultureInfo.InvariantCulture);
        StateText = Profile.DisplayState;
        var isFailure = StateText.Contains("失败", StringComparison.Ordinal)
            || StateText.Contains("错误", StringComparison.Ordinal)
            || StateText.Contains("耗尽", StringComparison.Ordinal);
        var isWorking = Profile.IsBusy || StateText.Contains("中", StringComparison.Ordinal);
        StateBrush = Profile.IsActive
            ? brush("SuccessBrush")
            : isFailure ? brush("AlertBrush")
            : isWorking ? brush("SignalDeepBrush")
            : brush("MutedBrush");
        ActionText = Profile.IsRunning ? "停止" : "启动";
        CanToggle = !Profile.IsBusy && !Profile.IsBackgroundConfigured;
        CanEdit = !Profile.IsBusy;
        CanDelete = !Profile.IsActive && !Profile.IsBackgroundConfigured && !Profile.IsBusy;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const int SwHide = 0;
    private const int SwRestore = 9;

    private readonly ProfileStore _store = new();
    private readonly AppSettingsStore _settingsStore = new();
    private readonly TomlEndpointParser _endpointParser = new();
    private readonly DispatcherQueue _queue;
    private readonly DispatcherQueueTimer _backgroundStateTimer;
    private readonly DispatcherQueueTimer _noticeTimer;
    private readonly DispatcherQueueTimer _copyFeedbackTimer;
    private readonly UISettings _uiSettings;
    private readonly ThemeSettings _themeSettings;
    /// <summary>当前明暗方案下每个中性令牌的"不透明"取值，材质关闭和高对比度退出时都还原到这份。</summary>
    private readonly Dictionary<string, Windows.UI.Color> _schemeColors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SolidColorBrush> _choiceSwatchBrushes = new(StringComparer.Ordinal);
    private bool _isDarkScheme;
    private readonly string _windowLayoutPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrpDesk", "window.json");
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly FrpcProcessManager _processManager;
    private readonly BackgroundTaskManager _backgroundTaskManager;

    private AppSettings _settings;
    private TunnelProfile? _selectedProfile;
    private TunnelProfile? _selectedEndpointProfile;
    private TunnelProfile? _editingTunnelProfile;
    private TunnelEndpoint? _editingEndpoint;
    private bool _isRefreshingEndpoints;
    private bool _endpointListInitialized;
    private string? _endpointConfigSnapshot;
    private bool _isLoadingSettings;
    private bool _isLoadingTunnelEditor;
    private bool _isAddingTunnel;
    private bool _isClosing;
    private bool _windowIsActive = true;
    private bool _allowExit;
    private bool _isBatchOperating;
    private bool _skipNextLayoutSave;
    private string? _startupWarning;
    private string? _backdropWarning;
    private string? _backdropApplyFailure;
    private bool _materialActive;
    private string _logText = "";
    private TunnelProfile? _logTextProfile;
    private TunnelProfile? _recentActivityProfile;
    private string? _recentActivitySignature;
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private XamlButton? _copyFeedbackButton;
    private object? _copyFeedbackOriginalContent;
    private Forms.NotifyIcon? _notifyIcon;
    private Drawing.Icon? _trayIcon;
    private TrayMenuWindow? _trayMenu;
    private Flyout? _profileFlyout;

    public MainWindow()
    {
        InitializeComponent();

        _uiSettings = new UISettings();
        _queue = DispatcherQueue.GetForCurrentThread();
        _isDarkScheme = RootGrid.ActualTheme == ElementTheme.Dark;
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        _themeSettings = ThemeSettings.CreateForWindowId(_appWindow.Id);
        ApplyColorScheme(_isDarkScheme);
        ShareAccentBrushesWithThemeDictionaries();
        SetHighContrastPalette(_themeSettings.HighContrast);
        RootGrid.ActualThemeChanged += (_, _) => DispatchToUi(() =>
        {
            ApplyColorScheme(RootGrid.ActualTheme == ElementTheme.Dark);
            RefreshTrayMenu();
        });
        ConfigureTitleBar();
        _uiSettings.AdvancedEffectsEnabledChanged += UiSettings_AdvancedEffectsEnabledChanged;
        _themeSettings.Changed += ThemeSettings_Changed;
        _appWindow.Title = "FrpDesk";
        _appWindow.Closing += AppWindow_Closing;
        Closed += Window_Closed;
        Activated += MainWindow_Activated;

        _settings = _settingsStore.Load(out var settingsWarning);
        _startupWarning = settingsWarning;
        _backdropWarning = ApplyWindowBackdrop(_settings.BackdropMaterial)
            ? null
            : GetBackdropFallbackReason(_settings.BackdropMaterial);
        BuildThemeChoices();
        ApplyTheme(_settings.Theme);
        UpdateRailLayout(0);
        _processManager = new FrpcProcessManager(ResolveFrpcPath(), DispatchToUi, _store);
        _backgroundTaskManager = new BackgroundTaskManager(ResolveFrpcPath(), _store);
        _backgroundStateTimer = _queue.CreateTimer();
        _backgroundStateTimer.Interval = TimeSpan.FromSeconds(3);
        _backgroundStateTimer.Tick += async (_, _) => await RefreshBackgroundStateAsync();
        _noticeTimer = _queue.CreateTimer();
        _noticeTimer.IsRepeating = false;
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            NoticeBar.IsOpen = false;
        };
        _copyFeedbackTimer = _queue.CreateTimer();
        _copyFeedbackTimer.Interval = TimeSpan.FromMilliseconds(1400);
        _copyFeedbackTimer.IsRepeating = false;
        _copyFeedbackTimer.Tick += (_, _) => RestoreCopyFeedback();

        RootGrid.DataContext = this;
        LogLinesList.ContextFlyout = CreateLogMenu();
        CreateTrayIcon();
        SystemEvents.SessionEnding += SystemEvents_SessionEnding;

        SetDefaultWindowBounds();
        RestoreWindowLayout();
        SetWindowIcon();

        try
        {
            var loaded = _store.Load(out var profileWarning);
            _startupWarning ??= profileWarning;
            foreach (var profile in loaded) AddProfileToList(profile);
        }
        catch (Exception exception)
        {
            _startupWarning = $"读取实例失败：{exception.Message}";
        }

        SelectedProfile = Profiles.FirstOrDefault();
        UpdateSummary();
        LoadSettingsPage();
        ShowHomePage();
        RootGrid.Loaded += RootGrid_Loaded;
    }

    public ObservableCollection<TunnelProfile> Profiles { get; } = [];
    public ObservableCollection<ProfileRow> ProfileRows { get; } = [];
    public ObservableCollection<TunnelEndpoint> Endpoints { get; } = [];
    public ObservableCollection<ActivityEntry> RecentActivityRows { get; } = [];
    public ObservableCollection<LogEntry> LogEntries { get; } = [];
    public string AppVersion => AppInfo.Version;

    public TunnelProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (ReferenceEquals(_selectedProfile, value)) return;
            _selectedProfile = value;
            OnPropertyChanged(nameof(SelectedProfile));
            UpdateSelectedPanel();
            RefreshEndpointProfiles();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void StartHidden()
    {
        Activate();
        ShowWindow(_hwnd, SwHide);
    }

    public void ShowFromTray()
    {
        ShowWindow(_hwnd, SwRestore);
        Activate();
        if (_appWindow.Presenter is OverlappedPresenter presenter
            && presenter.State == OverlappedPresenterState.Minimized)
        {
            presenter.Restore();
        }
    }

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= RootGrid_Loaded;
        // 构造期 ActualTheme 可能还没落到最终值，这里按真正生效的主题再刷一次。
        ApplyColorScheme(RootGrid.ActualTheme == ElementTheme.Dark);
        UpdateTitleBarInsets();
        if (!string.IsNullOrWhiteSpace(_startupWarning))
        {
            ShowNotice(_startupWarning!, InfoBarSeverity.Warning);
            _startupWarning = null;
        }

        _ = LoadBackgroundStateAsync();
        _backgroundStateTimer.Start();
        EnsureTrayMenu();
    }

    private void DispatchToUi(Action action)
    {
        if (!_queue.TryEnqueue(() => action()))
        {
            // The window is closing; there is no UI left to update.
        }
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        _windowIsActive = args.WindowActivationState != WindowActivationState.Deactivated;
        if (_backdropConfiguration is not null)
            _backdropConfiguration.IsInputActive = _windowIsActive;
    }

    private async void AddProfile_Click(object sender, RoutedEventArgs e) => await AddProfileAsync();

    private async Task<TunnelProfile?> AddProfileAsync()
    {
        var dialog = new ProfileDialog(ResolveFrpcPath());
        if (!await ShowProfileDialogAsync(dialog)) return null;

        try
        {
            var profile = _store.CreateProfile(dialog.ProfileName, dialog.ConfigText, Profiles);
            AddProfileToList(profile);
            SelectedProfile = profile;
            UpdateSummary();
            ShowNotice("配置已添加。", InfoBarSeverity.Success);
            return profile;
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("保存失败", exception.Message, InfoBarSeverity.Error);
            return null;
        }
    }

    private async Task EditProfileAsync(TunnelProfile profile)
    {
        var wasRunning = profile.IsRunning;
        var wasBackgroundConfigured = profile.IsBackgroundConfigured;
        var dialog = new ProfileDialog(ResolveFrpcPath(), profile);
        if (!await ShowProfileDialogAsync(dialog)) return;

        try
        {
            _store.UpdateProfile(profile, dialog.ProfileName, dialog.ConfigText, Profiles);
            profile.ClearDiagnostic();
            _store.SaveDiagnostic(profile);
            profile.AddLog("配置已保存。");
            var applied = true;
            if (wasRunning || wasBackgroundConfigured)
                applied = await ApplyProfileConfigAsync(profile, wasRunning, wasBackgroundConfigured);
            RefreshEndpointProfiles();
            UpdateSummary();
            if (applied) ShowNotice("配置已保存。", InfoBarSeverity.Success);
            else ShowNotice("配置已保存，但运行中的实例未能应用新配置。", InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("保存配置失败", exception.Message, InfoBarSeverity.Error);
        }
    }

    private async Task<bool> ApplyProfileConfigAsync(TunnelProfile profile, bool wasRunning, bool wasBackgroundConfigured)
    {
        try
        {
            _store.SaveConfig(profile);
            if (wasBackgroundConfigured)
            {
                await _backgroundTaskManager.RestartAsync(profile);
                return true;
            }

            if (!wasRunning) return true;
            if (HasFrpcAdminApi(profile.Config))
            {
                try
                {
                    await _processManager.ReloadAsync(profile, _store.ConfigPath(profile));
                    return true;
                }
                catch (InvalidDataException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    profile.AddLog($"热更新失败：{exception.Message} 正在改用重启应用配置。");
                }
            }

            profile.AddLog("当前配置未启用 frpc 管理接口，正在重启以应用配置。");
            await _processManager.StopAsync(profile);
            return await StartProfileAsync(profile, showError: true);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("应用配置失败", exception.Message, InfoBarSeverity.Error);
            return false;
        }
    }

    private static bool HasFrpcAdminApi(string config)
    {
        foreach (var rawLine in config.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("webServer.", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("[webServer]", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("admin_addr", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("admin_port", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null) return;
        if (SelectedProfile.IsBackgroundRunning)
        {
            await ShowMessageAsync("实例正在后台运行", "此实例正在后台运行，请先停止后台运行。", InfoBarSeverity.Informational);
            return;
        }

        try
        {
            if (SelectedProfile.IsRunning)
                await _processManager.StopAsync(SelectedProfile);
            else
                await StartProfileAsync(SelectedProfile, showError: true);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("实例操作失败", exception.Message, InfoBarSeverity.Error);
        }

        UpdateSummary();
    }

    private async void StartAll_Click(object sender, RoutedEventArgs e)
    {
        if (_isBatchOperating) return;
        var candidates = Profiles
            .Where(profile => !profile.IsBackgroundConfigured && !profile.IsRunning && !profile.IsBusy)
            .ToArray();
        if (candidates.Length == 0)
        {
            await ShowMessageAsync("全部运行", "没有可以直接运行的实例。后台服务实例需要单独管理。",
                InfoBarSeverity.Informational);
            return;
        }

        _isBatchOperating = true;
        try
        {
            var results = await Task.WhenAll(candidates.Select(profile => StartProfileAsync(profile, showError: false)));
            var failed = candidates.Where((_, index) => !results[index]).Select(profile => profile.Name).ToArray();
            if (failed.Length > 0)
            {
                await ShowMessageAsync("部分实例启动失败",
                    $"已尝试运行 {candidates.Length} 个实例，其中 {failed.Length} 个启动失败：\n" +
                    $"{string.Join("、", failed)}\n\n请选中失败实例查看诊断信息。",
                    InfoBarSeverity.Warning);
            }
        }
        finally
        {
            _isBatchOperating = false;
            UpdateSummary();
        }
    }

    private async void StopAll_Click(object sender, RoutedEventArgs e)
    {
        var errors = await StopEverythingAsync();
        if (errors.Count > 0)
            await ShowMessageAsync("停止实例失败", string.Join("\n", errors), InfoBarSeverity.Error);

        UpdateSummary();
    }

    private async Task<IReadOnlyList<string>> StopEverythingAsync()
    {
        // 前台进程和后台服务要分别处理：一边失败不应阻止另一边停止。
        var errors = new List<string>();
        try
        {
            await _processManager.StopAllAsync(Profiles);
        }
        catch (Exception exception)
        {
            errors.Add($"前台实例：{exception.Message}");
        }

        try
        {
            await _backgroundTaskManager.StopManyAsync(Profiles);
        }
        catch (Exception exception)
        {
            errors.Add($"后台服务：{exception.Message}");
        }

        return errors;
    }

    private async void ProfileRowToggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProfileRow row) return;
        var profile = row.Profile;
        if (profile.IsBusy || profile.IsBackgroundConfigured) return;
        try
        {
            if (profile.IsRunning)
                await _processManager.StopAsync(profile);
            else
                await StartProfileAsync(profile, showError: true);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("实例操作失败", exception.Message, InfoBarSeverity.Error);
        }

        UpdateSummary();
    }

    private async void ProfileRowEdit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ProfileRow row)
            await EditProfileAsync(row.Profile);
    }

    private async void ProfileRowDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ProfileRow row)
            await DeleteProfileAsync(row.Profile);
    }

    private async Task DeleteProfileAsync(TunnelProfile profile)
    {
        if (profile.IsActive || profile.IsBackgroundConfigured)
        {
            await ShowMessageAsync("无法删除", "请先停止运行中的实例。", InfoBarSeverity.Informational);
            return;
        }

        var confirmed = await ConfirmAsync("删除实例",
            $"确定删除“{profile.Name}”？\n对应的 TOML 配置会移入可恢复的隔离目录。",
            "删除");
        if (!confirmed) return;

        try
        {
            _store.DeleteProfile(profile, Profiles);
            profile.PropertyChanged -= Profile_PropertyChanged;
            profile.Logs.CollectionChanged -= Logs_CollectionChanged;
            var wasSelected = ReferenceEquals(SelectedProfile, profile);
            Profiles.Remove(profile);
            if (wasSelected) SelectedProfile = Profiles.FirstOrDefault();
            if (ReferenceEquals(_selectedEndpointProfile, profile)) _selectedEndpointProfile = null;
            RefreshEndpointProfiles();
            UpdateSummary();
            ShowNotice("配置已删除；数据已移入隔离目录。", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("删除失败", exception.Message, InfoBarSeverity.Error);
        }
    }

    private void ProfileMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor) return;
        var rows = new StackPanel { Spacing = 2 };
        foreach (var profile in Profiles)
        {
            var selected = ReferenceEquals(profile, SelectedProfile);
            var check = new FontIcon
            {
                Glyph = "\uE73E",
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Foreground = ResourceBrush("SignalDeepBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = selected ? 1 : 0
            };
            var label = new TextBlock
            {
                Text = profile.Name,
                FontSize = 14,
                FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            var content = new Grid { ColumnSpacing = 8 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(label, 1);
            content.Children.Add(check);
            content.Children.Add(label);

            var row = new XamlButton
            {
                Content = content,
                Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["ProfileMenuRow"]
            };
            ToolTipService.SetToolTip(row, profile.Name);
            row.Click += (_, _) =>
            {
                SelectedProfile = profile;
                UpdateSummary();
                HideProfileFlyout();
            };
            rows.Children.Add(row);
        }

        var addIcon = new FontIcon
        {
            Glyph = "\uE710",
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = ResourceBrush("InkSoftBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var addLabel = new TextBlock
        {
            Text = "添加配置",
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center
        };
        var addContent = new Grid { ColumnSpacing = 8 };
        addContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        addContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(addLabel, 1);
        addContent.Children.Add(addIcon);
        addContent.Children.Add(addLabel);
        var addRow = new XamlButton
        {
            Content = addContent,
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["ProfileMenuRow"],
            Margin = new Thickness(0, 2, 0, 0)
        };
        addRow.Click += async (_, _) =>
        {
            HideProfileFlyout();
            await AddProfileAsync();
        };
        rows.Children.Add(addRow);

        var scroll = new ScrollViewer
        {
            Content = rows,
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var flyout = new Flyout
        {
            Content = scroll,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
            FlyoutPresenterStyle = (Style)Microsoft.UI.Xaml.Application.Current.Resources["ProfileMenuPresenter"]
        };
        flyout.Opened += (_, _) =>
        {
            // 弹层和触发按钮同宽，看起来就是从按钮里展开的；图标态保证至少可读。
            scroll.Width = Math.Max(anchor.ActualWidth - 18, 184);
        };
        _profileFlyout = flyout;
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_profileFlyout, flyout)) _profileFlyout = null;
        };
        flyout.ShowAt(anchor);
    }

    private void HideProfileFlyout()
    {
        _profileFlyout?.Hide();
        _profileFlyout = null;
    }

    private async Task<bool> ShowProfileDialogAsync(ProfileDialog dialog)
    {
        dialog.XamlRoot = RootGrid.XamlRoot;
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary || dialog.SaveRequested;
    }

    private void Home_Click(object sender, RoutedEventArgs e) => ShowHomePage();

    private void Profiles_Click(object sender, RoutedEventArgs e) => ShowProfilesPage();

    private void Endpoints_Click(object sender, RoutedEventArgs e) => ShowEndpointsPage();

    private void Logs_Click(object sender, RoutedEventArgs e) => ShowLogsPage();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        LoadSettingsPage();
        ShowSettingsPage();
    }

    private void LoadSettingsPage()
    {
        _isLoadingSettings = true;
        try
        {
            CloseToTrayToggle.IsOn = _settings.CloseToTray;
            RestoreLayoutToggle.IsOn = _settings.RestoreWindowLayout;
            AutoStartToggle.IsOn = StartupRegistration.IsEnabled();
            LogLimitBox.SelectedValue = _settings.LogLineLimit.ToString();
            BackdropMaterialBox.SelectedValue = _settings.BackdropMaterial;
            RailModeBox.SelectedValue = _settings.RailMode == "icon" ? "icon" : "wide";
            ApplyTheme(_settings.Theme);
            SetBackdropNote(_backdropWarning);
            DataPathBox.Text = _settingsStore.RootDirectory;
            AboutVersionText.Text = $"FrpDesk {AppInfo.Version}";
            SettingsStatusText.Text = string.Empty;
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    private void BackdropMaterial_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || BackdropMaterialBox.SelectedValue is not string material) return;
        var previous = _settings.BackdropMaterial;
        if (material == previous) return;

        var applied = ApplyWindowBackdrop(material);
        var updated = _settings.Clone();
        updated.BackdropMaterial = material;
        try
        {
            _settingsStore.Save(updated);
            _settings = updated;
            _backdropWarning = applied ? null : GetBackdropFallbackReason(material);
            SetBackdropNote(_backdropWarning);
            SettingsStatusText.Text = applied
                ? $"背景材质已应用并保存：{material}"
                : $"已保存 {material}；{_backdropWarning}";
            SettingsStatusText.Foreground = applied
                ? ResourceBrush("SuccessBrush")
                : ResourceBrush("WarningBrush");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var restored = ApplyWindowBackdrop(previous);
            _isLoadingSettings = true;
            BackdropMaterialBox.SelectedValue = previous;
            _isLoadingSettings = false;
            _backdropWarning = restored ? null : GetBackdropFallbackReason(previous);
            SetBackdropNote(_backdropWarning ?? "材质选择未保存，已恢复上次设置。");
            SettingsStatusText.Text = "材质选择未保存，已恢复上次设置。";
            SettingsStatusText.Foreground = ResourceBrush("WarningBrush");
            _ = ShowMessageAsync("保存背景材质失败", exception.Message, InfoBarSeverity.Error);
        }
    }

    private bool ApplyWindowBackdrop(string material)
    {
        _materialActive = TryApplyBackdrop(material);
        ApplySurfaceMode(_materialActive);
        return _materialActive;
    }

    private bool TryApplyBackdrop(string material)
    {
        _backdropApplyFailure = null;
        SystemBackdrop = null;
        _acrylicController?.Dispose();
        _acrylicController = null;
        _backdropConfiguration = null;

        if (GetBackdropFallbackReason(material) is not null) return false;

        try
        {
            if (material is "Mica" or "Mica Alt")
            {
                SystemBackdrop = new MicaBackdrop
                {
                    Kind = material == "Mica Alt" ? MicaKind.BaseAlt : MicaKind.Base
                };
                return true;
            }

            _backdropConfiguration = new SystemBackdropConfiguration
            {
                Theme = _isDarkScheme ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light,
                IsInputActive = _windowIsActive,
                IsHighContrast = _themeSettings.HighContrast
            };
            _acrylicController = new DesktopAcrylicController
            {
                Kind = material == "Acrylic Thin" ? DesktopAcrylicKind.Thin : DesktopAcrylicKind.Base
            };
            _acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
            _acrylicController.SetSystemBackdropConfiguration(_backdropConfiguration);
            return true;
        }
        catch (Exception exception) when (exception is COMException
            or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            SystemBackdrop = null;
            _acrylicController?.Dispose();
            _acrylicController = null;
            _backdropConfiguration = null;
            _backdropApplyFailure = $"无法应用“{material}”，已回退为纯色背景：{exception.Message}";
            return false;
        }
    }

    /// <summary>
    /// 框架自己的主题字典会就近解析 AccentFillColorDefaultBrush（ComboBox 选中项、焦点环等都在用），
    /// 应用级改同名键抢不过来，所以把这一批键直接写进 App 的 Light/Dark 字典截住。
    /// 全部指向同一批共享笔刷实例，ApplyTheme / ApplyColorScheme 改一次颜色就全跟着变。
    /// </summary>
    private void ShareAccentBrushesWithThemeDictionaries()
    {
        var dictionaries = Microsoft.UI.Xaml.Application.Current.Resources.ThemeDictionaries;
        foreach (var theme in new object[] { "Light", "Dark" })
        {
            ResourceDictionary dictionary;
            if (dictionaries.TryGetValue(theme, out var existing) && existing is ResourceDictionary existingDictionary)
            {
                dictionary = existingDictionary;
            }
            else
            {
                dictionary = new ResourceDictionary();
                dictionaries[theme] = dictionary;
            }

            foreach (var key in new[]
            {
                "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush"
            })
                dictionary[key] = ResourceBrush(key);
        }
    }

    /// <summary>
    /// 表面层令牌。材质开启时 chrome 直接就是那层裸材质（不铺任何色），
    /// 内容卡与子卡改用半透明变体；回退时全部换回实测的实心色。
    /// 高对比度下不接管卡片配色，让系统配色说话。
    /// </summary>
    private void ApplySurfaceMode(bool materialActive)
    {
        RootGrid.Background = materialActive ? null : ResourceBrush("CanvasBrush");
        if (_themeSettings.HighContrast) return;
        foreach (var (solid, translucent) in SurfaceTintPairs)
            ResourceBrush(solid).Color = materialActive
                ? ResourceBrush(translucent).Color
                : _schemeColors[solid];
    }

    private static readonly (string Solid, string Translucent)[] SurfaceTintPairs =
    [
        ("ContentCardBrush", "ContentCardBackdropBrush"),
        ("PaperBrush", "PaperBackdropBrush"),
        ("SelectionPillBrush", "SelectionPillBackdropBrush"),
        ("SoftBrush", "SoftBackdropBrush")
    ];

    private void ApplyTheme(string key)
    {
        var palette = ThemePalette.Find(key);
        if (!_themeSettings.HighContrast)
        {
            var accent = palette.Resolve(_isDarkScheme);
            ResourceBrush("SignalBrush").Color = accent.Signal;
            ResourceBrush("SignalDeepBrush").Color = accent.Deep;
            ResourceBrush("SignalSoftBrush").Color = accent.Soft;
            ResourceBrush("SignalWashBrush").Color = accent.Wash;
            ResourceBrush("SignalInkBrush").Color = accent.OnSignal;
            ResourceBrush("AccentFillColorDefaultBrush").Color = accent.Signal;
            ResourceBrush("AccentFillColorSecondaryBrush").Color = accent.Deep;
            ResourceBrush("AccentFillColorTertiaryBrush").Color = accent.Wash;
        }

        foreach (var (swatchKey, brush) in _choiceSwatchBrushes)
            brush.Color = ThemePalette.Find(swatchKey).Resolve(_isDarkScheme).Signal;

        if (ThemeChoicePanel is not null)
        {
            foreach (var child in ThemeChoicePanel.Children)
            {
                if (child is not XamlButton button) continue;
                var active = Equals(button.Tag, palette.Key);
                button.Background = ResourceBrush(active ? "SignalWashBrush" : "PaperBrush");
                button.BorderBrush = ResourceBrush(active ? "SignalBrush" : "RuleBrush");
                button.FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold
                                            : Microsoft.UI.Text.FontWeights.Normal;
            }
        }
    }

    private void BuildThemeChoices()
    {
        ThemeChoicePanel.Children.Clear();
        foreach (var palette in ThemePalette.All)
        {
            var content = new StackPanel
            {
                Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
                Spacing = 7
            };
            var swatchBrush = new SolidColorBrush(palette.Resolve(_isDarkScheme).Signal);
            _choiceSwatchBrushes[palette.Key] = swatchBrush;
            content.Children.Add(new Border
            {
                Width = 13,
                Height = 13,
                CornerRadius = new CornerRadius(4),
                Background = swatchBrush,
                VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
            });
            content.Children.Add(new TextBlock
            {
                Text = palette.Label,
                FontSize = 14,
                Foreground = ResourceBrush("InkBrush"),
                VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
            });

            var button = new XamlButton
            {
                Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["ThemeChoiceButton"],
                Content = content,
                Tag = palette.Key
            };
            button.Click += ThemeChoice_Click;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{palette.Label}主题");
            ToolTipService.SetToolTip(button, $"使用{palette.Label}作为点缀色");
            ThemeChoicePanel.Children.Add(button);
        }
    }

    private void ThemeChoice_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings || sender is not XamlButton { Tag: string key }) return;
        if (key == _settings.Theme) return;

        var updated = _settings.Clone();
        updated.Theme = key;
        try
        {
            _settingsStore.Save(updated);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _ = ShowMessageAsync("保存主题失败", exception.Message, InfoBarSeverity.Error);
            return;
        }

        _settings = updated;
        ApplyTheme(key);
        SettingsStatusText.Text = $"点缀色已切换为{ThemePalette.Find(key).Label}。";
        SettingsStatusText.Foreground = ResourceBrush("SuccessBrush");
    }

    private string? GetBackdropFallbackReason(string material)
    {
        if (_themeSettings.HighContrast)
            return "系统高对比度已开启，当前使用纯色背景。";
        if (!_uiSettings.AdvancedEffectsEnabled)
            return "系统关闭了透明效果，当前使用纯色背景。";
        if ((material is "Mica" or "Mica Alt") && !MicaController.IsSupported())
            return "当前 Windows 版本不支持 Mica，已回退为纯色背景。";
        if ((material is "Acrylic" or "Acrylic Thin") && !DesktopAcrylicController.IsSupported())
            return "当前 Windows 版本不支持 Acrylic，已回退为纯色背景。";
        return _backdropApplyFailure;
    }

    /// <summary>
    /// 整套中性令牌跟着系统明暗走。这里只改共享笔刷的 Color，
    /// 所以所有 StaticResource 引用（包括 VSM 里的选中态）会一起翻面，不需要重建可视树。
    /// </summary>
    private void ApplyColorScheme(bool isDark)
    {
        _isDarkScheme = isDark;
        foreach (var (key, pair) in ThemeScheme.Neutrals)
        {
            if (Microsoft.UI.Xaml.Application.Current.Resources[key] is not SolidColorBrush brush) continue;
            var color = isDark ? pair.Dark : pair.Light;
            brush.Color = color;
            _schemeColors[key] = color;
        }

        ApplyTheme(_settings?.Theme ?? ThemePalette.Graphite.Key);
        ApplySurfaceMode(_materialActive);
        if (_backdropConfiguration is not null)
            _backdropConfiguration.Theme = isDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        if (_themeSettings.HighContrast) SetHighContrastPalette(true);
        UpdateTitleBarColors();
    }

    private void SetHighContrastPalette(bool enabled)
    {
        if (!enabled)
        {
            foreach (var (key, color) in _schemeColors)
                ((SolidColorBrush)Microsoft.UI.Xaml.Application.Current.Resources[key]).Color = color;
            ApplySurfaceMode(_materialActive);
            if (_settings is not null) ApplyTheme(_settings.Theme);
            return;
        }

        var background = _uiSettings.GetColorValue(UIColorType.Background);
        var foreground = _uiSettings.GetColorValue(UIColorType.Foreground);
        foreach (var key in new[]
        {
            "CanvasBrush", "ContentCardBrush", "ContentCardBackdropBrush",
            "PaperBrush", "PaperBackdropBrush", "FloatingSurfaceBrush",
            "SelectionPillBrush", "SelectionPillBackdropBrush",
            "SoftBrush", "SoftBackdropBrush",
            "LogSurfaceBrush", "LogWarningRowBrush", "LogErrorRowBrush",
            "SignalSoftBrush", "SignalWashBrush", "AlertSoftBrush", "WarningSoftBrush", "SuccessSoftBrush"
        })
            ((SolidColorBrush)Microsoft.UI.Xaml.Application.Current.Resources[key]).Color = background;

        foreach (var key in new[]
        {
            "InkBrush", "InkSoftBrush", "MutedBrush", "RuleBrush", "ControlBorderBrush",
            "SelectionBorderBrush", "SignalBrush", "SignalDeepBrush", "AlertBrush",
            "WarningBrush", "SuccessBrush", "AccentFillColorDefaultBrush",
            "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush"
        })
            ((SolidColorBrush)Microsoft.UI.Xaml.Application.Current.Resources[key]).Color = foreground;

        ((SolidColorBrush)Microsoft.UI.Xaml.Application.Current.Resources["SignalInkBrush"]).Color = background;
    }

    private void UiSettings_AdvancedEffectsEnabledChanged(UISettings sender, object args) =>
        DispatchToUi(RefreshWindowBackdrop);

    private void ThemeSettings_Changed(ThemeSettings sender, object args) =>
        DispatchToUi(() =>
        {
            SetHighContrastPalette(sender.HighContrast);
            UpdateTitleBarColors();
            RefreshWindowBackdrop();
            RefreshTrayMenu();
        });

    private void ConfigureTitleBar()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            AppTitleBar.Visibility = Visibility.Collapsed;
            return;
        }

        ExtendsContentIntoTitleBar = true;
        _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SetTitleBar(TitleBarDragArea);
        TitleBarRow.Height = new GridLength(AppTitleBar.Height);
        _customTitleBarEnabled = true;
        UpdateTitleBarInsets();
        UpdateTitleBarColors();
        _appWindow.Changed += AppWindow_Changed;
    }

    private bool _customTitleBarEnabled;

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args) =>
        UpdateTitleBarInsets();

    private void UpdateTitleBarInsets()
    {
        if (!_customTitleBarEnabled) return;

        var scale = RootGrid.XamlRoot?.RasterizationScale ?? DpiScale;
        if (scale <= 0) scale = 1;
        var titleBar = _appWindow.TitleBar;
        TitleBarLeftInsetColumn.Width = new GridLength(Math.Max(0, titleBar.LeftInset / scale));
        TitleBarRightInsetColumn.Width = new GridLength(titleBar.RightInset / scale + 12);
    }

    private void UpdateTitleBarColors()
    {
        if (!_customTitleBarEnabled) return;

        var titleBar = _appWindow.TitleBar;
        titleBar.BackgroundColor = Colors.Transparent;
        titleBar.InactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        if (_themeSettings.HighContrast)
        {
            titleBar.ButtonForegroundColor = null;
            titleBar.ButtonInactiveForegroundColor = null;
            return;
        }

        titleBar.ButtonForegroundColor = _schemeColors["InkBrush"];
        titleBar.ButtonInactiveForegroundColor = _schemeColors["MutedBrush"];
    }

    private void RefreshWindowBackdrop()
    {
        var applied = ApplyWindowBackdrop(_settings.BackdropMaterial);
        _backdropWarning = applied ? null : GetBackdropFallbackReason(_settings.BackdropMaterial);
        if (BackdropSupportText is not null)
            SetBackdropNote(_backdropWarning);
    }

    // 关于材质的说明只在系统不支持所选效果时出现；平时这一行整个收起来，不占版面。
    private void SetBackdropNote(string? note)
    {
        BackdropSupportText.Text = note ?? string.Empty;
        BackdropSupportText.Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RailToggle_Click(object sender, RoutedEventArgs e)
    {
        var next = _settings.RailMode == "icon" ? "wide" : "icon";
        if (!SaveRailMode(next)) return;
        if (!_isLoadingSettings) RailModeBox.SelectedValue = next;
        UpdateRailLayout(RootGrid.ActualWidth);
    }

    private void RailMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || RailModeBox.SelectedValue is not string mode) return;
        if (mode == _settings.RailMode) return;
        if (!SaveRailMode(mode)) return;
        UpdateRailLayout(RootGrid.ActualWidth);
    }

    private bool SaveRailMode(string mode)
    {
        var updated = _settings.Clone();
        updated.RailMode = mode;
        try
        {
            _settingsStore.Save(updated);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _ = ShowMessageAsync("保存侧边栏状态失败", exception.Message, InfoBarSeverity.Warning);
            return false;
        }

        _settings = updated;
        return true;
    }

    /// <summary>两态侧栏：展开显示图标加名称，收起只留一列图标。导航条始终铺满侧栏宽度，四根一样长。</summary>
    private void UpdateRailLayout(double availableWidth)
    {
        if (_settings is null || NavigationRail is null) return;
        var iconOnly = _settings.RailMode == "icon";
        // 图标态留 72：侧栏根节点左右各 12 边距、按钮左右各 10 内边距，
        // 再窄就把 20 的字形挤到贴边，看着像被切掉一圈。
        RailColumn.Width = iconOnly
            ? new GridLength(72)
            : new GridLength(availableWidth is > 0 and < 920 ? 196 : 216);

        var labels = iconOnly ? Visibility.Collapsed : Visibility.Visible;
        RailBrandText.Visibility = labels;
        RailProfileText.Visibility = labels;
        RailProfileChevron.Visibility = labels;
        HomeNavLabel.Visibility = labels;
        ProfilesNavLabel.Visibility = labels;
        EndpointsNavLabel.Visibility = labels;
        LogsNavLabel.Visibility = labels;
        SettingsNavLabel.Visibility = labels;
        RailProfileTextColumn.Width = new GridLength(iconOnly ? 0 : 1, GridUnitType.Star);
        RailBrandRow.HorizontalAlignment = iconOnly
            ? Microsoft.UI.Xaml.HorizontalAlignment.Center
            : Microsoft.UI.Xaml.HorizontalAlignment.Left;
        // StackPanel 的 Spacing 对 Collapsed 子项照样生效，收起时不清零，标志会被推离中线。
        RailBrandRow.Spacing = iconOnly ? 0 : 9;
        // 收起时文字和箭头都藏了，列间距和行内间距必须一起归零，否则图标会被剩下的空隙推偏。
        RailProfileGrid.ColumnSpacing = iconOnly ? 0 : 11;
        RailProfileGrid.HorizontalAlignment = iconOnly
            ? Microsoft.UI.Xaml.HorizontalAlignment.Center
            : Microsoft.UI.Xaml.HorizontalAlignment.Stretch;
        NavigationButtons.Margin = iconOnly ? new Thickness(0, 12, 0, 0) : new Thickness(0, 24, 0, 0);
        ProfileMenuButton.Padding = iconOnly ? new Thickness(8) : new Thickness(9, 8, 9, 8);
        ProfileMenuButton.MinHeight = iconOnly ? 40 : 48;
        ProfileMenuButton.CornerRadius = new CornerRadius(10);
        foreach (var button in new[] { HomeNavButton, ProfilesNavButton, EndpointsNavButton, LogsNavButton, SettingsNavButton })
        {
            button.HorizontalContentAlignment = iconOnly
                ? Microsoft.UI.Xaml.HorizontalAlignment.Center
                : Microsoft.UI.Xaml.HorizontalAlignment.Left;
            // 收起态换成方形扁条，和配置块同宽；展开态回到整行扁条。
            button.MinHeight = iconOnly ? 40 : 36;
            button.CornerRadius = new CornerRadius(iconOnly ? 10 : 6);
            if (button.Content is StackPanel content)
            {
                content.HorizontalAlignment = iconOnly
                    ? Microsoft.UI.Xaml.HorizontalAlignment.Center
                    : Microsoft.UI.Xaml.HorizontalAlignment.Left;
                content.Spacing = iconOnly ? 0 : 11;
            }
        }
    }

    private void ShowHomePage()
    {
        HomeContent.Visibility = Visibility.Visible;
        ProfilesContent.Visibility = Visibility.Collapsed;
        EndpointsContent.Visibility = Visibility.Collapsed;
        TunnelEditorContent.Visibility = Visibility.Collapsed;
        LogsContent.Visibility = Visibility.Collapsed;
        SettingsContent.Visibility = Visibility.Collapsed;
        SetActiveNavigation(HomeNavButton);
    }

    private void ShowProfilesPage()
    {
        HomeContent.Visibility = Visibility.Collapsed;
        ProfilesContent.Visibility = Visibility.Visible;
        EndpointsContent.Visibility = Visibility.Collapsed;
        TunnelEditorContent.Visibility = Visibility.Collapsed;
        LogsContent.Visibility = Visibility.Collapsed;
        SettingsContent.Visibility = Visibility.Collapsed;
        SetActiveNavigation(ProfilesNavButton);
        RefreshProfileRows();
    }

    private void ShowEndpointsPage()
    {
        HomeContent.Visibility = Visibility.Collapsed;
        ProfilesContent.Visibility = Visibility.Collapsed;
        EndpointsContent.Visibility = Visibility.Visible;
        TunnelEditorContent.Visibility = Visibility.Collapsed;
        LogsContent.Visibility = Visibility.Collapsed;
        SettingsContent.Visibility = Visibility.Collapsed;
        SetActiveNavigation(EndpointsNavButton);
        RefreshEndpointProfiles();
    }

    private void ShowTunnelEditorPage()
    {
        HomeContent.Visibility = Visibility.Collapsed;
        ProfilesContent.Visibility = Visibility.Collapsed;
        EndpointsContent.Visibility = Visibility.Collapsed;
        TunnelEditorContent.Visibility = Visibility.Visible;
        LogsContent.Visibility = Visibility.Collapsed;
        SettingsContent.Visibility = Visibility.Collapsed;
        SetActiveNavigation(EndpointsNavButton);
    }

    private void ShowLogsPage()
    {
        HomeContent.Visibility = Visibility.Collapsed;
        ProfilesContent.Visibility = Visibility.Collapsed;
        EndpointsContent.Visibility = Visibility.Collapsed;
        TunnelEditorContent.Visibility = Visibility.Collapsed;
        LogsContent.Visibility = Visibility.Visible;
        SettingsContent.Visibility = Visibility.Collapsed;
        SetActiveNavigation(LogsNavButton);
    }

    private void ShowSettingsPage()
    {
        HomeContent.Visibility = Visibility.Collapsed;
        ProfilesContent.Visibility = Visibility.Collapsed;
        EndpointsContent.Visibility = Visibility.Collapsed;
        TunnelEditorContent.Visibility = Visibility.Collapsed;
        LogsContent.Visibility = Visibility.Collapsed;
        SettingsContent.Visibility = Visibility.Visible;
        SetActiveNavigation(SettingsNavButton);
    }

    private void SetActiveNavigation(XamlButton active)
    {
        HomeNavButton.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources[
            ReferenceEquals(active, HomeNavButton) ? "NavigationActiveButton" : "NavigationButton"];
        ProfilesNavButton.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources[
            ReferenceEquals(active, ProfilesNavButton) ? "NavigationActiveButton" : "NavigationButton"];
        EndpointsNavButton.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources[
            ReferenceEquals(active, EndpointsNavButton) ? "NavigationActiveButton" : "NavigationButton"];
        LogsNavButton.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources[
            ReferenceEquals(active, LogsNavButton) ? "NavigationActiveButton" : "NavigationButton"];
        SettingsNavButton.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources[
            ReferenceEquals(active, SettingsNavButton) ? "NavigationActiveButton" : "NavigationButton"];

        var inactiveIcon = ResourceBrush("InkSoftBrush");
        var activeIcon = ResourceBrush("SignalDeepBrush");
        HomeNavIcon.Foreground = ReferenceEquals(active, HomeNavButton) ? activeIcon : inactiveIcon;
        ProfilesNavIcon.Foreground = ReferenceEquals(active, ProfilesNavButton) ? activeIcon : inactiveIcon;
        EndpointsNavIcon.Foreground = ReferenceEquals(active, EndpointsNavButton) ? activeIcon : inactiveIcon;
        LogsNavIcon.Foreground = ReferenceEquals(active, LogsNavButton) ? activeIcon : inactiveIcon;
        SettingsNavIcon.Foreground = ReferenceEquals(active, SettingsNavButton) ? activeIcon : inactiveIcon;
    }

    private void RefreshEndpointProfiles()
    {
        if (_isRefreshingEndpoints) return;
        var preserveSelection = ReferenceEquals(_selectedEndpointProfile, SelectedProfile);
        _isRefreshingEndpoints = true;
        try
        {
            if (SelectedProfile is null || !Profiles.Contains(SelectedProfile))
                _selectedProfile = Profiles.FirstOrDefault();
            _selectedEndpointProfile = SelectedProfile;
        }
        finally
        {
            _isRefreshingEndpoints = false;
        }

        RefreshEndpointList(preserveSelection);
    }

    private void RefreshEndpointList(bool preserveSelection)
    {
        var selectedIndex = preserveSelection && EndpointList.SelectedItem is TunnelEndpoint selected
            ? selected.Index
            : -1;
        EndpointList.SelectedItem = null;
        Endpoints.Clear();
        var profile = SelectedProfile;
        _selectedEndpointProfile = profile;
        var hasProfile = profile is not null;
        if (profile is null)
        {
            NoProfileDetail.Text = "隧道保存在配置文件中；请先添加或选择一个 FRP 配置。";
            EmptyEndpointsDetail.Text = "请先添加或选择一个 FRP 配置。";
            HomeEmptyTunnelsTitle.Text = "尚无配置";
            HomeEmptyTunnelsDetail.Text = "添加一个 FRP 配置后，隧道会显示在这里。";
        }
        else
        {
            var live = profile.IsActive;
            foreach (var endpoint in _endpointParser.Parse(profile.Config))
            {
                endpoint.Status = live ? profile.StatusOf(endpoint.Name) : TunnelProxyStatus.Unstarted;
                Endpoints.Add(endpoint);
            }

            var isConfigEmpty = string.IsNullOrWhiteSpace(profile.Config);
            NoProfileDetail.Text = "隧道保存在配置文件中；请先添加或选择一个 FRP 配置。";
            EmptyEndpointsTitle.Text = isConfigEmpty ? "当前配置还没有隧道" : "没有可显示的隧道";
            EmptyEndpointsDetail.Text = isConfigEmpty
                ? "可添加首条隧道，或编辑完整 TOML 配置。"
                : "当前 TOML 中没有可识别的 [[proxies]] 段落；新增隧道时会保留现有文本。";
            HomeEmptyTunnelsTitle.Text = isConfigEmpty ? "当前配置还没有隧道" : "没有可显示的隧道";
            HomeEmptyTunnelsDetail.Text = isConfigEmpty
                ? "添加首条隧道后，运行状态会在这里汇总。"
                : "当前 TOML 中没有可识别的 [[proxies]] 段落；原始配置会保留。";
        }

        var hasEndpoints = Endpoints.Count > 0;
        if (selectedIndex >= 0 && selectedIndex < Endpoints.Count)
            EndpointList.SelectedItem = Endpoints[selectedIndex];
        else if (hasEndpoints)
            EndpointList.SelectedItem = Endpoints[0];
        OverviewEndpointList.SelectedItem = EndpointList.SelectedItem;
        EndpointList.Visibility = hasEndpoints ? Visibility.Visible : Visibility.Collapsed;
        EndpointHeaderRow.Visibility = hasEndpoints ? Visibility.Visible : Visibility.Collapsed;
        EmptyEndpointsPanel.Visibility = hasEndpoints ? Visibility.Collapsed : Visibility.Visible;
        EndpointTableBorder.Visibility = hasProfile ? Visibility.Visible : Visibility.Collapsed;
        NoProfilePanel.Visibility = hasProfile ? Visibility.Collapsed : Visibility.Visible;
        OverviewEndpointList.Visibility = hasEndpoints ? Visibility.Visible : Visibility.Collapsed;
        HomeEmptyTunnelsPanel.Visibility = hasEndpoints ? Visibility.Collapsed : Visibility.Visible;
        OverviewTunnelHeader.Visibility = hasEndpoints ? Visibility.Visible : Visibility.Collapsed;
        AddTunnelButton.Visibility = hasProfile ? Visibility.Visible : Visibility.Collapsed;
        EditSelectedTunnelButton.Visibility = hasEndpoints ? Visibility.Visible : Visibility.Collapsed;
        EmptyAddTunnelButton.Visibility = hasProfile ? Visibility.Visible : Visibility.Collapsed;
        HomeEmptyAddProfileButton.Visibility = hasProfile ? Visibility.Collapsed : Visibility.Visible;
        HomeEmptyAddTunnelButton.Visibility = hasProfile ? Visibility.Visible : Visibility.Collapsed;
        _endpointConfigSnapshot = profile?.Config;
        _endpointListInitialized = true;
        UpdateEndpointCounters();
        AddTunnelButton.IsEnabled = profile is not null && !profile.IsBusy;
        UpdateEndpointSelectionState();
    }

    /// <summary>
    /// 后台状态每 3 秒轮询一次。只要配置和所选配置没变，就只改现有行的状态，
    /// 不再 Clear 重建集合——那会让两张表一起闪，还会把选中行重置。
    /// </summary>
    private void SyncEndpointStatuses()
    {
        var profile = SelectedProfile;
        if (!_endpointListInitialized
            || !ReferenceEquals(profile, _selectedEndpointProfile)
            || !string.Equals(profile?.Config, _endpointConfigSnapshot, StringComparison.Ordinal))
        {
            RefreshEndpointProfiles();
            return;
        }

        if (profile is null) return;
        var live = profile.IsActive;
        foreach (var endpoint in Endpoints)
            endpoint.Status = live ? profile.StatusOf(endpoint.Name) : TunnelProxyStatus.Unstarted;
        UpdateEndpointCounters();
        UpdateEndpointSelectionState();
    }

    private void UpdateEndpointCounters()
    {
        var runningCount = Endpoints.Count(endpoint => endpoint.Status == TunnelProxyStatus.Running);
        EndpointBandMeta.Text = $"{Endpoints.Count} 条已配置 · {runningCount} 条运行中";
        TunnelCountText.Text = Endpoints.Count == 0
            ? "暂无隧道"
            : $"{Endpoints.Count} 条隧道 · {runningCount} 条运行中";
    }

    private void EndpointList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SynchronizeEndpointSelection(EndpointList.SelectedItem as TunnelEndpoint);

    private void OverviewEndpointList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SynchronizeEndpointSelection(OverviewEndpointList.SelectedItem as TunnelEndpoint);

    private void SynchronizeEndpointSelection(TunnelEndpoint? selectedEndpoint)
    {
        if (!ReferenceEquals(EndpointList.SelectedItem, selectedEndpoint))
            EndpointList.SelectedItem = selectedEndpoint;
        if (!ReferenceEquals(OverviewEndpointList.SelectedItem, selectedEndpoint))
            OverviewEndpointList.SelectedItem = selectedEndpoint;
        UpdateEndpointSelectionState();
    }

    private void UpdateEndpointSelectionState()
    {
        var hasSelection = EndpointList.SelectedItem is TunnelEndpoint;
        EditSelectedTunnelButton.IsEnabled = hasSelection && SelectedProfile is not null && !SelectedProfile.IsBusy;
        ToolTipService.SetToolTip(
            EditSelectedTunnelButton,
            SelectedProfile?.IsBusy == true
                ? "当前配置正在处理中。"
                : hasSelection ? "编辑所选隧道。" : "选择一个隧道后即可编辑。");
    }

    private void CopyRemoteEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if (sender is XamlButton button && button.Tag is TunnelEndpoint endpoint && endpoint.HasCopyableRemoteAddress)
        {
            if (EndpointList.Items.Contains(endpoint)) EndpointList.SelectedItem = endpoint;
            CopyEndpointText(endpoint.RemoteCopyText, button);
        }
    }

    private void CopyEndpointText(string text, XamlButton? button = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            XamlClipboard.SetContent(package);
            if (button is not null)
            {
                RestoreCopyFeedback();
                _copyFeedbackButton = button;
                _copyFeedbackOriginalContent = button.Content;
                button.Content = "已复制";
                _copyFeedbackTimer.Stop();
                _copyFeedbackTimer.Start();
            }
        }
        catch (Exception exception)
        {
            _ = ShowMessageAsync("复制地址失败", exception.Message, InfoBarSeverity.Warning);
        }
    }

    private void RestoreCopyFeedback()
    {
        _copyFeedbackTimer.Stop();
        if (_copyFeedbackButton is not null)
            _copyFeedbackButton.Content = _copyFeedbackOriginalContent;
        _copyFeedbackButton = null;
        _copyFeedbackOriginalContent = null;
    }

    private async void AddTunnel_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null)
        {
            await AddProfileAsync();
            if (SelectedProfile is null) return;
        }

        BeginTunnelEditor(SelectedProfile, null);
    }

    private async void EditSelectedTunnel_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null || EndpointList.SelectedItem is not TunnelEndpoint endpoint || SelectedProfile.IsBusy)
            return;
        if (!endpoint.SupportsStructuredEditing)
        {
            await EditProfileAsync(SelectedProfile);
            return;
        }

        BeginTunnelEditor(SelectedProfile, endpoint);
    }

    private async void EditEndpointConfig_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null || SelectedProfile.IsBusy) return;
        await EditProfileAsync(SelectedProfile);
    }

    private void BeginTunnelEditor(TunnelProfile profile, TunnelEndpoint? endpoint)
    {
        _editingTunnelProfile = profile;
        _editingEndpoint = endpoint;
        _isAddingTunnel = endpoint is null;
        _isLoadingTunnelEditor = true;
        try
        {
            TunnelEditorTitle.Text = _isAddingTunnel ? "添加隧道" : "编辑隧道";
            TunnelNameBox.Text = endpoint?.Name ?? "";
            TunnelNoteBox.Text = endpoint?.Note ?? "";
            TunnelLocalHostBox.Text = endpoint?.LocalHost ?? "127.0.0.1";
            TunnelLocalPortBox.Text = endpoint?.LocalPortText ?? "";
            TunnelTypeBox.SelectedValue = endpoint?.Type.ToLowerInvariant() ?? "tcp";
            TunnelRemotePortBox.Text = endpoint?.RemotePortText ?? "";
            TunnelDomainsBox.Text = endpoint?.DomainText ?? "";
            TunnelUseSubdomainCheck.IsChecked = endpoint?.UsesSubdomain ?? false;
            TunnelPublicPortBox.Text = endpoint?.PublicPortText
                ?? DefaultPublicPortText(TunnelTypeBox.SelectedValue?.ToString() ?? "tcp");
            TunnelServerAddressBox.Text = _endpointParser.GetServerEndpoint(profile.Config);
            TunnelHttpServerBox.Text = TunnelServerAddressBox.Text;
            UpdateTunnelTypeFields();
        }
        finally
        {
            _isLoadingTunnelEditor = false;
        }

        ShowTunnelEditorPage();
    }

    private void TunnelType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingTunnelEditor) return;
        if (_isAddingTunnel)
        {
            var previousType = e.RemovedItems.OfType<ComboBoxItem>().FirstOrDefault()?.Tag?.ToString();
            var currentText = TunnelPublicPortBox.Text;
            // 只有端口还是上一类型的默认值时才跟着切换；用户手填的端口要保留。
            if (currentText.Length == 0
                || (previousType is not null && currentText == DefaultPublicPortText(previousType)))
            {
                TunnelPublicPortBox.Text = DefaultPublicPortText(
                    TunnelTypeBox.SelectedValue?.ToString() ?? "tcp");
            }
        }
        UpdateTunnelTypeFields();
    }

    // 新增 HTTP 隧道时必须沿用配置里的全局端口，否则保存会把 vhostHTTPPort 改回 80。
    private string DefaultPublicPortText(string type)
    {
        var isHttps = type.Equals("https", StringComparison.OrdinalIgnoreCase);
        var configured = _editingTunnelProfile is null
            ? null
            : _endpointParser.GetGlobalPort(
                _editingTunnelProfile.Config,
                isHttps ? "vhostHTTPSPort" : "vhostHTTPPort");
        return (configured ?? (isHttps ? 443 : 80)).ToString(CultureInfo.InvariantCulture);
    }

    private void UpdateTunnelTypeFields()
    {
        var type = TunnelTypeBox.SelectedValue?.ToString()?.ToLowerInvariant() ?? "tcp";
        var isHttp = type is "http" or "https";
        TcpRemotePanel.Visibility = isHttp ? Visibility.Collapsed : Visibility.Visible;
        HttpRemotePanel.Visibility = isHttp ? Visibility.Visible : Visibility.Collapsed;
        var usesSubdomain = TunnelUseSubdomainCheck.IsChecked == true;
        TunnelDomainsLabel.Text = usesSubdomain ? "服务端子域名" : "自定义域名（每行一个）";
        TunnelDomainsBox.PlaceholderText = usesSubdomain ? "例如：www" : "example.com";
        TunnelEditorHintText.Text = isHttp
            ? "HTTP/HTTPS 公网端口是对应协议的全局端口，修改后会影响同配置中的同类隧道。"
            : "此页展示配置的端点；实际运行状态由 frpc 实例报告。";
    }

    private void TunnelUseSubdomain_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isLoadingTunnelEditor) UpdateTunnelTypeFields();
    }

    private void TunnelEditorCancel_Click(object sender, RoutedEventArgs e)
    {
        _editingTunnelProfile = null;
        _editingEndpoint = null;
        ShowEndpointsPage();
    }

    private async void SaveTunnel_Click(object sender, RoutedEventArgs e)
    {
        var profile = _editingTunnelProfile;
        if (profile is null || profile.IsBusy) return;

        var type = TunnelTypeBox.SelectedValue?.ToString() ?? "tcp";
        if (!int.TryParse(TunnelLocalPortBox.Text, out var localPort))
        {
            await ShowMessageAsync("端口无效", "请填写 1 到 65535 之间的本地端口。", InfoBarSeverity.Warning);
            TunnelLocalPortBox.Focus(FocusState.Programmatic);
            return;
        }

        var isHttp = type is "http" or "https";
        int? remotePort = null;
        int? publicPort = null;
        var usesSubdomain = isHttp && TunnelUseSubdomainCheck.IsChecked == true;
        var domains = TunnelDomainsBox.Text
            .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (isHttp)
        {
            var publicPortText = TunnelPublicPortBox.Text.Trim();
            var defaultPublicPort = type == "http" ? 80 : 443;
            if (publicPortText.Length == 0)
                publicPort = defaultPublicPort;
            else if (int.TryParse(publicPortText, out var parsedPublicPort))
                publicPort = parsedPublicPort;
            else
            {
                await ShowMessageAsync("端口无效", "公网端口必须是 1 到 65535 之间的数字。", InfoBarSeverity.Warning);
                TunnelPublicPortBox.Focus(FocusState.Programmatic);
                return;
            }
            if (usesSubdomain && domains.Length != 1)
            {
                await ShowMessageAsync("子域名无效", "使用子域名时请只填写一个子域名。", InfoBarSeverity.Warning);
                TunnelDomainsBox.Focus(FocusState.Programmatic);
                return;
            }
        }
        else if (!int.TryParse(TunnelRemotePortBox.Text, out var parsedRemotePort))
        {
            await ShowMessageAsync("端口无效", "请填写 1 到 65535 之间的公网端口。", InfoBarSeverity.Warning);
            TunnelRemotePortBox.Focus(FocusState.Programmatic);
            return;
        }
        else
        {
            remotePort = parsedRemotePort;
        }

        var edit = new TunnelEndpointEdit(
            TunnelNameBox.Text.Trim(),
            type,
            TunnelLocalHostBox.Text.Trim(),
            localPort,
            remotePort,
            domains,
            publicPort,
            usesSubdomain,
            TunnelNoteBox.Text.Trim());
        var wasRunning = profile.IsRunning;
        var wasBackgroundConfigured = profile.IsBackgroundConfigured;
        try
        {
            var config = _isAddingTunnel
                ? _endpointParser.AppendProxy(profile.Config, edit)
                : _endpointParser.UpdateProxy(profile.Config, _editingEndpoint!.Index, edit);
            _store.UpdateProfile(profile, profile.Name, config, Profiles);
            profile.ClearDiagnostic();
            _store.SaveDiagnostic(profile);
            profile.AddLog($"隧道“{edit.Name}”配置已保存。");
            var applied = true;
            if (wasRunning || wasBackgroundConfigured)
                applied = await ApplyProfileConfigAsync(profile, wasRunning, wasBackgroundConfigured);

            _editingTunnelProfile = null;
            _editingEndpoint = null;
            RefreshEndpointProfiles();
            UpdateSummary();
            ShowEndpointsPage();
            if (applied) ShowNotice("隧道配置已保存。", InfoBarSeverity.Success);
            else ShowNotice("隧道配置已保存，但运行中的实例未能应用新配置。", InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("保存隧道失败", exception.Message, InfoBarSeverity.Error);
        }
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var limit = LogLimitBox.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Tag?.ToString(), out var selectedLimit)
            ? selectedLimit
            : 400;
        var settings = _settings.Clone();
        settings.CloseToTray = CloseToTrayToggle.IsOn;
        settings.RestoreWindowLayout = RestoreLayoutToggle.IsOn;
        settings.LogLineLimit = limit;
        settings.AutoStart = AutoStartToggle.IsOn;
        settings.BackdropMaterial = BackdropMaterialBox.SelectedValue?.ToString() ?? "Mica";
        settings.RailMode = RailModeBox.SelectedValue?.ToString() ?? "wide";

        try
        {
            _settingsStore.Save(settings);
            _settings = settings;
            var backdropApplied = ApplyWindowBackdrop(_settings.BackdropMaterial);
            _backdropWarning = backdropApplied ? null : GetBackdropFallbackReason(_settings.BackdropMaterial);
            SetBackdropNote(_backdropWarning);
            var startupApplied = StartupRegistration.TryApply(_settings.AutoStart, out var startupError);
            AutoStartToggle.IsOn = StartupRegistration.IsEnabled();
            foreach (var profile in Profiles) profile.SetLogLineLimit(_settings.LogLineLimit);
            RefreshLogText(SelectedProfile);
            SettingsStatusText.Text = !startupApplied
                ? $"设置已保存，但启动项未更新：{startupError}"
                : _backdropWarning is not null
                    ? $"设置已保存；{_backdropWarning}"
                    : "设置已保存";
            SettingsStatusText.Foreground = startupApplied && _backdropWarning is null
                ? ResourceBrush("SuccessBrush")
                : ResourceBrush("WarningBrush");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _ = ShowMessageAsync("保存设置失败", exception.Message, InfoBarSeverity.Error);
        }
    }

    private const string ReleasesUrl = "https://github.com/YM040923/FrpDesk/releases";
    // 仓库里还挂着 fnOS 端的发布，/latest 可能返回非 Windows 版本，所以自己从列表里挑。
    private const string ReleasesApi = "https://api.github.com/repos/YM040923/FrpDesk/releases?per_page=30";
    private const string WindowsTagPrefix = "windows-v";

    private static readonly HttpClient ReleaseClient = new() { Timeout = TimeSpan.FromSeconds(8) };

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        AboutUpdateText.Visibility = Visibility.Visible;
        AboutUpdateText.Text = "正在检查…";
        CheckUpdateButton.IsEnabled = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
            request.Headers.UserAgent.ParseAdd("FrpDesk");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await ReleaseClient.SendAsync(request);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            {
                AboutUpdateText.Text = "自动检查暂不可用，请点“发布页”手动查看。";
                return;
            }

            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
            var latest = FindLatestWindowsRelease(document.RootElement);
            var current = Version.TryParse(AppInfo.Version, out var parsedCurrent) ? parsedCurrent : null;
            AboutUpdateText.Text = latest is { } release && current is not null && release.Version > current
                ? $"发现新版本 {release.Tag}，请到发布页下载。"
                : $"已是最新版本（{AppInfo.Version}）。";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            AboutUpdateText.Text = "检查失败，请稍后重试，或直接打开发布页查看。";
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private static (string Tag, Version Version)? FindLatestWindowsRelease(JsonElement releases)
    {
        if (releases.ValueKind != JsonValueKind.Array) return null;
        (string Tag, Version Version)? latest = null;
        foreach (var release in releases.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;
            if (!release.TryGetProperty("tag_name", out var tagElement)) continue;
            var tag = tagElement.GetString();
            if (tag is null || !tag.StartsWith(WindowsTagPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Version.TryParse(tag[WindowsTagPrefix.Length..], out var version)) continue;
            if (latest is null || version > latest.Value.Version) latest = (tag, version);
        }

        return latest;
    }

    private void OpenReleases_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ReleasesUrl) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _ = ShowMessageAsync("打开发布页失败", exception.Message, InfoBarSeverity.Warning);
        }
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_settingsStore.RootDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settingsStore.RootDirectory}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            _ = ShowMessageAsync("打开目录失败", exception.Message, InfoBarSeverity.Warning);
        }
    }

    private void ResetWindowLayout_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(_windowLayoutPath)) File.Delete(_windowLayoutPath);
            _skipNextLayoutSave = true;
            if (_appWindow.Presenter is OverlappedPresenter presenter
                && presenter.State != OverlappedPresenterState.Restored)
            {
                presenter.Restore();
            }

            _appWindow.Resize(DipSize(980, 700));
            CenterWindow();
            SettingsStatusText.Text = "窗口布局已恢复默认";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _ = ShowMessageAsync("恢复失败", exception.Message, InfoBarSeverity.Warning);
        }
    }

    private async void BackgroundServiceButton_Click(object sender, RoutedEventArgs e)
    {
        var profile = SelectedProfile;
        if (profile is null || profile.IsBusy) return;
        // 提权操作可能停在 UAC 或服务安装上几十秒，先锁住按钮避免重复触发。
        profile.IsBusy = true;
        UpdateSummary();
        try
        {
            if (profile.IsBackgroundRunning
                && !profile.BackgroundStatus.StartsWith("旧版后台任务", StringComparison.Ordinal))
            {
                await _backgroundTaskManager.StopAsync(profile);
            }
            else if (profile.IsRunning)
            {
                await ShowMessageAsync("实例正在运行",
                    "请先停止当前运行中的实例，再切换到后台运行。",
                    InfoBarSeverity.Informational);
            }
            else
            {
                await _backgroundTaskManager.StartAsync(profile);
            }
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("后台运行失败", exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            profile.IsBusy = false;
        }

        UpdateSummary();
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        var compact = width < 720;
        var narrow = width < 920;
        UpdateRailLayout(width);
        var inset = compact ? 18 : narrow ? 22 : 26;
        var topInset = compact ? 18 : 22;
        HomeLayout.Margin = new Thickness(inset, topInset, inset, 20);
        ProfilesContent.Margin = new Thickness(inset, topInset, inset, 20);
        EndpointsContent.Margin = new Thickness(inset, topInset, inset, 20);
        LogsContent.Margin = new Thickness(inset, topInset, inset, 20);
        // 编辑页与设置页的内边距必须留在滚动区里面，否则滚动条会跟着缩进，离开卡片边缘。
        TunnelEditorHeaderGrid.Margin = new Thickness(inset, topInset, inset, 0);
        TunnelEditorForm.Margin = new Thickness(inset, 0, inset, 20);
        SettingsHeaderGrid.Margin = new Thickness(inset, topInset, inset, 18);
        SettingsLayout.Margin = new Thickness(inset, 0, inset, 20);

        ServiceSummaryGrid.ColumnDefinitions[0].Width = new GridLength(compact ? 1 : 0.9, GridUnitType.Star);
        ServiceSummaryGrid.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 1.1, GridUnitType.Star);
        Grid.SetColumn(ServiceMetaContent, compact ? 0 : 1);
        Grid.SetRow(ServiceMetaContent, compact ? 1 : 0);
        ServiceMetaContent.Margin = compact ? new Thickness(0, 16, 0, 0) : new Thickness(0);
        ServiceSummaryGrid.ColumnSpacing = compact ? 0 : 28;

        var stackActions = width < 760;
        Grid.SetRow(ProfilesHeaderActions, stackActions ? 1 : 0);
        ProfilesHeaderActions.HorizontalAlignment = stackActions
            ? Microsoft.UI.Xaml.HorizontalAlignment.Left
            : Microsoft.UI.Xaml.HorizontalAlignment.Right;
        ProfilesHeaderActions.Margin = stackActions ? new Thickness(0, 10, 0, 0) : new Thickness(0);
        Grid.SetRow(ProfilesBandMeta, stackActions ? 2 : 1);
        Grid.SetRow(EndpointHeaderActions, stackActions ? 1 : 0);
        EndpointHeaderActions.HorizontalAlignment = stackActions
            ? Microsoft.UI.Xaml.HorizontalAlignment.Left
            : Microsoft.UI.Xaml.HorizontalAlignment.Right;
        EndpointHeaderActions.Margin = stackActions ? new Thickness(0, 10, 0, 0) : new Thickness(0);
        Grid.SetRow(EditorHeaderActions, stackActions ? 1 : 0);
        EditorHeaderActions.HorizontalAlignment = stackActions
            ? Microsoft.UI.Xaml.HorizontalAlignment.Left
            : Microsoft.UI.Xaml.HorizontalAlignment.Right;
        EditorHeaderActions.Margin = stackActions ? new Thickness(0, 10, 0, 0) : new Thickness(0);
    }

    private void NoticeBar_CloseButtonClick(InfoBar sender, object args)
    {
        _noticeTimer.Stop();
        NoticeBar.IsOpen = false;
    }

    private MenuFlyout CreateLogMenu()
    {
        var menu = new MenuFlyout();
        var copy = new MenuFlyoutItem { Text = "复制所选行" };
        var copyAll = new MenuFlyoutItem { Text = "复制全部" };
        var selectAll = new MenuFlyoutItem { Text = "全选" };
        copy.Click += (_, _) => CopyLogText(string.Join(Environment.NewLine,
            LogEntries.Where(entry => LogLinesList.SelectedItems.Contains(entry)).Select(entry => entry.RawText)));
        copyAll.Click += (_, _) => CopyLogText(_logText);
        selectAll.Click += (_, _) => LogLinesList.SelectAll();
        menu.Items.Add(copy);
        menu.Items.Add(copyAll);
        menu.Items.Add(selectAll);
        menu.Opening += (_, _) =>
        {
            copy.IsEnabled = LogLinesList.SelectedItems.Count > 0;
            copyAll.IsEnabled = _logText.Length > 0;
            selectAll.IsEnabled = LogEntries.Count > 0;
        };
        return menu;
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        CopyLogText(_logText);
    }

    private void CopyDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null || !SelectedProfile.HasDiagnostic) return;
        CopyLogText(SelectedProfile.DiagnosticCopyText);
    }

    private void CopyLogText(string text)
    {
        if (string.IsNullOrEmpty(text)) text = _logText;
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            XamlClipboard.SetContent(package);
            ShowNotice("内容已复制。", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            _ = ShowMessageAsync("复制日志失败", exception.Message, InfoBarSeverity.Warning);
        }
    }

    private string ResolveFrpcPath()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "assets", "frpc.exe");
        return File.Exists(bundled)
            ? bundled
            : Path.Combine(AppContext.BaseDirectory, "frpc.exe");
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (_isClosing)
        {
            // 首次关闭已经在异步停止实例，本次再关会绕过停止流程。
            e.Cancel = true;
            return;
        }
        SaveWindowLayout();

        if (!_allowExit && _settings.CloseToTray)
        {
            e.Cancel = true;
            ShowWindow(_hwnd, SwHide);
            return;
        }

        var running = Profiles.Where(profile => profile.IsRunning).ToArray();
        if (running.Length == 0) return;

        e.Cancel = true;
        _isClosing = true;
        try
        {
            await _processManager.StopAllAsync(running);
            _isClosing = false;
            _allowExit = true;
            Close();
        }
        catch (Exception exception)
        {
            _isClosing = false;
            await ShowMessageAsync("停止实例失败", exception.Message, InfoBarSeverity.Error);
        }
    }

    private bool _isRefreshingBackground;

    private async Task LoadBackgroundStateAsync()
    {
        if (_isRefreshingBackground) return;
        _isRefreshingBackground = true;
        try
        {
            await _backgroundTaskManager.LoadStateAsync(Profiles);
            UpdateSummary();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("读取后台实例状态失败", exception.Message, InfoBarSeverity.Warning);
        }
        finally
        {
            _isRefreshingBackground = false;
        }
    }

    private async Task RefreshBackgroundStateAsync()
    {
        if (_isRefreshingBackground) return;
        _isRefreshingBackground = true;
        try
        {
            await _backgroundTaskManager.RefreshStateAsync(Profiles);
            UpdateSummary();
        }
        catch
        {
            // A transient service query failure must not interrupt the UI timer.
        }
        finally
        {
            _isRefreshingBackground = false;
        }
    }

    private void SystemEvents_SessionEnding(object? sender, SessionEndingEventArgs e)
    {
        _allowExit = true;
    }

    private async Task<bool> StartProfileAsync(TunnelProfile profile, bool showError)
    {
        try
        {
            _store.SaveConfig(profile);
            await _processManager.StartAsync(profile, _store.ConfigPath(profile));
            return true;
        }
        catch (Exception exception)
        {
            if (showError)
                await ShowMessageAsync("启动失败", exception.Message, InfoBarSeverity.Error);
            return false;
        }
        finally
        {
            UpdateSummary();
        }
    }

    private void AddProfileToList(TunnelProfile profile)
    {
        profile.SetLogLineLimit(_settings.LogLineLimit);
        Profiles.Add(profile);
        profile.PropertyChanged += Profile_PropertyChanged;
        profile.Logs.CollectionChanged += Logs_CollectionChanged;
    }

    private void Profile_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TunnelProfile.State)
            or nameof(TunnelProfile.Name)
            or nameof(TunnelProfile.Config)
            or nameof(TunnelProfile.IsBusy)
            or nameof(TunnelProfile.IsBackgroundRunning)
            or nameof(TunnelProfile.IsBackgroundConfigured)
            or nameof(TunnelProfile.BackgroundStatus)
            or nameof(TunnelProfile.DiagnosticSummary)
            or nameof(TunnelProfile.DiagnosticDetail)
            or nameof(TunnelProfile.DiagnosticSeverity)
            or nameof(TunnelProfile.LastExitCode)
            or nameof(TunnelProfile.LastFailureAt)
            or nameof(TunnelProfile.ProxyStatusVersion))
        {
            UpdateSummary();
        }
    }

    private void Logs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (SelectedProfile is null
            || sender is not ObservableCollection<string> logs
            || !ReferenceEquals(logs, SelectedProfile.Logs)) return;

        if (ReferenceEquals(_logTextProfile, SelectedProfile))
            ApplyLogCollectionChange(SelectedProfile, e);
        else
            RefreshLogText(SelectedProfile);

        UpdateRecentActivity(SelectedProfile);
    }

    private void ApplyLogCollectionChange(TunnelProfile profile, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null && e.NewStartingIndex >= 0:
                for (var offset = 0; offset < e.NewItems.Count; offset++)
                {
                    if (e.NewItems[offset] is not string line) continue;
                    LogEntries.Insert(Math.Min(e.NewStartingIndex + offset, LogEntries.Count), ParseLogEntry(line));
                }
                break;
            case NotifyCollectionChangedAction.Remove when e.OldStartingIndex >= 0 && e.OldItems is not null:
                for (var offset = 0; offset < e.OldItems.Count; offset++)
                {
                    if (e.OldStartingIndex < LogEntries.Count) LogEntries.RemoveAt(e.OldStartingIndex);
                }
                break;
            default:
                RefreshLogText(profile, force: true);
                return;
        }

        _logText = string.Join(Environment.NewLine, profile.Logs);
        UpdateLogFooter();
        ScrollLogsToEnd();
    }

    private void RefreshLogText(TunnelProfile? profile, bool force = false)
    {
        if (LogLinesList is null) return;
        if (!force && ReferenceEquals(profile, _logTextProfile)) return;
        _logTextProfile = profile;
        _logText = profile is null ? "" : string.Join(Environment.NewLine, profile.Logs);
        LogEntries.Clear();
        if (profile is not null)
        {
            foreach (var line in profile.Logs)
                LogEntries.Add(ParseLogEntry(line));
        }

        UpdateLogFooter();
        ScrollLogsToEnd();
    }

    private void UpdateLogFooter()
    {
        if (LogLinesList is null) return;
        LogEmptyText.Visibility = LogEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CopyLogButton.IsEnabled = LogEntries.Count > 0;
    }

    private void ScrollLogsToEnd()
    {
        if (LogEntries.Count == 0 || LogLinesList is null) return;
        var last = LogEntries[^1];
        LogLinesList.DispatcherQueue.TryEnqueue(() => LogLinesList.ScrollIntoView(last));
    }

    private void UpdateSummary()
    {
        if (SelectedProfile is null || !Profiles.Contains(SelectedProfile))
            SelectedProfile = Profiles.FirstOrDefault();
        RefreshProfileRows();
        SyncEndpointStatuses();
        UpdateSelectedPanel();
        UpdateRecentActivity(SelectedProfile);
    }

    /// <summary>配置表按 Id 复用行，只更新变化的字段；每 3 秒轮询不会重建列表。</summary>
    private void RefreshProfileRows()
    {
        if (ProfileList is null) return;
        for (var index = ProfileRows.Count - 1; index >= 0; index--)
        {
            if (!Profiles.Contains(ProfileRows[index].Profile))
                ProfileRows.RemoveAt(index);
        }

        for (var index = 0; index < Profiles.Count; index++)
        {
            var profile = Profiles[index];
            var row = ProfileRows.FirstOrDefault(item => ReferenceEquals(item.Profile, profile));
            if (row is null)
            {
                row = new ProfileRow(profile, ResourceBrush("MutedBrush"));
                ProfileRows.Insert(Math.Min(index, ProfileRows.Count), row);
            }

            row.Refresh(_endpointParser, ResourceBrush);
            var currentIndex = ProfileRows.IndexOf(row);
            if (currentIndex != index) ProfileRows.Move(currentIndex, index);
        }

        var running = Profiles.Count(profile => profile.IsRunning || profile.IsBackgroundRunning);
        ProfilesBandMeta.Text = Profiles.Count == 0
            ? "0 个配置"
            : $"{Profiles.Count} 个配置 · {running} 个运行中";
        var hasProfiles = Profiles.Count > 0;
        ProfileList.Visibility = hasProfiles ? Visibility.Visible : Visibility.Collapsed;
        ProfilesHeaderRow.Visibility = hasProfiles ? Visibility.Visible : Visibility.Collapsed;
        ProfilesEmptyPanel.Visibility = hasProfiles ? Visibility.Collapsed : Visibility.Visible;
        ProfilesStartAllButton.IsEnabled = Profiles.Any(profile =>
            !profile.IsBackgroundConfigured && !profile.IsRunning && !profile.IsBusy);
        ProfilesStopAllButton.IsEnabled = Profiles.Any(profile => profile.IsRunning || profile.IsBackgroundRunning);
    }

    private void UpdateBackgroundServicePanel()
    {
        if (BackgroundServiceButton is null) return;
        var profile = SelectedProfile;
        if (profile is null)
        {
            BackgroundServiceButton.Content = "启用后台服务";
            BackgroundServiceButton.IsEnabled = false;
            ToolTipService.SetToolTip(BackgroundServiceButton, "请先添加或选择一个配置。");
            return;
        }

        var isLegacy = profile.BackgroundStatus.StartsWith("旧版后台任务", StringComparison.Ordinal);
        BackgroundServiceButton.Content = isLegacy
            ? "切换为后台服务"
            : profile.IsBackgroundConfigured
                ? profile.IsBackgroundRunning ? "停止后台服务" : "重新启动后台服务"
                : "启用后台服务";
        BackgroundServiceButton.IsEnabled = !profile.IsBusy && !profile.IsRunning;
        ToolTipService.SetToolTip(BackgroundServiceButton,
            profile.IsRunning ? "请先停止当前运行中的实例。" : $"只影响「{profile.Name}」这一个配置。");
    }

    private void UpdateSelectedPanel()
    {
        var profile = SelectedProfile;
        SelectedStateText.Text = profile?.DisplayState ?? "未选择配置";
        var serverEndpoint = profile is null ? "" : _endpointParser.GetServerEndpoint(profile.Config);
        ServerEndpointText.Text = string.IsNullOrWhiteSpace(serverEndpoint) ? "未配置" : serverEndpoint;
        ToolTipService.SetToolTip(ServerEndpointText, ServerEndpointText.Text);
        var state = SelectedStateText.Text;
        var isFailure = state.Contains("失败", StringComparison.Ordinal)
            || state.Contains("错误", StringComparison.Ordinal)
            || state.Contains("耗尽", StringComparison.Ordinal);
        var isWorking = profile?.IsBusy == true || state.Contains("中", StringComparison.Ordinal);
        var stateBrush = profile?.IsActive == true
            ? ResourceBrush("SuccessBrush")
            : isFailure ? ResourceBrush("AlertBrush")
            : isWorking ? ResourceBrush("SignalDeepBrush")
            : ResourceBrush("MutedBrush");
        SelectedStateText.Foreground = stateBrush;
        ServiceStateDot.Fill = stateBrush;
        ServiceStateGlyph.Stroke = stateBrush;

        var isRunning = profile?.IsRunning == true;
        RunButton.Content = isRunning
            ? "停止服务"
            : profile?.IsBackgroundConfigured == true
                ? profile.IsBackgroundRunning ? "后台运行中" : "后台服务未运行"
                : "运行服务";
        RunButton.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources[
            isRunning ? "QuietButton" : "SignalButton"];
        RunButton.IsEnabled = profile is not null && !profile.IsBusy && !profile.IsBackgroundConfigured;
        var isLogLive = profile?.IsActive == true;
        var isFailureState = isFailure || profile?.DiagnosticSeverity == DiagnosticSeverity.Error;
        var logStatusBrush = isLogLive ? ResourceBrush("SuccessBrush")
            : isFailureState ? ResourceBrush("AlertBrush")
            : isWorking ? ResourceBrush("SignalDeepBrush")
            : ResourceBrush("MutedBrush");
        LogLiveDot.Fill = logStatusBrush;
        LogLiveStatus.Foreground = logStatusBrush;
        LogLiveStatus.Text = profile is null
            ? "未选择配置"
            : isLogLive ? "实时接收"
            : isWorking ? "服务处理中"
            : isFailureState ? "服务启动失败"
            : "服务未运行";
        LogEmptyText.Text = profile is null
            ? "选择或添加配置后，运行日志会显示在这里。"
            : isLogLive ? "正在等待 frpc 输出…"
            : isWorking ? "服务正在处理，frpc 输出将在启动后显示。"
            : isFailureState ? "服务启动失败。请查看概览中的诊断信息，或重试启动。"
            : "启动服务后，frpc 输出会显示在这里。";
        UpdateDiagnosticPanel(profile);
        RefreshLogText(profile);
        UpdateEndpointSelectionState();
        UpdateBackgroundServicePanel();
    }

    private void UpdateRecentActivity(TunnelProfile? profile)
    {
        var signature = profile is null
            ? string.Empty
            : $"{profile.Logs.Count}\u001F{string.Join('\u001F', profile.Logs.Reverse().Take(4))}";
        if (ReferenceEquals(profile, _recentActivityProfile) && signature == _recentActivitySignature) return;
        _recentActivityProfile = profile;
        _recentActivitySignature = signature;
        RecentActivityRows.Clear();
        if (profile is not null)
        {
            foreach (var line in profile.Logs.Reverse().Take(4))
            {
                if (!LogText.TryParse(line, out var date, out var time, out var level, out var message))
                    continue;
                RecentActivityRows.Add(new ActivityEntry(
                    message,
                    LogText.DescribeMoment(date, time),
                    LevelGlyph(level),
                    ResourceBrush(LevelTextBrush(level))));
            }
        }

        RecentActivityEmptyText.Visibility = RecentActivityRows.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        RecentActivityEmptyText.Text = profile is null
            ? "选择或添加配置后，这里会显示近期输出。"
            : "当前暂无 frpc 输出。";
    }

    private void UpdateDiagnosticPanel(TunnelProfile? profile)
    {
        var diagnostic = profile?.HasDiagnostic == true ? profile : null;
        DiagnosticPanel.Visibility = diagnostic is null ? Visibility.Collapsed : Visibility.Visible;
        if (diagnostic is null) return;

        DiagnosticTitleText.Text = diagnostic.DiagnosticSummary;
        DiagnosticDetailText.Text = diagnostic.DiagnosticDetail;
        var isWarning = diagnostic.DiagnosticSeverity == DiagnosticSeverity.Warning;
        var isInfo = diagnostic.DiagnosticSeverity == DiagnosticSeverity.Info;
        DiagnosticPanel.Background = isInfo
            ? ResourceBrush("SuccessSoftBrush")
            : isWarning ? ResourceBrush("WarningSoftBrush") : ResourceBrush("AlertSoftBrush");
        DiagnosticPanel.BorderBrush = isInfo
            ? ResourceBrush("SuccessBrush")
            : isWarning ? ResourceBrush("WarningBrush") : ResourceBrush("AlertBrush");
        DiagnosticTitleText.Foreground = isInfo
            ? ResourceBrush("SuccessBrush")
            : isWarning ? ResourceBrush("WarningBrush") : ResourceBrush("AlertBrush");
        DiagnosticDetailText.Foreground = ResourceBrush("InkBrush");
    }

    private void RefreshLogText(TunnelProfile? profile) => RefreshLogText(profile, force: true);

    private LogEntry ParseLogEntry(string rawText)
    {
        if (!LogText.TryParse(rawText, out var date, out var time, out var level, out var message))
        {
            rawText = LogText.Normalize(rawText);
            LogText.TryParse(rawText, out date, out time, out level, out message);
        }

        return new LogEntry(
            rawText,
            LogText.DescribeMoment(date, time),
            level,
            message,
            ResourceBrush(LevelTextBrush(level)),
            ResourceBrush(LevelRowBrush(level)));
    }

    private static string LevelGlyph(string level) => level switch
    {
        "ERROR" => Glyph(0xE711),
        "WARN" => Glyph(0xE7BA),
        "SUCCESS" => Glyph(0xE73E),
        "DEBUG" => Glyph(0xE8F1),
        _ => Glyph(0xE946)
    };

    private static string LevelTextBrush(string level) => level switch
    {
        "ERROR" => "AlertBrush",
        "WARN" => "WarningBrush",
        "SUCCESS" => "SuccessBrush",
        "DEBUG" => "MutedBrush",
        _ => "InkSoftBrush"
    };

    private static string LevelRowBrush(string level) => level switch
    {
        "ERROR" => "LogErrorRowBrush",
        "WARN" => "LogWarningRowBrush",
        _ => "LogSurfaceBrush"
    };

    private static string Glyph(int codePoint) => char.ConvertFromUtf32(codePoint);

    private static SolidColorBrush ResourceBrush(string key) =>
        (SolidColorBrush)Microsoft.UI.Xaml.Application.Current.Resources[key];

    private void ShowNotice(string message, InfoBarSeverity severity, TimeSpan? autoCloseAfter = null)
    {
        NoticeBar.Message = message;
        NoticeBar.Severity = severity;
        NoticeBar.IsOpen = true;
        _noticeTimer.Stop();
        if (severity is not InfoBarSeverity.Error)
        {
            _noticeTimer.Interval = autoCloseAfter ?? TimeSpan.FromSeconds(7);
            _noticeTimer.Start();
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer
            {
                MaxHeight = 220,
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap
                }
            },
            PrimaryButtonText = confirmText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowMessageAsync(string title, string message, InfoBarSeverity severity)
    {
        ShowNotice(message, severity);
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer
            {
                MaxHeight = 260,
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap
                }
            },
            CloseButtonText = "知道了",
            XamlRoot = RootGrid.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private void CreateTrayIcon()
    {
        _trayIcon = LoadTrayIcon();
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "FrpDesk",
            Icon = _trayIcon ?? Drawing.SystemIcons.Application,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => _queue.TryEnqueue(ShowFromTray);
        _notifyIcon.MouseUp += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Right) _queue.TryEnqueue(ShowTrayMenu);
        };
    }

    private void EnsureTrayMenu()
    {
        if (_trayMenu is not null) return;
        var menu = new TrayMenuWindow();
        menu.OpenRequested += (_, _) => _queue.TryEnqueue(ShowFromTray);
        menu.StopAllRequested += (_, _) => _queue.TryEnqueue(() => _ = StopAllFromTrayAsync());
        menu.ExitRequested += (_, _) => _queue.TryEnqueue(ExitFromTray);
        ApplyTrayPalette(menu);
        _trayMenu = menu;
    }

    private void ShowTrayMenu()
    {
        EnsureTrayMenu();
        if (_trayMenu is null) return;
        ApplyTrayPalette(_trayMenu);
        _trayMenu.ShowAtCursor(Profiles.Any(profile => profile.IsRunning || profile.IsBackgroundRunning));
    }

    private void ApplyTrayPalette(TrayMenuWindow menu)
    {
        if (_themeSettings.HighContrast)
        {
            var background = _uiSettings.GetColorValue(UIColorType.Background);
            var foreground = _uiSettings.GetColorValue(UIColorType.Foreground);
            menu.ApplyPalette(
                background,
                ToUiColor(Drawing.SystemColors.Highlight),
                ToUiColor(Drawing.SystemColors.Highlight),
                foreground,
                ToUiColor(Drawing.SystemColors.HighlightText),
                ToUiColor(Drawing.SystemColors.GrayText),
                foreground,
                _isDarkScheme);
            return;
        }

        // 托盘窗口不透明，直接用方案里的实心色，避免材质模式把半透明卡面叠到桌面上。
        menu.ApplyPalette(
            SchemeColor("ContentCardBrush"),
            SchemeColor("SoftBrush"),
            SchemeColor("TableSelectionBrush"),
            SchemeColor("InkBrush"),
            SchemeColor("InkBrush"),
            SchemeColor("MutedBrush"),
            SchemeColor("RuleBrush"),
            _isDarkScheme);
    }

    private Windows.UI.Color SchemeColor(string key)
    {
        var pair = ThemeScheme.Neutrals[key];
        return _isDarkScheme ? pair.Dark : pair.Light;
    }

    private static Windows.UI.Color ToUiColor(Drawing.Color color) =>
        Windows.UI.Color.FromArgb(255, color.R, color.G, color.B);

    private void RefreshTrayMenu()
    {
        if (_trayMenu is not null) ApplyTrayPalette(_trayMenu);
    }

    private static Drawing.Icon? LoadTrayIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "assets", "FrpDesk.ico");
            if (!File.Exists(path)) return null;
            using var source = new Drawing.Icon(path);
            return (Drawing.Icon)source.Clone();
        }
        catch
        {
            return null;
        }
    }

    private void SetWindowIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "assets", "FrpDesk.ico");
            if (File.Exists(path)) _appWindow.SetIcon(path);
        }
        catch
        {
            // The executable still carries ApplicationIcon as a fallback.
        }
    }

    private async Task StopAllFromTrayAsync()
    {
        var errors = await StopEverythingAsync();
        UpdateSummary();
        if (errors.Count > 0)
        {
            // 窗口可能收在托盘里，先恢复窗口，错误提示才看得见。
            ShowFromTray();
            await ShowMessageAsync("停止实例失败", string.Join("\n", errors), InfoBarSeverity.Error);
            return;
        }

        ShowNotice("所有实例已停止。", InfoBarSeverity.Success);
    }

    private void ExitFromTray()
    {
        _allowExit = true;
        Close();
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        SystemEvents.SessionEnding -= SystemEvents_SessionEnding;
        _appWindow.Closing -= AppWindow_Closing;
        if (_customTitleBarEnabled) _appWindow.Changed -= AppWindow_Changed;
        Activated -= MainWindow_Activated;
        _uiSettings.AdvancedEffectsEnabledChanged -= UiSettings_AdvancedEffectsEnabledChanged;
        _themeSettings.Changed -= ThemeSettings_Changed;
        _notifyIcon?.Dispose();
        _notifyIcon = null;
        _trayMenu?.Close();
        _trayMenu = null;
        _trayIcon?.Dispose();
        _trayIcon = null;
        _backgroundStateTimer.Stop();
        _noticeTimer.Stop();
        RestoreCopyFeedback();
        _acrylicController?.Dispose();
        _acrylicController = null;
        _backdropConfiguration = null;
    }

    private void SetDefaultWindowBounds()
    {
        _appWindow.Resize(DipSize(980, 700));
        CenterWindow();
    }

    private void RestoreWindowLayout()
    {
        if (!_settings.RestoreWindowLayout || !File.Exists(_windowLayoutPath)) return;
        try
        {
            var layout = JsonSerializer.Deserialize<WindowLayout>(File.ReadAllText(_windowLayoutPath));
            if (layout is null || layout.Width < 640 || layout.Height < 540) return;

            var scale = DpiScale;
            var width = Math.Max(640, (int)Math.Round(layout.Width * scale));
            var height = Math.Max(540, (int)Math.Round(layout.Height * scale));
            var bounds = new Drawing.Rectangle(
                (int)Math.Round(layout.Left * scale),
                (int)Math.Round(layout.Top * scale),
                width,
                height);
            if (!IsVisibleOnScreen(bounds))
            {
                var work = Forms.Screen.PrimaryScreen?.WorkingArea
                    ?? new Drawing.Rectangle(0, 0, 1920, 1080);
                bounds.X = work.Left + Math.Max(0, (work.Width - bounds.Width) / 2);
                bounds.Y = work.Top + Math.Max(0, (work.Height - bounds.Height) / 2);
            }

            _appWindow.Resize(new SizeInt32(bounds.Width, bounds.Height));
            _appWindow.Move(new PointInt32(bounds.Left, bounds.Top));
            if (layout.IsMaximized && _appWindow.Presenter is OverlappedPresenter presenter)
                presenter.Maximize();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Window layout is optional and must never prevent startup.
        }
    }

    private static bool IsVisibleOnScreen(Drawing.Rectangle bounds) =>
        Forms.Screen.AllScreens.Any(screen =>
            Drawing.Rectangle.Intersect(bounds, screen.WorkingArea) is var intersection
            && intersection.Width > 64
            && intersection.Height > 32);

    private void SaveWindowLayout()
    {
        if (!_settings.RestoreWindowLayout) return;
        if (_skipNextLayoutSave)
        {
            _skipNextLayoutSave = false;
            try
            {
                if (File.Exists(_windowLayoutPath)) File.Delete(_windowLayoutPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            return;
        }

        try
        {
            var bounds = _appWindow.Position;
            var size = _appWindow.Size;
            if (size.Width < 640 || size.Height < 540) return;
            var scale = DpiScale;
            var layout = new WindowLayout(
                bounds.X / scale,
                bounds.Y / scale,
                size.Width / scale,
                size.Height / scale,
                _appWindow.Presenter is OverlappedPresenter presenter
                    && presenter.State == OverlappedPresenterState.Maximized);
            Directory.CreateDirectory(Path.GetDirectoryName(_windowLayoutPath)!);
            File.WriteAllText(_windowLayoutPath, JsonSerializer.Serialize(layout));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Window layout is convenience state and must not block closing.
        }
    }

    private void CenterWindow()
    {
        var work = Forms.Screen.PrimaryScreen?.WorkingArea
            ?? new Drawing.Rectangle(0, 0, 1920, 1080);
        var size = _appWindow.Size;
        _appWindow.Move(new PointInt32(
            work.Left + Math.Max(0, (work.Width - size.Width) / 2),
            work.Top + Math.Max(0, (work.Height - size.Height) / 2)));
    }

    private SizeInt32 DipSize(double width, double height)
    {
        var scale = DpiScale;
        return new SizeInt32((int)Math.Round(width * scale), (int)Math.Round(height * scale));
    }

    private double DpiScale
    {
        get
        {
            try
            {
                var dpi = GetDpiForWindow(_hwnd);
                return dpi > 0 ? dpi / 96d : 1d;
            }
            catch { return 1d; }
        }
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed record WindowLayout(double Left, double Top, double Width, double Height, bool IsMaximized);
}

internal static class MainWindowNativeMethods
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);
}

file static class MainWindowNativeAliases
{
    public static void ShowWindow(IntPtr hwnd, int command) => MainWindowNativeMethods.ShowWindow(hwnd, command);
    public static uint GetDpiForWindow(IntPtr hwnd) => MainWindowNativeMethods.GetDpiForWindow(hwnd);
}

partial class MainWindow
{
    private static void ShowWindow(IntPtr hwnd, int command) => MainWindowNativeAliases.ShowWindow(hwnd, command);
    private static uint GetDpiForWindow(IntPtr hwnd) => MainWindowNativeAliases.GetDpiForWindow(hwnd);
}
