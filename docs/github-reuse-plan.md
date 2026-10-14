# 万维计划：GitHub 复用清单与下一步

更新：2026-10-14。目标是减少重复开发，同时保留核心要求：**无需共同局域网，按需建立无线直连，默认保留原 Wi-Fi**。

项目采用公开仓库与 Apache-2.0 许可路线。公开源码、内部设备调试和软件发布是三个不同环节：开源不代表已经完成文件传输，封装安装包也不会自动补齐认证、兼容性与保网能力。第三方代码保留各自许可，实际采用范围以 [第三方声明](../THIRD_PARTY_NOTICES.md) 为准。

## 当前进度

Windows 0.2.2 与 Android 0.2.5 已有安装运行形态、发现、系统配对、Direct 状态与端点报告、有界退出。Android 0.2.5 增加组主偏好实验；它不是 5 GHz 开关。

项目组在 2026-10-12 使用 Windows 0.2.2 与 Android 0.2.4 观察到一次双方实际建连、端点对应，以及手机 5 GHz 原 Wi-Fi 与 2.4 GHz Direct 共存；约 20 秒无业务流量的采样期间，双方原接入点未变。10 月 13 日的角色偏好实验未建立连接，手机系统出现会暂时断开 WLAN 的提示；另一次默认对照未完成电脑确认，不能用于判断硬件能力。

这些是项目组的本地设备观测摘要。原始设备日志和现场照片不随公共源码发布，读者不能仅凭本仓库独立复核这些历史结论；应按公开源码重新在自己的设备上验证。它们不证明连续互联网业务、稳定成功率、双 5 GHz、MLO 或吞吐量。**应用身份认证、TLS 与真实文件收发仍未实现。**

## 四项最值得使用的来源

