# FrpDesk 0.1.8

## Fixes

- 停止服务 no longer reports “No process is associated with this object.” when `frpc` exits at the same moment as the stop request. An exit during stop is treated as a normal stop, while genuine kill failures still surface a readable message.
- 启用后台服务 now works from the installed application. The Windows service launches the complete installation before WinUI starts, instead of an incomplete executable copy that was missing its runtime dependencies.
- After a successful administrator operation, the app no longer reads the exit code from an already-disposed process, which could show the same “No process is associated with this object.” error even though the service was installed.
- Background services now run as LocalSystem from the installed folder. Legacy per-service runtime copies and virtual-account permissions are removed on the next enable or stop.
- The background service button and per-profile actions are disabled while an administrator prompt or service operation is running, so repeated clicks no longer queue several installs.
- 全部停止 stops foreground instances and background services independently; a failure in one no longer prevents the other from stopping.
- Saving a profile or tunnel while an instance is running now reports whether the new configuration was actually applied instead of always claiming success.

---

# FrpDesk 0.1.7

## Windows interface

- Rebuilt the add/edit profile dialog to match the current surface language. The title appears once, the config toolbar and footer buttons fit inside the dialog, and the stray Ctrl+S hint is gone.
- Added a dedicated 配置管理 page with a table for starting, stopping, editing, and deleting every profile, plus 全部运行 and 全部停止.
- The profile switcher is now a compact menu that expands from the trigger at the same width. It only switches profiles or opens 添加配置.
- Moved 启用后台服务 to Settings, bound to the currently selected profile.
- Removed the 读取状态 and 重新加载 actions, which could not work reliably.
- Dialog and popup surfaces are opaque, so page content no longer shows through them.

## Fixes

- The installer now closes any old FrpDesk and foreground `frpc.exe` from the installation folder before replacing files. Upgrading with instances still running no longer fails with `DeleteFile failed; code 5. Access is denied`.

---

# FrpDesk 0.1.6

## Fixes

- Fixed the TOML newline error that blocked starting a profile after pasting a configuration. Config text is now normalized to Windows line endings when it is typed, imported, saved, and loaded, so frpc no longer rejects bare-CR input.
- Fixed the overview card expanding to display the whole configuration. The endpoint parser now splits CR-only configs correctly, so the card shows the server address and tunnel endpoints as intended.
- Adding an HTTP or HTTPS tunnel now reuses the existing `vhostHTTPPort` / `vhostHTTPSPort` instead of resetting it to 80 / 443.
- 全部停止 now stops every profile with a single administrator prompt instead of one prompt per profile.
- Fixed the close flow while instances are still stopping, and surfaced stop failures from the tray instead of failing silently.
- A background instance now clears its old error message after reconnecting successfully.
- The auto-start switch no longer reports enabled when the registry points at an old installation path.
- The profile editor's unsaved-changes check now compares normalized config text.

---

# FrpDesk 0.1.5

## Windows interface

- Reworked the window chrome into one continuous surface with a calmer card hierarchy, consistent spacing, and a compact collapsible navigation rail.
- Added switchable accent themes for both light and dark schemes. Accent color is limited to selection, switches, and signal states; all surfaces stay on the measured neutral tokens.
- Flattened table headers, removed row rules, and tightened the tunnel management layout. Sidebar, settings, overview, and log pages share the same surface language.
- Removed redundant copy from the title bar, overview, and settings.
- Replaced the WinForms-drawn tray menu with a WinUI popup that uses the same font, row height, hover treatment, and DWM border as the in-app menus. It opens against the taskbar, follows per-monitor DPI, and closes on outside click or Escape.
- Simplified tray actions to 打开主界面, 全部停止, and 退出; 全部停止 stays disabled when nothing is running.
- Updated the update check to read the public GitHub release list and prefer `windows-v*` tags.

## Reliability

- Tunnel status updates now happen in place, and the three-second background poll only rebuilds lists when the profile or configuration actually changes.
- Recent activity and log views re-render only when their content changes, so the tables and logs no longer flicker or jump to the bottom during polling.
- Theme and high-contrast changes are applied to the tray menu without restarting the app.

## Package

- Windows x64, self-contained WinUI 3 application directory, includes `assets\frpc.exe`.
- The setup installs to `Program Files`; administrator authorization is required for service management.
- The installer and executables are unsigned unless release signing is explicitly configured; SmartScreen may warn.
- User data stays in `%LOCALAPPDATA%\FrpDesk` across upgrades and uninstall.

---

# FrpDesk 0.1.4

This Windows release also includes the fixes from the earlier 0.1.3 development iteration, which was not published separately.

## Windows interface

- Rebuilt the Windows desktop interface with WinUI 3, including overview, tunnel management and editing, logs, and settings.
- Added a custom title area integrated with the app content and Windows window controls.
- Added Mica, Mica Alt, Acrylic, and Acrylic Thin window-background choices with instant preview, saved selection, and a solid-color fallback when Windows cannot apply the effect.
- Improved empty states, tunnel address layout, status icons, and copy feedback while keeping the tunnel/configuration workflow intact.

