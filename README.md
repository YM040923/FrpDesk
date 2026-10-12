# FrpDesk

FrpDesk 是一款管理 FRP 客户端与 TOML 配置的工具，包含 **Windows 桌面版**和**飞牛原生版**。两版使用不同的程序和安装包，源码分别放在各自目录。

| 版本 | 源码位置 | 技术与安装包 | 说明 |
|---|---|---|---|
| Windows 桌面版 | 仓库根目录 | C#、WPF、.NET 10；Windows x64 ZIP 安装包 | [Windows 使用、构建与发布说明](README-WINDOWS.md)，[Windows 更新记录](RELEASE_NOTES.md) |
| 飞牛原生版 | [`fnos-frpdesk/`](fnos-frpdesk/) | Go Core、静态网页界面；飞牛 x86_64 `.fpk` 安装包 | [飞牛版源码与构建说明](fnos-frpdesk/README.md) |

## 源码结构

- 根目录的 `App.xaml`、`MainWindow.xaml`、`Models/`、`Services/`、`Views/` 和 `scripts/` 属于 Windows 桌面版。
- `fnos-frpdesk/core/`、`web/`、`packaging/`、`resources/` 和 `scripts/` 属于飞牛原生版。
- 两版都支持多实例和 TOML 配置，但各自独立构建、安装和保存运行数据。

## 版本

- Windows 桌面版：`0.1.1`。
- 飞牛原生版：`0.1.13`，版本号定义在 [`fnos-frpdesk/package.json`](fnos-frpdesk/package.json)。

## 构建入口

- Windows：按 [Windows 构建说明](README-WINDOWS.md)运行 `scripts/Fetch-Frpc.ps1` 和发布脚本。
- 飞牛：按[飞牛构建说明](fnos-frpdesk/README.md)构建 Core、准备运行目录并生成 `.fpk`。

Windows 发布压缩包和飞牛 `.fpk` 都是构建产物，不作为源码提交；可在对应的 `artifacts/` 或 `dist/` 目录生成。

FrpDesk 是独立项目，不是 FrpPilot。当前仓库为私有仓库；FrpDesk 自身的再分发许可证尚未声明。
