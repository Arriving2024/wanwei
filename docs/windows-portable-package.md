# Windows 便携实验版

当前实验版本为 **0.3.1**。已实现固定 1 MiB 合成文件、TLS 1.3 双向应用认证、完整公钥指纹绑定及安全保存；当前实机结果与待验项目见[文件实验记录](p0-file-transfer.md)。尚不支持任意用户文件，测速、iOS 互通、多链路聚合和投屏仍待开发或验证。

当前下载：[0.3.1 实验版](https://github.com/Arriving2024/wanwei/releases/tag/p0-0.3.1)。此前的 [0.3.0](https://github.com/Arriving2024/wanwei/releases/tag/p0-0.3.0) 与 [2026-10-14 连接实验版](https://github.com/Arriving2024/wanwei/releases/tag/lab-2026.10.14)保留；下文历史核验数据不用于新包。

## 使用

完整解压 `wanwei-windows-lab-0.3.1-win-x64.zip`，双击文件夹内的 `Wanwei.Windows.exe`。本地重新打包时版本名自动来自工程配置。不要仅复制 EXE；同目录的 DLL 和运行时文件必须保留。优先面向 Windows 11 x64，其他机器的 Wi-Fi Direct 能力仍取决于网卡、驱动和系统。

本包包含 .NET 运行时，不要求接收电脑下载 .NET SDK。它不安装服务、驱动或开机启动项。普通启动后的报告默认写入 `%LOCALAPPDATA%\WanweiLab\Reports`。连接实验由使用者在界面发起；若系统提示将断开现有 WLAN，拒绝该轮实验，不应计为保网成功。

应用尚未代码签名，Windows 可能显示未知发布者。这一包不绕过系统安全设置，也不冒用发布者身份。项目代码和第三方组件的许可分别见包内 `LICENSE`、`licenses/` 与 `DEPENDENCIES.json`。

普通文件实验需双方核对完整公钥指纹、线下交换动态端口，并逐次接受 OFFER。开发用 `--usb-p0-lab` 入口需要显式固定数据预授权；USB 仅承载控制参数，1 MiB 文件仍走已验证的原生 Direct socket。报告不会将辅助预授权伪记为人工接收。详见 [文件实验说明](p0-file-transfer.md)。原始设备报告、身份与密钥文件、实验 nonce、个人照片留在本地，不公开上传。

0.3.2 开发版增加“开始 1 MiB 文件互传”入口，同次点击明确开启文件实验，避免漏勾后仅观察连接20秒。等主界面实际显示“已开始等待”再让手机扫描；两次配对确认与建连仍共用45秒，连接后文件窗口最多保留180秒。新提示增加有界任务栏闪烁及实际窗口绘制状态记录，不能据此认定用户已看到提示；窗口改进后的现场反向收发仍待验证。0.3.2 默认自动交换端口，首次完整公钥核对与逐次收件确认保留；上述0.3.1公开包不含这些改动。

## 重新生成

在仓库根目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Package-Windows.ps1
```

脚本使用项目固定的 SDK，先验证原项目锁文件，再执行 `dotnet publish -c Release -r win-x64 --self-contained true`。运行时发布使用单独的锁文件，不修改源码锁文件。每次新建 `artifacts/packages/<时间戳>-<随机标识>/`，保留已有产物。

目录中包含 ZIP、SHA-256、`verification.json`、发布日志和解压核验目录。每个归档附带文件哈希清单，以及这次恢复的准确 .NET / WindowsDesktop 运行时许可、声明和版本。Windows SDK NuGet 包的官方许可链接返回 RTF 文档，脚本原样保存；CsWinRT MIT 许可按照实际 `WinRT.Runtime.dll` 产品版本内的源提交获取。两者都记录来源和哈希。

脚本对解压后的 EXE 运行 `--self-test`，不扫描、不广播、不配对，也不操作手机；本地检查包含本机回环 TLS 与固定数据测试。使用空的 `DOTNET_ROOT`，并通过主机日志确认载入包内 `coreclr.dll`。这是不依赖开发 SDK 的本机自检证据；机器网络没有被关闭，不能冒充独立干净电脑或物理无线互传的完整验收。另需在测试者电脑上验收 GUI、系统权限和真实无线兼容性。

2026-10-15 的 Windows 0.3.1 发布包：ZIP 为 72,371,278 字节，解压文件哈希逐项核验、解压后 EXE 的 213 项本地检查及包内运行时加载验证均通过，签名状态为 `NotSigned`。SHA-256 为 `71A2DE08F25598F98CA9DB099C8DD897B0275316F705F4B03F5A031362EE7D94`。该次打包没有执行无线操作；同版本源码的独立实机结果见[文件实验记录](p0-file-transfer.md)。

2026-10-14 的历史 0.2.2 公开包已按补齐后的许可重新生成：压缩包 72,313,045 字节，包内 416 个清单文件逐一解压校验通过，EXE 的 97 项本地逻辑检查通过。已单独确认解压包包含根 `NOTICE`，且它、第三方声明及 Microsoft 示例来源清单都与当时源码逐字节匹配。运行时为 .NET / WindowsDesktop 10.0.12，应用 EXE 的签名状态为 `NotSigned`。此轮未启动无线实验、手机操作或 GUI 验收。该包的 SHA-256 为 `8099EC87DB52BD20706EB2CFCECBBD7D8F5EFB006F6AC986EF047131349B3443`；重新构建会产生新的时间戳、清单及归档哈希。

2026-10-14 对 Windows 0.3.0 的本地核验：ZIP 包 72,364,479 字节，清单文件逐一解压校验通过，解压后 EXE 的 178 项本地检查通过，并核实加载包内运行时。EXE 为 `NotSigned`；此核验未进行无线互传、GUI 或干净机器验收。该包 SHA-256 为 `A61D113BFDE80B779E4572C652F55EEE35DE92562DA1D2E4E6E162810DAFD13D`，与 GitHub 资产摘要一致。

目录式发布不启用裁剪或单文件打包，以保留 WPF 与 WinRT 运行时文件。随包运行时不会自动使用系统后续 .NET 安全补丁；应更新工具链、重新发布并提供新包。

依据：[Microsoft .NET 部署说明](https://learn.microsoft.com/en-us/dotnet/core/deploying/)、[Windows SDK 包所指向的许可](https://aka.ms/WinSDKLicenseURL)、[CsWinRT 许可](https://github.com/microsoft/CsWinRT/blob/master/LICENSE)。
