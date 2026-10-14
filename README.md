# 万维计划 · Wanwei

探索 Windows、Android、iOS 与 iPadOS 之间按需建立的 Wi-Fi 直连：无需共同路由器，并尽可能保留双方原 Wi-Fi。

**当前 0.3.0 已实现受限的安全文件实验，Windows ↔ 荣耀真机互传仍待验收。** 仅收发程序生成的 1 MiB 测试数据，使用 TLS 1.3 双向认证和完整公钥指纹绑定；尚不支持选择用户文件。普通确认流程、负载下保网、吞吐、投屏及 Apple 跨平台互通仍需验证。欢迎参与源码与真机兼容性研究；不要将 Wi-Fi 代际、API 成功回调或构建通过视为互通证明。

## 当前进展

| 端 | 当前内容 | 验证边界 |
| --- | --- | --- |
| Windows 0.3.0 | WPF 原生 Direct 建链；绑定实际直连端点的 P0 文件收发、TLS 1.3、公钥确认与安全保存 | 178 项本地检查通过，包括本机 TLS 角色互换与错误指纹拒绝；0.3.0 物理设备互传待验收 |
| Android 0.3.0 | Kotlin 原生 Direct 建链；AndroidKeyStore 身份、固定 1 MiB 文件收发、逐次接收确认与诊断报告 | 编译、Lint、24 项本地测试及 Debug APK 签名校验通过；0.3.0 物理设备互传待验收 |
| Apple P0 | SwiftUI 能力探测源码，目标 iOS/iPadOS 26+ | 已通过 GitHub Mac 不签名编译；签名、安装、真机无线能力未验证 |

维护者的 2026-10-12 本地实验曾观察到 Android 原 5 GHz Wi-Fi 与 2.4 GHz Direct 共存约 20 秒，双方端点匹配并完成清理。没有业务数据或连续互联网测试，不能推广为所有设备支持。2026-10-13 角色实验出现暂时断开 WLAN 的系统提示；另一轮默认对照未完成电脑确认。双 5 GHz、MLO 和聚合提速仍无结论。原始设备报告和个人媒体不在公开仓库中。

## 使用与构建

首个连接实验版下载：[GitHub 预发布页](https://github.com/Arriving2024/wanwei/releases/tag/lab-2026.10.14)。该页的 Windows 0.2.2、Android 0.2.5 为此前建连版本，包含便携 ZIP、Debug APK 和校验文件；不包含 0.3.0 新增文件实验或 Apple 安装包。请以各版本实际资产和检查结果为准。

- Windows：便携实验包完整解压后运行 `Wanwei.Windows.exe`；自行构建可运行根目录 `Start-Windows.cmd`。见 [构建说明](docs/building.md) 和 [便携包说明](docs/windows-portable-package.md)。
- Android：使用 Android Studio 或 Gradle wrapper 构建 Debug APK，安装后名为“万维实验室”；[构建说明](docs/building.md) 列出 SDK 与权限要求。
- iPhone / iPad：可在 Windows 编辑源码，在兼容的 Mac + Xcode 上编译、签名和真机调试。开发者模式不能代替 Mac 构建环境，见 [Apple 上机步骤](docs/apple-p0-quickstart.md)。

Windows、Android 与 Apple 不签名编译的首轮完整 [云端检查](https://github.com/Arriving2024/wanwei/actions/runs/38498065696) 已通过。自动检查只验证构建和逻辑，不操作无线设备，不替代设备现场的确认、连接和保网验收。

0.3.0 文件实验需先核对双方完整 64 位 SPKI SHA-256 指纹，监听端只绑定当前原生 P2P 接口，发送端也绑定本机实际 P2P 地址。端口与指纹目前通过线下实验参数交换。为排除远程操作工具的确认延迟，调试版另有显式启用的 USB 辅助固定数据实验；USB 只传控制参数，文件内容仍必须走已验证的无线 Direct socket。此实验使用受限预授权，并在报告中与人工点击接收区分。见 [文件实验步骤与边界](docs/p0-file-transfer.md)。

Android Debug APK 已具备安装所需的调试签名；Windows 尚未做可信发布者代码签名。个人设备报告、私钥、受保护身份文件、实验 nonce、手机照片及本机路径资料留在本地，不上传公开仓库。

## 开发方向

默认保留双方现有 Wi-Fi，再按实际能力选择直连路径。已连 5 GHz 时可优先尝试兼容的 2.4 GHz Direct；已连 2.4 GHz 时探索 5 GHz Direct，但不硬编码设备必然支持。遇到系统要求断开 WLAN 的提示，保网测试应拒绝并结束本轮。

下一步验收双向 1 MiB 文件的真实保存与负载下保网，再做普通用户文件、自动选路和可选提速，最后研究多链路与投屏。见 [路线与验收](docs/roadmap.md)、[P0 协议契约](protocol/README.md)、[开源复用方案](docs/github-reuse-plan.md)。

## 参与

欢迎通过 Issue 提交脱敏后的机型/系统兼容性结果，通过 Pull Request 改进构建、状态恢复、协议和 UI。请阅读 [贡献说明](CONTRIBUTING.md)。当前最需要可重复的双端收发、普通确认与保网测试，以及 Mac 上 Apple 签名和真机验证。

项目原创源码与文档采用 [Apache-2.0](LICENSE)；第三方材料保持原许可，见 [NOTICE](NOTICE) 和 [第三方声明](THIRD_PARTY_NOTICES.md)。
