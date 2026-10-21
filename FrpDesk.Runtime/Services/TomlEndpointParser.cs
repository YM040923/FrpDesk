using System.Text.RegularExpressions;
using FrpDesk.Models;

namespace FrpDesk.Services;

public sealed class TomlEndpointParser
{
    private const string NotePrefix = "# FrpDesk note:";
    private static readonly Regex KeyValuePattern = new(
        "^\\s*(?<key>[A-Za-z0-9_-]+)\\s*=\\s*(?<value>.+?)\\s*$",
        RegexOptions.Compiled);
    private static readonly Regex FieldPattern = new(
        "^(?<indent>\\s*)(?<key>[A-Za-z0-9_-]+)\\s*=",
        RegexOptions.Compiled);

    public IReadOnlyList<TunnelEndpoint> Parse(string config)
    {
        config = TomlText.NormalizeLineEndings(config);
        if (string.IsNullOrWhiteSpace(config)) return [];

        var lines = SplitLines(config);
        var endpoints = new List<TunnelEndpoint>();
        var global = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        var note = "";
        var index = 0;

        while (index < lines.Count)
        {
            var rawLine = lines[index];
            if (current is not null && TryReadNote(rawLine, out var parsedNote))
            {
                note = parsedNote;
                index++;
                continue;
            }

            var line = RemoveInlineComment(rawLine).Trim();
            if (line.Length == 0)
            {
                index++;
                continue;
            }

            if (line.Equals("[[proxies]]", StringComparison.OrdinalIgnoreCase))
            {
                AddProxy(endpoints, global, current, note);
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                note = "";
                index++;
                continue;
            }

            if (line.StartsWith("[[", StringComparison.Ordinal) || line.StartsWith("[", StringComparison.Ordinal))
            {
                AddProxy(endpoints, global, current, note);
                current = null;
                note = "";
                index++;
                continue;
            }

            var match = KeyValuePattern.Match(line);
            if (!match.Success)
            {
                index++;
                continue;
            }

            var key = NormalizeKey(match.Groups["key"].Value);
            var value = match.Groups["value"].Value.Trim();
            var nextIndex = ReadMultiLineValue(lines, index, out var fullValue);
            if (fullValue != null) value = fullValue;
            index = nextIndex;

            if (current is null) global[key] = value;
            else current[key] = value;
        }

        AddProxy(endpoints, global, current, note);
        return endpoints;
    }

    private static int ReadMultiLineValue(IReadOnlyList<string> lines, int index, out string? combinedValue)
    {
        combinedValue = null;
        var line = RemoveInlineComment(lines[index]).Trim();
        var match = KeyValuePattern.Match(line);
        if (!match.Success) return index + 1;

        var value = match.Groups["value"].Value.Trim();
        if (!value.StartsWith('[')) return index + 1;

        var balance = BracketBalance(value);
        if (balance <= 0) return index + 1;

        var parts = new List<string> { value };
        var next = index + 1;
        while (balance > 0 && next < lines.Count)
        {
            var trimmed = RemoveInlineComment(lines[next]).Trim();
            if (IsTableHeader(lines[next])) break;
            balance += BracketBalance(trimmed);
            parts.Add(trimmed);
            next++;
        }
        combinedValue = string.Join(" ", parts);
        return next;
    }

