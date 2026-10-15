# Apple P0：能力检查与系统配对准备

更新：2026-10-15。

Apple 0.1.1 源码保留单机能力检查，新增由用户主动操作的系统配对准备页。本次新增页面已在 [GitHub Mac 环境](https://github.com/Arriving2024/wanwei/actions/runs/38702523633) 使用 Xcode 不签名编译通过；**尚未签名安装，没有 iPhone/iPad 真机或直连结果**。当前暂时借不到 Mac，先保留已编译工程，不能把云端编译当作真机验收。云端构建不接触本地 iPad。

仓库中的工程位置：`apps/apple/WanweiAppleLab.xcodeproj`。完整源码在同级 `WanweiAppleLab` 文件夹。不要只复制 `.xcodeproj`，请把整个 `apps/apple` 文件夹带到 Mac。

## Windows 与 iPad 分别能做什么

Windows 可以编辑源码、协议和界面定义，也可以提交 GitHub 后让 macOS runner 检查不签名编译。现有工程使用原生 SwiftUI；Windows 没有运行它所需的 Xcode 和 Apple SDK。完整真机调试推荐兼容的 Mac + Xcode + 本地 iPad。

开启开发者模式后仍需要正确签名、设备信任及应用能力配置。把 iPad 插到 Windows 不会得到 Android ADB 那样的完整开发流程；云端构建机也不能直接访问本地 iPad 的无线环境。开发者模式、编译、签名、安装和直连分别验收。

普通设备开发与 Wi-Fi Aware entitlement 的资格不同，先在 Xcode 核实账户/Team 支持，不提前承诺需要或不需要付费。官方依据：[Xcode 系统要求](https://developer.apple.com/xcode/system-requirements)、[Developer Mode](https://developer.apple.com/documentation/xcode/enabling-developer-mode-on-a-device)、[开发者账户](https://developer.apple.com/help/account/basics/about-your-developer-account)。

## 本轮要确认什么

程序会显示系统版本、真机/模拟器环境、Wi-Fi Aware 支持标志、服务配置与服务登记，并能导出 JSON。支持标志由系统 API 返回，不按手机型号、地区或 Wi-Fi 代际推测。[Apple 能力查询文档](https://developer.apple.com/documentation/wifiaware/wacapabilities/supportedfeatures)

| 项目 | 本版处理 | 得出结论的条件 |
|---|---|---|
| iPhone/iPad 是否向应用报告 Aware 支持 | 查询 `WACapabilities` | 目标真机成功运行并导出报告 |
| 应用服务配置是否正确 | 本地验证 Info.plist；支持时读取服务登记 | 页面配置有效，发布/订阅均已登记 |
| 签名是否包含所需角色 | 页面明确显示待 Mac 核验 | 查看实际构建应用的签名 entitlement，并成功安装运行 |
| 用户是否允许配对 | 用户主动进入准备页并点击系统控件；签名和真机行为待验 | 两台真机的系统界面及人工记录；列表计数或 endpoint 回调不能单独证明新配对成功 |
| 是否直连、是否保留原 Wi-Fi | 本版未测试 | 后续双机连接与连续网络观测 |
| Android / Windows 是否可以与 Apple Aware 互通 | 当前没有可用的兼容 Aware 传输实现 | 对端先有可用且兼容的底层能力；Apple 单机或 Apple 间配对不能补齐它 |

## 先分清跨端路径

Apple 的公开路径是 iOS/iPadOS 26 的 Wi-Fi Aware（NAN）。Apple 还提供另一种 Apple 点对点 Wi-Fi，但其无线机制没有向第三方公开，只能用于 Apple 设备之间；不能把它当作 Windows/Android 的公开 Wi-Fi Direct API。[Apple TN3111](https://developer.apple.com/documentation/technotes/tn3111-ios-wifi-api-overview)

当前 Windows/Android 已验证的是 Wi-Fi Direct；Windows 使用 `WiFiDirectDevice`，不是 Aware 发布/订阅或配对。Microsoft 的公开 [Wi-Fi Direct API](https://learn.microsoft.com/en-us/uwp/api/windows.devices.wifidirect) 文档不提供把这条连接转换为 Apple Aware 的接口，本项目也没有 Windows Aware 实现。不能据此宣称 Windows 永远不能支持 Aware，但目前没有可直接复用的公开跨端路径。

2026-10-15 的当前 Android 样机报告中，`frameworkApiPresent=true`、`hardwareFeatureReported=false`、`servicePresent=false`，`runtimeAvailable` 和 `pairingSupported` 都是未知。这里缺少系统向第三方应用开放的能力和服务，不是已经支持 Aware 后的暂时不可用。双 Wi-Fi/多网络支持不能替代它，也不能靠普通 APK 把系统没有开放的服务变出来。如果以后换到支持 Aware 的 Android 设备，还须核验 API 34 起的 `isAwarePairingSupported()` 及实际配对互通，不能只看 Android 版本。[Android 能力与可用性](https://developer.android.com/develop/connectivity/wifi/wifi-aware)、[配对能力](https://developer.android.com/reference/android/net/wifi/aware/Characteristics#isAwarePairingSupported())

因此本阶段先准备 iPhone ↔ iPad 的 Apple 间系统配对。Apple ↔ 当前荣耀、Apple ↔ 当前 Windows 都仍是未打通的独立任务。加入普通热点不是保留现有 Wi-Fi 的替代验收方式。

## 借到 Mac 后的操作

1. 在 Mac 安装能够提供 **iOS 26 或更高 SDK** 的 Xcode，首次打开并完成组件安装。真机系统也需要 iOS/iPadOS 26 或更高版本。工程最低部署版本是 26.0；所借 Mac 是否满足该 Xcode 的运行要求，需拿到机器后确认。
2. 将整个 `apps/apple` 复制到 Mac，例如“文稿/万维计划/apple”。双击 `WanweiAppleLab.xcodeproj`，选择 `WanweiAppleLab` Scheme。
3. 先做不签名编译，检查源码是否被当前 SDK 接受。可在终端进入复制后的 `apple` 文件夹，执行 `bash check-on-mac.sh`。它先校验配置，再构建 iOS 真机目标；成功会显示 `Unsigned build completed`。**这一步成功仍没有可安装签名。** 若失败，请保留首条编译错误和使用的 Xcode/SDK 版本，不把失败当成手机不支持。
4. 在 Xcode 的 Target → Signing & Capabilities 选择用于开发的 Team，保留自动签名，将 Bundle Identifier 改成该团队可使用的唯一名称。确认 Wi-Fi Aware 能力及 `Publish`、`Subscribe` 角色已经纳入签名配置。源码已写入对应 entitlement；Apple 账户、团队和描述文件是否允许使用，仍以 Xcode 实际结果为准，不承诺免费个人团队一定能签。签名报错时先解决签名，不删除授权项来伪造成功。[官方 entitlement 说明](https://developer.apple.com/documentation/bundleresources/entitlements/com.apple.developer.wifi-aware)
5. 用数据线连接 iPhone 17 Pro Max 或 iPad M5，在设备上完成信任与系统要求的开发者模式设置。Apple 账户登录、设备信任和开发者模式确认由设备持有人操作。Xcode 顶部选择这台真机后点运行。
6. 打开“万维实验室”。记录页面结果，点“导出 JSON 报告”，保存到“文件”中方便取回的位置。先把文件名分别改为 `iphone17pm-capability.json`、`ipadm5-capability.json`，避免两个设备互相覆盖。报告不含序列号、Wi-Fi 名称或 MAC 地址。

不要用模拟器得出无线能力结论；本程序在模拟器中主动跳过能力查询，显示“需要在真机检查”。真正的 Aware 无线联调需要实际设备。[Apple 真机要求](https://developer.apple.com/documentation/wifiaware/building-peer-to-peer-apps)

## 验证签名与保存结果

在 Xcode 的 Products 里对 `WanweiAppleLab.app` 使用“Show in Finder”，查看**此次真机构建的应用**。开发人员可在 Mac 终端运行下列命令，将占位路径替换为该 `.app` 的真实位置：

```sh
codesign -d --entitlements :- "/实际构建路径/WanweiAppleLab.app"
```

确认输出中的 `com.apple.developer.wifi-aware` 包含 `Publish` 和 `Subscribe`。此输出证明签名里声明了什么；它不代替真机安装、系统授权或后续成功连接。报告仍将签名状态写成 `not_verified_by_app`，不会因源码存在 entitlement 就显示“已获授权”。无需把完整描述文件或账户资料放进项目。

建议将本轮真机 JSON、Xcode/SDK 版本、编译结果以及人工签名核验结论保存到独立现场记录。报告的 `probeStatus` 可能为：

| 值 | 含义与下一步 |
|---|---|
| `supported_at_capture` | 本次系统报告支持，继续检查签名与服务登记；尚未验证双机连接 |
| `not_supported_at_capture` | 本次系统没有报告支持；先核实具体系统/设备与运行条件，不自动解释为永久硬件不支持 |
| `configuration_invalid` | 本应用配置与约定不符，已跳过框架查询；修复工程配置后重试 |
| `simulator_not_device_evidence` | 模拟器结果不作真机能力证据 |
| `os_api_unavailable` / `sdk_framework_unavailable` | 系统 API 或构建 SDK 条件不满足；检查开发环境 |

首页能力探测器只检查一个声明为发布和订阅的 `_wanwei-lab._tcp` 服务，不启动无线发现或数据会话；能力报告的 `pairingAuthorization=not_requested_by_capability_probe` 只描述该探测器，不记录整个应用的配对历史。[配置规则](https://developer.apple.com/documentation/wifiaware/adopting-wi-fi-aware)

## 两台 Apple 真机的配对准备验收

以下步骤要等签名安装完成后再执行；当前没有真机结论。

1. 两台设备各自完成首页能力检查与 JSON 导出。真机、系统支持和发布/订阅登记都通过时，才会在“系统配对准备”页提供系统控件；签名状态仍需前文的独立核验。
2. 在 iPhone 和 iPad 上分别进入“系统配对准备”。一台选择“等待另一台设备”，另一台选择“寻找等待中的设备”。两端各点相应的系统按钮，选择本次目标设备；系统 PIN、信任及确认由设备持有人操作。若出现 fallback，记录无法使用，不能改走热点或把未发现判成对端一定不支持。
3. 本版使用 `DevicePairingView` 与 `DevicePicker` 的 `.userSpecifiedDevices` 范围，只涉及用户在系统界面选择或配对的设备。该范围专用于系统配对控件，不用于数据 listener/browser。[官方配对示例](https://developer.apple.com/documentation/wifiaware/building-peer-to-peer-apps)、[发布范围](https://developer.apple.com/documentation/wifiaware/wapublisherlistener/devices/userspecifieddevices)、[订阅范围](https://developer.apple.com/documentation/wifiaware/wasubscriberbrowser/devices/userspecifieddevices)
4. 分别导出“配对准备报告”。`systemPairingControlsConfigured` 只表示本机门槛通过并已配置控件；`systemUIFallbackObserved` 记录 fallback。`pairedDeviceCount` 是应用可访问的配对设备快照数量，可能来自此前配对。订阅端的 `subscriberEndpointReturnCount` 只记录系统实际返回 endpoint 的次数，不保留 endpoint，不推断它是新配对，更不等于数据连接成功。
5. 人工记录两台目标设备的系统流程是否完成、是否首次 PIN 配对或选择既有设备、两个准备报告的时间是否对应，并交换角色再试。报告不输出设备名称/配对 ID，因此不能仅凭“数量大于零”证明配到了预期对端。取消、拒绝、系统入口不可用也应单独记录。

配对列表观察使用 `WAPairedDevice.allDevices`，只保存计数和时间；离开页面或进入非活动状态即取消观察，不在后台保持任务。取消观察不会撤销系统已建立的配对。本版不创建 `NetworkListener`、`NetworkBrowser` 或 `NetworkConnection`，不保留或使用返回 endpoint，不发送应用 payload；`wifiAwareDataSessionStarted=false` 仅指本应用未创建数据会话，并不表示系统配对不使用无线电。

`wanwei.apple.pairing-preparation.v1` 报告把 `newPairingSuccess` 固定为 `not_inferred_from_count_or_endpoint`，`connectionTest=not_implemented`、`originalWiFiContinuity=not_tested`、`applicationPayloadSent=false`。签名、系统配对、数据路径、应用身份认证、文件保存和原 Wi-Fi 连续性分别验收。

## 接下来的验收门槛

本次准备页已通过 Mac CI 编译，仍要按上述步骤完成签名安装、单机与双机系统配对。之后才实现选定对端的数据连接、应用公钥身份与 TLS、固定测试文件、落盘及哈希核验；不能把系统 Wi-Fi 层加密当作既有 P0 应用身份校验。文件传输采用官方建议的 bulk 模式起步，双方性能模式一致，低延迟和投屏以后另验。最后把双方连到各自原有 Wi-Fi，独立检查传输期间原 AP 与互联网是否持续可用。[连接接口与配对要求](https://developer.apple.com/documentation/wifiaware/connecting-paired-devices)

Apple ↔ 荣耀和 Apple ↔ Windows 仍是独立互通任务。当前荣耀未提供 Aware 服务，不能因为 Apple 端查询成功就宣称已解决跨平台底层互通。

源码、官方 API 索引与验证记录见 [Apple 工程说明](../apps/apple/README.md)。
