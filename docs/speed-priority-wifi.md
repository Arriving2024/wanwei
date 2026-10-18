# 速度优先的独立本地 Wi-Fi 实验

更新：2026-10-19。用户已明确允许必要时临时断开原 Wi-Fi，以探索高速直连；结束后须尝试恢复并核验原网络。此授权扩大了实验范围，不改变独立连接、保护凭据和据实记录的要求。

## 两种产品模式

- 保网优先：尽可能保留原 WLAN。遇到要求断网时退出本轮，不能用采样一致代替持续互联网验证。
- 速度优先：明确告知原 Wi-Fi 可能临时断开，创建独立临时连接，结束后恢复原网络；不能承诺断开必然提速。

新版 Android build 12 通过 Android 16 的公开 `startLocalOnlyHotspotWithConfiguration` 请求单 5 GHz 本地网络，由系统生成受保护的随机连接参数。它不请求蜂窝网络共享，也不要求双方连接共同路由器。Windows 通过 Native WLAN 的 temporary profile 加入，原关联可能被替换；只接受可恢复的单适配器原网络，在连接失败、取消或结束时独立尝试恢复原 profile 并确认原 SSID。

这条路线现阶段只验证临时网络的创建、电脑接入、真实连接属性及清理。未接入 P0 文件收发链路；旧 Wi-Fi Direct 的 TLS/文件成功不能移作新路线的成功证据。

## Wi-Fi 7 和 MLO 的边界

Wi-Fi 7 STA 能力、SoftAP 能力、当前关联标准、MLO 活跃链路和文件吞吐是不同证据。当前公开 Android SDK 36 的本应用入口请求单 5 GHz，没有强制 MLO 或 802.11be 开关。Wi-Fi 6 设备也不能仅靠软件把两个频段变成标准 MLO。

Windows 报告原生 PHY、信道号、信号和收发链路速率；信道号单独不能区分所有频段。链路速率不能当作文件传输速度，`be` 也不能单独证明 MLO。MLO 未独立核验时始终标为未验证。

依据：[Android 本地热点 API](https://developer.android.com/reference/android/net/wifi/WifiManager#startLocalOnlyHotspotWithConfiguration(android.net.wifi.SoftApConfiguration,%20java.util.concurrent.Executor,%20android.net.wifi.WifiManager.LocalOnlyHotspotCallback))、[Android Wi-Fi 7](https://source.android.com/docs/core/connect/wifi-7)、[Windows Wi-Fi 7 驱动能力](https://learn.microsoft.com/en-us/windows-hardware/drivers/netcx/wificx-wi-fi-7)。

## 实验实现和凭据

手机启动等待最多 15 秒，已启动请求最多保留 120 秒。尚未获得 reservation 时没有公开取消接口；停止或超时保留迟到回调的清理责任，并锁住本进程后续无线操作。关闭 reservation 只表示释放本应用请求，不能宣称系统全局热点已经消失。

Debug APK 的专用 provider 只允许 ADB shell UID 2000 读取活动请求参数；release 清单不声明此 provider。参数由 USB 在内存中交给 Windows 标准输入，不使用命令行参数、报告、文件或普通日志保存密码。USB 辅助参数交接不代表普通产品配对流程已完成。

Windows `--local-wifi-lab` 是维护者显式实验入口；参数要求 `wanwei.local-wifi.lab/1`，仅通过标准输入接收。连接和恢复各自最多轮询 20 秒，观察 1–15 秒。恢复报告必须检查 `Confirmed`；强制结束进程、系统崩溃等情况不能保证自动恢复。该入口不传文件、不打开接收端口，无需新建防火墙规则。

## 当前验证

Windows 387 项本地检查通过，包含 Native WLAN ABI、XML 编码、安全属性、基线恢复门槛以及原有 TLS 回环检查。Android 75 项策略检查、编译、Lint 和 APK v2 调试签名校验通过，build 12 已覆盖安装且原身份保留；本地检查不代替射频与跨端验收。

2026-10-19 荣耀与 Windows 完成一轮真实接入：Android 公开接口返回受保护的活动请求，Windows 在约 1.6 秒后确认目标 SSID、temporary mode 和 WPA2-PSK/CCMP，连续 5 个样本均匹配。实际 PHY 为 **802.11ax（Wi-Fi 6）**，原生信道号 153，收发链路速率均 **1201 Mbps**；本轮没有独立测量中心频率，不把请求的 5 GHz 或信道编号写成实际频率证据。

电脑结束后约 1.2 秒核验已恢复原 profile 和原 SSID。手机释放本应用请求且没有重试锁，临时开启的定位已恢复关闭；后续独立读取手机仍连原 2437 MHz Wi-Fi。实验期间 15 个 Wi-Fi 采样点中的 14 个可比较点保持同一原 AP，无丢失事件；未测持续互联网。

维护者控制器首次未能解析子进程输出的中文报告路径，原始编排回执的 Windows 结果为空；随后从本轮唯一 Windows 报告按 session ID 和 UTC 时间独立核对上述结果，保留原始回执并修复控制器，无额外射频重试。脱敏报告留在本地，不上传设备身份或连接密码。

**本轮没有传文件、没有测速，也没有验证 MLO。** WPA2 是当前 Windows 实验的兼容基线；纯 WPA3 请求明确拒绝。后续应单独验证 WPA3、SoftAP 802.11be 和活跃多链路能力，不能把当前安全兼容路线称为 Wi-Fi 7 全速方案。

下一阶段是在连接证据明确后，为该路线单独实现地址来源验证、双向认证和显式接收，再逐项验收双向落盘哈希、大文件吞吐、取消和恢复。不会直接复用旧 Direct 的“已验证路径”标记。
