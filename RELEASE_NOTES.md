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
- The installer verifies every package file against `release-manifest.json` and refuses upgrades while configured FRP Windows services are still present, preventing services from continuing to use an older installation.

## Package

The Windows x64 package is self-contained and includes the bundled `frpc.exe` and its upstream license.

- Run `Install-FrpDesk.cmd` from the extracted package to install it for the current Windows user.
- Uninstall from Windows Apps or run `Uninstall-FrpDesk.cmd`.
- User profiles and logs remain under `%LOCALAPPDATA%\FrpDesk` after uninstall.
