using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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

public sealed class ActivityEntry(string message, string time)
{
    public string Message { get; set; } = message;
    public string Time { get; set; } = time;
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
    private readonly Dictionary<string, Windows.UI.Color> _defaultBrushColors = new(StringComparer.Ordinal);
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
    private string _logText = "";
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private XamlButton? _copyFeedbackButton;
    private object? _copyFeedbackOriginalContent;
    private Forms.NotifyIcon? _notifyIcon;
    private Drawing.Icon? _trayIcon;

    public MainWindow()
    {
        InitializeComponent();

        _uiSettings = new UISettings();
        _queue = DispatcherQueue.GetForCurrentThread();
        CaptureDefaultBrushColors();
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        _themeSettings = ThemeSettings.CreateForWindowId(_appWindow.Id);
        SetHighContrastPalette(_themeSettings.HighContrast);
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
        UpdateTitleBarInsets();
        if (!string.IsNullOrWhiteSpace(_startupWarning))
        {
            ShowNotice(_startupWarning!, InfoBarSeverity.Warning);
            _startupWarning = null;
        }

        _ = LoadBackgroundStateAsync();
        _backgroundStateTimer.Start();
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

    private async void EditProfile_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null) return;
        await EditProfileAsync(SelectedProfile);
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
            profile.AddLog($"{DateTime.Now:HH:mm:ss}  配置已保存。");
            if (wasRunning || wasBackgroundConfigured)
                await ApplyProfileConfigAsync(profile, wasRunning, wasBackgroundConfigured);
            RefreshEndpointProfiles();
            UpdateSummary();
            ShowNotice("配置已保存。", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("保存配置失败", exception.Message, InfoBarSeverity.Error);
        }
    }

    private async void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null || SelectedProfile.IsBusy) return;
        await ApplyProfileConfigAsync(SelectedProfile, SelectedProfile.IsRunning, SelectedProfile.IsBackgroundConfigured);
        UpdateSummary();
    }

    private async void StatusButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null || SelectedProfile.IsBusy) return;
        var profile = SelectedProfile;
        try
        {
            var output = await _processManager.StatusAsync(profile, _store.ConfigPath(profile));
            foreach (var line in output) profile.AddLog($"{DateTime.Now:HH:mm:ss}  {line}");
            if (!profile.IsBackgroundConfigured) profile.State = "进程运行中";
            UpdateSummary();
        }
        catch (Exception exception)
        {
            profile.SetDiagnostic("无法读取 FRP 状态",
                $"{exception.Message} 建议确认配置已开启 frpc 管理接口。",
                DiagnosticSeverity.Warning);
            try { _store.SaveDiagnostic(profile); } catch (IOException) { }
            profile.AddLog($"{DateTime.Now:HH:mm:ss}  读取状态失败：{exception.Message}");
            UpdateSummary();
            await ShowMessageAsync("读取状态失败", exception.Message, InfoBarSeverity.Warning);
        }
    }

    private async Task ApplyProfileConfigAsync(TunnelProfile profile, bool wasRunning, bool wasBackgroundConfigured)
    {
        try
        {
            _store.SaveConfig(profile);
            if (wasBackgroundConfigured)
            {
                await _backgroundTaskManager.RestartAsync(profile);
                return;
            }

            if (!wasRunning) return;
            if (HasFrpcAdminApi(profile.Config))
            {
                try
                {
                    await _processManager.ReloadAsync(profile, _store.ConfigPath(profile));
                    return;
                }
                catch (InvalidDataException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    profile.AddLog($"{DateTime.Now:HH:mm:ss}  热更新失败：{exception.Message} 正在改用重启应用配置。");
                }
            }

            profile.AddLog($"{DateTime.Now:HH:mm:ss}  当前配置未启用 frpc 管理接口，正在重启以应用配置。");
            await _processManager.StopAsync(profile);
            await StartProfileAsync(profile, showError: true);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("应用配置失败", exception.Message, InfoBarSeverity.Error);
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
        try
        {
            await _processManager.StopAllAsync(Profiles);
            foreach (var profile in Profiles.Where(profile => profile.IsBackgroundConfigured))
                await _backgroundTaskManager.StopAsync(profile);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("停止实例失败", exception.Message, InfoBarSeverity.Error);
        }

        UpdateSummary();
    }

    private async void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null) return;
        var profile = SelectedProfile;
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
            Profiles.Remove(profile);
            SelectedProfile = Profiles.FirstOrDefault();
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
        var flyout = new MenuFlyout();
        foreach (var profile in Profiles)
        {
            var item = new MenuFlyoutItem
            {
                Text = ReferenceEquals(profile, SelectedProfile) ? $"✓ {profile.Name}" : profile.Name,
                Tag = profile
            };
            item.Click += (_, _) =>
            {
                SelectedProfile = profile;
                UpdateSummary();
            };
            flyout.Items.Add(item);
        }
        if (Profiles.Count > 0) flyout.Items.Add(new MenuFlyoutSeparator());

        var add = new MenuFlyoutItem { Text = "添加配置" };
        add.Click += AddProfile_Click;
        flyout.Items.Add(add);
        if (SelectedProfile is not null)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            var edit = new MenuFlyoutItem { Text = "编辑完整 TOML" };
            edit.Click += EditProfile_Click;
            var delete = new MenuFlyoutItem { Text = "删除配置" };
            delete.Click += DeleteProfile_Click;
            flyout.Items.Add(edit);
            flyout.Items.Add(delete);

            var profile = SelectedProfile;
            var readStatus = new MenuFlyoutItem { Text = "读取状态" };
            readStatus.IsEnabled = !profile.IsBusy && (profile.IsRunning || profile.IsBackgroundRunning);
            readStatus.Click += StatusButton_Click;
            var reload = new MenuFlyoutItem { Text = "重新加载" };
            reload.IsEnabled = !profile.IsBusy && (profile.IsRunning || profile.IsBackgroundRunning);
            reload.Click += ReloadButton_Click;
            var backgroundText = profile.BackgroundStatus.StartsWith("旧版后台任务", StringComparison.Ordinal)
                ? "切换为后台服务"
                : profile.IsBackgroundConfigured
                    ? profile.IsBackgroundRunning ? "关闭后台服务" : "重新启动后台服务"
                    : "启用后台服务";
            var background = new MenuFlyoutItem { Text = backgroundText };
            background.IsEnabled = !profile.IsBusy && !profile.IsRunning;
            background.Click += BackgroundButton_Click;
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(readStatus);
            flyout.Items.Add(reload);
            flyout.Items.Add(background);
        }
        flyout.Items.Add(new MenuFlyoutSeparator());
        var startAll = new MenuFlyoutItem { Text = "全部运行" };
        startAll.Click += StartAll_Click;
        var stopAll = new MenuFlyoutItem { Text = "全部停止" };
        stopAll.Click += StopAll_Click;
        flyout.Items.Add(startAll);
        flyout.Items.Add(stopAll);
        flyout.ShowAt(anchor);
    }

    private async Task<bool> ShowProfileDialogAsync(ProfileDialog dialog)
    {
        dialog.XamlRoot = RootGrid.XamlRoot;
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary || dialog.SaveRequested;
    }

    private void Home_Click(object sender, RoutedEventArgs e) => ShowHomePage();

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
            BackdropSupportText.Text = _backdropWarning
                ?? "材质由 Windows 合成；若系统不支持所选效果，窗口会使用浅色背景。";
            DataPathBox.Text = _settingsStore.RootDirectory;
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
            BackdropSupportText.Text = _backdropWarning ?? $"{material} 已即时预览并保存。";
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
            BackdropSupportText.Text = _backdropWarning ?? "材质选择未保存，已恢复上次设置。";
            SettingsStatusText.Text = "材质选择未保存，已恢复上次设置。";
            SettingsStatusText.Foreground = ResourceBrush("WarningBrush");
            _ = ShowMessageAsync("保存背景材质失败", exception.Message, InfoBarSeverity.Error);
        }
    }

    private bool ApplyWindowBackdrop(string material)
    {
        _backdropApplyFailure = null;
        SystemBackdrop = null;
        _acrylicController?.Dispose();
        _acrylicController = null;
        _backdropConfiguration = null;

        var fallback = GetBackdropFallbackReason(material);
        if (fallback is not null)
        {
            RootGrid.Background = ResourceBrush("CanvasBrush");
            return false;
        }

        try
        {
            RootGrid.Background = ResourceBrush("CanvasBackdropBrush");
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
                Theme = SystemBackdropTheme.Light,
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
            RootGrid.Background = ResourceBrush("CanvasBrush");
            _backdropApplyFailure = $"无法应用“{material}”，已回退为纯色背景：{exception.Message}";
            return false;
        }
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

    private void CaptureDefaultBrushColors()
    {
        foreach (var key in new[]
        {
            "CanvasBrush", "CanvasBackdropBrush", "PaperBrush", "SoftBrush",
            "AddressChipBrush", "LogSurfaceBrush", "LogWarningRowBrush", "LogErrorRowBrush",
            "InkBrush", "InkSoftBrush", "MutedBrush", "RuleBrush",
            "ControlBorderBrush", "SelectionBorderBrush", "ServiceBorderBrush",
            "SignalBrush", "SignalDeepBrush", "SignalSoftBrush", "SignalWashBrush", "SignalInkBrush",
            "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush",
            "AlertBrush", "AlertSoftBrush", "WarningBrush", "WarningSoftBrush", "SuccessBrush", "SuccessSoftBrush"
        })
        {
            if (Microsoft.UI.Xaml.Application.Current.Resources[key] is SolidColorBrush brush)
                _defaultBrushColors[key] = brush.Color;
        }
    }

    private void SetHighContrastPalette(bool enabled)
    {
        if (!enabled)
        {
            foreach (var (key, color) in _defaultBrushColors)
                ((SolidColorBrush)Microsoft.UI.Xaml.Application.Current.Resources[key]).Color = color;
            return;
        }

        var background = _uiSettings.GetColorValue(UIColorType.Background);
        var foreground = _uiSettings.GetColorValue(UIColorType.Foreground);
        foreach (var key in new[]
        {
            "CanvasBrush", "CanvasBackdropBrush", "PaperBrush", "SoftBrush",
            "AddressChipBrush", "LogSurfaceBrush", "LogWarningRowBrush", "LogErrorRowBrush",
            "SignalSoftBrush", "SignalWashBrush", "AlertSoftBrush", "WarningSoftBrush", "SuccessSoftBrush"
        })
            ((SolidColorBrush)Microsoft.UI.Xaml.Application.Current.Resources[key]).Color = background;

        foreach (var key in new[]
        {
            "InkBrush", "InkSoftBrush", "MutedBrush", "RuleBrush", "ControlBorderBrush",
            "SelectionBorderBrush", "ServiceBorderBrush", "SignalBrush", "SignalDeepBrush", "AlertBrush",
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
        SetTitleBar(AppTitleBar);
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
        TitleBarLeftInsetColumn.Width = new GridLength(Math.Max(40, titleBar.LeftInset / scale + 12));
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

        titleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 23, 40, 58);
        titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 88, 106, 122);
    }

    private void RefreshWindowBackdrop()
    {
        var applied = ApplyWindowBackdrop(_settings.BackdropMaterial);
        _backdropWarning = applied ? null : GetBackdropFallbackReason(_settings.BackdropMaterial);
        if (BackdropSupportText is not null)
            BackdropSupportText.Text = _backdropWarning ?? $"{_settings.BackdropMaterial} 已应用。";
    }

    private void ShowHomePage()
    {
        TitleBarPageText.Text = "运行概览";
        HomeContent.Visibility = Visibility.Visible;
        EndpointsContent.Visibility = Visibility.Collapsed;
        TunnelEditorContent.Visibility = Visibility.Collapsed;
        LogsContent.Visibility = Visibility.Collapsed;
        SettingsContent.Visibility = Visibility.Collapsed;
        SetActiveNavigation(HomeNavButton);
    }

    private void ShowEndpointsPage()
    {
        TitleBarPageText.Text = "隧道管理";
        HomeContent.Visibility = Visibility.Collapsed;
        EndpointsContent.Visibility = Visibility.Visible;
        TunnelEditorContent.Visibility = Visibility.Collapsed;
        LogsContent.Visibility = Visibility.Collapsed;
        SettingsContent.Visibility = Visibility.Collapsed;
        SetActiveNavigation(EndpointsNavButton);
        RefreshEndpointProfiles();
    }

    private void ShowTunnelEditorPage()
    {
        TitleBarPageText.Text = $"隧道管理  ›  {TunnelEditorTitle.Text}";
        HomeContent.Visibility = Visibility.Collapsed;
        EndpointsContent.Visibility = Visibility.Collapsed;
        TunnelEditorContent.Visibility = Visibility.Visible;
        LogsContent.Visibility = Visibility.Collapsed;
        SettingsContent.Visibility = Visibility.Collapsed;
        SetActiveNavigation(EndpointsNavButton);
    }

    private void ShowLogsPage()
    {
        TitleBarPageText.Text = "运行日志";
        HomeContent.Visibility = Visibility.Collapsed;
        EndpointsContent.Visibility = Visibility.Collapsed;
        TunnelEditorContent.Visibility = Visibility.Collapsed;
        LogsContent.Visibility = Visibility.Visible;
        SettingsContent.Visibility = Visibility.Collapsed;
        SetActiveNavigation(LogsNavButton);
    }

    private void ShowSettingsPage()
    {
        TitleBarPageText.Text = "设置";
        HomeContent.Visibility = Visibility.Collapsed;
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
        EndpointsNavButton.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources[
            ReferenceEquals(active, EndpointsNavButton) ? "NavigationActiveButton" : "NavigationButton"];
        LogsNavButton.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources[
            ReferenceEquals(active, LogsNavButton) ? "NavigationActiveButton" : "NavigationButton"];
        SettingsNavButton.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources[
            ReferenceEquals(active, SettingsNavButton) ? "NavigationActiveButton" : "NavigationButton"];

        var inactiveIcon = ResourceBrush("InkSoftBrush");
        var activeIcon = ResourceBrush("SignalDeepBrush");
        HomeNavIcon.Foreground = ReferenceEquals(active, HomeNavButton) ? activeIcon : inactiveIcon;
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
        if (profile is null)
        {
            EmptyEndpointsTitle.Text = "尚未选择配置";
            EmptyEndpointsDetail.Text = "隧道保存在配置文件中；请先添加或选择一个 FRP 配置。";
            HomeEmptyTunnelsTitle.Text = "尚无配置";
            HomeEmptyTunnelsDetail.Text = "添加一个 FRP 配置后，隧道会显示在这里。";
        }
        else
        {
            foreach (var endpoint in _endpointParser.Parse(profile.Config)) Endpoints.Add(endpoint);
            var isConfigEmpty = string.IsNullOrWhiteSpace(profile.Config);
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
        EndpointTableBorder.Visibility = hasEndpoints ? Visibility.Visible : Visibility.Collapsed;
        EmptyEndpointsPanel.Visibility = hasEndpoints ? Visibility.Collapsed : Visibility.Visible;
        OverviewEndpointList.Visibility = hasEndpoints ? Visibility.Visible : Visibility.Collapsed;
        HomeEmptyTunnelsPanel.Visibility = hasEndpoints ? Visibility.Collapsed : Visibility.Visible;
        OverviewTunnelHeader.Visibility = hasEndpoints ? Visibility.Visible : Visibility.Collapsed;
        AddTunnelButton.Visibility = profile is null ? Visibility.Collapsed : Visibility.Visible;
        EditSelectedTunnelButton.Visibility = hasEndpoints ? Visibility.Visible : Visibility.Collapsed;
        EmptyAddProfileButton.Visibility = profile is null ? Visibility.Visible : Visibility.Collapsed;
        HomeEmptyAddProfileButton.Visibility = profile is null ? Visibility.Visible : Visibility.Collapsed;
        HomeEmptyAddTunnelButton.Visibility = profile is null ? Visibility.Collapsed : Visibility.Visible;
        TunnelCountText.Text = $"{Endpoints.Count} 个已配置";
        AddTunnelButton.IsEnabled = profile is not null && !profile.IsBusy;
        UpdateEndpointSelectionState();
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

    private void CopyLocalEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if (sender is XamlButton button && button.Tag is TunnelEndpoint endpoint)
        {
            if (EndpointList.Items.Contains(endpoint)) EndpointList.SelectedItem = endpoint;
            CopyEndpointText(endpoint.LocalAddress, button);
        }
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
                ?? (TunnelTypeBox.SelectedValue?.ToString() == "https" ? "443" : "80");
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
        if (_isAddingTunnel && (TunnelPublicPortBox.Text is "" or "80" or "443"))
            TunnelPublicPortBox.Text = TunnelTypeBox.SelectedValue?.ToString() == "https" ? "443" : "80";
        UpdateTunnelTypeFields();
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
            profile.AddLog($"{DateTime.Now:HH:mm:ss}  隧道“{edit.Name}”配置已保存。");
            if (wasRunning || wasBackgroundConfigured)
                await ApplyProfileConfigAsync(profile, wasRunning, wasBackgroundConfigured);

            _editingTunnelProfile = null;
            _editingEndpoint = null;
            RefreshEndpointProfiles();
            UpdateSummary();
            ShowEndpointsPage();
            ShowNotice("隧道配置已保存。", InfoBarSeverity.Success);
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
        var settings = new AppSettings
        {
            CloseToTray = CloseToTrayToggle.IsOn,
            RestoreWindowLayout = RestoreLayoutToggle.IsOn,
            LogLineLimit = limit,
            AutoStart = AutoStartToggle.IsOn,
            BackdropMaterial = BackdropMaterialBox.SelectedValue?.ToString() ?? "Mica"
        };

        try
        {
            _settingsStore.Save(settings);
            _settings = settings;
            var backdropApplied = ApplyWindowBackdrop(_settings.BackdropMaterial);
            _backdropWarning = backdropApplied ? null : GetBackdropFallbackReason(_settings.BackdropMaterial);
            BackdropSupportText.Text = _backdropWarning ?? $"{_settings.BackdropMaterial} 已应用。";
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

    private async void BackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null) return;
        try
        {
            if (SelectedProfile.IsBackgroundRunning
                && !SelectedProfile.BackgroundStatus.StartsWith("旧版后台任务", StringComparison.Ordinal))
            {
                await _backgroundTaskManager.StopAsync(SelectedProfile);
            }
            else if (SelectedProfile.IsRunning)
            {
                await ShowMessageAsync("实例正在运行",
                    "请先停止当前运行中的实例，再切换到后台运行。",
                    InfoBarSeverity.Informational);
            }
            else
            {
                await _backgroundTaskManager.StartAsync(SelectedProfile);
            }
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("后台运行失败", exception.Message, InfoBarSeverity.Error);
        }

        UpdateSummary();
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        var compact = width < 720;
        var narrow = width < 920;
        RailColumn.Width = new GridLength(compact ? 176 : width < 900 ? 200 : 214);
        var inset = compact ? 16 : narrow ? 22 : 31;
        var topInset = compact ? 18 : 25;
        HomeLayout.Margin = new Thickness(inset, topInset, inset, 24);
        EndpointsContent.Margin = new Thickness(inset, topInset, inset, 24);
        TunnelEditorContent.Margin = new Thickness(inset, topInset, inset, 24);
        LogsContent.Margin = new Thickness(inset, topInset, inset, 24);
        SettingsContent.Margin = new Thickness(inset, topInset, inset, 24);

        ServiceSummaryGrid.ColumnDefinitions[0].Width = new GridLength(compact ? 1 : 0.9, GridUnitType.Star);
        ServiceSummaryGrid.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 1.1, GridUnitType.Star);
        Grid.SetColumn(ServiceMetaContent, compact ? 0 : 1);
        Grid.SetRow(ServiceMetaContent, compact ? 1 : 0);
        ServiceMetaContent.Margin = compact ? new Thickness(0, 12, 0, 0) : new Thickness(0);
        ServiceSummaryGrid.ColumnSpacing = compact ? 0 : 20;

        OverviewColumns.ColumnDefinitions[0].Width = new GridLength(narrow ? 1 : 1.6, GridUnitType.Star);
        OverviewColumns.ColumnDefinitions[1].MinWidth = narrow ? 0 : 240;
        OverviewColumns.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 0.78, GridUnitType.Star);
        OverviewColumns.ColumnSpacing = narrow ? 0 : 24;
        OverviewColumns.RowSpacing = narrow ? 20 : 0;
        Grid.SetColumn(RecentActivitySection, narrow ? 0 : 1);
        Grid.SetRow(RecentActivitySection, narrow ? 1 : 0);
        RecentActivitySection.BorderThickness = narrow ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0);
        RecentActivitySection.Padding = narrow ? new Thickness(0, 20, 0, 0) : new Thickness(22, 0, 0, 0);

        var stackActions = width < 760;
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
        if (_isClosing) return;
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
            _allowExit = true;
            Close();
        }
        catch (Exception exception)
        {
            _isClosing = false;
            await ShowMessageAsync("停止实例失败", exception.Message, InfoBarSeverity.Error);
        }
    }

    private async Task LoadBackgroundStateAsync()
    {
        try
        {
            await _backgroundTaskManager.LoadStateAsync(Profiles);
            UpdateSummary();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("读取后台实例状态失败", exception.Message, InfoBarSeverity.Warning);
        }
    }

    private async Task RefreshBackgroundStateAsync()
    {
        try
        {
            await _backgroundTaskManager.RefreshStateAsync(Profiles);
            UpdateSummary();
        }
        catch
        {
            // A transient service query failure must not interrupt the UI timer.
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
            or nameof(TunnelProfile.LastFailureAt))
        {
            UpdateSummary();
        }
    }

    private void Logs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (sender is ObservableCollection<string> logs
            && SelectedProfile is not null
            && ReferenceEquals(logs, SelectedProfile.Logs))
        {
            RefreshLogText(SelectedProfile);
            UpdateRecentActivity(SelectedProfile);
        }
    }

    private void UpdateSummary()
    {
        if (SelectedProfile is null || !Profiles.Contains(SelectedProfile))
            _selectedProfile = Profiles.FirstOrDefault();
        RefreshEndpointProfiles();
        UpdateSelectedPanel();
        UpdateRecentActivity(SelectedProfile);
    }

    private void UpdateSelectedPanel()
    {
        var profile = SelectedProfile;
        SelectedNameText.Text = profile?.Name ?? "尚未选择配置";
        SelectedStateText.Text = profile?.DisplayState ?? "未选择配置";
        var serverEndpoint = profile is null ? "" : _endpointParser.GetServerEndpoint(profile.Config);
        ServerEndpointText.Text = string.IsNullOrWhiteSpace(serverEndpoint) ? "未配置" : serverEndpoint;
        ToolTipService.SetToolTip(ServerEndpointText, ServerEndpointText.Text);
        var state = SelectedStateText.Text;
        var isFailure = state.Contains("失败", StringComparison.Ordinal)
            || state.Contains("错误", StringComparison.Ordinal)
            || state.Contains("耗尽", StringComparison.Ordinal);
        var stateBrush = profile?.IsActive == true
            ? ResourceBrush("SuccessBrush")
            : isFailure ? ResourceBrush("AlertBrush")
            : profile?.IsBusy == true || state.Contains("中", StringComparison.Ordinal)
                ? ResourceBrush("SignalDeepBrush")
                : ResourceBrush("MutedBrush");
        SelectedStateText.Foreground = stateBrush;
        ServiceStateGlyph.Stroke = stateBrush;
        ServiceStateIcon.Background = profile?.IsActive == true
            ? ResourceBrush("SuccessSoftBrush")
            : isFailure ? ResourceBrush("AlertSoftBrush")
            : profile?.IsBusy == true || state.Contains("中", StringComparison.Ordinal)
                ? ResourceBrush("SignalSoftBrush")
                : ResourceBrush("SoftBrush");

        RunButton.Content = profile?.IsRunning == true
            ? "停止服务"
            : profile?.IsBackgroundConfigured == true
                ? profile.IsBackgroundRunning ? "后台运行中" : "后台服务未运行"
                : "运行服务";
        RunButton.IsEnabled = profile is not null && !profile.IsBusy && !profile.IsBackgroundConfigured;
        var isLogLive = profile?.IsActive == true;
        var isStarting = profile?.IsBusy == true || state.Contains("中", StringComparison.Ordinal);
        var isFailureState = state.Contains("失败", StringComparison.Ordinal)
            || state.Contains("错误", StringComparison.Ordinal)
            || state.Contains("耗尽", StringComparison.Ordinal)
            || profile?.DiagnosticSeverity == DiagnosticSeverity.Error;
        var logStatusBrush = isLogLive ? ResourceBrush("SuccessBrush")
            : isFailureState ? ResourceBrush("AlertBrush")
            : isStarting ? ResourceBrush("SignalDeepBrush")
            : ResourceBrush("MutedBrush");
        LogLiveDot.Fill = logStatusBrush;
        LogLiveStatus.Foreground = logStatusBrush;
        LogLiveStatus.Text = profile is null
            ? "未选择配置"
            : isLogLive ? "实时接收"
            : isStarting ? "服务处理中"
            : isFailureState ? "服务启动失败"
            : "服务未运行";
        LogEmptyText.Text = profile is null
            ? "选择或添加配置后，运行日志会显示在这里。"
            : isLogLive ? "正在等待 frpc 输出…"
            : isStarting ? "服务正在处理，frpc 输出将在启动后显示。"
            : isFailureState ? "服务启动失败。请查看概览中的诊断信息，或重试启动。"
            : "启动服务后，frpc 输出会显示在这里。";
        UpdateDiagnosticPanel(profile);
        RefreshLogText(profile);
        UpdateEndpointSelectionState();
    }

    private void UpdateRecentActivity(TunnelProfile? profile)
    {
        RecentActivityRows.Clear();
        if (profile is not null)
        {
            foreach (var line in profile.Logs.Reverse().Take(3))
            {
                var hasTimestamp = line.Length >= 10
                    && line[8] == ' '
                    && line[9] == ' '
                    && TimeOnly.TryParseExact(line[..8], "HH:mm:ss", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out _);
                RecentActivityRows.Add(new ActivityEntry(
                    hasTimestamp ? line[10..] : line,
                    hasTimestamp ? line[..8] : ""));
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

    private void RefreshLogText(TunnelProfile? profile)
    {
        if (LogLinesList is null) return;
        _logText = profile is null ? string.Empty : string.Join(Environment.NewLine, profile.Logs);
        LogEntries.Clear();
        if (profile is not null)
        {
            foreach (var line in profile.Logs)
                LogEntries.Add(ParseLogEntry(line));
        }

        LogEmptyText.Visibility = LogEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CopyLogButton.IsEnabled = LogEntries.Count > 0;
        if (LogEntries.Count > 0)
            LogLinesList.DispatcherQueue.TryEnqueue(() => LogLinesList.ScrollIntoView(LogEntries[LogEntries.Count - 1]));
    }

    private LogEntry ParseLogEntry(string rawText)
    {
        var hasTimestamp = rawText.Length >= 10
            && rawText[8] == ' '
            && rawText[9] == ' '
            && TimeOnly.TryParseExact(rawText[..8], "HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _);
        var time = hasTimestamp ? rawText[..8] : "";
        var message = hasTimestamp ? rawText[10..] : rawText;
        var level = "INFO";
        var levelBrush = ResourceBrush("SignalBrush");
        var rowBrush = ResourceBrush("LogSurfaceBrush");

        if (message.Contains("[E]", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
            || message.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("失败", StringComparison.Ordinal))
        {
            level = "ERROR";
            levelBrush = ResourceBrush("AlertBrush");
            rowBrush = ResourceBrush("LogErrorRowBrush");
        }
        else if (message.Contains("[W]", StringComparison.OrdinalIgnoreCase)
            || message.Contains("WARN", StringComparison.OrdinalIgnoreCase)
            || message.Contains("stopped by configuration", StringComparison.OrdinalIgnoreCase))
        {
            level = "WARN";
            levelBrush = ResourceBrush("WarningBrush");
            rowBrush = ResourceBrush("LogWarningRowBrush");
        }
        else if (message.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase)
            || message.Contains(" is now connected", StringComparison.OrdinalIgnoreCase)
            || message.Contains("连接已恢复", StringComparison.Ordinal))
        {
            level = "SUCCESS";
            levelBrush = ResourceBrush("SuccessBrush");
        }

        return new LogEntry(rawText, time, level, message, levelBrush, rowBrush);
    }

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
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 FrpDesk", null, (_, _) => _queue.TryEnqueue(ShowFromTray));
        menu.Items.Add("全部停止", null, (_, _) => _queue.TryEnqueue(() => _ = StopAllFromTrayAsync()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出 FrpDesk", null, (_, _) => _queue.TryEnqueue(ExitFromTray));

        _trayIcon = LoadTrayIcon();
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "FrpDesk",
            Icon = _trayIcon ?? Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => _queue.TryEnqueue(ShowFromTray);
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
        try
        {
            await _processManager.StopAllAsync(Profiles);
            foreach (var profile in Profiles.Where(profile => profile.IsBackgroundConfigured))
                await _backgroundTaskManager.StopAsync(profile);
            UpdateSummary();
            ShowNotice("所有实例已停止。", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("停止实例失败", exception.Message, InfoBarSeverity.Error);
        }
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
        catch (Exception exception) when (exception is JsonException or IOException)
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
            catch (IOException) { }
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
        catch (IOException)
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
            try { return GetDpiForWindow(_hwnd) / 96d; }
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
