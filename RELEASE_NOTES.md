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
