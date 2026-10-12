using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using FrpDesk.Models;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using TextBox = System.Windows.Controls.TextBox;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using TextDataFormat = System.Windows.TextDataFormat;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using MessageBox = System.Windows.MessageBox;

namespace FrpDesk.Views;

public partial class ProfileDialog : Window
{
    private readonly string _frpcPath;
    private bool _isValidating;
    private bool _allowClose;
    private string _initialName = "";
    private string _initialConfig = "";

    public ProfileDialog(string frpcPath, TunnelProfile? profile = null)
    {
        InitializeComponent();
        _frpcPath = frpcPath;
        WindowHeading = profile is not null ? "编辑实例" : "添加实例";
        NameBox.Text = profile?.Name ?? "";
        ConfigBox.Text = profile?.Config ?? "";
        _initialName = NameBox.Text;
        _initialConfig = ConfigBox.Text;
        DataContext = this;
        NameBox.ContextMenu = CreateEditMenu(NameBox);
        ConfigBox.ContextMenu = CreateEditMenu(ConfigBox);
        UpdateConfigStats();
        Loaded += (_, _) => NameBox.Focus();
    }

    public string WindowHeading { get; }
    public string ProfileName => NameBox.Text.Trim();
    public string ConfigText => ConfigBox.Text;

    private static ContextMenu CreateEditMenu(TextBox target)
    {
        var menu = new ContextMenu { Style = (Style)Application.Current.FindResource("FrpContextMenu") };
        var cut = CreateEditItem("剪切(T)", "Ctrl+X", () => { if (target.SelectionLength > 0) target.Cut(); });
        var copy = CreateEditItem("复制(C)", "Ctrl+C", () => { if (target.SelectionLength > 0) target.Copy(); });
        var paste = CreateEditItem("粘贴(P)", "Ctrl+V", () => target.Paste());
        var selectAll = CreateEditItem("全选(A)", "Ctrl+A", target.SelectAll);
        menu.Items.Add(cut);
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        menu.Items.Add(selectAll);
        target.ContextMenuOpening += (_, _) =>
        {
            cut.IsEnabled = !target.IsReadOnly && target.SelectionLength > 0;
            copy.IsEnabled = target.SelectionLength > 0;
            try
            {
                paste.IsEnabled = !target.IsReadOnly && Clipboard.ContainsText(TextDataFormat.UnicodeText);
            }
            catch (ExternalException)
            {
                paste.IsEnabled = false;
            }
            selectAll.IsEnabled = target.Text.Length > 0;
        };
        return menu;
    }

    private void ConfigBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateConfigStats();

    private void UpdateConfigStats()
    {
        ConfigStatsText.Text = $"{ConfigText.Length:N0} 字符";
    }

    private static MenuItem CreateEditItem(string header, string gesture, Action action)
    {
        var item = new MenuItem
        {
            Header = header,
            InputGestureText = gesture,
            IsEnabled = true,
            Style = (Style)Application.Current.FindResource("FrpMenuItem")
        };
        item.Click += (_, _) => action();
        return item;
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "选择 frpc TOML 配置",
            Filter = "TOML 配置 (*.toml)|*.toml|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != true) return;
        var info = new FileInfo(picker.FileName);
        if (info.Length > 1024 * 1024)
        {
            ErrorText.Text = "配置文件不能超过 1 MB。";
            return;
        }

        try
        {
            ConfigBox.Text = File.ReadAllText(picker.FileName);
            ErrorText.Text = "";
        }
        catch (Exception exception)
        {
            ErrorText.Text = exception.Message;
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ConfigBox.Text)) return;
        var answer = MessageBox.Show(this, "确定清空当前 TOML 配置吗？", "清空配置", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        ConfigBox.Clear();
        SetStatus("配置已清空。", isError: false);
        ConfigBox.Focus();
    }

    private async void Validate_Click(object sender, RoutedEventArgs e)
    {
        if (_isValidating) return;
        if (string.IsNullOrWhiteSpace(ConfigText))
        {
            SetStatus("请先粘贴或导入 TOML 配置。", isError: true);
            ConfigBox.Focus();
            return;
        }
        if (ConfigText.Length > 1024 * 1024)
        {
            SetStatus("配置不能超过 1 MB。", isError: true);
            return;
        }

        _isValidating = true;
        ValidateButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
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
            process = new Process { StartInfo = startInfo };
            if (!process.Start()) throw new InvalidOperationException("无法启动 frpc 配置检查。");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var output = (await outputTask).Trim();
            var error = (await errorTask).Trim();
            if (process.ExitCode == 0)
            {
                SetStatus("配置检查通过，可以保存并运行。", isError: false);
            }
            else
            {
                var detail = string.IsNullOrWhiteSpace(error) ? output : error;
                SetStatus(string.IsNullOrWhiteSpace(detail) ? "配置检查失败，请检查 TOML 内容。" : detail, isError: true);
            }
        }
        catch (TimeoutException)
        {
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            SetStatus("配置检查超时。", isError: true);
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, isError: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (IOException) { }
            process?.Dispose();
            _isValidating = false;
            ValidateButton.IsEnabled = true;
            SaveButton.IsEnabled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            ErrorText.Text = "请给这个实例起一个名称。";
            NameBox.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(ConfigText))
        {
            ErrorText.Text = "请粘贴或导入 TOML 配置。";
            ConfigBox.Focus();
            return;
        }
        if (ConfigText.Length > 1024 * 1024)
        {
            ErrorText.Text = "配置不能超过 1 MB。";
            return;
        }

        _allowClose = true;
        DialogResult = true;
    }

    private void SetStatus(string message, bool isError)
    {
        ErrorText.Text = message;
        ErrorText.Foreground = (System.Windows.Media.Brush)FindResource(isError ? "Danger" : "Success");
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Dialog_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || !IsDirty) return;
        e.Cancel = true;
        var answer = MessageBox.Show(this, "配置有未保存的修改，要保存后再关闭吗？", "未保存的修改", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            Save_Click(this, new RoutedEventArgs());
        }
        else if (answer == MessageBoxResult.No)
        {
            _allowClose = true;
            DialogResult = false;
        }
    }

    private bool IsDirty => !string.Equals(NameBox.Text, _initialName, StringComparison.Ordinal)
        || !string.Equals(ConfigBox.Text, _initialConfig, StringComparison.Ordinal);

    private void Dialog_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.S &&
                 (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            Save_Click(sender, e);
            e.Handled = true;
        }
    }
}
