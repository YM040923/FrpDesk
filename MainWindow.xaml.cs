using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using Clipboard = System.Windows.Clipboard;
using FrpDesk.Models;
using FrpDesk.Services;
using FrpDesk.Views;

namespace FrpDesk;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly ProfileStore _store = new();
    private readonly AppSettingsStore _settingsStore = new();
    private readonly FrpcProcessManager _processManager;
    private readonly BackgroundTaskManager _backgroundTaskManager;
    private readonly TomlEndpointParser _endpointParser = new();
    private readonly DispatcherTimer _backgroundStateTimer;
    private readonly string _windowLayoutPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrpDesk", "window.json");
    private AppSettings _settings;
    private TunnelProfile? _selectedProfile;
    private TunnelProfile? _selectedEndpointProfile;
    private bool _isRefreshingEndpoints;
    private bool _isClosing;
    private bool _allowExit;
    private bool _isBatchOperating;
    private bool _skipNextLayoutSave;
    private Forms.NotifyIcon? _notifyIcon;
    private Drawing.Icon? _trayIcon;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsStore.Load();
        RestoreWindowLayout();
        SetWindowIcon();
        _processManager = new FrpcProcessManager(ResolveFrpcPath(), Dispatcher, _store);
        _backgroundTaskManager = new BackgroundTaskManager(ResolveFrpcPath(), _store);
        _backgroundStateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _backgroundStateTimer.Tick += async (_, _) => await RefreshBackgroundStateAsync();
        DataContext = this;
        LogTextBox.ContextMenu = CreateLogMenu();
        CreateTrayIcon();
        SystemEvents.SessionEnding += SystemEvents_SessionEnding;
        Loaded += async (_, _) =>
        {
            await LoadBackgroundStateAsync();
            _backgroundStateTimer.Start();
        };
        try
        {
            foreach (var profile in _store.Load()) AddProfileToList(profile);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "读取实例失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        SelectedProfile = Profiles.FirstOrDefault();
        UpdateSummary();
        LoadSettingsPage();
        ShowHomePage();
    }

    public ObservableCollection<TunnelProfile> Profiles { get; } = [];
    public ObservableCollection<TunnelEndpoint> Endpoints { get; } = [];
    public TunnelProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (ReferenceEquals(_selectedProfile, value)) return;
            _selectedProfile = value;
            OnPropertyChanged(nameof(SelectedProfile));
            UpdateSelectedPanel();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ProfileDialog(ResolveFrpcPath()) { Owner = this };
        if (!ShowProfileDialog(dialog)) return;

        var profile = new TunnelProfile(Guid.NewGuid(), dialog.ProfileName, dialog.ConfigText);
        try
        {
            _store.SaveConfig(profile);
            AddProfileToList(profile);
            _store.SaveProfiles(Profiles);
            SelectedProfile = profile;
            RefreshEndpointProfiles();
            UpdateSummary();
        }
        catch (Exception exception)
        {
            if (Profiles.Contains(profile)) Profiles.Remove(profile);
            profile.PropertyChanged -= Profile_PropertyChanged;
            profile.Logs.CollectionChanged -= Logs_CollectionChanged;
            try { _store.Delete(profile); } catch { }
            MessageBox.Show(this, exception.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void EditProfile_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null) return;
        var profile = SelectedProfile;
        var wasRunning = profile.IsRunning;
        var wasBackgroundConfigured = profile.IsBackgroundConfigured;
        var dialog = new ProfileDialog(ResolveFrpcPath(), profile) { Owner = this };
        if (ShowProfileDialog(dialog))
        {
            profile.Name = dialog.ProfileName;
            profile.Config = dialog.ConfigText;
            profile.ClearDiagnostic();
            _store.SaveConfig(profile);
            _store.SaveDiagnostic(profile);
            _store.SaveProfiles(Profiles);
            profile.AddLog($"{DateTime.Now:HH:mm:ss}  配置已保存。");
            if (wasRunning || wasBackgroundConfigured) await ApplyProfileConfigAsync(profile, wasRunning, wasBackgroundConfigured);
            RefreshEndpointProfiles();
        }
        UpdateSummary();
    }

    private async void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null || SelectedProfile.IsBusy) return;
        var profile = SelectedProfile;
        await ApplyProfileConfigAsync(profile, profile.IsRunning, profile.IsBackgroundConfigured);
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
            profile.State = profile.IsBackgroundConfigured ? profile.State : "进程运行中";
        }
        catch (Exception exception)
        {
            profile.SetDiagnostic("无法读取 FRP 状态", $"{exception.Message} 建议确认配置已开启 frpc 管理接口。", DiagnosticSeverity.Warning);
            try { _store.SaveDiagnostic(profile); } catch (IOException) { }
            profile.AddLog($"{DateTime.Now:HH:mm:ss}  读取状态失败：{exception.Message}");
            MessageBox.Show(this, exception.Message, "读取状态失败", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        UpdateSummary();
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
                catch (InvalidDataException) { throw; }
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
            MessageBox.Show(this, exception.Message, "应用配置失败", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                || line.StartsWith("admin_port", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null) return;
        if (SelectedProfile.IsBackgroundRunning)
        {
            MessageBox.Show(this, "此实例正在后台运行，请先停止后台运行。", "实例正在后台运行", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            if (SelectedProfile.IsRunning)
            {
                await _processManager.StopAsync(SelectedProfile);
            }
            else
            {
                await StartProfileAsync(SelectedProfile, showError: true);
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "实例操作失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        UpdateSummary();
    }

    private async void ProfileQuickAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not TunnelProfile profile) return;
        SelectedProfile = profile;
        if (profile.IsBackgroundConfigured)
        {
            MessageBox.Show(this, "此实例由后台服务管理，请使用“关闭后台服务”停止它。", "实例正在后台运行", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            if (profile.IsRunning) await _processManager.StopAsync(profile);
            else await StartProfileAsync(profile, showError: false);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "实例操作失败", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show(this, "没有可以直接运行的实例。后台服务实例需要单独管理。", "全部运行", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _isBatchOperating = true;
        UpdateBatchButtons();
        try
        {
            var results = await Task.WhenAll(candidates.Select(profile => StartProfileAsync(profile, showError: false)));
            var failed = candidates.Where((_, index) => !results[index]).Select(profile => profile.Name).ToArray();
            if (failed.Length > 0)
            {
                MessageBox.Show(this, $"已尝试运行 {candidates.Length} 个实例，其中 {failed.Length} 个启动失败：\n{string.Join("、", failed)}\n\n请选中失败实例查看诊断信息。", "部分实例启动失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            _isBatchOperating = false;
            UpdateSummary();
            UpdateBatchButtons();
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
            MessageBox.Show(this, exception.Message, "停止实例失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        UpdateSummary();
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null) return;
        var profile = SelectedProfile;
        if (profile.IsActive || profile.IsBackgroundConfigured)
        {
            MessageBox.Show(this, "请先停止运行中的实例。", "无法删除", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var result = MessageBox.Show(this, $"确定删除“{profile.Name}”？\n对应的 TOML 配置也会删除。", "删除实例", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        _store.Delete(profile);
        profile.PropertyChanged -= Profile_PropertyChanged;
        profile.Logs.CollectionChanged -= Logs_CollectionChanged;
        Profiles.Remove(profile);
        _store.SaveProfiles(Profiles);
        SelectedProfile = Profiles.FirstOrDefault();
        if (ReferenceEquals(_selectedEndpointProfile, profile)) _selectedEndpointProfile = null;
        RefreshEndpointProfiles();
        UpdateSummary();
    }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfilesList.SelectedItem is TunnelProfile profile) SelectedProfile = profile;
    }

    private void ProfileMenu_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not TunnelProfile profile) return;
        SelectedProfile = profile;
        var menu = new ContextMenu { Style = (Style)FindResource("FrpContextMenu") };
        var edit = new MenuItem { Header = "编辑配置", Style = (Style)FindResource("FrpMenuItemCompact") };
        edit.Click += EditProfile_Click;
        var delete = new MenuItem { Header = "删除实例", Style = (Style)FindResource("FrpMenuItemCompact") };
        delete.Click += DeleteProfile_Click;
        menu.Items.Add(edit);
        menu.Items.Add(delete);
        menu.IsOpen = true;
    }

    private bool ShowProfileDialog(ProfileDialog dialog)
    {
        ModalBackdrop.Visibility = Visibility.Visible;
        try
        {
            return dialog.ShowDialog() == true;
        }
        finally
        {
            ModalBackdrop.Visibility = Visibility.Collapsed;
        }
    }

    private void Home_Click(object sender, RoutedEventArgs e) => ShowHomePage();

    private void Endpoints_Click(object sender, RoutedEventArgs e) => ShowEndpointsPage();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        LoadSettingsPage();
        ShowSettingsPage();
    }

    private void LoadSettingsPage()
    {
        CloseToTrayToggle.IsChecked = _settings.CloseToTray;
        RestoreLayoutToggle.IsChecked = _settings.RestoreWindowLayout;
        LogLimitBox.SelectedValue = _settings.LogLineLimit.ToString();
        DataPathBox.Text = _settingsStore.RootDirectory;
        SettingsStatusText.Text = string.Empty;
    }

    private void ShowHomePage()
    {
        HomeContent.Visibility = Visibility.Visible;
        EndpointsContent.Visibility = Visibility.Collapsed;
        SettingsContent.Visibility = Visibility.Collapsed;
        HomeNavButton.Background = (System.Windows.Media.Brush)FindResource("AccentSoft");
        EndpointsNavButton.Background = System.Windows.Media.Brushes.Transparent;
        SettingsNavButton.Background = System.Windows.Media.Brushes.Transparent;
        HomeNavIcon.Stroke = (System.Windows.Media.Brush)FindResource("Accent");
        HomeNavLabel.Foreground = (System.Windows.Media.Brush)FindResource("Accent");
        EndpointsNavIcon.Stroke = (System.Windows.Media.Brush)FindResource("TextSecondary");
        EndpointsNavLabel.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary");
        SettingsNavIcon.Fill = (System.Windows.Media.Brush)FindResource("TextSecondary");
        SettingsNavLabel.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary");
    }

    private void ShowEndpointsPage()
    {
        HomeContent.Visibility = Visibility.Collapsed;
        EndpointsContent.Visibility = Visibility.Visible;
        SettingsContent.Visibility = Visibility.Collapsed;
        HomeNavButton.Background = System.Windows.Media.Brushes.Transparent;
        EndpointsNavButton.Background = (System.Windows.Media.Brush)FindResource("AccentSoft");
        SettingsNavButton.Background = System.Windows.Media.Brushes.Transparent;
        HomeNavIcon.Stroke = (System.Windows.Media.Brush)FindResource("TextSecondary");
        HomeNavLabel.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary");
        EndpointsNavIcon.Stroke = (System.Windows.Media.Brush)FindResource("Accent");
        EndpointsNavLabel.Foreground = (System.Windows.Media.Brush)FindResource("Accent");
        SettingsNavIcon.Fill = (System.Windows.Media.Brush)FindResource("TextSecondary");
        SettingsNavLabel.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary");
        RefreshEndpointProfiles();
    }

    private void ShowSettingsPage()
    {
        HomeContent.Visibility = Visibility.Collapsed;
        EndpointsContent.Visibility = Visibility.Collapsed;
        SettingsContent.Visibility = Visibility.Visible;
        HomeNavButton.Background = System.Windows.Media.Brushes.Transparent;
        EndpointsNavButton.Background = System.Windows.Media.Brushes.Transparent;
        SettingsNavButton.Background = (System.Windows.Media.Brush)FindResource("AccentSoft");
        HomeNavIcon.Stroke = (System.Windows.Media.Brush)FindResource("TextSecondary");
        HomeNavLabel.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary");
        EndpointsNavIcon.Stroke = (System.Windows.Media.Brush)FindResource("TextSecondary");
        EndpointsNavLabel.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary");
        SettingsNavIcon.Fill = (System.Windows.Media.Brush)FindResource("Accent");
        SettingsNavLabel.Foreground = (System.Windows.Media.Brush)FindResource("Accent");
    }

    private void RefreshEndpointProfiles()
    {
        if (_isRefreshingEndpoints) return;
        _isRefreshingEndpoints = true;
        try
        {
            if (_selectedEndpointProfile is null || !Profiles.Contains(_selectedEndpointProfile))
                _selectedEndpointProfile = SelectedProfile ?? Profiles.FirstOrDefault();
            EndpointProfileBox.SelectedItem = _selectedEndpointProfile;
        }
        finally
        {
            _isRefreshingEndpoints = false;
        }

        RefreshEndpointList();
    }

    private void EndpointProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingEndpoints) return;
        _selectedEndpointProfile = EndpointProfileBox.SelectedItem as TunnelProfile;
        RefreshEndpointList();
    }

    private void RefreshEndpointList()
    {
        Endpoints.Clear();
        var profile = _selectedEndpointProfile;
        if (profile is null)
        {
            EndpointSummaryText.Text = "还没有配置实例";
            EmptyEndpointsTitle.Text = "还没有配置实例";
            EmptyEndpointsDetail.Text = "先在“实例”页面添加一个 frpc 配置。";
        }
        else
        {
            foreach (var endpoint in _endpointParser.Parse(profile.Config)) Endpoints.Add(endpoint);
            EndpointSummaryText.Text = Endpoints.Count == 0 ? "未解析到代理" : $"共 {Endpoints.Count} 个代理";
            EmptyEndpointsTitle.Text = string.IsNullOrWhiteSpace(profile.Config) ? "配置为空" : "没有解析到代理";
            EmptyEndpointsDetail.Text = string.IsNullOrWhiteSpace(profile.Config)
                ? "请先在实例编辑页粘贴 TOML 配置。"
                : "当前配置中没有识别到 [[proxies]] 段落。";
        }

        var hasEndpoints = Endpoints.Count > 0;
        EndpointList.Visibility = hasEndpoints ? Visibility.Visible : Visibility.Collapsed;
        EmptyEndpointsPanel.Visibility = hasEndpoints ? Visibility.Collapsed : Visibility.Visible;
        CopyAllEndpointsButton.IsEnabled = hasEndpoints;
    }

    private void CopyLocalEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is TunnelEndpoint endpoint) CopyEndpointText(endpoint.LocalAddress);
    }

    private void CopyRemoteEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is TunnelEndpoint endpoint && endpoint.HasCopyableRemoteAddress)
            CopyEndpointText(endpoint.RemoteAddress);
    }

    private void CopyAllEndpoints_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedEndpointProfile is null || Endpoints.Count == 0) return;
        var lines = new List<string> { $"配置：{_selectedEndpointProfile.Name}" };
        foreach (var endpoint in Endpoints)
        {
            lines.Add(string.Empty);
            lines.Add($"{endpoint.Name} [{endpoint.DisplayType}]");
            lines.Add($"本地地址：{endpoint.LocalAddress}");
            lines.Add($"穿透地址：{endpoint.RemoteAddress}");
        }
        CopyEndpointText(string.Join(Environment.NewLine, lines));
    }

    private void CopyEndpointText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "复制地址失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var settings = new AppSettings
        {
            CloseToTray = CloseToTrayToggle.IsChecked == true,
            RestoreWindowLayout = RestoreLayoutToggle.IsChecked == true,
            LogLineLimit = int.TryParse(LogLimitBox.SelectedValue as string, out var limit) ? limit : 400
        };

        try
        {
            _settingsStore.Save(settings);
            _settings = settings;
            foreach (var profile in Profiles) profile.SetLogLineLimit(_settings.LogLineLimit);
            RefreshLogText(SelectedProfile);
            SettingsStatusText.Text = "设置已保存";
        }
        catch (IOException exception)
        {
            MessageBox.Show(this, exception.Message, "保存设置失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_settingsStore.RootDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settingsStore.RootDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "打开目录失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ResetWindowLayout_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(_windowLayoutPath)) File.Delete(_windowLayoutPath);
            _skipNextLayoutSave = true;
            WindowState = WindowState.Normal;
            Width = 1100;
            Height = 720;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            CenterWindowOnScreen();
            SettingsStatusText.Text = "窗口布局已恢复默认";
        }
        catch (IOException exception)
        {
            MessageBox.Show(this, exception.Message, "恢复失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void BackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null) return;
        try
        {
            if (SelectedProfile.IsBackgroundRunning && !SelectedProfile.BackgroundStatus.StartsWith("旧版后台任务", StringComparison.Ordinal))
                await _backgroundTaskManager.StopAsync(SelectedProfile);
            else if (SelectedProfile.IsRunning)
                MessageBox.Show(this, "请先停止当前运行中的实例，再切换到后台运行。", "实例正在运行", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                await _backgroundTaskManager.StartAsync(SelectedProfile);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "后台运行失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        UpdateSummary();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
        MaximizeIcon.Data = WindowState == WindowState.Maximized
            ? (Geometry)FindResource("RestoreGeometry")
            : (Geometry)FindResource("MaximizeGeometry");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void RestoreWindowLayout()
    {
        if (!_settings.RestoreWindowLayout) return;
        if (!File.Exists(_windowLayoutPath)) return;
        try
        {
            var layout = JsonSerializer.Deserialize<WindowLayout>(File.ReadAllText(_windowLayoutPath));
            if (layout is null || layout.Width < MinWidth || layout.Height < MinHeight) return;

            var width = Math.Min(layout.Width, Math.Max(MinWidth, SystemParameters.VirtualScreenWidth - 24));
            var height = Math.Min(layout.Height, Math.Max(MinHeight, SystemParameters.VirtualScreenHeight - 24));
            var bounds = new Rect(layout.Left, layout.Top, width, height);
            if (!IsVisibleOnScreen(bounds))
            {
                bounds.X = SystemParameters.WorkArea.Left + Math.Max(0, (SystemParameters.WorkArea.Width - width) / 2);
                bounds.Y = SystemParameters.WorkArea.Top + Math.Max(0, (SystemParameters.WorkArea.Height - height) / 2);
            }

            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = bounds.Left;
            Top = bounds.Top;
            Width = bounds.Width;
            Height = bounds.Height;
            if (layout.IsMaximized) WindowState = WindowState.Maximized;
        }
        catch (JsonException)
        {
            // A broken layout should never prevent the app from starting.
        }
        catch (IOException)
        {
            // The layout is optional and can be unavailable during shutdown.
        }
    }

    private static bool IsVisibleOnScreen(Rect bounds)
    {
        var right = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        var bottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        return bounds.Right > SystemParameters.VirtualScreenLeft + 64
            && bounds.Left < right - 64
            && bounds.Bottom > SystemParameters.VirtualScreenTop + 32
            && bounds.Top < bottom - 32;
    }

    private void SaveWindowLayout()
    {
        if (!_settings.RestoreWindowLayout) return;
        if (_skipNextLayoutSave)
        {
            try
            {
                if (File.Exists(_windowLayoutPath)) File.Delete(_windowLayoutPath);
            }
            catch (IOException) { }
            return;
        }
        try
        {
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            if (bounds.Width < MinWidth || bounds.Height < MinHeight) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_windowLayoutPath)!);
            var layout = new WindowLayout(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                WindowState == WindowState.Maximized);
            File.WriteAllText(_windowLayoutPath, JsonSerializer.Serialize(layout));
        }
        catch (IOException)
        {
            // Window layout is a convenience and must not block closing.
        }
    }

    private void CenterWindowOnScreen()
    {
        Left = SystemParameters.WorkArea.Left + Math.Max(0, (SystemParameters.WorkArea.Width - Width) / 2);
        Top = SystemParameters.WorkArea.Top + Math.Max(0, (SystemParameters.WorkArea.Height - Height) / 2);
    }

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 FrpDesk", null, (_, _) => Dispatcher.BeginInvoke(ShowFromTray));
        menu.Items.Add("全部停止", null, (_, _) => Dispatcher.BeginInvoke(async () => await StopAllFromTrayAsync()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出 FrpDesk", null, (_, _) => Dispatcher.BeginInvoke(ExitFromTray));

        _trayIcon = LoadTrayIcon();
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "FrpDesk",
            Icon = _trayIcon ?? Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowFromTray);
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
            if (File.Exists(path)) Icon = new BitmapImage(new Uri(path, UriKind.Absolute));
        }
        catch
        {
            // The executable still carries ApplicationIcon as a fallback.
        }
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private async Task StopAllFromTrayAsync()
    {
        try
        {
            await _processManager.StopAllAsync(Profiles);
            foreach (var profile in Profiles.Where(profile => profile.IsBackgroundConfigured))
                await _backgroundTaskManager.StopAsync(profile);
            UpdateSummary();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "停止实例失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExitFromTray()
    {
        _allowExit = true;
        Close();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        SystemEvents.SessionEnding -= SystemEvents_SessionEnding;
        _notifyIcon?.Dispose();
        _notifyIcon = null;
        _trayIcon?.Dispose();
        _trayIcon = null;
        _backgroundStateTimer.Stop();
    }

    private async Task StartIfAskedAsync(TunnelProfile profile, bool startImmediately)
    {
        if (!startImmediately) return;
        await StartProfileAsync(profile, showError: true);
        UpdateSelectedPanel();
        UpdateSummary();
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
                MessageBox.Show(this, exception.Message, "启动失败", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        if (e.PropertyName is nameof(TunnelProfile.State) or nameof(TunnelProfile.Name) or nameof(TunnelProfile.IsBusy) or nameof(TunnelProfile.IsBackgroundRunning) or nameof(TunnelProfile.IsBackgroundConfigured) or nameof(TunnelProfile.BackgroundStatus)
            or nameof(TunnelProfile.DiagnosticSummary) or nameof(TunnelProfile.DiagnosticDetail) or nameof(TunnelProfile.DiagnosticSeverity) or nameof(TunnelProfile.LastExitCode) or nameof(TunnelProfile.LastFailureAt)) UpdateSummary();
    }

    private void Logs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (sender is ObservableCollection<string> logs && SelectedProfile is not null && ReferenceEquals(logs, SelectedProfile.Logs))
            RefreshLogText(SelectedProfile);
    }

    private void UpdateSummary()
    {
        RunningCountText.Text = Profiles.Count(profile => profile.IsActive).ToString();
        ProfileCountText.Text = Profiles.Count.ToString();
        EmptyProfilesPanel.Visibility = Profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProfilesList.Visibility = Profiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateSelectedPanel();
        UpdateBatchButtons();
    }

    private void UpdateBatchButtons()
    {
        var canStart = Profiles.Any(profile => !profile.IsBackgroundConfigured && !profile.IsRunning && !profile.IsBusy);
        var canStop = Profiles.Any(profile => profile.IsRunning || profile.IsBackgroundConfigured);
        StartAllButton.IsEnabled = !_isBatchOperating && canStart;
        StopAllButton.IsEnabled = !_isBatchOperating && canStop;
    }

    private void UpdateSelectedPanel()
    {
        var profile = SelectedProfile;
        SelectedNameText.Text = profile?.Name ?? "选择一个实例";
        SelectedStateText.Text = profile?.State ?? "从左侧选择配置开始";
        if (profile?.IsBackgroundConfigured == true)
            SelectedStateText.Text = profile.BackgroundStatus == "后台运行中"
                ? "后台运行中，独立于窗口和 RDP 会话"
                : profile.BackgroundStatus;
        BackgroundButton.Content = profile?.BackgroundStatus.StartsWith("旧版后台任务", StringComparison.Ordinal) == true
            ? "切换为后台服务"
            : profile?.IsBackgroundConfigured == true && profile.IsBackgroundRunning ? "关闭后台服务" :
                profile?.IsBackgroundConfigured == true ? "重新启动后台服务" : "启用后台服务";
        BackgroundButton.IsEnabled = profile is not null && !profile.IsBusy && !profile.IsRunning;
        EditButton.IsEnabled = profile is not null && !profile.IsBusy;
        ReloadButton.ToolTip = profile?.IsBackgroundConfigured == true ? "重启后台服务并应用最新配置" : "通过 frpc 管理接口热更新；未启用时自动重启实例";
        ReloadButton.IsEnabled = profile is not null && !profile.IsBusy && (profile.IsRunning || profile.IsBackgroundRunning);
        StatusButton.ToolTip = "读取当前 frpc 代理状态并追加到运行日志";
        StatusButton.IsEnabled = profile is not null && !profile.IsBusy && (profile.IsRunning || profile.IsBackgroundRunning);
        RunButton.Content = profile?.IsRunning == true ? "■ 停止" : "▶ 运行";
        RunButton.IsEnabled = profile is not null && !profile.IsBusy && !profile.IsBackgroundConfigured;
        UpdateDiagnosticPanel(profile);
        RefreshLogText(profile);
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
        DiagnosticPanel.Background = isInfo ? (System.Windows.Media.Brush)FindResource("AccentSoft")
            : isWarning ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 248, 231))
            : (System.Windows.Media.Brush)FindResource("DangerSoft");
        DiagnosticPanel.BorderBrush = isInfo ? (System.Windows.Media.Brush)FindResource("Accent")
            : isWarning ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 215, 165))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(243, 205, 209));
        DiagnosticTitleText.Foreground = isInfo ? (System.Windows.Media.Brush)FindResource("Accent")
            : isWarning ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(157, 105, 30))
            : (System.Windows.Media.Brush)FindResource("Danger");
        DiagnosticDetailText.Foreground = isInfo ? (System.Windows.Media.Brush)FindResource("TextSecondary")
            : isWarning ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(112, 87, 43))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(140, 77, 85));
    }

    private void RefreshLogText(TunnelProfile? profile)
    {
        var selectedStart = LogTextBox.SelectionStart;
        var selectedLength = LogTextBox.SelectionLength;
        LogTextBox.Text = profile is null ? string.Empty : string.Join(Environment.NewLine, profile.Logs);
        LogEmptyText.Visibility = string.IsNullOrEmpty(LogTextBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        CopyLogButton.IsEnabled = !string.IsNullOrEmpty(LogTextBox.Text);

        if (selectedLength > 0 && selectedStart < LogTextBox.Text.Length)
        {
            LogTextBox.Select(selectedStart, Math.Min(selectedLength, LogTextBox.Text.Length - selectedStart));
            return;
        }

        LogTextBox.Dispatcher.BeginInvoke(() => LogTextBox.ScrollToEnd());
    }

    private ContextMenu CreateLogMenu()
    {
        var menu = new ContextMenu { Style = (Style)FindResource("FrpContextMenu") };
        var copy = new MenuItem { Header = "复制(C)", InputGestureText = "Ctrl+C", Style = (Style)FindResource("FrpMenuItem") };
        var copyAll = new MenuItem { Header = "复制全部", Style = (Style)FindResource("FrpMenuItemCompact") };
        var selectAll = new MenuItem { Header = "全选(A)", InputGestureText = "Ctrl+A", Style = (Style)FindResource("FrpMenuItem") };

        copy.Click += (_, _) => CopyLogText(LogTextBox.SelectedText);
        copyAll.Click += (_, _) => CopyLogText(LogTextBox.Text);
        selectAll.Click += (_, _) => LogTextBox.SelectAll();
        menu.Items.Add(copy);
        menu.Items.Add(copyAll);
        menu.Items.Add(selectAll);
        menu.Opened += (_, _) =>
        {
            copy.IsEnabled = LogTextBox.SelectionLength > 0;
            copyAll.IsEnabled = LogTextBox.Text.Length > 0;
            selectAll.IsEnabled = LogTextBox.Text.Length > 0;
        };
        return menu;
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        CopyLogText(LogTextBox.SelectedText);
    }

    private void CopyDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null || !SelectedProfile.HasDiagnostic) return;
        CopyLogText(SelectedProfile.DiagnosticCopyText);
    }

    private void CopyLogText(string text)
    {
        if (string.IsNullOrEmpty(text)) text = LogTextBox.Text;
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "复制日志失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private string ResolveFrpcPath()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "assets", "frpc.exe");
        if (File.Exists(bundled)) return bundled;
        var direct = Path.Combine(AppContext.BaseDirectory, "frpc.exe");
        if (File.Exists(direct)) return direct;
        throw new FileNotFoundException("找不到内置 frpc.exe，请重新构建或检查 assets 目录。", bundled);
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isClosing) return;
        SaveWindowLayout();
        if (!_allowExit && _settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        var running = Profiles.Where(profile => profile.IsRunning).ToArray();
        if (running.Length == 0) return;
        e.Cancel = true;
        _isClosing = true;
        try
        {
            await _processManager.StopAllAsync(running);
            Close();
        }
        catch (Exception exception)
        {
            _isClosing = false;
            MessageBox.Show(this, exception.Message, "停止实例失败", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show(this, exception.Message, "读取后台实例状态失败", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            // A transient Windows service query failure should not interrupt the UI timer.
        }
    }

    private void SystemEvents_SessionEnding(object? sender, SessionEndingEventArgs e) => _allowExit = true;

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed record WindowLayout(double Left, double Top, double Width, double Height, bool IsMaximized);
}
