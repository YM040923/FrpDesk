using System.IO;
using System.Text;
using System.Text.Json;
using FrpDesk.Models;

namespace FrpDesk.Services;

public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _root;
    private readonly string _indexPath;

    public ProfileStore(string? rootDirectory = null)
    {
        _root = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrpDesk");
        _indexPath = Path.Combine(_root, "profiles.json");
    }

    public string RootDirectory => _root;

    public IReadOnlyList<TunnelProfile> Load() => Load(out _);

    public IReadOnlyList<TunnelProfile> Load(out string? warning)
    {
        warning = null;
        if (!File.Exists(_indexPath))
        {
            var recovered = RecoverOrphanedProfiles([], out warning);
            return recovered;
        }
        try
        {
            var entries = JsonSerializer.Deserialize<List<ProfileEntry>>(File.ReadAllText(_indexPath)) ?? [];
            var indexed = entries.Where(entry => entry.Id != Guid.Empty).ToList();
            var profiles = indexed
                .Select(entry =>
                {
                    var profile = new TunnelProfile(entry.Id, entry.Name, ReadConfig(entry.Id));
                    profile.ApplyDiagnostic(LoadDiagnostic(entry.Id));
                    return profile;
                })
                .ToList();

            var orphans = RecoverOrphanedProfiles(indexed, out var recoveryWarning);
            if (orphans.Count > 0)
            {
                profiles.AddRange(orphans);
                warning = recoveryWarning;
            }
            return profiles;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            var brokenPath = _indexPath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try
            {
                File.Move(_indexPath, brokenPath, overwrite: true);
                warning = $"实例列表无法读取，原文件已保留为 {brokenPath}。";
            }
            catch (Exception moveException) when (moveException is IOException or UnauthorizedAccessException)
            {
                warning = $"实例列表无法读取：{exception.Message}";
            }
            throw new InvalidDataException(warning, exception);
        }
    }

    public string ConfigPath(TunnelProfile profile) => Path.Combine(ProfileDirectory(profile), "frpc.toml");

    public string ProfileDirectory(TunnelProfile profile) => Path.Combine(_root, "instances", profile.Id.ToString("N"));

    public TunnelProfile CreateProfile(string name, string config, IEnumerable<TunnelProfile> existing)
    {
        var profile = new TunnelProfile(Guid.NewGuid(), name, config);
        try
        {
            SaveConfig(profile);
            SaveEntries(existing.Append(profile));
            return profile;
        }
        catch
        {
            try { TryQuarantine(profile); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    public void UpdateProfile(TunnelProfile profile, string name, string config, IEnumerable<TunnelProfile> profiles)
    {
        var configPath = ConfigPath(profile);
        var oldConfig = File.Exists(configPath) ? File.ReadAllText(configPath) : null;
        var oldIndex = File.Exists(_indexPath) ? File.ReadAllText(_indexPath) : null;
        try
        {
            AtomicWrite(configPath, config);
            SaveEntries(profiles.Select(item => new ProfileEntry(
                item.Id,
                ReferenceEquals(item, profile) ? name : item.Name)));
        }
        catch
        {
            RestoreFile(configPath, oldConfig);
            RestoreFile(_indexPath, oldIndex);
            throw;
        }

        profile.Name = name;
        profile.Config = config;
    }

    public string? DeleteProfile(TunnelProfile profile, IEnumerable<TunnelProfile> profiles)
    {
        var oldIndex = File.Exists(_indexPath) ? File.ReadAllText(_indexPath) : null;
        string? quarantinePath = null;
        try
        {
            quarantinePath = TryQuarantine(profile);
            SaveEntries(profiles.Where(item => item.Id != profile.Id));
            return quarantinePath;
        }
        catch
        {
            if (quarantinePath is not null && Directory.Exists(quarantinePath))
            {
                try { Directory.Move(quarantinePath, ProfileDirectory(profile)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            RestoreFile(_indexPath, oldIndex);
            throw;
        }
    }

    public void SaveConfig(TunnelProfile profile)
    {
        Directory.CreateDirectory(ProfileDirectory(profile));
        AtomicWrite(ConfigPath(profile), profile.Config);
    }

    public void SaveDiagnostic(TunnelProfile profile)
    {
        try
        {
            var snapshot = profile.CreateDiagnosticSnapshot();
            var path = DiagnosticPath(profile);
            if (snapshot is null)
            {
                try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
                return;
            }

            AtomicWrite(path, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Diagnostics are supplemental state and must never block the main workflow.
        }
    }

    public string DiagnosticPath(TunnelProfile profile) => Path.Combine(ProfileDirectory(profile), "diagnostic.json");

    public void SaveProfiles(IEnumerable<TunnelProfile> profiles) => SaveEntries(profiles);

    private void SaveEntries(IEnumerable<TunnelProfile> profiles) =>
        SaveEntries(profiles.Select(profile => new ProfileEntry(profile.Id, profile.Name)));

    private void SaveEntries(IEnumerable<ProfileEntry> entries)
    {
        Directory.CreateDirectory(_root);
        AtomicWrite(_indexPath, JsonSerializer.Serialize(entries.ToList(), JsonOptions));
    }

    private string? TryQuarantine(TunnelProfile profile)
    {
        var source = ProfileDirectory(profile);
        if (!Directory.Exists(source)) return null;
        var quarantineRoot = Path.Combine(_root, "quarantine");
        Directory.CreateDirectory(quarantineRoot);
        var target = Path.Combine(
            quarantineRoot,
            DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + profile.Id.ToString("N"));
        Directory.Move(source, target);
        return target;
    }

    private string ReadConfig(Guid id)
    {
        var path = Path.Combine(_root, "instances", id.ToString("N"), "frpc.toml");
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private List<TunnelProfile> RecoverOrphanedProfiles(IReadOnlyCollection<ProfileEntry> indexed, out string? warning)
    {
        warning = null;
        var instancesRoot = Path.Combine(_root, "instances");
        if (!Directory.Exists(instancesRoot)) return [];

        var known = indexed.Select(entry => entry.Id).ToHashSet();
        var recovered = new List<TunnelProfile>();
        foreach (var directory in Directory.EnumerateDirectories(instancesRoot))
        {
            var name = Path.GetFileName(directory);
            if (!Guid.TryParseExact(name, "N", out var id) || id == Guid.Empty || known.Contains(id)) continue;
            var configPath = Path.Combine(directory, "frpc.toml");
            if (!File.Exists(configPath)) continue;

            var profile = new TunnelProfile(id, $"恢复的实例 {name[..8]}", ReadConfig(id));
            profile.ApplyDiagnostic(LoadDiagnostic(id));
            recovered.Add(profile);
        }

        if (recovered.Count == 0) return recovered;
        try
        {
            SaveEntries(indexed.Concat(recovered.Select(profile => new ProfileEntry(profile.Id, profile.Name))));
            warning = $"发现 {recovered.Count} 个未登记实例，已恢复到实例列表。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warning = $"发现 {recovered.Count} 个未登记实例，但实例列表暂时无法更新：{exception.Message}";
        }
        return recovered;
    }

    private DiagnosticSnapshot? LoadDiagnostic(Guid id)
    {
        var path = Path.Combine(_root, "instances", id.ToString("N"), "diagnostic.json");
        if (!File.Exists(path)) return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<DiagnosticSnapshot>(File.ReadAllText(path));
            if (snapshot is null) return null;
            // Older builds saved a generic summary and raw ANSI; reclassify once on load.
            var result = snapshot.Summary is "FRP 服务器连接异常" or "FRP 认证失败"
                ? Diagnostics.RuntimeLine(snapshot.Detail) ?? snapshot
                : snapshot;
            result = result with { Summary = LogText.Clean(result.Summary), Detail = LogText.Clean(result.Detail) };
            if (result != snapshot) AtomicWrite(path, JsonSerializer.Serialize(result, JsonOptions));
            return result;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void RestoreFile(string path, string? content)
    {
        try
        {
            if (content is null)
            {
                if (File.Exists(path)) File.Delete(path);
            }
            else
            {
                AtomicWrite(path, content);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The original exception is more useful than a rollback failure.
        }
    }

    private static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private sealed record ProfileEntry(Guid Id, string Name);
}
