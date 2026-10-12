using FrpDesk.Models;

namespace FrpDesk.Services;

public static class Diagnostics
{
    public static DiagnosticSnapshot ConfigurationInvalid(IEnumerable<string> output, int? exitCode = null)
    {
        var detail = LastUsefulLine(output) ?? "frpc 没有返回具体错误信息。";
        return new DiagnosticSnapshot(
            "TOML 配置校验失败",
            $"{detail} 建议先修正配置，再重新运行。",
            DiagnosticSeverity.Error,
            exitCode,
            DateTimeOffset.Now);
    }

    public static DiagnosticSnapshot VerificationUnavailable(Exception exception) => new(
        "配置校验无法完成",
        $"{exception.Message} 建议确认内置 frpc 文件存在且有权限运行。",
        DiagnosticSeverity.Error,
        null,
        DateTimeOffset.Now);

    public static DiagnosticSnapshot ProcessStartFailed(Exception exception) => new(
        "frpc 启动失败",
        $"{exception.Message} 建议确认配置目录和内置 frpc 文件可访问。",
        DiagnosticSeverity.Error,
        null,
        DateTimeOffset.Now);

    public static DiagnosticSnapshot ReloadFailed(Exception exception) => new(
        "配置应用失败",
        $"{exception.Message} 建议检查 frpc 管理接口配置；如果未开启管理接口，FrpDesk 会改用重启实例的方式应用配置。",
        DiagnosticSeverity.Error,
        null,
        DateTimeOffset.Now);

    public static DiagnosticSnapshot ProcessExited(int exitCode) => new(
        "frpc 异常退出",
        $"frpc 进程意外退出，退出代码为 {exitCode}。请查看下方运行日志，重点检查服务器地址、端口和认证配置。",
        DiagnosticSeverity.Error,
        exitCode,
        DateTimeOffset.Now);

    public static DiagnosticSnapshot BackgroundRetrying(int retryCount, int? exitCode, string? detail = null) => new(
        "后台连接中断，正在自动恢复",
        detail ?? $"frpc 已退出，后台将在稍后进行第 {retryCount} 次重试。",
        DiagnosticSeverity.Warning,
        exitCode,
        DateTimeOffset.Now);

    public static DiagnosticSnapshot BackgroundFailed(int? exitCode, string? detail = null) => new(
        "后台自动恢复失败",
        detail ?? "已达到最大自动重试次数。请检查运行日志和网络配置，然后关闭并重新开启后台服务。",
        DiagnosticSeverity.Error,
        exitCode,
        DateTimeOffset.Now);

    public static DiagnosticSnapshot BackgroundServiceFailed(Exception exception) => new(
        "后台服务操作失败",
        $"{exception.Message} 建议确认已使用发布版运行，并允许管理员权限。",
        DiagnosticSeverity.Error,
        null,
        DateTimeOffset.Now);

    public static DiagnosticSnapshot? RuntimeLine(string line)
    {
        var text = line.Trim();
        if (text.Length == 0) return null;
        var lower = text.ToLowerInvariant();
        if (lower.Contains("authentication failed") || lower.Contains("login to server failed") || lower.Contains("auth failed"))
            return new DiagnosticSnapshot("FRP 认证失败", $"{text} 建议检查 token、鉴权方式和服务端配置。", DiagnosticSeverity.Error, null, DateTimeOffset.Now);
        if (lower.Contains("connection refused") || lower.Contains("dial tcp") || lower.Contains("no such host")
            || lower.Contains("i/o timeout") || lower.Contains("tls handshake timeout") || lower.Contains("proxyconnect"))
            return new DiagnosticSnapshot("无法连接 FRP 服务器", $"{text} 建议检查服务器地址、端口、防火墙和网络连通性。", DiagnosticSeverity.Error, null, DateTimeOffset.Now);
        if ((lower.Contains("connect") || lower.Contains("server")) && (lower.Contains("failed") || lower.Contains("error") || lower.Contains("timeout")))
            return new DiagnosticSnapshot("FRP 服务器连接异常", $"{text} 建议检查服务器地址、端口和网络状态。", DiagnosticSeverity.Warning, null, DateTimeOffset.Now);
        return null;
    }

    private static string? LastUsefulLine(IEnumerable<string> output) => output
        .Select(line => line.Trim())
        .Where(line => line.Length > 0)
        .LastOrDefault();
}
