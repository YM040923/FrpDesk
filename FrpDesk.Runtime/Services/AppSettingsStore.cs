using System.IO;
using System.Text.Json;
using FrpDesk.Models;

namespace FrpDesk.Services;

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AppSettingsStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrpDesk");
        SettingsPath = Path.Combine(RootDirectory, "settings.json");
    }

    public string RootDirectory { get; }
    public string SettingsPath { get; }

    public AppSettings Load() => Load(out _);

    public AppSettings Load(out string? warning)
    {
        warning = null;
        if (!File.Exists(SettingsPath)) return new AppSettings();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
            Normalize(settings);
            return settings;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            var brokenPath = SettingsPath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try
            {
                File.Move(SettingsPath, brokenPath, overwrite: true);
                warning = $"设置文件无法读取，已保留为 {brokenPath}，本次使用默认设置。";
            }
            catch (Exception moveException) when (moveException is IOException or UnauthorizedAccessException)
            {
                warning = $"设置文件无法读取：{exception.Message}";
            }
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Normalize(settings);
        Directory.CreateDirectory(RootDirectory);
        var tempPath = SettingsPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tempPath, SettingsPath, true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private static void Normalize(AppSettings settings)
    {
        settings.LogLineLimit = settings.LogLineLimit switch
        {
            <= 200 => 200,
            <= 400 => 400,
            _ => 800
        };
        if (settings.BackdropMaterial is not ("Mica" or "Mica Alt" or "Acrylic" or "Acrylic Thin"))
            settings.BackdropMaterial = "Mica";
    }
}
