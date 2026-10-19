# 速度优先的独立本地 Wi-Fi 实验

更新：2026-10-19。用户已明确允许必要时临时断开原 Wi-Fi，以探索高速直连；结束后须尝试恢复并核验原网络。此授权扩大了实验范围，不改变独立连接、保护凭据和据实记录的要求。

## 两种产品模式

- 保网优先：尽可能保留原 WLAN。遇到要求断网时退出本轮，不能用采样一致代替持续互联网验证。
- 速度优先：明确告知原 Wi-Fi 可能临时断开，创建独立临时连接，结束后恢复原网络；不能承诺断开必然提速。

新版 Android build 12 通过 Android 16 的公开 `startLocalOnlyHotspotWithConfiguration` 请求单 5 GHz 本地网络，由系统生成受保护的随机连接参数。它不请求蜂窝网络共享，也不要求双方连接共同路由器。Windows 通过 Native WLAN 的 temporary profile 加入，原关联可能被替换；只接受可恢复的单适配器原网络，在连接失败、取消或结束时独立尝试恢复原 profile 并确认原 SSID。

build 12 验证临时网络的创建、电脑接入、真实连接属性及清理。build 13 为该路线单独接入受控的 P0 固定文件实验：Android 与 Windows 使用独立的本地热点路径类型，不伪造 Wi-Fi Direct 组主或沿用其已验证标记。旧 Wi-Fi Direct 的 TLS/文件成功不能移作新路线的成功证据。

## Wi-Fi 7 和 MLO 的边界

Wi-Fi 7 STA 能力、SoftAP 能力、当前关联标准、MLO 活跃链路和文件吞吐是不同证据。当前公开 Android SDK 36 的本应用入口请求单 5 GHz，没有强制 MLO 或 802.11be 开关。Wi-Fi 6 设备也不能仅靠软件把两个频段变成标准 MLO。

Windows 报告原生 PHY、信道号、信号和收发链路速率；信道号单独不能区分所有频段。链路速率不能当作文件传输速度，`be` 也不能单独证明 MLO。MLO 未独立核验时始终标为未验证。

