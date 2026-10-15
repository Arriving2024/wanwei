# 万维计划 · Wanwei

探索 Windows、Android、iOS 与 iPadOS 之间按需建立的 Wi-Fi 直连：无需共同路由器，并尽可能保留双方原 Wi-Fi。

**Windows / Android 0.3.1 已通过双向固定数据实测，每个方向各 1 MiB。** 实验使用 TLS 1.3 双向认证和完整公钥指纹绑定，两端保存及独立磁盘哈希核验通过。当前仅收发程序生成的测试数据，尚不支持选择用户文件；普通确认流程、连续保网、吞吐、投屏及 Apple 跨平台互通仍需验证。

## 当前进展

| 端 | 当前内容 | 验证边界 |
| --- | --- | --- |
| Windows 0.3.1 | WPF 原生 Direct 建链、只读系统关联检查；绑定实际直连端点的 P0 文件收发与 TLS 1.3 | 213 项本地检查通过，包括 TLS 角色互换与错误指纹拒绝；与 Android 0.3.1 的双向固定数据收发已实测 |
| Android 0.3.1 | Kotlin 原生 Direct 建链；AndroidKeyStore 身份、固定 1 MiB 收发；保存兼容修复 | 30 项本地检查通过；更新 APK 已安装，应用身份保持；双向收发、保存与独立哈希核验通过 |
| Apple P0 | SwiftUI 能力探测源码，目标 iOS/iPadOS 26+ | 已通过 GitHub Mac 不签名编译；签名、安装、真机无线能力未验证 |

2026-10-15 的本地实验在同一原生 Wi-Fi Direct 连接内完成两个方向的独立 TLS 文件传输，实际 native 与 socket 端点互逆，接收端保存后才确认 COMPLETE。双方原 AP 在采样点未变化，未验证连续互联网；Windows 本应用资源释放已确认，Android 原生连接清理仍未确认。该实验使用 USB 辅助控制和固定夹具预授权，USB 未搬运文件内容，不能当作普通用户操作流程已通过。详见[文件实验结果与边界](docs/p0-file-transfer.md)。

此前仅在特定设备上观察到原 5 GHz Wi-Fi 与 2.4 GHz Direct 共存；双 5 GHz、MLO 和聚合提速仍无结论。原始设备报告和个人媒体不在公开仓库中。

## 使用与构建

下载 [0.3.1 预发布版](https://github.com/Arriving2024/wanwei/releases/tag/p0-0.3.1)，包含 Windows 便携 ZIP、Android Debug APK 和 SHA-256 校验文件。Android 保存兼容修复已在上述设备组合上验证；连续重复投送与普通确认流程仍待验收。没有 Apple 签名安装包。此前的 [0.3.0](https://github.com/Arriving2024/wanwei/releases/tag/p0-0.3.0) 和[连接实验版](https://github.com/Arriving2024/wanwei/releases/tag/lab-2026.10.14)保留为历史版本。

- Windows：便携实验包完整解压后运行 `Wanwei.Windows.exe`；自行构建可运行根目录 `Start-Windows.cmd`。见 [构建说明](docs/building.md) 和 [便携包说明](docs/windows-portable-package.md)。
- Android：使用 Android Studio 或 Gradle wrapper 构建 Debug APK，安装后名为“万维实验室”；[构建说明](docs/building.md) 列出 SDK 与权限要求。
- iPhone / iPad：可在 Windows 编辑源码，在兼容的 Mac + Xcode 上编译、签名和真机调试。开发者模式不能代替 Mac 构建环境，见 [Apple 上机步骤](docs/apple-p0-quickstart.md)。

0.3.1 源码提交 `f37fe88` 的 Windows、Android 与 Apple 未签名编译三个[云端检查任务](https://github.com/Arriving2024/wanwei/actions/runs/38673423408)均已通过。表中的检查计数来自本地验证；自动检查不能替代现场的确认、保存和保网验收。

文件实验需先核对双方完整 64 位 SPKI SHA-256 指纹，监听与发送均绑定实际原生 P2P 地址。端口及身份目前通过线下参数交换；调试版的 USB 辅助入口仅限固定数据实验，报告明确区分实验预授权与人工点击接收。见[实验步骤](docs/p0-file-transfer.md)。

Android Debug APK 已具备安装所需的调试签名；Windows 尚未做可信发布者代码签名。个人设备报告、私钥、受保护身份文件、实验 nonce、手机照片及本机路径资料留在本地，不上传公开仓库。

## 开发方向

默认保留双方现有 Wi-Fi，再按实际能力选择直连路径。已连 5 GHz 时可优先尝试兼容的 2.4 GHz Direct；已连 2.4 GHz 时探索 5 GHz Direct，但不硬编码设备必然支持。遇到系统要求断开 WLAN 的提示，保网测试应拒绝并结束本轮。

下一步解决 Android 原生连接清理，验收普通确认流程、负载下保网及用户文件，再推进自动选路、性能、多链路与投屏。见[路线与验收](docs/roadmap.md)、[P0 协议契约](protocol/README.md)、[开源复用方案](docs/github-reuse-plan.md)。

## 参与

欢迎通过 Issue 提交脱敏后的机型/系统兼容性结果，通过 Pull Request 改进构建、状态恢复、协议和 UI。请阅读 [贡献说明](CONTRIBUTING.md)。当前最需要可重复的双端收发、普通确认与保网测试，以及 Mac 上 Apple 签名和真机验证。

项目原创源码与文档采用 [Apache-2.0](LICENSE)；第三方材料保持原许可，见 [NOTICE](NOTICE) 和 [第三方声明](THIRD_PARTY_NOTICES.md)。
