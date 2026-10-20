using System.IO;
using FrpDesk.Models;
using FrpDesk.Services;
using Xunit;

namespace FrpDesk.Tests;

public sealed class TomlEndpointParserTests
{
    [Fact]
    public void ParsesTcpRemotePortAndAllowsCopy()
    {
        var endpoint = Assert.Single(new TomlEndpointParser().Parse("""
            serverAddr = "frp.example.com"
            serverPort = 7000
            [[proxies]]
            name = "web"
            type = "tcp"
            localIP = "127.0.0.1"
            localPort = 8080
            remotePort = 18080
            """));

        Assert.Equal("frp.example.com:18080", endpoint.RemoteAddress);
        Assert.Equal("frp.example.com:18080", endpoint.RemoteCopyText);
        Assert.True(endpoint.HasCopyableRemoteAddress);
    }

    [Fact]
    public void ParsesHttpVhostPort()
    {
        var endpoint = Assert.Single(new TomlEndpointParser().Parse("""
            serverAddr = "frp.example.com"
            vhostHTTPPort = 8081
            [[proxies]]
            name = "site"
            type = "http"
            localPort = 3000
            customDomains = ["example.com"]
            """));

        Assert.Equal("http://example.com:8081", endpoint.RemoteAddress);
        Assert.Equal("http://example.com:8081", endpoint.RemoteCopyText);
    }

    [Fact]
    public void ParsesHttpsSubdomain()
    {
        var endpoint = Assert.Single(new TomlEndpointParser().Parse("""
            serverAddr = "frp.example.com"
            vhostHTTPSPort = 443
            [[proxies]]
            name = "site"
            type = "https"
            localPort = 3000
            subdomain = "demo"
            """));

        Assert.Equal("https://demo.frp.example.com", endpoint.RemoteAddress);
        Assert.Equal("https://demo.frp.example.com", endpoint.RemoteCopyText);
    }

    [Fact]
    public void RejectsMultilineNoteForTcp()
    {
        var edit = new TunnelEndpointEdit(
            "ssh", "tcp", "127.0.0.1", 22, 60022, [], null, false, "first line\nsecond line");

        Assert.Throws<ArgumentException>(() => new TomlEndpointParser().AppendProxy("", edit));
    }
}

