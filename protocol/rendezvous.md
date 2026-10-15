# Direct 接收端口提示 · 0.1

状态：Windows / Android 0.3.2 开发中的独立控制协议。它不修改 `wanwei-p0/0.1` 文件帧、完整公钥信任、TLS 1.3 或接收确认要求。真实设备自动端口流程须另行验收。

首次仍当面核对完整 SPKI 指纹并明确确认。接收方点击等待接收后，先将 TCP listener 绑定本轮实际原生 P2P IPv4 地址的动态端口，再绑定同一地址的 UDP 45873。发送方在自己的实际 P2P IPv4 上使用临时 UDP 端口，单播查询原生适配器已提供的对端 IPv4:45873。禁止通配地址监听、局域网回退、广播、子网扫描、DNS 和按常见 IP 猜测接口。

Android 当前适配器只可靠提供组主 IPv4，因此自动模式仅支持手机为 P2P 客户端、Windows 为组主的角色组合。手机作为组主时明确拒绝自动模式；手工实验必须由操作者明确切换，不能自动回退。Wi-Fi 频段仍由系统实际协商，协议不承诺双 5 GHz 或多链路。

每个 UDP 数据报是严格 UTF-8 JSON，1–1,024 字节，无 BOM、重复键、额外键、数组、嵌套对象、布尔隐式转换或尾部数据。以下是假身份测试向量，不能用于实际信任：

```json
{"protocol":"wanwei-rendezvous/0.1","type":"QUERY","nonce":"cccccccccccccccccccccccccccccccc","senderIdentity":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","receiverIdentity":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}
```

```json
{"protocol":"wanwei-rendezvous/0.1","type":"ANSWER","nonce":"cccccccccccccccccccccccccccccccc","senderIdentity":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","receiverIdentity":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","port":53127}
```

字段集合必须精确匹配上述两种类型。`nonce` 为每次发送按钮操作新生成的 16 字节随机值，32 位小写十六进制，同一次有界查询的重试沿用它。两项身份均为 64 位小写十六进制且不相同。`port` 是无前导零的十进制整数 1–65535，不接受字符串、小数、指数、符号、null 或溢出。

接收端仅回复来自实际原生对端 IP、且双方身份字段与本次已确认的公钥一致的 QUERY。回复从本机原生地址:45873 发往该查询的临时源端口。发送端仅采用实际对端 IP:45873、当前 nonce 和双方身份完全一致的 ANSWER。

这些字段全是**未认证线索**。nonce 只排除无关及旧响应，不提供身份认证；数据报中的公开指纹可被仿造，也会暴露给当前直连对端。不得据此建立信任、自动接受文件或判断成功。发送方只把返回端口交给原有 P0 socket；完整公钥绑定的 TLS 1.3 双向认证、HELLO 身份核验、接收方逐次接受及安全保存仍全部执行。错误提示最多使一次连接失败，不能导入地址、路径、文件或命令。

查询最多 10 秒、20 次，间隔 500 ms。接收端提示最多与初始 TCP 等待相同的 30 秒、64 次回复，相邻回复至少 100 ms。无匹配响应、端口冲突或无法验证原生路径时停止并明确失败。退出 listening、TCP 已接受、取消、离开页面或原生连接失效时关闭 UDP；清理未确认时阻止继续使用该传输流程。控制提示不覆盖或延长 P0 的 TCP/TLS 与文件会话等待上限。

报告必须区分自动端口提示、手工参数和 USB 固定数据实验；不能因为端口已自动交换就把 `applicationIdentityAuthenticated` 或接收同意标为 true。host loopback 的 UDP/解析/超时测试不是无线实测，也不证明手机已断开 USB 后可用。
