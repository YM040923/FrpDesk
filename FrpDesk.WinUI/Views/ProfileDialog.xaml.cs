using System.Diagnostics;
using System.IO;
using System.Text;
using FrpDesk.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Forms = System.Windows.Forms;

namespace FrpDesk.Views;

public sealed partial class ProfileDialog : ContentDialog
{
    private readonly string _frpcPath;
    private readonly string _initialName;
    private readonly string _initialConfig;
    private bool _isValidating;
    private bool _allowClose;
    private DialogAction _pendingAction;

    public ProfileDialog(string frpcPath, TunnelProfile? profile = null)
    {
        InitializeComponent();
        _frpcPath = frpcPath;
        WindowHeading = profile is null ? "添加实例" : "编辑实例";
        NameBox.Text = profile?.Name ?? string.Empty;
        ConfigBox.Text = profile?.Config ?? string.Empty;
        _initialName = NameBox.Text;
        _initialConfig = ConfigText;
        DataContext = this;
        UpdateConfigStats();
    }

    public string WindowHeading { get; }
    public string ProfileName => NameBox.Text.Trim();
    public string ConfigText => TomlText.NormalizeLineEndings(ConfigBox.Text);
    public bool SaveRequested { get; private set; }

    private void ConfigBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateConfigStats();

    private void UpdateConfigStats() => ConfigStatsText.Text = $"{ConfigBox.Text.Length:N0} 字符";

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        using var picker = new Forms.OpenFileDialog
        {
            Title = "选择 frpc TOML 配置",
            Filter = "TOML 配置 (*.toml)|*.toml|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog() != Forms.DialogResult.OK) return;

        var info = new FileInfo(picker.FileName);
        if (info.Length > 1024 * 1024)
        {
            SetStatus("配置文件不能超过 1 MB。", isError: true);
            return;
        }

        try
        {
            ConfigBox.Text = TomlText.NormalizeLineEndings(File.ReadAllText(picker.FileName));
            SetStatus("已导入配置文件。", isError: false);
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, isError: true);
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ConfigBox.Text)) return;
        _pendingAction = DialogAction.Clear;
        ShowAction("确定清空当前 TOML 配置吗？");
    }

    private async void Validate_Click(object sender, RoutedEventArgs e)
    {
        if (_isValidating || !ValidateInput(requireName: false)) return;

        _isValidating = true;
        ValidateButton.IsEnabled = false;
        SaveProfileButton.IsEnabled = false;
        SetStatus("正在检查配置…", isError: false);
        var tempPath = Path.Combine(Path.GetTempPath(), $"frpdesk-verify-{Guid.NewGuid():N}.toml");
        Process? process = null;
        try
        {
            await File.WriteAllTextAsync(tempPath, ConfigText, new UTF8Encoding(false));
            var startInfo = new ProcessStartInfo
            {
                FileName = _frpcPath,
                WorkingDirectory = Path.GetDirectoryName(tempPath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add("verify");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(tempPath);
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动 frpc 配置检查。");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();
            SetStatus(process.ExitCode == 0
                ? "配置检查通过，可以保存并运行。"
                : string.IsNullOrWhiteSpace(error) ? output : error,
                isError: process.ExitCode != 0);
        }
        catch (TimeoutException)
        {
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException) { }
            SetStatus("配置检查超时。", isError: true);
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, isError: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            process?.Dispose();
            _isValidating = false;
            ValidateButton.IsEnabled = true;
            SaveProfileButton.IsEnabled = true;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (TrySave()) Hide();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        SaveRequested = false;
        Hide();
    }

    private bool TrySave()
    {
        if (!ValidateInput(requireName: true)) return false;

        SaveRequested = true;
        _allowClose = true;
        return true;
    }

    private void Save_KeyboardAcceleratorInvoked(
        Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (TrySave()) Hide();
        args.Handled = true;
    }

    private void Close_KeyboardAcceleratorInvoked(
        Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        Hide();
        args.Handled = true;
    }

    private bool ValidateInput(bool requireName)
    {
        if (requireName && string.IsNullOrWhiteSpace(ProfileName))
        {
            SetStatus("请给这个实例起一个名称。", isError: true);
            NameBox.Focus(FocusState.Programmatic);
            return false;
        }

        if (string.IsNullOrWhiteSpace(ConfigText))
        {
            SetStatus("请粘贴或导入 TOML 配置。", isError: true);
            ConfigBox.Focus(FocusState.Programmatic);
            return false;
        }

        if (ConfigText.Length > 1024 * 1024)
        {
            SetStatus("配置不能超过 1 MB。", isError: true);
            return false;
        }

        return true;
    }

    private void SetStatus(string message, bool isError)
    {
        ErrorText.Text = message;
        ErrorText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources[
            isError ? "AlertBrush" : "SuccessBrush"];
    }

    private void Dialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (_allowClose || !IsDirty) return;
        args.Cancel = true;
        _pendingAction = DialogAction.Close;
        ShowAction("配置有未保存的修改，要保存后再关闭吗？");
    }

    private void ShowAction(string message)
    {
        ActionText.Text = message;
        ActionPanel.Visibility = Visibility.Visible;
        ActionPrimaryButton.Content = _pendingAction == DialogAction.Clear ? "清空" : "保存并关闭";
        ActionSecondaryButton.Content = _pendingAction == DialogAction.Clear ? "取消清空" : "放弃修改";
    }

    private void ActionPrimary_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingAction == DialogAction.Clear)
        {
            ConfigBox.Text = string.Empty;
            ActionPanel.Visibility = Visibility.Collapsed;
            SetStatus("配置已清空。", isError: false);
            ConfigBox.Focus(FocusState.Programmatic);
            return;
        }

        if (!TrySave()) return;
        Hide();
    }

    private void ActionSecondary_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingAction == DialogAction.Clear)
        {
            ActionCancel_Click(sender, e);
            return;
        }

        SaveRequested = false;
        _allowClose = true;
        Hide();
    }

    private void ActionCancel_Click(object sender, RoutedEventArgs e)
    {
        _pendingAction = DialogAction.None;
        ActionPanel.Visibility = Visibility.Collapsed;
    }

    private bool IsDirty =>
        !string.Equals(NameBox.Text, _initialName, StringComparison.Ordinal)
        || !string.Equals(ConfigText, _initialConfig, StringComparison.Ordinal);

    private enum DialogAction
    {
        None,
        Close,
        Clear
    }
}
