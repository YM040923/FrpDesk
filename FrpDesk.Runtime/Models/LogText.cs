using System.Text.RegularExpressions;

namespace FrpDesk.Models;

public static class LogText
{
    private static readonly Regex AnsiEscape = new(
        @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\))",
        RegexOptions.Compiled);

    public static string Clean(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : AnsiEscape.Replace(text, string.Empty).Trim();
}
