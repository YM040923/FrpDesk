using System.Text.RegularExpressions;
using FrpDesk.Models;

namespace FrpDesk.Services;

public sealed class TomlEndpointParser
{
    private static readonly Regex KeyValuePattern = new(
        "^\\s*(?<key>[A-Za-z0-9_-]+)\\s*=\\s*(?<value>.+?)\\s*$",
        RegexOptions.Compiled);

    public IReadOnlyList<TunnelEndpoint> Parse(string config)
    {
        if (string.IsNullOrWhiteSpace(config)) return [];

        var endpoints = new List<TunnelEndpoint>();
        var global = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;

        foreach (var rawLine in config.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var line = RemoveInlineComment(rawLine).Trim();
            if (line.Length == 0) continue;

            if (line.Equals("[[proxies]]", StringComparison.OrdinalIgnoreCase))
            {
                AddProxy(endpoints, global, current);
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            if (line.StartsWith("[[", StringComparison.Ordinal) || line.StartsWith("[", StringComparison.Ordinal))
            {
                AddProxy(endpoints, global, current);
                current = null;
                continue;
            }

            var match = KeyValuePattern.Match(line);
            if (!match.Success) continue;
            var key = NormalizeKey(match.Groups["key"].Value);
            var value = match.Groups["value"].Value.Trim();
            if (current is null) global[key] = value;
            else current[key] = value;
        }

        AddProxy(endpoints, global, current);
        return endpoints;
    }

    private static void AddProxy(List<TunnelEndpoint> endpoints, IReadOnlyDictionary<string, string> global, Dictionary<string, string>? proxy)
    {
        if (proxy is null || proxy.Count == 0) return;

        var name = ReadString(proxy, "name");
        if (string.IsNullOrWhiteSpace(name)) name = $"代理 {endpoints.Count + 1}";
        var type = ReadString(proxy, "type");
        if (string.IsNullOrWhiteSpace(type)) type = "tcp";

        var localIp = ReadString(proxy, "localip");
        if (string.IsNullOrWhiteSpace(localIp)) localIp = "127.0.0.1";
        var localPort = ReadString(proxy, "localport");
        var localAddress = string.IsNullOrWhiteSpace(localPort) ? localIp : $"{localIp}:{localPort}";

        var serverAddress = ReadString(global, "serveraddr");
        var remoteAddress = BuildRemoteAddress(type, serverAddress, proxy, name);
        endpoints.Add(new TunnelEndpoint(name, type, localAddress, remoteAddress));
    }

    private static string BuildRemoteAddress(string type, string serverAddress, IReadOnlyDictionary<string, string> proxy, string name)
    {
        var normalizedType = type.Trim().ToLowerInvariant();
        var remotePort = ReadString(proxy, "remoteport");
        if (normalizedType is "tcp" or "udp")
        {
            if (string.IsNullOrWhiteSpace(remotePort)) return "未配置远端端口";
            return string.IsNullOrWhiteSpace(serverAddress)
                ? $"远端端口：{remotePort}"
                : $"{serverAddress}:{remotePort}";
        }

        if (normalizedType is "http" or "https")
        {
            var domains = ReadArray(proxy, "customdomains");
            var scheme = normalizedType + "://";
            if (domains.Count > 0) return string.Join("、", domains.Select(domain => scheme + domain));
            var subdomain = ReadString(proxy, "subdomain");
            if (!string.IsNullOrWhiteSpace(subdomain)) return $"{scheme}{subdomain}（需要服务端域名）";
            return "未配置域名";
        }

        if (normalizedType is "stcp" or "sudp" or "xtcp")
        {
            return $"需配合访问端（访问名称：{name}）";
        }

        return string.IsNullOrWhiteSpace(remotePort) ? "请按此类型配置访问方式" : $"远端端口：{remotePort}";
    }

    private static string ReadString(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var value)) return string.Empty;
        value = value.Trim();
        if (value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2)
            return value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Trim();
        if (value.StartsWith('\'') && value.EndsWith('\'') && value.Length >= 2)
            return value[1..^1].Trim();
        return value.Trim();
    }

    private static List<string> ReadArray(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var value)) return [];
        value = value.Trim();
        if (value.StartsWith('[') && value.EndsWith(']')) value = value[1..^1];
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim().Trim('"', '\''))
            .Where(item => item.Length > 0)
            .ToList();
    }

    private static string NormalizeKey(string key) => key.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();

    private static string RemoveInlineComment(string line)
    {
        var inQuotes = false;
        var quote = '\0';
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if ((character == '"' || character == '\'') && (index == 0 || line[index - 1] != '\\'))
            {
                if (!inQuotes) { inQuotes = true; quote = character; }
                else if (quote == character) inQuotes = false;
            }
            else if (character == '#' && !inQuotes) return line[..index];
        }
        return line;
    }
}
