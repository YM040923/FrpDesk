using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace FrpDesk.Models;

public enum DiagnosticSeverity
{
    None,
    Info,
    Warning,
    Error
}

public enum TunnelProxyStatus
{
    Unstarted,
    Running,
    Failed
}

public sealed record DiagnosticSnapshot(
    string Summary,
    string Detail,
    DiagnosticSeverity Severity,
    int? LastExitCode,
    DateTimeOffset? LastFailureAt);

public sealed class TunnelProfile : INotifyPropertyChanged
{
    private string _name;
    private string _config = "";
    private string _state = "已停止";
    private bool _isBusy;
    private bool _isBackgroundConfigured;
    private bool _isBackgroundRunning;
    private string _backgroundStatus = "后台已停止";
    private string _diagnosticSummary = "";
    private string _diagnosticDetail = "";
    private DiagnosticSeverity _diagnosticSeverity;
    private int? _lastExitCode;
    private DateTimeOffset? _lastFailureAt;
    private int _logLineLimit = 400;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _stoppedAt;
    private DateTimeOffset? _lastCheckedAt;
    private int _proxyStatusVersion;
    private readonly Dictionary<string, TunnelProxyStatus> _proxyStatus = new(StringComparer.OrdinalIgnoreCase);

    // frpc 0.71 每条隧道的生命周期都打在 [名称] 后面的这几个短语上，实测自 assets/frpc.exe 的字符串表。
    private static readonly Regex ProxyEvent = new(
        @"\[(?<name>[^\[\]\s]{1,64})\]\s*(?<event>start proxy success|start error|is already registered|is duplicated|stopped|error)",
        RegexOptions.Compiled);

    public TunnelProfile(Guid id, string name, string config)
    {
        Id = id;
        _name = name;
        Config = config;
    }

