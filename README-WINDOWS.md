# FrpDesk

A small Windows desktop manager for multiple independent `frpc` TOML configurations.

- Paste or import a TOML file, save it as an instance, then start or stop it independently.
- Each configuration runs as its own `frpc` process and stores its logs in the app window.
- The official FRP Windows x64 client is bundled; no separate FRP installation is required.
- Profiles live under `%LOCALAPPDATA%\FrpDesk\instances\`.
- Closing the window hides FrpDesk in the system tray; choose “退出 FrpDesk” from the tray menu to stop foreground `frpc` processes.
- Use “后台运行” for an instance that must survive closing the window, disconnecting RDP, and logging off. FrpDesk registers one Windows service per instance, set to start automatically with Windows; Windows will request administrator approval when the switch is enabled or disabled.
- The service host runs the bundled `frpc.exe` under a per-instance virtual service account and retries unexpected exits after 30 seconds, 1 minute, 2 minutes, and 4 minutes. After five starts it reports “后台重试耗尽” and waits for the user to turn the switch on again.
- The app keeps a diagnostic summary for each instance. TOML validation failures, frpc startup failures, unexpected exits, connection/authentication errors, background service failures, and exhausted retries are classified separately, with the latest detail, exit code, time, and suggested next step available in the instance panel and copyable with “复制诊断”.
- The instance list has a quick run/stop action for each foreground instance. “全部运行” starts all stopped foreground instances together; Windows service instances are skipped and remain managed by their service switch. “全部停止” stops both foreground processes and configured background services.
- The settings page is a full page inside the main window. It controls close-to-tray behavior, remembered window layout, in-memory log retention, and opening the local data directory. It does not duplicate per-instance background service controls.
- The address page lets you choose any saved profile and copy each proxy's local address, public TCP/UDP endpoint, HTTP/HTTPS domain, or access-side name for private proxy types. It also supports copying the complete address list for the selected profile.
- The profile editor can validate the pasted TOML with the bundled `frpc` before saving, shows the current character count, supports import, clear, select-all, and warns before discarding unsaved changes.
- A running foreground instance can apply a saved configuration with “重载配置”. When the TOML enables an frpc management API, FrpDesk uses `frpc reload`; otherwise it restarts that instance to apply the file. A background-service instance is restarted through its Windows service so it remains independent of the desktop session.
- Background services are not created automatically for existing profiles. Enable “后台运行” for each profile that should start with Windows.

## Build

Requires the .NET 10 SDK on Windows:

```powershell
./scripts/Fetch-Frpc.ps1
dotnet run
dotnet publish -c Release -o publish
```

`Fetch-Frpc.ps1` downloads official FRP v0.71.0, verifies its published SHA256, and extracts `frpc.exe` and its license into `assets/`. The app is published as a self-contained single-file executable with `assets/frpc.exe` beside it.

The FRP project and included client are distributed under their upstream license in [`assets/frp-LICENSE`](assets/frp-LICENSE). FrpDesk is an independent, unofficial client.

## Release

Create the versioned Windows x64 package with:

```powershell
./scripts/Publish-Release.ps1 -Version 0.1.1
```

The package is written to `artifacts/` as a self-contained ZIP plus a SHA256 file. Extract it and run `Install-FrpDesk.cmd` for a per-user installation. Uninstalling preserves profiles and logs under `%LOCALAPPDATA%\FrpDesk`; the uninstaller only removes the registered FrpDesk installation directory. Close FrpDesk and every configured background service before uninstalling.

When upgrading, close FrpDesk and turn off every configured background service first. The installer verifies the release manifest and stops if an old service could otherwise keep using the previous installation.
