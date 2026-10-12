# FrpDesk 飞牛原生版

这是 FrpDesk 的飞牛原生应用源码，独立构建为 x86_64 `.fpk` 安装包。它与仓库根目录的 Windows 桌面版共用产品名称，但使用各自的程序、安装包和运行数据。

## 版本与组成

当前源码版本由 [package.json](package.json) 中的 `version` 字段定义。

- `core/`：Go 编写的本地 Core，负责配置、FRP 进程和 Web API。
- `web/`：内置静态网页界面。
- `packaging/fnos/`：fnOS manifest、生命周期脚本、权限和安装向导。
- `resources/frpc/linux/frpc`：Linux x86_64 FRP 客户端；对应许可证在 `resources/frp-LICENSE`。
- `scripts/`：构建运行目录、FPK 打包及本地包检查脚本。

## 构建

需要 Go、Windows PowerShell，以及 WSL（含 Bash、Python 3 和 tar）。Linux FRP 客户端及 `fnpack` 打包工具已放在源码目录中。

先在 Windows PowerShell 中从仓库根目录进入 `fnos-frpdesk/`，交叉编译 Core 并准备运行目录：

```powershell
Set-Location .\fnos-frpdesk
.\scripts\build-core.ps1
.\scripts\prepare-runtime.ps1
```

将包含 `dist/` 的 `fnos-frpdesk/` 目录放到 WSL 的 Linux 文件系统，再在 WSL 中进入该目录。打包脚本调用 Linux 版 `fnpack`，所以这一步不要在 Windows PowerShell 或 NTFS 挂载目录中运行。先确保输入的 Linux 可执行文件权限正确，然后打包与检查：

```bash
chmod +x ./tools-fnpack \
  ./resources/frpc/linux/frpc \
  ./dist/fnos/frpdesk/bin/frpdesk-core \
  ./dist/fnos/frpdesk/resources/frpc/linux/frpc
bash ./scripts/build-fpk.sh
bash ./scripts/verify-fpk.sh
bash ./scripts/verify-fnos-package.sh
```

生成的安装包位于 `dist/fnos/`，文件名格式为 `FrpDesk-<版本>-x86_64.fpk`。打包脚本会在 Linux 临时目录组装归档并设置 fnOS 所需权限。

本地包检查通过只说明 FPK 结构和内容符合检查项；实际安装、应用中心升级和设备运行仍需在 fnOS 设备上单独验证。
