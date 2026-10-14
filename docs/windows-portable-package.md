# Windows 便携实验版

当前应用版本仍为 **0.2.2**。这是让其他测试者免装开发 SDK 即可运行的分发包，软件功能没有因此升级：当前能做能力检查、发现和用户确认后的系统配对/直连实验，**文件传输、应用身份认证、测速、iOS 互通和多链路聚合仍未实现**。

## 使用

完整解压 `wanwei-windows-lab-0.2.2-win-x64.zip`，双击文件夹内的 `Wanwei.Windows.exe`。不要仅复制 EXE；同目录的 DLL 和运行时文件必须保留。优先面向 Windows 11 x64，其他机器的 Wi-Fi Direct 能力仍取决于网卡、驱动和系统。

本包包含 .NET 运行时，不要求接收电脑下载 .NET SDK。它不安装服务、驱动或开机启动项。普通启动后的报告默认写入 `%LOCALAPPDATA%\WanweiLab\Reports`。连接实验由使用者在界面发起；若系统提示将断开现有 WLAN，拒绝该轮实验，不应计为保网成功。

应用尚未代码签名，Windows 可能显示未知发布者。这一包不绕过系统安全设置，也不冒用发布者身份。项目代码和第三方组件的许可分别见包内 `LICENSE`、`licenses/` 与 `DEPENDENCIES.json`。

## 重新生成

在仓库根目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Package-Windows.ps1
```

脚本使用项目固定的 SDK，先验证原项目锁文件，再执行 `dotnet publish -c Release -r win-x64 --self-contained true`。运行时发布使用单独的锁文件，不修改源码锁文件。每次新建 `artifacts/packages/<时间戳>-<随机标识>/`，保留已有产物。

目录中包含 ZIP、SHA-256、`verification.json`、发布日志和解压核验目录。每个归档附带文件哈希清单，以及这次恢复的准确 .NET / WindowsDesktop 运行时许可、声明和版本。Windows SDK NuGet 包的官方许可链接返回 RTF 文档，脚本原样保存；CsWinRT MIT 许可按照实际 `WinRT.Runtime.dll` 产品版本内的源提交获取。两者都记录来源和哈希。

脚本对解压后的 EXE 运行已有 `--self-test`，不扫描、不广播、不配对，也不操作手机；使用空的 `DOTNET_ROOT`，并通过主机日志确认载入包内 `coreclr.dll`。这是不依赖开发 SDK 的本机自检证据；机器网络没有被关闭，不能冒充独立干净电脑的完整验收。另需在测试者电脑上验收 GUI、系统权限和真实无线兼容性。

2026-10-14 已完成一次最终包核验：压缩包 72,311,598 字节，包内 415 个清单文件逐一解压校验通过，EXE 的 97 项本地逻辑检查通过。运行时为 .NET / WindowsDesktop 10.0.12，应用 EXE 的签名状态为 `NotSigned`。此轮未启动无线实验、手机操作或 GUI 验收。该包的 SHA-256 为 `C4155F7DAEA6F9544F2FFEB06EC32468CC157EDAA1711CBC002E97FD3BEB1ABF`；重新构建会产生新的时间戳、清单及归档哈希。

目录式发布不启用裁剪或单文件打包，以保留 WPF 与 WinRT 运行时文件。随包运行时不会自动使用系统后续 .NET 安全补丁；应更新工具链、重新发布并提供新包。

依据：[Microsoft .NET 部署说明](https://learn.microsoft.com/en-us/dotnet/core/deploying/)、[Windows SDK 包所指向的许可](https://aka.ms/WinSDKLicenseURL)、[CsWinRT 许可](https://github.com/microsoft/CsWinRT/blob/master/LICENSE)。
