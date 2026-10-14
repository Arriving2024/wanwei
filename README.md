# 万维计划 · Wanwei

探索 Windows、Android、iOS 与 iPadOS 之间按需建立的 Wi-Fi 直连：无需共同路由器，并尽可能保留双方原 Wi-Fi。

**目前是连接实验室，不是可传文件的成品。** 文件传输、应用身份认证、吞吐测试、投屏及 Apple 跨平台互通尚未完成。欢迎参与源码与真机兼容性研究；不要将 Wi-Fi 代际、API 成功回调或构建通过视为互通证明。

## 当前进展

| 端 | 当前内容 | 验证边界 |
| --- | --- | --- |
| Windows 0.2.2 | WPF 能力探测、发现、限时广播、逐次确认配对、端点与清理报告 | 97 项本地逻辑检查；与一台 Android 设备曾建立真实 Direct 连接 |
| Android 0.2.5 | Kotlin 探测、扫描、配对与清理；默认关闭的手机组主偏好实验 | 5 项策略测试；最后成功真机基线为 0.2.4，0.2.5 两轮尝试未建连 |
| Apple P0 | SwiftUI 能力探测源码，目标 iOS/iPadOS 26+ | 已通过 GitHub Mac 不签名编译；签名、安装、真机无线能力未验证 |

维护者的 2026-10-12 本地实验曾观察到 Android 原 5 GHz Wi-Fi 与 2.4 GHz Direct 共存约 20 秒，双方端点匹配并完成清理。没有业务数据或连续互联网测试，不能推广为所有设备支持。2026-10-13 角色实验出现暂时断开 WLAN 的系统提示；另一轮默认对照未完成电脑确认。双 5 GHz、MLO 和聚合提速仍无结论。原始设备报告和个人媒体不在公开仓库中。

## 使用与构建

实验版下载：[GitHub 预发布页](https://github.com/Arriving2024/wanwei/releases/tag/lab-2026.10.14)。包含 Windows 便携 ZIP、Android Debug APK 和 SHA-256 校验文件；没有 Apple 安装包。

- Windows：便携实验包完整解压后运行 `Wanwei.Windows.exe`；自行构建可运行根目录 `Start-Windows.cmd`。见 [构建说明](docs/building.md) 和 [便携包说明](docs/windows-portable-package.md)。
- Android：使用 Android Studio 或 Gradle wrapper 构建 Debug APK，安装后名为“万维实验室”；[构建说明](docs/building.md) 列出 SDK 与权限要求。
- iPhone / iPad：可在 Windows 编辑源码，在兼容的 Mac + Xcode 上编译、签名和真机调试。开发者模式不能代替 Mac 构建环境，见 [Apple 上机步骤](docs/apple-p0-quickstart.md)。

Windows、Android 与 Apple 不签名编译的首轮完整 [云端检查](https://github.com/Arriving2024/wanwei/actions/runs/38498065696) 已通过。自动检查只验证构建和逻辑，不操作无线设备，不替代设备现场的确认、连接和保网验收。

## 开发方向

默认保留双方现有 Wi-Fi，再按实际能力选择直连路径。已连 5 GHz 时可优先尝试兼容的 2.4 GHz Direct；已连 2.4 GHz 时探索 5 GHz Direct，但不硬编码设备必然支持。遇到系统要求断开 WLAN 的提示，保网测试应拒绝并结束本轮。

先完成安全的小文件互传与负载下保网，再做自动选路、可选提速，最后研究多链路和投屏。见 [路线与验收](docs/roadmap.md)、[P0 协议草案](protocol/README.md)、[开源复用方案](docs/github-reuse-plan.md)。

## 参与

欢迎通过 Issue 提交脱敏后的机型/系统兼容性结果，通过 Pull Request 改进构建、状态恢复、协议和 UI。请阅读 [贡献说明](CONTRIBUTING.md)。当前最需要可重复的双端测试、Mac 编译验证，以及绑定真实 P2P 端点的安全传输实现。

项目原创源码与文档采用 [Apache-2.0](LICENSE)；第三方材料保持原许可，见 [NOTICE](NOTICE) 和 [第三方声明](THIRD_PARTY_NOTICES.md)。
