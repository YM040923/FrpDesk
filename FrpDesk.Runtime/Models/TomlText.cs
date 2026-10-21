namespace FrpDesk.Models;

public static class TomlText
{
    /// <summary>WinUI 多行 TextBox 用裸 CR 表示换行，而 frpc 的 TOML 解析器只接受 CRLF/LF，会以
    /// "windows new line should be \r\n" 拒绝整份配置；入库前统一成 Windows 换行。</summary>
    public static string NormalizeLineEndings(string? text) =>
        string.IsNullOrEmpty(text)
            ? string.Empty
            : string.Join("\r\n", text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None));
}