    private static int BracketBalance(string value)
    {
        var depth = 0;
        var inString = false;
        var quote = '\0';
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (inString)
            {
                if (c == '\\' && quote == '"') { i++; continue; }
                if (c == quote) inString = false;
                continue;
            }
            if (c is '"' or '\'') { inString = true; quote = c; continue; }
            if (c == '[') depth++;
            else if (c == ']') depth--;
        }
        return depth;
    }

    public string GetServerEndpoint(string config)
    {
        var global = ReadGlobal(config);
        var address = ReadString(global, "serveraddr");
        if (string.IsNullOrWhiteSpace(address)) return "";
        var port = ReadString(global, "serverport");
        return string.IsNullOrWhiteSpace(port) ? address : $"{address}:{port}";
    }

    public bool HasServerAddress(string config) =>
        !string.IsNullOrWhiteSpace(ReadString(ReadGlobal(config), "serveraddr"));

    public int? GetGlobalPort(string config, string key)
    {
        var values = ReadGlobal(config);
        return int.TryParse(ReadString(values, key), out var port) && port is > 0 and <= 65535
            ? port
            : null;
    }

    public string UpdateProxy(string config, int index, TunnelEndpointEdit edit)
    {
        ValidateEdit(edit);
        config = TomlText.NormalizeLineEndings(config);
        var lines = SplitLines(config);
        var starts = FindProxyStarts(lines);
        if (index < 0 || index >= starts.Count)
            throw new ArgumentOutOfRangeException(nameof(index), "找不到要编辑的隧道。");

        var existingEndpoints = Parse(config);
        ValidateUniqueName(existingEndpoints, edit.Name, index);

        var start = starts[index];
        var end = index + 1 < starts.Count ? starts[index + 1] : lines.Count;
        var rootEnd = FindRootEnd(lines, start, end);
        SetProxyKey(lines, start, ref rootEnd, "name", Quote(edit.Name));
        SetProxyKey(lines, start, ref rootEnd, "type", Quote(NormalizeType(edit.Type)));
        SetProxyKey(lines, start, ref rootEnd, "localIP", Quote(edit.LocalHost));
        SetProxyKey(lines, start, ref rootEnd, "localPort", edit.LocalPort.ToString());

        var type = NormalizeType(edit.Type);
        if (type is "tcp" or "udp")
        {
            SetProxyKey(lines, start, ref rootEnd, "remotePort", edit.RemotePort!.Value.ToString());
            SetProxyKey(lines, start, ref rootEnd, "customDomains", null);
            SetProxyKey(lines, start, ref rootEnd, "subdomain", null);
        }
        else
        {
            SetProxyKey(lines, start, ref rootEnd, "remotePort", null);
            SetProxyKey(lines, start, ref rootEnd, "customDomains",
                edit.UsesSubdomain || edit.Domains.Count == 0 ? null : FormatArray(edit.Domains));
            SetProxyKey(lines, start, ref rootEnd, "subdomain",
                edit.UsesSubdomain ? Quote(edit.Domains[0]) : null);
            SetGlobalPort(lines, type == "http" ? "vhostHTTPPort" : "vhostHTTPSPort", edit.PublicPort!.Value,
                type == "http" ? 80 : 443);
        }

        SetNote(lines, start, ref rootEnd, edit.Note);
        return string.Join(DetectNewLine(config), lines);
    }

    public string AppendProxy(string config, TunnelEndpointEdit edit)
    {
        ValidateEdit(edit);
        config = TomlText.NormalizeLineEndings(config);
        var existingEndpoints = Parse(config);
        ValidateUniqueName(existingEndpoints, edit.Name, null);

        var type = NormalizeType(edit.Type);
        if (type is "http" or "https")
            config = SetGlobalPort(config, type == "http" ? "vhostHTTPPort" : "vhostHTTPSPort",
                edit.PublicPort!.Value, type == "http" ? 80 : 443);

        var newLine = DetectNewLine(config);
        var block = string.Join(newLine, BuildProxyLines(edit));
        if (string.IsNullOrEmpty(config)) return block;

        var result = config;
        if (!result.EndsWith('\n')) result += newLine;
        if (!result.EndsWith(newLine + newLine, StringComparison.Ordinal)) result += newLine;
        return result + block;
    }

    private static void ValidateUniqueName(IReadOnlyList<TunnelEndpoint> endpoints, string name, int? excludeIndex)
    {
        var trimmed = name.Trim();
        if (endpoints.Any(endpoint => endpoint.Index != excludeIndex && string.Equals(endpoint.Name, trimmed, StringComparison.Ordinal)))
            throw new ArgumentException($"隧道名称“{trimmed}”已存在，请使用其他名称。", nameof(name));
    }

    private static void AddProxy(
        List<TunnelEndpoint> endpoints,
        IReadOnlyDictionary<string, string> global,
        Dictionary<string, string>? proxy,
        string note)
    {
        if (proxy is null) return;

        var name = ReadString(proxy, "name");
        if (string.IsNullOrWhiteSpace(name)) name = $"代理 {endpoints.Count + 1}";
        var type = ReadString(proxy, "type");
        if (string.IsNullOrWhiteSpace(type)) type = "tcp";

        var localIp = ReadString(proxy, "localip");
        if (string.IsNullOrWhiteSpace(localIp)) localIp = "127.0.0.1";
        var localPort = ReadString(proxy, "localport");
        var localAddress = string.IsNullOrWhiteSpace(localPort) ? localIp : $"{localIp}:{localPort}";
        var serverAddress = ReadString(global, "serveraddr");
        var serverPort = ReadString(global, "serverport");
        var httpPort = ReadString(global, "vhosthttpport");
        var httpsPort = ReadString(global, "vhosthttpsport");
        var customDomains = ReadArray(proxy, "customdomains");
        var subdomain = ReadString(proxy, "subdomain");
        var usesSubdomain = customDomains.Count == 0 && !string.IsNullOrWhiteSpace(subdomain);
        var remotePort = ReadString(proxy, "remoteport");
        var remoteAddress = BuildRemoteAddress(
            type,
            serverAddress,
            remotePort,
            customDomains,
            subdomain,
            httpPort,
            httpsPort);
        var remoteCopyText = BuildRemoteCopyText(
            type,
            serverAddress,
            remotePort,
            customDomains,
            subdomain,
            httpPort,
            httpsPort);
        var publicPort = type.Equals("http", StringComparison.OrdinalIgnoreCase)
            ? EffectivePort(httpPort, 80)
            : type.Equals("https", StringComparison.OrdinalIgnoreCase)
                ? EffectivePort(httpsPort, 443)
                : null;
        var serverEndpoint = string.IsNullOrWhiteSpace(serverAddress)
            ? ""
            : string.IsNullOrWhiteSpace(serverPort) ? serverAddress : $"{serverAddress}:{serverPort}";

        endpoints.Add(new TunnelEndpoint(name, type, localAddress, remoteAddress)
        {
            Index = endpoints.Count,
            LocalHost = localIp,
            LocalPortText = localPort,
            RemotePortText = remotePort,
            PublicPortText = publicPort?.ToString() ?? "",
            ServerAddress = serverAddress,
            ServerEndpoint = serverEndpoint,
            CustomDomains = customDomains,
            Subdomain = subdomain,
            UsesSubdomain = usesSubdomain,
            Note = note,
            RemoteCopyText = remoteCopyText
        });
    }

    private static string BuildRemoteAddress(
        string type,
        string serverAddress,
        string remotePort,
        IReadOnlyList<string> domains,
        string subdomain,
        string httpPort,
        string httpsPort)
    {
        var normalizedType = NormalizeType(type);
        if (normalizedType is "tcp" or "udp")
        {
            if (string.IsNullOrWhiteSpace(remotePort)) return "未配置远端端口";
            return string.IsNullOrWhiteSpace(serverAddress)
                ? "未配置服务器地址"
                : $"{serverAddress}:{remotePort}";
        }

        if (normalizedType is "http" or "https")
        {
            var port = normalizedType == "http" ? httpPort : httpsPort;
            var suffix = PortSuffix(port, normalizedType == "http" ? 80 : 443);
            if (domains.Count > 0)
                return string.Join("、", domains.Select(domain => $"{normalizedType}://{domain}{suffix}"));
            if (!string.IsNullOrWhiteSpace(subdomain))
            {
                if (string.IsNullOrWhiteSpace(serverAddress)) return "需配置服务端域名";
                return $"{normalizedType}://{subdomain}.{serverAddress}{suffix}";
            }
            return "未配置域名";
        }

        if (normalizedType is "stcp" or "sudp" or "xtcp")
            return "需配合访问端配置";

        return string.IsNullOrWhiteSpace(remotePort) ? "请按此类型配置访问方式" : $"远端端口：{remotePort}";
    }

    private static string BuildRemoteCopyText(
        string type,
        string serverAddress,
        string remotePort,
        IReadOnlyList<string> domains,
        string subdomain,
        string httpPort,
        string httpsPort)
    {
        var normalizedType = NormalizeType(type);
        if (normalizedType is "tcp" or "udp")
            return string.IsNullOrWhiteSpace(serverAddress) || string.IsNullOrWhiteSpace(remotePort)
                ? ""
                : $"{serverAddress}:{remotePort}";

        if (normalizedType is not ("http" or "https")) return "";

        var port = normalizedType == "http" ? httpPort : httpsPort;
        var suffix = PortSuffix(port, normalizedType == "http" ? 80 : 443);
        if (domains.Count > 0)
            return string.Join(Environment.NewLine, domains.Select(domain => $"{normalizedType}://{domain}{suffix}"));
        if (!string.IsNullOrWhiteSpace(subdomain) && !string.IsNullOrWhiteSpace(serverAddress))
            return $"{normalizedType}://{subdomain}.{serverAddress}{suffix}";
        return "";
    }

    private static string PortSuffix(string port, int defaultPort) =>
        int.TryParse(port, out var value) && value > 0 && value != defaultPort ? $":{value}" : string.Empty;

    private static int? EffectivePort(string port, int defaultPort) =>
        int.TryParse(port, out var value) && value is > 0 and <= 65535 ? value : defaultPort;

    private static Dictionary<string, string> ReadGlobal(string config)
    {
        config = TomlText.NormalizeLineEndings(config);
        var global = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in config.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var line = RemoveInlineComment(rawLine).Trim();
            if (line.StartsWith("[", StringComparison.Ordinal)) break;
            var match = KeyValuePattern.Match(line);
            if (match.Success) global[NormalizeKey(match.Groups["key"].Value)] = match.Groups["value"].Value.Trim();
        }
        return global;
    }

    private static List<int> FindProxyStarts(IReadOnlyList<string> lines)
    {
        var result = new List<int>();
        for (var index = 0; index < lines.Count; index++)
        {
            if (RemoveInlineComment(lines[index]).Trim().Equals("[[proxies]]", StringComparison.OrdinalIgnoreCase))
                result.Add(index);
        }
        return result;
    }

    private static int FindRootEnd(IReadOnlyList<string> lines, int start, int end)
    {
        for (var index = start + 1; index < end; index++)
            if (IsTableHeader(lines[index])) return index;
        return end;
    }

    private static bool IsTableHeader(string line)
    {
        var trimmed = RemoveInlineComment(line).Trim();
        return trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal);
    }

    private static void SetProxyKey(List<string> lines, int start, ref int rootEnd, string key, string? value) =>
        rootEnd = SetKey(lines, start + 1, rootEnd, key, value);

    private static int SetKey(List<string> lines, int start, int end, string key, string? value)
    {
        var matches = new List<int>();
        for (var index = start; index < end; index++)
        {
            var match = FieldPattern.Match(lines[index]);
            if (match.Success && NormalizeKey(match.Groups["key"].Value) == NormalizeKey(key))
                matches.Add(index);
        }

        if (matches.Count > 1)
            throw new InvalidDataException($"配置中存在重复的 {key} 字段，未修改隧道。");

        if (matches.Count == 1)
        {
            var index = matches[0];
            var rawLine = lines[index];
            var commentIndex = FindInlineCommentIndex(rawLine);
            var comment = commentIndex < 0 ? "" : rawLine[commentIndex..].TrimEnd();
            var field = FieldPattern.Match(rawLine);
            var indent = field.Groups["indent"].Value;
            var spanEnd = FindValueSpanEnd(lines, index, end);
            var removed = spanEnd - index - 1;

            if (value is null)
            {
                if (comment.Length == 0)
                {
                    lines.RemoveRange(index, spanEnd - index);
                    return end - (spanEnd - index);
                }
                lines[index] = comment;
                if (removed > 0) lines.RemoveRange(index + 1, removed);
                return end - removed;
            }

            lines[index] = $"{indent}{key} = {value}{(comment.Length == 0 ? "" : " " + comment)}";
            if (removed > 0) lines.RemoveRange(index + 1, removed);
            return end - removed;
        }

        if (value is null) return end;
        lines.Insert(end, $"{key} = {value}");
        return end + 1;
    }

    private static int FindValueSpanEnd(IReadOnlyList<string> lines, int index, int end)
    {
        var raw = lines[index];
        var commentIndex = FindInlineCommentIndex(raw);
        var valueText = (commentIndex < 0 ? raw : raw[..commentIndex]).Trim();
        var match = KeyValuePattern.Match(valueText);
        if (!match.Success) return index + 1;
        var value = match.Groups["value"].Value.Trim();
        if (!value.StartsWith('[')) return index + 1;
        var balance = BracketBalance(value);
        var next = index + 1;
        while (balance > 0 && next < end)
        {
            var trimmed = RemoveInlineComment(lines[next]).Trim();
            if (IsTableHeader(lines[next])) break;
            balance += BracketBalance(trimmed);
            next++;
        }
        return next;
    }

    private static void SetGlobalPort(List<string> lines, string key, int value, int defaultPort)
    {
        var rootEnd = FindGlobalEnd(lines);
        SetKey(lines, 0, rootEnd, key, value == defaultPort ? null : value.ToString());
    }

    private static string SetGlobalPort(string config, string key, int value, int defaultPort)
    {
        var lines = SplitLines(config);
        SetGlobalPort(lines, key, value, defaultPort);
        return string.Join(DetectNewLine(config), lines);
    }

    private static int FindGlobalEnd(IReadOnlyList<string> lines)
    {
        for (var index = 0; index < lines.Count; index++)
            if (IsTableHeader(lines[index])) return index;
        return lines.Count;
    }

    private static void SetNote(List<string> lines, int start, ref int rootEnd, string note)
    {
        var notes = new List<int>();
        for (var index = start + 1; index < rootEnd; index++)
        {
            if (lines[index].TrimStart().StartsWith(NotePrefix, StringComparison.OrdinalIgnoreCase))
                notes.Add(index);
        }

        if (notes.Count == 0)
        {
            if (note.Length == 0) return;
            lines.Insert(start + 1, $"{NotePrefix} {SingleLine(note)}");
            rootEnd++;
            return;
        }

        lines[notes[0]] = note.Length == 0 ? "" : $"{NotePrefix} {SingleLine(note)}";
        for (var i = notes.Count - 1; i >= 1; i--)
            lines.RemoveAt(notes[i]);
        if (note.Length == 0)
        {
            for (var i = notes.Count - 1; i >= 0; i--)
            {
                var index = notes[i];
                if (index < lines.Count && lines[index].Length == 0) lines.RemoveAt(index);
            }
            rootEnd -= notes.Count;
        }
        else
        {
            rootEnd -= notes.Count - 1;
        }
    }

    private static List<string> BuildProxyLines(TunnelEndpointEdit edit)
    {
        var type = NormalizeType(edit.Type);
        var lines = new List<string> { "[[proxies]]" };
        if (!string.IsNullOrWhiteSpace(edit.Note)) lines.Add($"{NotePrefix} {SingleLine(edit.Note)}");
        lines.Add($"name = {Quote(edit.Name)}");
        lines.Add($"type = {Quote(type)}");
        lines.Add($"localIP = {Quote(edit.LocalHost)}");
        lines.Add($"localPort = {edit.LocalPort}");
        if (type is "tcp" or "udp")
            lines.Add($"remotePort = {edit.RemotePort}");
        else if (edit.UsesSubdomain)
            lines.Add($"subdomain = {Quote(edit.Domains[0])}");
        else
            lines.Add($"customDomains = {FormatArray(edit.Domains)}");
        return lines;
    }

    private static void ValidateEdit(TunnelEndpointEdit edit)
    {
        if (string.IsNullOrWhiteSpace(edit.Name) || ContainsLineBreak(edit.Name))
            throw new ArgumentException("隧道名称不能为空，也不能包含换行。", nameof(edit));
        if (string.IsNullOrWhiteSpace(edit.LocalHost) || ContainsLineBreak(edit.LocalHost))
            throw new ArgumentException("本地地址不能为空，也不能包含换行。", nameof(edit));

        var type = NormalizeType(edit.Type);
        if (type is not ("tcp" or "udp" or "http" or "https"))
            throw new ArgumentException("隧道类型仅支持 TCP、UDP、HTTP 或 HTTPS。", nameof(edit));
        if (edit.LocalPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(edit), "本地端口必须在 1 到 65535 之间。");
        if (ContainsLineBreak(edit.Note))
            throw new ArgumentException("备注不能包含换行。", nameof(edit));

        if (type is "tcp" or "udp")
        {
            if (edit.RemotePort is not (>= 1 and <= 65535))
                throw new ArgumentOutOfRangeException(nameof(edit), "远端端口必须在 1 到 65535 之间。");
            return;
        }

        if (edit.PublicPort is not (>= 1 and <= 65535))
            throw new ArgumentOutOfRangeException(nameof(edit), "公网端口必须在 1 到 65535 之间。");
        if (edit.Domains.Count == 0 || edit.Domains.Any(domain =>
                string.IsNullOrWhiteSpace(domain)
                || domain.Any(char.IsWhiteSpace)
                || domain.Contains("://", StringComparison.Ordinal)
                || domain.Contains('/', StringComparison.Ordinal)
                || domain.Contains(':', StringComparison.Ordinal)))
            throw new ArgumentException("请填写有效域名；多个域名可用换行或逗号分隔。", nameof(edit));
    }

    private static string FormatArray(IEnumerable<string> values) =>
        "[" + string.Join(", ", values.Select(Quote)) + "]";

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string NormalizeType(string type) => type.Trim().ToLowerInvariant();

    private static string SingleLine(string value) =>
        value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();

    private static bool ContainsLineBreak(string value) =>
        value.Contains('\r', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal);

    private static string NormalizeKey(string key) =>
        key.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();

    private static string ReadString(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(NormalizeKey(key), out var value)) return "";
        value = value.Trim();
        if (value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2)
            return value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal).Trim();
        if (value.StartsWith('\'') && value.EndsWith('\'') && value.Length >= 2)
            return value[1..^1].Trim();
        return value.Trim();
    }

    private static List<string> ReadArray(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(NormalizeKey(key), out var value)) return [];
        value = value.Trim();
        if (value.StartsWith('[') && value.EndsWith(']')) value = value[1..^1];
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim().Trim('"', '\''))
            .Where(item => item.Length > 0)
            .ToList();
    }

    private static bool TryReadNote(string rawLine, out string note)
    {
        var trimmed = rawLine.TrimStart();
        if (trimmed.StartsWith(NotePrefix, StringComparison.OrdinalIgnoreCase))
        {
            note = trimmed[NotePrefix.Length..].Trim();
            return true;
        }
        note = "";
        return false;
    }

    private static int FindInlineCommentIndex(string line)
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
            else if (character == '#' && !inQuotes)
            {
                return index;
            }
        }
        return -1;
    }

    private static string RemoveInlineComment(string line)
    {
        var commentIndex = FindInlineCommentIndex(line);
        return commentIndex < 0 ? line : line[..commentIndex];
    }

    private static List<string> SplitLines(string config) =>
        config.Split(["\r\n", "\n"], StringSplitOptions.None).ToList();

    private static string DetectNewLine(string config) =>
        config.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
}