| 来源与固定版本 | 采用程度 | 下一步能减少的工作 | 不能替代的工作 |
| --- | --- | --- | --- |
| [Microsoft WiFiDirect 示例](https://github.com/microsoft/Windows-universal-samples/tree/4eb2fcb499c5bc549e918920cfd2b64396a650d9/Samples/WiFiDirect/cs)，MIT | 已改造广播、监听、Custom pairing 与 UI 线程建链流程 | 延续端点绑定和资源生命周期做法 | 桌面应用的有界确认、取消、迟到回调、实际路径核对、应用认证与文件收发 |
| [AOSP WiFiDirectDemo](https://github.com/aosp-mirror/platform_development/tree/88f7870b2c652922dc2afb01c076737e8bc50130/samples/WiFiDirectDemo)，Apache-2.0 | 已保存参考；本项目 Kotlin 状态机未直接编译其 Java 文件 | 连接变化回调、组主信息与接口读取的流程参考 | 当前权限、厂商差异、连接请求不等于连接成功、现有组归属与安全清理 |
| [LocalSend v1.18.2](https://github.com/localsend/localsend/tree/af0416be50770a97760f7070684bc667b759a15c)，Apache-2.0 | 新增重点参考；未引入源码或依赖 | 接收确认、会话与取消、流式读写、校验失败处理，尤其 TLS 身份不匹配时不得泄漏元数据或文件的测试思路 | 无线建链、只走 Direct 的地址绑定、可信身份初次确认，以及万维自身协议实现 |
| [scrcpy v5.0.1](https://github.com/Genymobile/scrcpy/tree/a60891aea193d92e7e5c3942700eca63f9d19a5f)，Apache-2.0 | 投屏阶段的参考；未引入源码或二进制 | Android 编码、媒体帧与时间戳、电脑解码显示、延迟与缓冲取舍 | 面向普通用户的屏幕授权流程、iOS/iPadOS 捕获、跨平台直连与安全会话；正常投屏路径依赖 ADB，不能直接作为无需开发者模式的产品功能 |

本轮不再扩展大规模依赖清单。先复用前两项已经验证过的系统适配流程，参考 LocalSend 完成文件闭环；投屏来源保留到文件阶段通过后再评估。[Microsoft 示例范围](https://learn.microsoft.com/en-us/samples/microsoft/windows-universal-samples/wifidirect/)、[Android Direct 概览](https://developer.android.com/develop/connectivity/wifi/wifip2p)、[scrcpy 使用条件](https://github.com/Genymobile/scrcpy/blob/a60891aea193d92e7e5c3942700eca63f9d19a5f/README.md)。

## LocalSend：值得借鉴，但不整体搬入

2026-10-14 核查到 LocalSend 当前发布版本为 v1.18.2，固定提交为 `af0416be50770a97760f7070684bc667b759a15c`。该版本已有 Rust `packages/core`，因此不能继续把它只理解为 Flutter 页面工程。现有万维客户端为 C#/Kotlin；接入 Rust 核心需要额外的 FFI、跨端构建、接口绑定及协议适配，当前没有证据表明整体接入比先完成最小原生传输更省事。[发布说明](https://github.com/localsend/localsend/releases/tag/v1.18.2)、[固定版本 core](https://github.com/localsend/localsend/tree/af0416be50770a97760f7070684bc667b759a15c/packages/core)。

### 身份与 TLS

优先参考 [`v2_tls_pinning.rs`](https://github.com/localsend/localsend/blob/af0416be50770a97760f7070684bc667b759a15c/packages/core/tests/v2_tls_pinning.rs) 的测试边界：证书不匹配必须在发送元数据和文件之前失败；缺少要求的客户端证书也必须失败。万维仍按 [P0 契约](../protocol/README.md) 使用标准 TLS、经过用户确认的公钥绑定与每次接收确认。

不能把广播中自行声明的指纹当成已确认身份，也不能直接套用 LocalSend 的发现阶段信任策略或可选 HTTP 路径。LocalSend 的完整证书指纹与万维当前 SPKI 公钥指纹定义不同，网络协议与信任记录不能混用。平台 TLS 库负责密码算法；复用的是状态设计和验证目标，不自行重写加密算法。

### 会话、流式传输与取消

LocalSend v2.2 协议提供先发送元数据、接收方决定、按会话和文件令牌上传、取消等结构，可减少产品状态设计的工作。参考其处理忙碌、拒绝、哈希不一致的分支；万维沿用自己的消息与错误定义，不宣称协议兼容。[协议固定快照](https://github.com/localsend/protocol/blob/62bd3406ec80d62f2ed46269cdc06c4dcc391083/README.md)。

本次核查的该 v2.2 文档没有定义断点续传的偏移、分块确认与重连恢复契约；HTTP 流式读取和多文件并行也不等于可续传。P0 继续仅传一个 1 MiB 合成文件、核对 SHA-256、明确取消和最终保存结果。续传放到大文件阶段，再设计块索引、块校验、临时文件持久化、恢复授权和过期清理，不将其描述为已从 LocalSend 获得的现成功能。

### 必须改造的网络边界

固定版本的 LocalSend [`start_with_port`](https://github.com/localsend/localsend/blob/af0416be50770a97760f7070684bc667b759a15c/packages/core/src/http/server/mod.rs) 绑定 IPv4/IPv6 未指定地址；这与万维只监听已核实 Direct 地址的 P0 要求不同。不能原样启动服务器并用默认路由通信，再把成功传输当作 Direct 验证。

任何未来抽取都必须先接受我们提供的本次连接地址/接口，在两端记录真实 socket 端点，并验证异常、取消后停止监听。发现与网络适配层继续由万维管理；文件业务层不能悄悄退回现有局域网。

### 许可记录

LocalSend 主仓库 v1.18.2 有 [Apache-2.0 许可证](https://github.com/localsend/localsend/blob/af0416be50770a97760f7070684bc667b759a15c/LICENSE)，可按适用条款评估代码抽取。实际复制前须记录文件、固定提交、修改和依赖许可，并补充第三方声明。

独立 `localsend/protocol` 仓库在本次固定快照 `62bd3406ec80d62f2ed46269cdc06c4dcc391083` 的文件树中没有单独 LICENSE；不要自动认为它继承主仓库许可。本项目仅链接和概述其设计，不复制该仓库的文档或图。主仓库许可也不自动覆盖第三方依赖、标志或其他资产。

## 可直接开工的顺序

1. **保留原生 Direct 适配器。** Windows 输出真实端点；Android 通过组接口核对本地地址。传输模块获得结构化连接句柄，不接收猜测的固定 IP，也不自行切换默认路由。调用成功、系统配对成功和数据通路就绪分别记录。
2. **完成认证后的小文件传输。** 实现 P0 的身份确认、平台 TLS、接收决定、流式写入、哈希与保存确认。两种发送方向分别测试；组主角色不决定文件方向。先采用平台库，参考 LocalSend 的上述负向测试设计。
3. **覆盖真实失败和保网。** 用户拒绝、取消、断链、TLS 指纹不符、磁盘写入失败都必须结束本次任务并释放资源。没有共同 AP 仍可传输；已有 Wi-Fi 时同时观测 AP 与持续互联网业务。两项验收分开，不能用蜂窝接管上网作为保网成功。
4. **再形成可用预览版。** 提供文件选择、设备选择、进度和失败原因，整理安装/卸载及版本信息。先发布明确标为实验版本的构建；大文件、断点续传与自动选频有实测依据后逐项加入，投屏在其后。

## iOS/iPadOS 与投屏仍有哪些缺口

LocalSend 可以在 Apple 平台进行局域网传输，不会因此替万维建立跨系统无线直连。Apple 端仍须依照 [Wi-Fi Aware 官方文档](https://developer.apple.com/documentation/wifiaware/building-peer-to-peer-apps) 验证设备、系统、授权、服务和对端接口；不能把 Aware、Android/Windows Direct 与苹果 AirDrop 当作可直接互换的协议。当前 Apple 工程尚需 Mac 编译签名与真机验证。

scrcpy 可帮助理解 Android 媒体链路，但不解决 Apple 捕获或无需调试模式的授权。面向普通应用的 Android 屏幕捕获需使用 [MediaProjection](https://developer.android.com/media/grow/media-projection) 的系统授权；Apple 端另按平台接口实现。屏幕编码通常涉及压缩，不能因文件传输无损就宣传无损投屏；端到端延迟、画质、音画同步及 Wi-Fi 拥塞需要独立验收。

## 公开源码与内部开发的分工

公共仓库放可复现源码、构建说明、协议、测试向量、兼容性限制和第三方归属。设备日志、现场照片、个人路径、签名私钥、令牌及工具缓存留在内部；将来邀请他人测试时使用脱敏模板收集机型、系统、版本、结果与复现步骤。

公开源码不要求同时开放全部内部调试资料；私有工作分支或内部记录也不能代替对发布代码的许可证与依赖核对。GitHub 发布页、安装包分发和应用商店上架分别处理。任何尚未实现的能力，在公开 README、版本说明和界面中都标为计划或实验，尤其是 iOS 跨系统直连、双 5 GHz、MLO、软件双频聚合与投屏。

本次只更新复用计划，没有复制新上游代码、fork 仓库、向维护者发送消息或新增运行时依赖。
