namespace FrpDesk.Models;

public sealed class AppSettings
{
    public bool CloseToTray { get; set; } = true;
    public bool RestoreWindowLayout { get; set; } = true;
    public bool AutoStart { get; set; }
    public int LogLineLimit { get; set; } = 400;
    public string BackdropMaterial { get; set; } = "Mica";

    public AppSettings Clone() => new()
    {
        CloseToTray = CloseToTray,
        RestoreWindowLayout = RestoreWindowLayout,
        AutoStart = AutoStart,
        LogLineLimit = LogLineLimit,
        BackdropMaterial = BackdropMaterial
    };
}
