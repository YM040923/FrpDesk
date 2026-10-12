using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FrpDesk.Models;

public enum DiagnosticSeverity
{
    None,
    Info,
    Warning,
    Error
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

    public TunnelProfile(Guid id, string name, string config)
    {
        Id = id;
        _name = name;
        Config = config;
    }

    public Guid Id { get; }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Config { get; set; }
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
        DiagnosticSummary = summary;
        DiagnosticDetail = detail;
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

    public void AddLog(string line)
    {
        Logs.Add(line);
        while (Logs.Count > _logLineLimit) Logs.RemoveAt(0);
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
