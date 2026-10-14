# 第三方参考源码声明

更新：2026-10-14。

本文记录随项目保存的第三方参考文件与构建入口。**Windows 自 v0.2.0 起，已改造采用 Microsoft WiFiDirect 示例的广播、Custom pairing 设置与 UI 线程连接流程；当前 v0.2.2 延续此范围。** Android AOSP 示例仍作流程参考，原 Java 文件未直接编译引入。完整许可证与原始源文件一并存放。项目原创源码按根目录 Apache-2.0 许可分发，不替代第三方材料原许可。

本文不是构建工具、SDK 或系统组件的完整依赖清单。Windows 便携包另附实际运行时版本、来源、哈希和许可，见包内 `DEPENDENCIES.json` 与 `licenses/`。Android Debug APK 和后续正式分发仍需按对应构建产物核对依赖。

## Microsoft Windows Universal Samples — WiFiDirect

- 上游：[`microsoft/Windows-universal-samples`](https://github.com/microsoft/Windows-universal-samples/tree/4eb2fcb499c5bc549e918920cfd2b64396a650d9/Samples/WiFiDirect/cs)
- 固定提交：`4eb2fcb499c5bc549e918920cfd2b64396a650d9`
- 原始版权：`Copyright (c) Microsoft Corporation`
- 许可证：MIT，完整文本见 [LICENSE](references/upstream/windows-wifidirect/LICENSE)。
- 保存位置：`references/upstream/windows-wifidirect/`
- 保存的源码：`Scenario1_Advertiser.xaml.cs`、`Scenario2_Connector.xaml.cs`、`ConnectionSettingsPanel.xaml.cs`。
- 来源及逐文件 SHA-256：[source-manifest.json](references/upstream/windows-wifidirect/source-manifest.json)。
- v0.1.1 采用范围：`Scenario1_Advertiser.xaml.cs` 的 publisher/listener 初始化、状态订阅与通过 `Dispose()` 拒绝连接请求的流程，改造位置为 [`apps/windows/DirectAdvertisement.cs`](apps/windows/DirectAdvertisement.cs)。该普通广播模式仍拒绝全部连接请求。
- v0.2.0 新增采用范围：参考 `Scenario1_Advertiser.xaml.cs` 与 `ConnectionSettingsPanel.xaml.cs` 的请求处理、Custom `PairAsync` 设置和在 UI 线程开始 `WiFiDirectDevice.FromIdAsync` 的流程，改造位置为 [`apps/windows/DirectPairing.cs`](apps/windows/DirectPairing.cs)。实际连接对象用于记录状态及端点对。原始参考文件保持不变，没有将整份示例直接作为项目源码编译。
- 本项目改造：用户逐次确认、PIN 交互、单次请求接纳、deferral 收尾、阶段超时、取消、晚到结果清理、原 AP 匿名采样及资源释放未确认时锁定重试。v0.2.2 分别限制等待首请求 60 秒、协商 45 秒和已连接观察 20 秒；Legacy 和自主组主均关闭，不删除已有 OS 配对、不修改默认路由。
- 未采用原示例中的热点模式和 socket、文件或消息收发代码。`Scenario2_Connector.xaml.cs` 继续作为连接与端点 API 参考，未直接编译引入。
- v0.2.1 新增的 GUI 每轮报告自动保存属于本项目实现，本次没有新增第三方运行时源码。

Microsoft 版权与 MIT 归属由本声明、改造文件中的来源说明及随附许可证共同记录。分发包含该代码的产物时须随附 MIT 许可证全文；后续采用范围变化继续更新此表。

## Android Open Source Project — WiFiDirectDemo

- 上游镜像：[`aosp-mirror/platform_development`](https://github.com/aosp-mirror/platform_development/tree/88f7870b2c652922dc2afb01c076737e8bc50130/samples/WiFiDirectDemo)
- 固定提交：`88f7870b2c652922dc2afb01c076737e8bc50130`
- 原始版权：`Copyright (C) 2011 The Android Open Source Project`
- 许可证：各源码版权头声明 Apache License, Version 2.0；完整文本见 [LICENSE-2.0.txt](references/upstream/android-wifidirect/LICENSE-2.0.txt)，文本来源为 [Apache 官方许可证](https://www.apache.org/licenses/LICENSE-2.0.txt)。该文本是补存的许可证副本，不冒充来自同一提交的仓库文件。
- 保存位置：`references/upstream/android-wifidirect/`
- 保存的源码：`DeviceDetailFragment.java`、`WiFiDirectActivity.java`、`WiFiDirectBroadcastReceiver.java`。
- 源码来源及逐文件 SHA-256：[source-manifest.json](references/upstream/android-wifidirect/source-manifest.json)。
- 当前用途：检查连接请求、连接变化回调和组主地址处理流程；保存的源码内容及版权头保持原样，未直接编译引入。[`DirectPairingSession.kt`](apps/android/app/src/main/java/dev/wanwei/androidlab/DirectPairingSession.kt) 是本项目 Kotlin 状态机，v0.2.0 加入用户选择/确认、连接证据、20 秒观测及有界清理；v0.2.1 加入最多 8 秒重新发现原目标，并在最终清理时停止发现。未采用旧示例的明文文件服务，也未将 AOSP 系统服务代码纳入运行时。

后续如复制或改造，应保留适用的版权及归属声明、附带 Apache 2.0 许可证、标明修改，并核查所采用范围是否有应随附的上游 NOTICE。当前只保存这三个源码文件，不宣称已审查整个上游仓库的所有组件。

## Gradle Wrapper 8.13

- 用官方 Gradle 8.13 生成 `apps/android/gradlew`、`gradlew.bat` 和 `gradle/wrapper/`。脚本原版权及 Apache-2.0 许可头保持原样，根目录附完整许可。
- Wrapper JAR SHA-256：`81a82aaea5abcc8ff68b3dfcb58b3c3c429378efd98e7433460610fecd7ae45f`，对照 [官方 JAR 校验](https://services.gradle.org/distributions/gradle-8.13-wrapper.jar.sha256) 核实。
- 分发 ZIP SHA-256：`20f1b1176237254a6fc204d8434196fa11a4cfb387567519c61556e8710aed78`，写入 wrapper 配置并对照 [官方分发校验](https://services.gradle.org/distributions/gradle-8.13-bin.zip.sha256)。ZIP 本身不纳入源码仓库。
- Gradle 本体、Android SDK、AGP、Kotlin 等构建工具使用各自许可，不属于运行时应用代码。下载的 SDK 与缓存保持本地。

## 尚未纳入的参考候选

LocalSend 和 Apple Wi-Fi Aware 官方示例目前仅被开发文档引用，未作为源码或运行时依赖纳入项目。引用一个网页不等于已完成其许可核验。若后续采用任何代码或资产，须按实际版本及文件补充来源、许可证、版权声明和修改记录；Apple 示例下载包许可尚未确认前，不复制代码。

本项目现有 [`apps/apple/`](apps/apple/README.md) 能力探测器为原创 SwiftUI 工程，按公开 API 文档实现；没有下载或复制 Apple 官方示例工程，也没有第三方源码或包依赖。Windows 静态检查通过不代表 Mac 编译、签名或真机运行通过。
