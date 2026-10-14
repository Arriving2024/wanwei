# Apple P0：先在真机确认能力

更新：2026-10-14。

Apple 端的第一步已准备成可以交给 Mac 的原生 SwiftUI 工程。当前完成的是源码和 Windows 上的静态核验；**没有在 Xcode 编译，没有签名安装，也没有 iPhone/iPad 直连结果**。

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
| 用户是否允许配对 | 本版未请求 | 后续加入系统配对界面后单独验证 |
| 是否直连、是否保留原 Wi-Fi | 本版未测试 | 后续双机连接与连续网络观测 |
| 荣耀是否可以与 Apple Aware 互通 | 没有结论 | 荣耀先有可用的兼容底层能力；Apple 单机结果不能补齐它 |

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

本版只检查一个声明为发布和订阅的 `_wanwei-lab._tcp` 服务。没有开始任何发布/订阅会话，也不打开系统配对界面。[配置规则](https://developer.apple.com/documentation/wifiaware/adopting-wi-fi-aware)

## 接下来的验收门槛

两台 Apple 真机都完成能力检查后，再实现一次用户主动确认的系统配对，随后传输小测试文件并验证哈希。最后把双方连到各自原有 Wi-Fi，检查直连期间原网络和互联网是否持续可用。能力、配对、传输、并发联网分别记结果，不合并成一个“支持”。

Apple ↔ 荣耀和 Apple ↔ Windows 仍是独立互通任务。当前荣耀未提供 Aware 服务，不能因为 Apple 端查询成功就宣称已解决跨平台底层互通。

源码、官方 API 索引与验证记录见 [Apple 工程说明](../apps/apple/README.md)。