依据：[Android 本地热点 API](https://developer.android.com/reference/android/net/wifi/WifiManager#startLocalOnlyHotspotWithConfiguration(android.net.wifi.SoftApConfiguration,%20java.util.concurrent.Executor,%20android.net.wifi.WifiManager.LocalOnlyHotspotCallback))、[Android Wi-Fi 7](https://source.android.com/docs/core/connect/wifi-7)、[Windows Wi-Fi 7 驱动能力](https://learn.microsoft.com/en-us/windows-hardware/drivers/netcx/wificx-wi-fi-7)。

## 实验实现和凭据

手机启动等待最多 15 秒，已启动请求最多保留 120 秒。尚未获得 reservation 时没有公开取消接口；停止或超时保留迟到回调的清理责任，并锁住本进程后续无线操作。关闭 reservation 只表示释放本应用请求，不能宣称系统全局热点已经消失。

Debug APK 的专用 provider 只允许 ADB shell UID 2000 读取活动请求参数；release 清单不声明此 provider。参数由 USB 在内存中交给 Windows 标准输入，不使用命令行参数、报告、文件或普通日志保存密码。USB 辅助参数交接不代表普通产品配对流程已完成。

Windows `--local-wifi-lab` 是维护者显式实验入口；参数要求 `wanwei.local-wifi.lab/1`，仅通过标准输入接收。连接和恢复各自最多轮询 20 秒，观察 1–15 秒。恢复报告必须检查 `Confirmed`；强制结束进程、系统崩溃等情况不能保证自动恢复。该入口不传文件、不打开接收端口，无需新建防火墙规则。

固定文件实验使用独立的 `--local-wifi-fixture-lab`。逐行标准输入和仅 Debug shell 可调用的手机入口绑定本次 session、一次性 nonce、完整对端公钥指纹和明确的合成文件授权；每方向最多一次。手机使用创建热点前后的唯一新 IPv4 接口，与 Windows 临时 WLAN 的唯一同子网 DHCP 网关交叉核对，存在歧义直接拒绝。这是 USB 辅助的实验拓扑验证，尚不是普通产品的自动地址发现。

两个文件方向均由 Windows 主动建立 TCP/TLS 连接、Android 提供监听，文件的 sender/receiver 角色与 TCP/TLS 角色分开。这样无需新增 Windows 入站防火墙规则。保留 TLS 1.3 双向证书证明、完整指纹、固定 1 MiB、接收端安全保存和 COMPLETE 确认；原网络、热点密码和文件内容不通过共同路由器中转，USB 只传参数、控制及结果摘要。

Windows 每个文件任务最多 25 秒，命令窗口最多 70 秒，最后独立恢复原网络；手机保持原来的 120 秒绝对期限，停止时先取消并等待文件 worker 清理，再释放自有热点请求。未能确认清理会锁住重试，不能据此报告成功。

## build 12 连接能力基线

Windows 387 项本地检查通过，包含 Native WLAN ABI、XML 编码、安全属性、基线恢复门槛以及原有 TLS 回环检查。Android 75 项策略检查、编译、Lint 和 APK v2 调试签名校验通过，build 12 已覆盖安装且原身份保留；本地检查不代替射频与跨端验收。

2026-10-19 荣耀与 Windows 完成一轮真实接入：Android 公开接口返回受保护的活动请求，Windows 在约 1.6 秒后确认目标 SSID、temporary mode 和 WPA2-PSK/CCMP，连续 5 个样本均匹配。实际 PHY 为 **802.11ax（Wi-Fi 6）**，原生信道号 153，收发链路速率均 **1201 Mbps**；本轮没有独立测量中心频率，不把请求的 5 GHz 或信道编号写成实际频率证据。

电脑结束后约 1.2 秒核验已恢复原 profile 和原 SSID。手机释放本应用请求且没有重试锁，临时开启的定位已恢复关闭；后续独立读取手机仍连原 2437 MHz Wi-Fi。实验期间 15 个 Wi-Fi 采样点中的 14 个可比较点保持同一原 AP，无丢失事件；未测持续互联网。

维护者控制器首次未能解析子进程输出的中文报告路径，原始编排回执的 Windows 结果为空；随后从本轮唯一 Windows 报告按 session ID 和 UTC 时间独立核对上述结果，保留原始回执并修复控制器，无额外射频重试。脱敏报告留在本地，不上传设备身份或连接密码。

**此前 build 12 连接实验没有传文件、没有测速，也没有验证 MLO。** WPA2 是当前 Windows 实验的兼容基线；纯 WPA3 请求明确拒绝。后续应单独验证 WPA3、SoftAP 802.11be 和活跃多链路能力，不能把当前安全兼容路线称为 Wi-Fi 7 全速方案。

新增开发代码的 Windows 430 项本地检查已通过，包含接收端主动建连的角色互换、错误完整指纹拒绝、唯一地址和网关、会话绑定及防重放；Android 本地策略检查增加到 82 项。无线固定文件实验单独记录，不用 host 回环测试代替跨端成功。

## build 13 双向固定文件实测

2026-10-19 荣耀 build 13 与 Windows 开发源码完成同一临时网络上的两次独立 TLS 文件任务：先 Windows → Android，再 Android → Windows，每向 **1,048,576 字节**。两端固定文件均安全保存，接收端实际文件另行计算 SHA-256，与协议固定值一致；发送端收到保存后的 COMPLETE。每向的 transfer ID、完整公钥身份和 native/socket 两端地址及端口相互吻合，TLS 均为 1.3。

两个方向均为 Windows TCP/TLS client、Android TCP/TLS server，报告明确记录本地热点路径；Android 的 `directPathVerified=false`、`localHotspotPathVerified=true`，没有把它冒充 Wi-Fi Direct。本次没有创建 Windows 入站防火墙规则。USB 保持连接，负责受控实验授权、公钥和地址/端口交接、读取报告及设备上计算的文件摘要，未搬运文件内容。

Windows 两次任务的 socket 和临时文件清理已确认，约 **1080 毫秒**核验恢复原 profile 和原 SSID。Android 文件 worker 在释放自有热点请求前已完成 socket/storage 清理，重试未锁定，定位恢复关闭；后续独立读取仍连接 2437 MHz 原 Wi-Fi。实验的 20 个可比较 AP 采样点保持相同，无丢失事件；仍不代表持续互联网可用。

本轮为显式 USB 辅助、固定合成文件实验预授权，**尚未验证普通用户配对与逐次接收流程**，不支持选择个人文件。连接协商仍为 Wi-Fi 6、收发链路速率 1201 Mbps；1 MiB 太小，本轮未做吞吐测速，也未验证 Wi-Fi 7 MLO、物理拔线、拒绝/中断和连续重连。此结果独立于此前 Wi-Fi Direct 的普通单向成功。

在已通过的双向固定文件基线上，下一阶段验收普通用户确认、重复连接、拒绝与中断，再扩展用户文件及大文件吞吐。不会直接复用旧 Direct 的“已验证路径”标记。
