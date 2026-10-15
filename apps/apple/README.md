# 万维 Apple 能力与配对准备工程

状态：Apple 0.1.1 源码加入了用户主动操作的系统配对准备页，等待本次 Xcode 不签名编译；历史能力探测器的 [GitHub Mac 不签名编译](https://github.com/Arriving2024/wanwei/actions/runs/38497404842/job/115636217538) 已通过，不能替代本次改动的编译结果。**签名、安装、系统配对和真机能力均未验证**。这里没有可安装的 IPA。

入口：`WanweiAppleLab.xcodeproj`，共享 Scheme：`WanweiAppleLab`。目标为 iPhone/iPad，iOS/iPadOS 26 起，不包含 Mac Catalyst。

使用步骤见 [Apple P0 快速开始](../../docs/apple-p0-quickstart.md)。历史 Windows 静态核验只检查工程引用、plist 和声明一致性；不代替 Xcode 编译或实际签名。GitHub Apple job 也只做不签名构建，不访问真机。

首页只检查系统的 `WACapabilities.supportedFeatures` 与本应用声明的服务登记，提供可手动保存的 JSON 报告，不发起无线会话。单机报告的配对字段是 `not_requested_by_capability_probe`，不代表整个应用的配对历史。

用户进入“系统配对准备”页后，应用只观察已获访问权限的配对设备数量；真机能力和服务登记通过时，提供官方 `DevicePairingView` / `DevicePicker` 控件，由用户点击并操作系统 PIN/确认流程。使用 `.userSpecifiedDevices` 限制为用户在系统界面选择或配对的设备。不会创建 `NetworkListener`、`NetworkBrowser` 或 `NetworkConnection`，也不使用返回的 endpoint 打开应用数据连接。

准备报告只含计数、时间和固定状态，不保存或导出设备名、配对 ID、endpoint 或网络身份。既有配对设备可能让计数非零；系统返回 endpoint 不等于新配对、连接或文件传输已通过。离页或进入非活动状态取消配对列表观察；不承诺撤销系统已建立的配对。签名授权仍为 `not_verified_by_app`，支持标志为真也不代表签名、实际直连或原 Wi-Fi 保持。

没有第三方源码或包依赖。Swift 源码和工程文件为本项目原创；按 Apple 公开文档使用 API 和配置格式，没有下载或复制官方示例工程。

## 官方 API 核对

核对日期：2026-10-15。以下页面的官方 Markdown 版本同时用于核对 Swift 签名及 iOS/iPadOS 26 的可用性。

- [WACapabilities](https://developer.apple.com/documentation/wifiaware/wacapabilities)：主机能力查询。
- [supportedFeatures](https://developer.apple.com/documentation/wifiaware/wacapabilities/supportedfeatures) 与 [Feature.wifiAware](https://developer.apple.com/documentation/wifiaware/wacapabilities/feature/wifiaware)：查询集合是否包含 Wi-Fi Aware 能力。
- [WAPublishableService.allServices](https://developer.apple.com/documentation/wifiaware/wapublishableservice/allservices) 与 [WASubscribableService.allServices](https://developer.apple.com/documentation/wifiaware/wasubscribableservice/allservices)：只读取服务声明形成的登记表。
- [Adopting Wi-Fi Aware](https://developer.apple.com/documentation/wifiaware/adopting-wi-fi-aware)：`WiFiAwareServices` 配置与服务命名约束。
- [Wi-Fi Aware entitlement](https://developer.apple.com/documentation/bundleresources/entitlements/com.apple.developer.wifi-aware)：`Publish` / `Subscribe` 授权声明。
- [Building peer-to-peer apps](https://developer.apple.com/documentation/wifiaware/building-peer-to-peer-apps)：实际无线联调须用真机。
- [DevicePairingView](https://developer.apple.com/documentation/devicediscoveryui/devicepairingview)、[DevicePicker](https://developer.apple.com/documentation/devicediscoveryui/devicepicker)：用户点击的系统配对/选择控件。
- [发布端 userSpecifiedDevices](https://developer.apple.com/documentation/wifiaware/wapublisherlistener/devices/userspecifieddevices)、[订阅端 userSpecifiedDevices](https://developer.apple.com/documentation/wifiaware/wasubscriberbrowser/devices/userspecifieddevices)：只供相应系统配对控件使用，不用于数据 listener/browser。
- [WAPairedDevice.allDevices](https://developer.apple.com/documentation/wifiaware/wapaireddevice/alldevices)：异步观察应用可访问的配对设备快照，本版只保存数量。
- [TN3111](https://developer.apple.com/documentation/technotes/tn3111-ios-wifi-api-overview)：区分标准 Wi-Fi Aware 和只适用于 Apple 设备间的 Apple 点对点 Wi-Fi。

## 工程范围

| 文件 | 用途 |
|---|---|
| `WanweiAppleLab/CapabilityReport.swift` | 查询能力、验证单一服务配置、构造不含网络身份信息的报告 |
| `WanweiAppleLab/ContentView.swift` | 中文 SwiftUI 页面与用户主动导出 |
| `WanweiAppleLab/PairingPreparationView.swift` | 用户主动的系统配对准备、可取消的数量观察及独立无身份报告；不创建数据连接 |
| `WanweiAppleLab/Info.plist` | 声明 `_wanwei-lab._tcp`，发布与订阅两种角色 |
| `WanweiAppleLab/WanweiAppleLab.entitlements` | 请求两种角色；最终以签名和系统处理为准 |
| `check-on-mac.sh` | 在 Mac 校验 plist、列出工程、尝试不签名编译；不会安装应用 |

JSON 中可空字段在尚未查询时可能省略；不得把字段缺失解释成 `false`。`physicalDevice` 只区分编译目标是真机还是模拟器，不代表目标已支持 Aware。报告不把系统配对列表数量当作运行期签名许可或新配对成功证明。当前没有与荣耀或 Windows 的兼容 Aware 底层实现，不能把现有 Wi-Fi Direct 连接视为 Apple 的可用传输路径。
