using System.Globalization;
using System.Text.RegularExpressions;

namespace FrpDesk.Models;

public static class LogText
{
    private static readonly Regex AnsiEscape = new(
        @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\))",
        RegexOptions.Compiled);

    // frpc 0.71 实测输出：2026-10-21 01:07:05.159 [I] [client/service.go:323] connect to server error: …
    private static readonly Regex FrpcLine = new(
        @"^(?<date>\d{4}-\d{2}-\d{2})[ T](?<time>\d{2}:\d{2}:\d{2})(?:\.\d+)?\s+\[(?<level>[IWED!])\]\s+(?:\[[^\]]*:\d+\]\s+)?(?<msg>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex CanonicalLine = new(
        @"^(?<date>\d{4}-\d{2}-\d{2}) (?<time>\d{2}:\d{2}:\d{2})  (?<level>[A-Z]+)  (?<msg>.*)$",
        RegexOptions.Compiled);

    // 旧版 FrpDesk 只写了时分秒，磁盘上已有的日志文件仍要能读。
    private static readonly Regex LegacyLine = new(
        @"^(?<time>\d{2}:\d{2}:\d{2})\s\s(?<msg>.*)$",
        RegexOptions.Compiled);

    public const string Separator = "  ";

    public static string Clean(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : AnsiEscape.Replace(text, string.Empty).Trim();

    /// <summary>把任意来源（frpc 输出、旧版日志文件、界面自己产生的提示）压成统一的一行。</summary>
    public static string Normalize(string? raw)
    {
        var text = Clean(raw);
        if (text.Length == 0) return string.Empty;

        var frpc = FrpcLine.Match(text);
        if (frpc.Success)
            return Format(frpc.Groups["date"].Value, frpc.Groups["time"].Value, LevelTag(frpc.Groups["level"].Value[0]), frpc.Groups["msg"].Value);

        if (CanonicalLine.IsMatch(text)) return text;

        var legacy = LegacyLine.Match(text);
        if (legacy.Success)
        {
            var message = legacy.Groups["msg"].Value;
            return Format(DateTime.Now.ToString("yyyy-MM-dd"), legacy.Groups["time"].Value, InferLevel(message), message);
        }

        var now = DateTime.Now;
        return Format(now.ToString("yyyy-MM-dd"), now.ToString("HH:mm:ss"), InferLevel(text), text);
    }

    public static string Format(string date, string time, string level, string message) =>
        $"{date} {time}{Separator}{level}{Separator}{message}";

    public static bool TryParse(string? line, out string date, out string time, out string level, out string message)
    {
        var match = CanonicalLine.Match(line ?? string.Empty);
        date = time = level = message = string.Empty;
        if (!match.Success) return false;
        date = match.Groups["date"].Value;
        time = match.Groups["time"].Value;
        level = match.Groups["level"].Value;
        message = match.Groups["msg"].Value;
        return true;
    }

    /// <summary>当天的行只显示时刻，更早的才带「昨天 / 月日」前缀，省下列宽。</summary>
    public static string DescribeMoment(string date, string time)
    {
        if (!DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return time;
        var today = DateTime.Today;
        if (day.Date == today) return time;
        var label = day.Date == today.AddDays(-1) ? "昨天" : day.ToString("MM-dd", CultureInfo.InvariantCulture);
        return $"{label} {time}";
    }

    private static string LevelTag(char marker) => marker switch
    {
        'E' or '!' => "ERROR",
        'W' => "WARN",
        'D' => "DEBUG",
        _ => "INFO"
    };

    private static string InferLevel(string message)
    {
        if (message.Contains("[E]", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
            || message.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("失败", StringComparison.Ordinal)
            || message.Contains("错误", StringComparison.Ordinal))
            return "ERROR";
        if (message.Contains("WARN", StringComparison.OrdinalIgnoreCase)
            || message.Contains("start error", StringComparison.OrdinalIgnoreCase)
            || message.Contains("警告", StringComparison.Ordinal))
            return "WARN";
        if (message.Contains("success", StringComparison.OrdinalIgnoreCase)
            || message.Contains("成功", StringComparison.Ordinal)
            || message.Contains("已恢复", StringComparison.Ordinal))
            return "SUCCESS";
        return "INFO";
    }
}