    public Guid Id { get; }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Config
    {
        get => _config;
        set => _config = TomlText.NormalizeLineEndings(value);
    }
    public string State { get => _state; set { if (Set(ref _state, value)) { OnPropertyChanged(nameof(IsRunning)); OnPropertyChanged(nameof(IsActive)); OnPropertyChanged(nameof(DisplayState)); } } }
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }
    public bool IsBackgroundConfigured { get => _isBackgroundConfigured; set { if (Set(ref _isBackgroundConfigured, value)) OnPropertyChanged(nameof(DisplayState)); } }
    public bool IsBackgroundRunning { get => _isBackgroundRunning; set { if (Set(ref _isBackgroundRunning, value)) { OnPropertyChanged(nameof(IsActive)); OnPropertyChanged(nameof(DisplayState)); } } }
    public string BackgroundStatus { get => _backgroundStatus; set { if (Set(ref _backgroundStatus, value)) OnPropertyChanged(nameof(DisplayState)); } }
    public string DiagnosticSummary { get => _diagnosticSummary; private set => Set(ref _diagnosticSummary, value); }
    public string DiagnosticDetail { get => _diagnosticDetail; private set => Set(ref _diagnosticDetail, value); }
    public DiagnosticSeverity DiagnosticSeverity { get => _diagnosticSeverity; private set => Set(ref _diagnosticSeverity, value); }
    public int? LastExitCode { get => _lastExitCode; private set => Set(ref _lastExitCode, value); }
    public DateTimeOffset? LastFailureAt { get => _lastFailureAt; private set => Set(ref _lastFailureAt, value); }
    public bool HasDiagnostic => DiagnosticSeverity != DiagnosticSeverity.None && !string.IsNullOrWhiteSpace(DiagnosticSummary);
    public bool IsRunning => State == "进程运行中";
    public bool IsActive => IsRunning || IsBackgroundRunning;
    public string DisplayState => IsBackgroundConfigured ? BackgroundStatus : State;
    public string DiagnosticCopyText
    {
        get
        {
            var lines = new List<string> { $"实例：{Name}", $"诊断：{DiagnosticSummary}" };
            if (!string.IsNullOrWhiteSpace(DiagnosticDetail)) lines.Add($"详情：{DiagnosticDetail}");
            if (LastExitCode is not null) lines.Add($"退出代码：{LastExitCode}");
            if (LastFailureAt is not null) lines.Add($"发生时间：{LastFailureAt.Value:yyyy-MM-dd HH:mm:ss zzz}");
            return string.Join(Environment.NewLine, lines);
        }
    }
    public ObservableCollection<string> Logs { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetDiagnostic(
        string summary,
        string detail,
        DiagnosticSeverity severity,
        int? exitCode = null,
        DateTimeOffset? occurredAt = null)
    {
        DiagnosticSummary = LogText.Clean(summary);
        DiagnosticDetail = LogText.Clean(detail);
        DiagnosticSeverity = severity;
        LastExitCode = exitCode;
        LastFailureAt = occurredAt ?? DateTimeOffset.Now;
        OnPropertyChanged(nameof(HasDiagnostic));
        OnPropertyChanged(nameof(DiagnosticCopyText));
    }

    public void ApplyDiagnostic(DiagnosticSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            ClearDiagnostic();
            return;
        }

        SetDiagnostic(snapshot.Summary, snapshot.Detail, snapshot.Severity, snapshot.LastExitCode, snapshot.LastFailureAt);
    }

    public DiagnosticSnapshot? CreateDiagnosticSnapshot() => HasDiagnostic
        ? new DiagnosticSnapshot(DiagnosticSummary, DiagnosticDetail, DiagnosticSeverity, LastExitCode, LastFailureAt)
        : null;

    public void ClearDiagnostic()
    {
        DiagnosticSummary = "";
        DiagnosticDetail = "";
        DiagnosticSeverity = DiagnosticSeverity.None;
        LastExitCode = null;
        LastFailureAt = null;
        OnPropertyChanged(nameof(HasDiagnostic));
        OnPropertyChanged(nameof(DiagnosticCopyText));
    }

    public void AddLog(string message)
    {
        var line = LogText.Normalize(message);
        if (line.Length == 0) return;
        Logs.Add(line);
        while (Logs.Count > _logLineLimit) Logs.RemoveAt(0);
        ReadProxyEvent(line);
    }

    public int ProxyStatusVersion => _proxyStatusVersion;

    public TunnelProxyStatus StatusOf(string name) =>
        _proxyStatus.GetValueOrDefault(name, TunnelProxyStatus.Unstarted);

    public void ResetProxyStatus()
    {
        if (_proxyStatus.Count == 0) return;
        _proxyStatus.Clear();
        BumpProxyStatus();
    }

    public DateTimeOffset? StartedAt => _startedAt;
    public DateTimeOffset? StoppedAt => _stoppedAt;
    public DateTimeOffset? LastCheckedAt => _lastCheckedAt;

    public void MarkStarted(DateTimeOffset? at = null)
    {
        _startedAt = at ?? DateTimeOffset.Now;
        _stoppedAt = null;
        _lastCheckedAt = DateTimeOffset.Now;
        ResetProxyStatus();
    }

    public void MarkStopped()
    {
        _startedAt = null;
        _stoppedAt = DateTimeOffset.Now;
        _lastCheckedAt = _stoppedAt;
    }

    public void MarkChecked(DateTimeOffset? at = null)
    {
        _lastCheckedAt = at ?? DateTimeOffset.Now;
    }

    /// <summary>概览页那一行「已连续运行 / 已停止」的文案，没跑过就留空。</summary>
    public string UptimeText
    {
        get
        {
            if (_startedAt is { } start) return "已连续运行 " + Elapsed(DateTimeOffset.Now - start);
            if (_stoppedAt is { } stop) return "已停止 " + Elapsed(DateTimeOffset.Now - stop);
            return "尚未运行";
        }
    }

    public string LastCheckedText
    {
        get
        {
            if (_lastCheckedAt is not { } check) return "尚未检查";
            var local = check.LocalDateTime;
            var time = local.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            return local.Date == DateTime.Today ? $"最近检查 {time}" : $"最近检查 {local:MM-dd} {time}";
        }
    }

    private static string Elapsed(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays} 天 {span.Hours:00}:{span.Minutes:00}";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} 小时 {span.Minutes:00} 分";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes} 分钟";
        return $"{Math.Max(0, (int)span.TotalSeconds)} 秒";
    }

    private void ReadProxyEvent(string line)
    {
        var message = LogText.TryParse(line, out _, out _, out _, out var parsed) ? parsed : line;
        var match = ProxyEvent.Match(message);
        if (!match.Success) return;
        var status = match.Groups["event"].Value switch
        {
            "start proxy success" => TunnelProxyStatus.Running,
            "stopped" => TunnelProxyStatus.Unstarted,
            _ => TunnelProxyStatus.Failed
        };
        var name = match.Groups["name"].Value;
        if (_proxyStatus.TryGetValue(name, out var current) && current == status) return;
        _proxyStatus[name] = status;
        BumpProxyStatus();
    }

    private void BumpProxyStatus()
    {
        _proxyStatusVersion++;
        OnPropertyChanged(nameof(ProxyStatusVersion));
    }

    public void SetLogLineLimit(int limit)
    {
        _logLineLimit = limit switch
        {
            <= 200 => 200,
            <= 400 => 400,
            _ => 800
        };
        while (Logs.Count > _logLineLimit) Logs.RemoveAt(0);
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        if (name is nameof(DiagnosticSummary) or nameof(DiagnosticDetail) or nameof(DiagnosticSeverity) or nameof(LastExitCode) or nameof(LastFailureAt))
        {
            OnPropertyChanged(nameof(HasDiagnostic));
            OnPropertyChanged(nameof(DiagnosticCopyText));
        }
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
