# P0 安全文件实验

更新：2026-10-14。Windows 与 Android 0.3.0 已实现本文的受限实验，**Windows ↔ 荣耀物理无线互传、普通操作确认和负载下保网尚待验收**。Windows 178 项、Android 24 项本地检查通过；本机 TLS 测试不等于设备互通。

## 测什么

只发送程序本地生成的 `ramp256-1mib-v1`：1,048,576 字节，字节值为位置模 256。固定 SHA-256 为：

```text
fbbab289f7f94b25736c58be46a994c441fd02552cc6022352e3d86d2fab7c83
```

每个方向建立新的 TCP/TLS 连接；两端可以在同一原生 Direct 建链窗口内交换角色。双方必须先确认完整 64 位公钥 SPKI SHA-256 指纹，通过平台 TLS 1.3 双向认证，且 HELLO 身份与已认证证书匹配。双方禁用会话恢复、不发送握手前的应用数据；Android 每连接使用全新 SSLContext 并关闭会话票据，Windows 使用新 SslStream 并设置 `AllowTlsResume=false`。文件帧、严格 JSON、任务 ID、阶段超时和保存条件以 [协议契约](../protocol/README.md) 为准。

数据 socket 只绑定当前原生适配器实际观测的 P2P 接口地址；无法核实路径就停止，不能改走普通局域网。接收端使用动态监听端口；当前由操作者线下交换端口与身份，这还不是自动发现的一键产品流程。没有任意文件选择、文件名导入、远程路径、断点续传、目录或投屏功能。

接收端在应用拥有的测试目录内创建随机临时文件，校验完整长度与哈希、同步并以不覆盖的方式提交，再发送 COMPLETE。发送端只有验证 COMPLETE 后才能显示“对方已保存”；网络中断不能被当作成功。

## 普通手动实验

1. 在两端安装/运行 0.3.0。Android 已有安装所需的 Debug 签名；Windows 还没有可信发布者代码签名。Android 安全文件实验需要 Android 10/API 29 以上。此版本没有 Apple 安装包。
2. 通过双方本机页面或设备持有人授权的 USB 调试渠道核对完整公钥指纹，在各端明确确认对端公钥。系统 Wi-Fi 配对不能代替应用公钥确认。
3. 明确启用文件实验，再完成原生 Direct 配对。普通建链观察默认 20 秒；文件实验的连接窗口为 180 秒。
4. 一端等待接收，并把本次实际监听端口交给另一端；另一端填写实际对端 P2P 地址/端口并发送。接收端必须逐次接受 OFFER；可以拒绝或取消。反向测试交换角色，重新建立 TLS 文件连接。
5. 对照双方报告的任务 ID、实际 socket 两端地址和端口、TLS 版本与公钥、字节数、哈希、本机提交/对方确认以及资源关闭结果。保网还需要独立比较传输前、中、后原 Wi-Fi 与互联网连通性。

首次 1 MiB 仅验证正确性，不能用于宣传峰值速度。普通确认流程是否可用、双方原 Wi-Fi 是否保持、是否在无共同 AP 条件下成功，都需要单独现场验收。

## USB 辅助固定数据实验

远程 UI 工具的操作往返可能耗尽系统配对时限，因此增加了显式启用的开发调试入口。设备持有人已经授权 USB 调试及本次固定合成数据实验，操作者才可使用它。USB 只传控制信息，文件字节仍通过已验证的无线 Direct socket。

Android 仅允许调试构建使用。先按普通步骤在 UI 核对对端公钥，再通过 `run-as` 在应用私有目录写入 `files/p0-lab-bootstrap.json`。bootstrap 必须且只能包含：

| 字段 | 要求 |
| --- | --- |
| `schema` | `wanwei.usb-fixture-lab/1` |
| `sessionNonce` | 本次密码学随机生成的 32 位小写十六进制 nonce |
| `peerPin` | 已在该应用 UI 核对确认的完整 64 位对端公钥指纹 |
| `fixtureConsent` | JSON 布尔值 `true`，表示仅此固定数据任务的实验预授权 |
| `fixture` | `ramp256-1mib-v1` |

随后通过显式 Activity 启动参数 `wanwei_lab_nonce` 传入同一 nonce；已有 Activity 支持 single-top 的新 Intent。匹配成功后消费 bootstrap，页面明显显示 USB 辅助固定数据实验。普通启动、错误 nonce、未确认公钥或非 Debug 构建都不能启用此入口。不会自动恢复后台实验或保存长期控制授权。

活动期间，操作者可以在应用私有目录写入 `files/p0-lab-command.json`。每个文件最多 16 KiB，严格 JSON、无重复键或额外字段。共同字段只有 `sessionNonce`、新的随机 `commandId` 和 `action`；动作限 `receive`、`send`、`acceptfixedfixture`、`cancel`。`send` 额外要求字面 IPv4 `peerAddress` 和整数 `port`；当前原生路径校验仍会约束最终目的地址。命令 ID 单次消费，失败也不会自动重放。此入口不能发送用户文件、导入路径、改变无线接口或系统设置。

写文件时先写本次私有临时文件，完成后再原子更名为正式名称，避免轮询读到未完成的 JSON。Android 仅在前台轮询，每个辅助会话最多 600 秒，超时不滚动延长；离开页面、关闭 Activity 或到期后停止文件任务与控制入口。原生建链请求时限仍为 45 秒；每个 TLS 文件任务的绝对上限仍为 180 秒。

Windows 通过明确的 `--usb-p0-lab` 启动参数读取本机 bootstrap；参数字段以 Windows 实现及维护者脚本为准，与 Android 私有文件的字段不完全相同。该入口同样限定已核对的完整公钥和固定 fixture，并保持页面可见、原生端点约束和有限连接窗口。不能将辅助实验的预先授权称为普通用户已手动完成所有系统确认。

辅助接收只对已认证的指定公钥和完整匹配的固定 OFFER 使用实验预授权。报告单独写 `operatorFixturePreauthorized` / `OperatorFixturePreauthorized`；`userAcceptedOffer` / `ReceiverUserAccepted` 不因此变为人工点击。正常 UI 接收仍逐次确认。

## 诊断与隐私

Android 的应用私有报告包括：

- `files/reports/public-identity.json`：公开证书与完整公钥指纹，不包含私钥。
- `files/reports/latest-transfer.json`：真实端点、TLS 与身份结果、字节数、哈希、随机保存文件 basename、完成和关闭结果。
- `files/reports/lab-status.json`：辅助入口/命令是否执行或拒绝及原因；使用 nonce 的 SHA-256 标签，不写原 nonce。

Windows 的本次实验报告位于操作者指定的本机输出目录。原始设备日志、公钥身份关联、私有/受保护密钥材料、实验 nonce、个人照片和本机参数文件留在本地；公开仓库仅包含审阅过的源码、协议和说明。公开兼容性结果应重新整理为脱敏摘要。
