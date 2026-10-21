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

    [Fact]
    public void ReadsExistingVhostPortsForNewTunnelDefaults()
    {
        const string config = "serverAddr = \"frp.example.com\"\nvhostHTTPPort = 8081\nvhostHTTPSPort = 8443\n";
        var parser = new TomlEndpointParser();

        Assert.Equal(8081, parser.GetGlobalPort(config, "vhostHTTPPort"));
        Assert.Equal(8443, parser.GetGlobalPort(config, "vhostHTTPSPort"));
        Assert.Null(parser.GetGlobalPort(config, "vhostOtherPort"));
    }

    [Fact]
    public void CrOnlyConfigStillParsesServerAndProxies()
    {
        var config = "serverAddr = \"frp.example.com\"\rserverPort = 7000\r"
            + "[[proxies]]\rname = \"ssh\"\rtype = \"tcp\"\rlocalPort = 22\rremotePort = 60022";
        var parser = new TomlEndpointParser();

        Assert.Equal("frp.example.com:7000", parser.GetServerEndpoint(config));
        Assert.Equal("ssh", Assert.Single(parser.Parse(config)).Name);
        Assert.Equal("serverAddr = \"frp.example.com\"\r\nserverPort = 7000\r\n",
            TomlText.NormalizeLineEndings("serverAddr = \"frp.example.com\"\nserverPort = 7000\n"));
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
    public void CrOnlyConfigIsStoredWithWindowsNewLines()
    {
        var root = Path.Combine(Path.GetTempPath(), "frpdesk-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileStore(root);
            var profile = store.CreateProfile("one", "serverAddr = \"a\"\rserverPort = 7000", []);

            Assert.Equal("serverAddr = \"a\"\r\nserverPort = 7000", profile.Config);
            Assert.Equal("serverAddr = \"a\"\r\nserverPort = 7000", File.ReadAllText(store.ConfigPath(profile)));
            Assert.Equal("serverAddr = \"a\"\r\nserverPort = 7000", Assert.Single(store.Load()).Config);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LegacyCrOnlyConfigOnDiskLoadsAsWindowsNewLines()
    {
        var root = Path.Combine(Path.GetTempPath(), "frpdesk-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileStore(root);
            var profile = store.CreateProfile("one", "serverAddr = \"a\"", []);
            File.WriteAllText(store.ConfigPath(profile),
                "serverAddr = \"frp.example.com\"\rserverPort = 7000\r[[proxies]]\rname = \"ssh\"\r"
                + "type = \"tcp\"\rlocalPort = 22\rremotePort = 60022");

            var loaded = Assert.Single(store.Load());
            var parser = new TomlEndpointParser();

            Assert.Equal("frp.example.com:7000", parser.GetServerEndpoint(loaded.Config));
            Assert.Equal("ssh", Assert.Single(parser.Parse(loaded.Config)).Name);
            Assert.Equal(
                "serverAddr = \"frp.example.com\"\r\nserverPort = 7000\r\n[[proxies]]\r\nname = \"ssh\"\r\n"
                + "type = \"tcp\"\r\nlocalPort = 22\r\nremotePort = 60022",
                loaded.Config);
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

    [Fact]
    public void ParsesMultilineCustomDomains()
    {
        var endpoint = Assert.Single(new TomlEndpointParser().Parse("""
            serverAddr = "frp.example.com"
            vhostHTTPPort = 8081
            [[proxies]]
            name = "site"
            type = "http"
            localPort = 3000
            customDomains = [
             "a.com",
             "b.com"
            ]
            """));

        Assert.Equal("http://a.com:8081、http://b.com:8081", endpoint.RemoteAddress);
        Assert.Contains("a.com", endpoint.CustomDomains);
        Assert.Contains("b.com", endpoint.CustomDomains);
    }

    [Fact]
    public void UpdateProxyReplacesMultilineArrayWithoutLeavingOrphans()
    {
        const string config = """
            serverAddr = "frp.example.com"
            vhostHTTPPort = 8081
            [[proxies]]
            name = "site"
            type = "http"
            localPort = 3000
            customDomains = [
             "a.com",
             "b.com"
            ]
            """;
        var parser = new TomlEndpointParser();
        var edit = new TunnelEndpointEdit("site", "http", "127.0.0.1", 3000, null, ["c.com"], 8081, false, "");
        var updated = parser.UpdateProxy(config, 0, edit);

        var endpoints = parser.Parse(updated);
        var endpoint = Assert.Single(endpoints);
        Assert.DoesNotContain("a.com", endpoint.CustomDomains);
        Assert.DoesNotContain("b.com", endpoint.CustomDomains);
        Assert.Contains("c.com", endpoint.CustomDomains);
        Assert.DoesNotContain("\"a.com\"", updated);
        Assert.DoesNotContain("\"b.com\"", updated);
    }

    [Fact]
    public void UpdateTcpRemovesMultilineArray()
    {
        const string config = """
            serverAddr = "frp.example.com"
            vhostHTTPPort = 8081
            [[proxies]]
            name = "web"
            type = "http"
            localPort = 3000
            customDomains = [
             "a.com",
             "b.com"
            ]
            """;
        var parser = new TomlEndpointParser();
        var edit = new TunnelEndpointEdit("web", "tcp", "127.0.0.1", 3000, 9000, [], null, false, "");
        var updated = parser.UpdateProxy(config, 0, edit);

        var endpoints = parser.Parse(updated);
        var endpoint = Assert.Single(endpoints);
        Assert.Equal("tcp", endpoint.Type);
        Assert.Equal("frp.example.com:9000", endpoint.RemoteAddress);
        Assert.Empty(endpoint.CustomDomains);
        Assert.DoesNotContain("\"a.com\"", updated);
        Assert.DoesNotContain("\"b.com\"", updated);
    }

    [Fact]
    public void AppendProxyRejectsDuplicateName()
    {
        const string config = """
            serverAddr = "frp.example.com"
            [[proxies]]
            name = "ssh"
            type = "tcp"
            localPort = 22
            remotePort = 60022
            """;
        var parser = new TomlEndpointParser();
        var edit = new TunnelEndpointEdit("ssh", "tcp", "127.0.0.1", 22, 60023, [], null, false, "");

        Assert.Throws<ArgumentException>(() => parser.AppendProxy(config, edit));
    }

    [Fact]
    public void UpdateProxyRejectsDuplicateName()
    {
        const string config = """
            serverAddr = "frp.example.com"
            [[proxies]]
            name = "first"
            type = "tcp"
            localPort = 22
            remotePort = 60022
            [[proxies]]
            name = "second"
            type = "tcp"
            localPort = 80
            remotePort = 60080
            """;
        var parser = new TomlEndpointParser();
        var edit = new TunnelEndpointEdit("first", "tcp", "127.0.0.1", 80, 60081, [], null, false, "");

        Assert.Throws<ArgumentException>(() => parser.UpdateProxy(config, 1, edit));
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

        Assert.True(LogText.TryParse(Assert.Single(profile.Logs), out _, out _, out var level, out var message));
        Assert.Equal("hello", message);
        Assert.Equal("INFO", level);
        Assert.Equal("detail", profile.DiagnosticDetail);
    }

    [Fact]
    public void FrpcOutputBecomesCanonicalLogLine()
    {
        var line = LogText.Normalize(
            "2026-10-21 01:07:05.159 [W] [client/service.go:323] connect to server error: dial tcp 127.0.0.1:7000");

        Assert.Equal("2026-10-21 01:07:05  WARN  connect to server error: dial tcp 127.0.0.1:7000", line);
        Assert.Equal(line, LogText.Normalize(line));
    }

    [Fact]
    public void ProxyEventsDriveTunnelStatus()
    {
        var profile = new TunnelProfile(Guid.NewGuid(), "one", "");
        profile.AddLog("2026-10-21 01:07:05.159 [I] [client/service.go:900] [web-entry] start proxy success");
        profile.AddLog("2026-10-21 01:07:06.159 [E] [client/service.go:901] [ssh-home] start error: dial tcp");

        Assert.Equal(TunnelProxyStatus.Running, profile.StatusOf("web-entry"));
        Assert.Equal(TunnelProxyStatus.Failed, profile.StatusOf("ssh-home"));
        Assert.Equal(TunnelProxyStatus.Unstarted, profile.StatusOf("rdp-lab"));
    }
}
