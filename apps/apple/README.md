# 万维 Apple 能力探测器

状态：原创 SwiftUI 工程已准备；Windows 静态配置核验及 2026-10-14 的 [GitHub Mac 不签名编译](https://github.com/Arriving2024/wanwei/actions/runs/38497404842/job/115636217538) 通过。**签名、安装和真机能力均未验证**。这里没有可安装的 IPA。

入口：`WanweiAppleLab.xcodeproj`，共享 Scheme：`WanweiAppleLab`。目标为 iPhone/iPad，iOS/iPadOS 26 起，不包含 Mac Catalyst。

使用步骤见 [Apple P0 快速开始](../../docs/apple-p0-quickstart.md)。历史 Windows 静态核验只检查工程引用、plist 和声明一致性；不代替 Xcode 编译或实际签名。GitHub Apple job 也只做不签名构建，不访问真机。

此版只检查系统的 `WACapabilities.supportedFeatures` 与本应用声明的服务登记，提供可手动保存的 JSON 报告。不会创建配对、发布广播、扫描对端、打开连接或更改原网络。报告将签名授权标为 `not_verified_by_app`，配对授权标为 `not_requested`。支持标志为真，也不代表已有可用直连或原 Wi-Fi 已保持。

没有第三方源码或包依赖。Swift 源码和工程文件为本项目原创；按 Apple 公开文档使用 API 和配置格式，没有下载或复制官方示例工程。

## 官方 API 核对

核对日期：2026-10-10。以下页面的官方 Markdown 版本同时用于核对 Swift 签名及 iOS/iPadOS 26 的可用性。

- [WACapabilities](https://developer.apple.com/documentation/wifiaware/wacapabilities)：主机能力查询。
- [supportedFeatures](https://developer.apple.com/documentation/wifiaware/wacapabilities/supportedfeatures) 与 [Feature.wifiAware](https://developer.apple.com/documentation/wifiaware/wacapabilities/feature/wifiaware)：查询集合是否包含 Wi-Fi Aware 能力。
- [WAPublishableService.allServices](https://developer.apple.com/documentation/wifiaware/wapublishableservice/allservices) 与 [WASubscribableService.allServices](https://developer.apple.com/documentation/wifiaware/wasubscribableservice/allservices)：只读取服务声明形成的登记表。
- [Adopting Wi-Fi Aware](https://developer.apple.com/documentation/wifiaware/adopting-wi-fi-aware)：`WiFiAwareServices` 配置与服务命名约束。
- [Wi-Fi Aware entitlement](https://developer.apple.com/documentation/bundleresources/entitlements/com.apple.developer.wifi-aware)：`Publish` / `Subscribe` 授权声明。
- [Building peer-to-peer apps](https://developer.apple.com/documentation/wifiaware/building-peer-to-peer-apps)：实际无线联调须用真机。

## 工程范围

| 文件 | 用途 |
|---|---|
| `WanweiAppleLab/CapabilityReport.swift` | 查询能力、验证单一服务配置、构造不含网络身份信息的报告 |
| `WanweiAppleLab/ContentView.swift` | 中文 SwiftUI 页面与用户主动导出 |
| `WanweiAppleLab/Info.plist` | 声明 `_wanwei-lab._tcp`，发布与订阅两种角色 |
| `WanweiAppleLab/WanweiAppleLab.entitlements` | 请求两种角色；最终以签名和系统处理为准 |
| `check-on-mac.sh` | 在 Mac 校验 plist、列出工程、尝试不签名编译；不会安装应用 |

JSON 中可空字段在尚未查询时可能省略；不得把字段缺失解释成 `false`。`physicalDevice` 只区分编译目标是真机还是模拟器，不代表目标已支持 Aware。报告也不提供运行期配对许可查询：此版没有发起配对许可流程。
