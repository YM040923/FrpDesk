namespace FrpDesk.Models;

public sealed class AppSettings
{
    public bool CloseToTray { get; set; } = true;
    public bool RestoreWindowLayout { get; set; } = true;
    public int LogLineLimit { get; set; } = 400;

    public AppSettings Clone() => new()
    {
        CloseToTray = CloseToTray,
        RestoreWindowLayout = RestoreWindowLayout,
        LogLineLimit = LogLineLimit
    };
}
