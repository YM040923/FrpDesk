using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FrpDesk.Models;

public sealed class TunnelEndpoint : INotifyPropertyChanged
{
    private TunnelProxyStatus _status;

    public TunnelEndpoint(string name, string type, string localAddress, string remoteAddress)
    {
        Name = name;
        Type = type;
        LocalAddress = localAddress;
        RemoteAddress = remoteAddress;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index { get; set; }
    public string Name { get; }
    public string Type { get; }
    public string LocalAddress { get; }
    public string RemoteAddress { get; }
    public string LocalHost { get; set; } = "127.0.0.1";
    public string LocalPortText { get; set; } = "";
    public string RemotePortText { get; set; } = "";
    public string PublicPortText { get; set; } = "";
    public string ServerAddress { get; set; } = "";
    public string ServerEndpoint { get; set; } = "";
    public IReadOnlyList<string> CustomDomains { get; set; } = [];
    public string Subdomain { get; set; } = "";
    public bool UsesSubdomain { get; set; }
    public string Note { get; set; } = "";
    public string RemoteCopyText { get; set; } = "";
    public TunnelProxyStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public string DisplayType => Type.ToUpperInvariant();
    public string StatusText => Status switch
    {
        TunnelProxyStatus.Running => "运行中",
        TunnelProxyStatus.Failed => "启动异常",
        _ => "未启动"
    };
    public string DomainText => UsesSubdomain ? Subdomain : string.Join(Environment.NewLine, CustomDomains);
    public string CopyActionLabel => Type.Equals("http", StringComparison.OrdinalIgnoreCase)
        || Type.Equals("https", StringComparison.OrdinalIgnoreCase)
            ? "复制链接"
            : "复制地址";
    public bool SupportsStructuredEditing => Type.Equals("tcp", StringComparison.OrdinalIgnoreCase)
        || Type.Equals("udp", StringComparison.OrdinalIgnoreCase)
        || Type.Equals("http", StringComparison.OrdinalIgnoreCase)
        || Type.Equals("https", StringComparison.OrdinalIgnoreCase);
    public bool HasCopyableRemoteAddress => !string.IsNullOrWhiteSpace(RemoteCopyText);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed record TunnelEndpointEdit(
    string Name,
    string Type,
    string LocalHost,
    int LocalPort,
    int? RemotePort,
    IReadOnlyList<string> Domains,
    int? PublicPort,
    bool UsesSubdomain,
    string Note);
