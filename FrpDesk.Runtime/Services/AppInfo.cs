using System.Reflection;

namespace FrpDesk.Services;

public static class AppInfo
{
    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
}
