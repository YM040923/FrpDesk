using System.IO;
using System.Text;
using System.Text.Json;
using FrpDesk.Models;

namespace FrpDesk.Services;

public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrpDesk");
    private readonly string _indexPath;

    public ProfileStore() => _indexPath = Path.Combine(_root, "profiles.json");

    public IReadOnlyList<TunnelProfile> Load()
    {
        if (!File.Exists(_indexPath)) return [];
        try
        {
            var entries = JsonSerializer.Deserialize<List<ProfileEntry>>(File.ReadAllText(_indexPath)) ?? [];
            return entries.Where(entry => entry.Id != Guid.Empty)
                .Select(entry =>
                {
                    var profile = new TunnelProfile(entry.Id, entry.Name, ReadConfig(entry.Id));
                    profile.ApplyDiagnostic(LoadDiagnostic(entry.Id));
                    return profile;
                })
                .ToArray();
        }
        catch (JsonException)
        {
            var brokenPath = _indexPath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Move(_indexPath, brokenPath);
            throw new InvalidDataException($"实例列表无法读取，原文件已保留为 {brokenPath}");
        }
    }

    public string ConfigPath(TunnelProfile profile) => Path.Combine(ProfileDirectory(profile), "frpc.toml");

    public string ProfileDirectory(TunnelProfile profile) => Path.Combine(_root, "instances", profile.Id.ToString("N"));

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
        catch (IOException)
        {
            // Diagnostics are supplemental state and must never block the main workflow.
        }
        catch (UnauthorizedAccessException)
        {
            // Diagnostics are supplemental state and must never block the main workflow.
        }
    }

    public string DiagnosticPath(TunnelProfile profile) => Path.Combine(ProfileDirectory(profile), "diagnostic.json");

    public void SaveProfiles(IEnumerable<TunnelProfile> profiles)
    {
        Directory.CreateDirectory(_root);
        var entries = profiles.Select(profile => new ProfileEntry(profile.Id, profile.Name)).ToList();
        AtomicWrite(_indexPath, JsonSerializer.Serialize(entries, JsonOptions));
    }

    public void Delete(TunnelProfile profile)
    {
        var directory = ProfileDirectory(profile);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private string ReadConfig(Guid id)
    {
        var path = Path.Combine(_root, "instances", id.ToString("N"), "frpc.toml");
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    private DiagnosticSnapshot? LoadDiagnostic(Guid id)
    {
        var path = Path.Combine(_root, "instances", id.ToString("N"), "diagnostic.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<DiagnosticSnapshot>(File.ReadAllText(path)); }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
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
