namespace FrpDesk.Models;

public sealed class TunnelEndpoint
{
    public TunnelEndpoint(string name, string type, string localAddress, string remoteAddress)
    {
        Name = name;
        Type = type;
        LocalAddress = localAddress;
        RemoteAddress = remoteAddress;
    }

    public string Name { get; }
    public string Type { get; }
    public string LocalAddress { get; }
    public string RemoteAddress { get; }
    public string DisplayType => Type.ToUpperInvariant();
    public bool HasCopyableRemoteAddress => !string.IsNullOrWhiteSpace(RemoteAddress)
        && !RemoteAddress.StartsWith("需", StringComparison.Ordinal)
        && !RemoteAddress.StartsWith("未", StringComparison.Ordinal)
        && !RemoteAddress.StartsWith("请", StringComparison.Ordinal)
        && !RemoteAddress.StartsWith("远端端口", StringComparison.Ordinal)
        && !RemoteAddress.Contains("需要服务端域名", StringComparison.Ordinal);
}