public sealed class ProfileStoreTests
{
    [Fact]
    public void CreateUpdateDeleteKeepsIndexAndQuarantinesData()
    {
        var root = Path.Combine(Path.GetTempPath(), "frpdesk-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileStore(root);
            var profile = store.CreateProfile("one", "serverAddr = \"a\"", []);
            Assert.Equal("one", Assert.Single(store.Load()).Name);

            store.UpdateProfile(profile, "two", "serverAddr = \"b\"", [profile]);
            Assert.Equal("two", Assert.Single(store.Load()).Name);
            Assert.Equal("serverAddr = \"b\"", File.ReadAllText(store.ConfigPath(profile)));

            var quarantine = store.DeleteProfile(profile, [profile]);
            Assert.Empty(store.Load());
            Assert.NotNull(quarantine);
            Assert.True(Directory.Exists(quarantine));
            Assert.False(Directory.Exists(store.ProfileDirectory(profile)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OrphanedInstanceDirectoryIsRestoredToTheIndex()
    {
        var root = Path.Combine(Path.GetTempPath(), "frpdesk-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileStore(root);
            var known = store.CreateProfile("one", "serverAddr = \"a\"", []);
            var orphanId = Guid.NewGuid();
            var orphanDirectory = Path.Combine(root, "instances", orphanId.ToString("N"));
            Directory.CreateDirectory(orphanDirectory);
            File.WriteAllText(Path.Combine(orphanDirectory, "frpc.toml"), "serverAddr = \"b\"");

            var loaded = store.Load(out var warning);

            Assert.Equal(2, loaded.Count);
            Assert.Contains(loaded, profile => profile.Id == orphanId);
            Assert.NotNull(warning);
            Assert.Equal(2, store.Load().Count);
            Assert.Equal("serverAddr = \"b\"", store.Load().Single(profile => profile.Id == orphanId).Config);
            Assert.Equal("one", store.Load().Single(profile => profile.Id == known.Id).Name);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LegacyDiagnosticIsReclassifiedWhenLoaded()
    {
        var root = Path.Combine(Path.GetTempPath(), "frpdesk-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileStore(root);
            var profile = store.CreateProfile("one", "serverAddr = \"a\"", []);
            var legacy = new DiagnosticSnapshot(
                "FRP 服务器连接异常",
                "connect to server error: 节点 API Token 无效（错误代码：api_token_invalid）",
                DiagnosticSeverity.Warning,
                null,
                DateTimeOffset.Now);
            File.WriteAllText(store.DiagnosticPath(profile), System.Text.Json.JsonSerializer.Serialize(legacy));

            var loaded = Assert.Single(store.Load());

            Assert.Equal("FRP 节点凭证无效", loaded.DiagnosticSummary);
            Assert.Equal(DiagnosticSeverity.Error, loaded.DiagnosticSeverity);

            store.SaveDiagnostic(loaded);
            Assert.Equal(loaded.DiagnosticDetail, Assert.Single(store.Load()).DiagnosticDetail);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class AppSettingsStoreTests
{
    [Fact]
    public void BrokenSettingsArePreserved()
    {
        var root = Path.Combine(Path.GetTempPath(), "frpdesk-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "settings.json"), "{ broken");
            var settings = new AppSettingsStore(root).Load(out var warning);

            Assert.Equal(400, settings.LogLineLimit);
            Assert.Equal("Mica", settings.BackdropMaterial);
            Assert.NotNull(warning);
            Assert.NotEmpty(Directory.GetFiles(root, "settings.json.broken-*"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("Mica")]
    [InlineData("Mica Alt")]
    [InlineData("Acrylic")]
    [InlineData("Acrylic Thin")]
    public void BackdropMaterialPersists(string material)
    {
        var root = Path.Combine(Path.GetTempPath(), "frpdesk-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AppSettingsStore(root);
            store.Save(new AppSettings { BackdropMaterial = material });

            Assert.Equal(material, store.Load().BackdropMaterial);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UnknownBackdropMaterialFallsBackToMica()
    {
        var root = Path.Combine(Path.GetTempPath(), "frpdesk-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "settings.json"), """{"BackdropMaterial":"Unknown"}""");

            Assert.Equal("Mica", new AppSettingsStore(root).Load().BackdropMaterial);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class DiagnosticsTests
{
    [Fact]
    public void TokenRejectionIsReportedAsCredentialErrorWithoutAnsi()
    {
        var line = "\u001B[0m\u001B[1;33m2026-10-19 09:36:55.376 [W] [client/service.go:323] "
            + "connect to server error: 【连接失败】节点 API Token 无效。"
            + "处理方法：请联系管理员检查节点凭证。（错误代码：api_token_invalid）";

        var diagnostic = Diagnostics.RuntimeLine(line);

        Assert.NotNull(diagnostic);
        Assert.Equal("FRP 节点凭证无效", diagnostic.Summary);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.DoesNotContain('\u001B', diagnostic.Detail);
        Assert.DoesNotContain("建议检查服务器地址、端口和网络状态", diagnostic.Detail);

        var withServer = Diagnostics.RuntimeLine(line, "160.30.231.130:7000");
        Assert.Contains("160.30.231.130:7000", withServer!.Detail);
    }

    [Fact]
    public void DuplicateProxyIsReportedSeparately()
    {
        var diagnostic = Diagnostics.RuntimeLine(
            "start error: 【隧道启动失败】隧道“diann”已经在运行。（错误代码：proxy_already_exists）");

        Assert.NotNull(diagnostic);
        Assert.Equal("FRP 隧道重复运行", diagnostic.Summary);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void LogsAndDiagnosticsStripAnsi()
    {
        var profile = new TunnelProfile(Guid.NewGuid(), "one", "");
        profile.AddLog("\u001B[1;33mhello\u001B[0m");
        profile.SetDiagnostic("title", "\u001B[31mdetail\u001B[0m", DiagnosticSeverity.Error);

        Assert.Equal("hello", Assert.Single(profile.Logs));
        Assert.Equal("detail", profile.DiagnosticDetail);
    }
}
