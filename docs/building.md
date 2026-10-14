# 从源码构建

更新：2026-10-14。Windows 与 Android 0.3.0 已实现固定 1 MiB 合成数据的安全文件实验，物理设备双向互传仍待验收。源码不包含 SDK、原始设备报告、签名密钥或个人媒体。

## Windows

优先验证环境为 Windows 11 x64，固定 .NET SDK 10.0.401。根目录 `Start-Windows.cmd` 初始化项目内工具、构建并打开程序；首次需联网，不需要管理员权限。

只做本地逻辑检查（不扫描无线）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-Windows.ps1
```

生成自带运行时的便携 ZIP：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Package-Windows.ps1
```

详见 [便携包说明](windows-portable-package.md)。开发构建和便携包均未做可信发布者代码签名；不要求关闭系统安全设置。

当前开发构建的 178 项本地检查通过，包括平台 TLS 1.3、本机角色互换和错误公钥指纹拒绝。该结果不能替代 Windows ↔ 手机的物理无线测试。

## Android

安装 JDK 17、Android SDK Platform 36、Build Tools 36.0.0，配置 `JAVA_HOME` 和 `ANDROID_HOME`。可由 Android Studio SDK Manager 安装；`local.properties` 只留本机。项目使用 AGP 8.13.2、Kotlin 2.2.21、Gradle 8.13，wrapper 固定官方分发校验值。

在 `apps/android` 下运行：

```powershell
.\gradlew.bat --no-daemon :app:assembleDebug :app:lintDebug :app:testDebugUnitTest
```

macOS/Linux 使用 `./gradlew`。首次需要联网。Windows/JDK 在中文路径中可能遇到 Gradle 测试进程参数文件问题；遇到时可将干净克隆放到纯 ASCII 路径重试，不把它误判成无线失败。

输出为 `app/build/outputs/apk/debug/app-debug.apk`，使用本机 Android Debug 签名，供个人设备实验；不是应用商店正式签名。不同开发机器的 Debug 签名可能不同，不能直接覆盖另一签名的安装，不自动卸载旧应用。

0.3.0 的编译、Lint、24 项本地测试与 APK v2 签名校验已通过。能力探测的最低系统版本仍为 Android 8；安全文件实验要求 Android 10/API 29 以上的平台 TLS 1.3。应用安装身份由 AndroidKeyStore 保管，公开报告只包含公钥证书与指纹。

连接自己的测试设备、授权 USB 调试后，可用 SDK 的 `adb install -r` 安装。开启 WLAN，按系统提示授予附近设备等权限。授权由设备持有人处理。

`scripts/Build-Android.ps1`、`Install-Android.ps1` 和 USB 诊断脚本使用维护者的 `.tools` 布局。普通贡献者优先使用上述标准 SDK + wrapper；本地脚本不会为干净克隆安装整个 Android 工具链。MTP 取报告脚本仅适配既有实验设备名。

普通文件实验需双方线下核对完整公钥、交换本次监听端口，并逐次接受文件请求。默认建连观察窗口为 20 秒；勾选文件实验后为 180 秒。Debug 版可以额外显式启用受限的 USB 辅助固定数据实验，连接窗口最多 600 秒，每个已认证文件会话仍最多 180 秒；离开页面会关闭辅助实验和文件任务。详见 [文件实验步骤](p0-file-transfer.md)。USB 不承载文件内容，不代表已实现自动发现端口或普通用户的一键收发。

## Apple

Windows 可编辑 Swift/SwiftUI 和项目配置。Apple 应用的编译、签名和完整真机调试需要兼容的 Mac 与 Xcode。iPad 开发者模式不提供 Windows 缺少的 Apple SDK。

将整个 `apps/apple` 复制或克隆到 Mac，运行 `bash check-on-mac.sh` 做不签名构建，再按 [真机步骤](apple-p0-quickstart.md) 配置 Team、Wi-Fi Aware 能力并连接 iPad。编译成功仍不代表签名成功或无线互通。

GitHub macOS runner 可以远程检查源码编译，但无法接触桌边的 iPad，也不代替本地无线实测。本仓库不向 CI 上传 Apple 证书或描述文件。

## 自动检查

GitHub Actions 在 Windows 与 Linux 分别构建并运行现有逻辑测试，Apple job 在 macOS 尝试不签名编译。检查不扫描附近设备、不配对、不自动发布；结果只代表对应提交的构建和逻辑验证。
