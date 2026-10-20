# FrpDesk

FrpDesk 用于管理多个独立的 FRP 客户端配置。Windows 桌面版与飞牛原生版是两个独立程序，各自构建、安装并保存运行数据。

| 版本 | 源码 | 技术栈 | 文档 |
|---|---|---|---|
| Windows 桌面版 | `FrpDesk.WinUI/`、`FrpDesk.Runtime/` | C#、WinUI 3、.NET 10 | [使用、构建与发布](README-WINDOWS.md) · [更新记录](RELEASE_NOTES.md) |
| 飞牛原生版 | [`fnos-frpdesk/`](fnos-frpdesk/) | Go、静态网页 | [飞牛版说明](fnos-frpdesk/README.md) |

## 下载

- Windows x64 `0.1.4`：[安装程序](https://github.com/YM040923/FrpDesk/releases/download/windows-v0.1.4/FrpDesk-0.1.4-win-x64-setup.exe) · [安装程序 SHA-256](https://github.com/YM040923/FrpDesk/releases/download/windows-v0.1.4/FrpDesk-0.1.4-win-x64-setup.exe.sha256) · [便携版 ZIP](https://github.com/YM040923/FrpDesk/releases/download/windows-v0.1.4/FrpDesk-0.1.4-win-x64.zip) · [ZIP SHA-256](https://github.com/YM040923/FrpDesk/releases/download/windows-v0.1.4/FrpDesk-0.1.4-win-x64.zip.sha256)
- 飞牛 x86_64 `0.1.13`：[FPK 安装包](https://github.com/YM040923/FrpDesk/releases/download/fnos-v0.1.13/FrpDesk-0.1.13-x86_64.fpk) · [SHA-256](https://github.com/YM040923/FrpDesk/releases/download/fnos-v0.1.13/FrpDesk-0.1.13-x86_64.fpk.sha256)

Windows 版的系统要求、安装步骤和本地构建命令见 [Windows 文档](README-WINDOWS.md)。飞牛版的构建与升级说明见 [飞牛文档](fnos-frpdesk/README.md)。

## 仓库与许可

此 GitHub 仓库保持私有。FrpDesk 本身尚未声明可供第三方再分发的许可证；请勿将源码或应用包公开转载、修改后分发。Windows 包含的 FRP 客户端遵循 [`assets/frp-LICENSE`](assets/frp-LICENSE) 中的上游许可证，该许可证不代表 FrpDesk 本身采用相同许可证。

飞牛 FPK 先前通过本地包结构检查；本轮未重新验证 NAS 应用中心的原位升级和数据保留。
