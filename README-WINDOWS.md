# FrpDesk Windows 桌面版

FrpDesk Windows 版用于在一台电脑上管理多个独立的 `frpc` TOML 配置。当前版本为 `0.1.8`，使用 C#、WinUI 3、Windows App SDK 2.5.1 和 .NET 10。

## 系统要求

- Windows x64；应用最低目标为 Windows 10 版本 1809（build 17763）。
- 安装包为自包含版本，不要求另外安装 .NET Runtime。
- Windows 11 可使用 Mica 背景；Acrylic 是否可用取决于 Windows 版本和系统效果设置。

## 功能

- 在运行概览、隧道管理、隧道编辑、运行日志和设置页面之间切换。
- 导入、创建、编辑和管理多个 TOML 配置；每个配置有独立的隧道列表和运行状态。
- 查看 `frpc` 输出与连接诊断；复制隧道地址时，HTTP/HTTPS 复制完整 URL，TCP/UDP 复制主机和端口。
- 以前台进程运行隧道，或按配置启用 Windows 后台服务。后台服务可在用户退出桌面会话后继续运行；启用、关闭和移除服务需要管理员授权。
- 设置窗口关闭时是否收进系统托盘、是否登录后自动启动、日志保留上限，以及窗口背景材质。
- 背景材质可选 Mica、Mica Alt、Acrylic 和 Acrylic Thin；选择即时预览并保存在本机。关闭系统透明效果或启用高对比度时，应用回退到纯色背景。材质由 Windows 合成，壁纸、系统版本和效果设置都会影响实际观感；内容面板保持不透明以保证可读性。

后台服务不会自动为已有配置启用。意外退出时，`frpc` 会按 30 秒、1 分钟、2 分钟和 4 分钟间隔重试；达到上限后需要在应用中重新启用服务。

## 安装与数据

- **安装程序**：运行 `FrpDesk-0.1.8-win-x64-setup.exe`，按提示安装到 `Program Files`。安装程序需要管理员授权，用于管理 Windows 服务。
- **便携版**：解压 `FrpDesk-0.1.8-win-x64.zip` 后运行 `FrpDesk.exe`。便携 ZIP 不是安装程序；启用后台服务时仍会请求管理员授权。
- **升级前**：退出 FrpDesk，并在应用中关闭所有后台服务。若仍有 `FrpDesk-Instance-*` 服务注册，安装程序会停止并提示先处理服务。
- **卸载**：从 Windows“已安装的应用”卸载。卸载程序只清理 FrpDesk 自有的服务和程序文件；配置、TOML 和本地日志保留在 `%LOCALAPPDATA%\FrpDesk`。

配置文件是本地普通文件。若 TOML 中含 FRP Token 或其他凭据，应用不会对其做额外加密；请保护 Windows 用户账户，不要在未检查和脱敏前分享配置或日志。

## 本地构建与测试

需要 Windows、.NET 10 SDK 和 Inno Setup 6（仅生成安装程序时需要）。在仓库根目录运行：

```powershell
.\scripts\Fetch-Frpc.ps1
dotnet restore .\FrpDesk.sln
dotnet test .\tests\FrpDesk.Tests\FrpDesk.Tests.csproj -c Release --no-restore
dotnet run --project .\FrpDesk.WinUI\FrpDesk.WinUI.csproj
```

`Fetch-Frpc.ps1` 下载官方 FRP `0.71.0` Windows x64 压缩包，先校验 SHA-256，再将 `frpc.exe` 和上游许可证放入被 Git 忽略的 `assets/`。发布包会包含这两个文件。

## 生成发布包

先完成上面的依赖恢复和 FRP 客户端下载，再运行：

```powershell
.\scripts\Publish-Release.ps1 -Version 0.1.8
```

脚本在 `artifacts/` 生成自包含应用目录、便携 ZIP、Inno Setup 安装程序及 SHA-256 校验文件。未提供 Authenticode 证书时，应用和安装程序不会签名，Windows SmartScreen 可能显示警告。

## 隐私与许可

FrpDesk 将配置、设置和诊断数据保存在本机 `%LOCALAPPDATA%\FrpDesk`。运行时会启动随包提供的 `frpc.exe`，并按用户配置连接 FRP 服务端。

FrpDesk 本身尚未声明再分发许可证。随包 FRP 客户端适用 [`assets/frp-LICENSE`](assets/frp-LICENSE)；该上游许可证不覆盖 FrpDesk。