## Reliability and diagnostics

- A rejected FRP node token is classified as a credential/configuration issue and directs the user to verify the configured server address.
- ANSI color escapes are removed from `frpc` logs and diagnostics; a successful login clears a stale connection error.
- Older diagnostic summaries are reclassified when loaded, and instance folders missing from the index are restored instead of silently disappearing.
- A failed hot reload no longer marks a still-running instance as stopped; duplicate proxy names receive a specific diagnostic.
- Added regression coverage for diagnostics, endpoint parsing, profile recovery, and saved backdrop settings.

## Package

- Windows x64, self-contained WinUI 3 application directory, includes `assets\frpc.exe`.
- The setup installs to `Program Files`; administrator authorization is required for service management.
- The installer and executables are unsigned unless release signing is explicitly configured; SmartScreen may warn.
- User data stays in `%LOCALAPPDATA%\FrpDesk` across upgrades and uninstall.

---

# FrpDesk 0.1.3

## Windows fixes

- One installer only. The setup installs to Program Files, removes the legacy per-user install and duplicate uninstall entry, and no longer ships a second PowerShell installer.
- The installer refuses to overwrite files while FrpDesk background services are still registered, verifies installed files against the release manifest, and the uninstaller stops and deletes only FrpDesk-owned services and processes.
- Missing `assets\frpc.exe` no longer crashes the process before the window opens; the app shows a readable error and keeps running.
- Profile create, edit and delete are transactional. Deleted profiles move to `%LOCALAPPDATA%\FrpDesk\quarantine` instead of being recursively deleted.
- The auto-start Run key is written only when the setting is saved. A copied or temporary executable no longer rewrites the user's startup entry.
- Added single-instance activation: launching FrpDesk again brings the existing tray window forward instead of starting a second copy.
- Background service requests now derive every path from the profile ID and current executable; arbitrary paths are rejected. Service batch stop uses one elevation prompt.
- Responsive layout and smaller minimum window sizes fix clipping on narrow screens and high DPI. Native MessageBox popups were replaced with an in-window notice bar and styled confirmation dialogs.
- Keyboard focus visuals, accessible names, incremental log scrolling, HTTP/HTTPS vhost ports, and automatic service log/status error handling were fixed.
- Added a Windows test project covering TOML endpoints, profile transactions, quarantine, and broken settings recovery.

## Package

- The setup requires administrator approval because it manages Windows services.
- The portable ZIP contains `FrpDesk.exe` and `assets\`; it is not an installer.
- Authenticode signing is supported through `Publish-Release.ps1 -SignThumbprint`. Without a certificate the package remains unsigned and may trigger SmartScreen.

---

# FrpDesk 0.1.2

## Included

- Multiple independent FRP TOML instances.
- Foreground and Windows service execution modes.
- Automatic recovery for unexpected background exits.
- Classified diagnostics with copyable details.
- Per-instance quick actions and batch foreground start.
- Tray operation and remembered window layout.
- Full settings page inside the main window, including close-to-tray, remembered layout, log retention, and data folder access.
- TOML editor improvements: bundled `frpc` validation before save, character count, import, clear, select-all, and unsaved-change protection.
- Per-instance FRP status lookup and configuration reload. Configurations with an enabled frpc management API use hot reload; other foreground instances restart automatically, while background instances are restarted through their Windows service.
- Installer and uninstaller guard against overlapping package/install paths, an active FrpDesk process, and mismatches with the registered install directory.

## What is new in 0.1.2

- Auto-start switch in the settings page. Windows launches FrpDesk after sign-in and opens it minimized to the tray.
- The auto-start entry is a per-user Run key.
- The uninstaller removes the auto-start entry instead of leaving a dead link.

## Package

The Windows x64 package is self-contained and includes the bundled `frpc.exe` and its upstream license.

- Uninstall from Windows Apps.
- User profiles and logs remain under `%LOCALAPPDATA%\FrpDesk` after uninstall.

---

# FrpDesk 0.1.1

## Included

- Multiple independent FRP TOML instances.
- Foreground and Windows service execution modes.
- Automatic recovery for unexpected background exits.
- Classified diagnostics with copyable details.
- Per-instance quick actions and batch foreground start.
- Tray operation and remembered window layout.
- Full settings page inside the main window, including close-to-tray, remembered layout, log retention, and data folder access.
- TOML editor improvements: bundled `frpc` validation before save, character count, import, clear, select-all, and unsaved-change protection.
- Per-instance FRP status lookup and configuration reload. Configurations with an enabled frpc management API use hot reload; other foreground instances restart automatically, while background instances are restarted through their Windows service.
- Installer and uninstaller guard against overlapping package/install paths, an active FrpDesk process, and mismatches with the registered install directory.

## Package

The Windows x64 package is self-contained and includes the bundled `frpc.exe` and its upstream license.

- Uninstall from Windows Apps.
- User profiles and logs remain under `%LOCALAPPDATA%\FrpDesk` after uninstall.
