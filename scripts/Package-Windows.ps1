param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Initialize-Toolchain.ps1')
$dotnetPath = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$projectPath = Join-Path $projectRoot 'apps\windows\Wanwei.Windows.csproj'
[xml]$projectXml = Get-Content -LiteralPath $projectPath -Raw
$version = [string]$projectXml.Project.PropertyGroup.Version
$packageName = "wanwei-windows-lab-$version-win-x64"
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$runRoot = Join-Path $projectRoot "artifacts\packages\$runId"
$packageRoot = Join-Path $runRoot $packageName
$buildRoot = Join-Path $runRoot 'build-evidence'
New-Item -ItemType Directory -Path $packageRoot, $buildRoot | Out-Null
$runtimeLock = Join-Path $buildRoot 'packages.win-x64.lock.json'
$originalLock = Join-Path $projectRoot 'apps\windows\packages.lock.json'
$originalLockHash = (Get-FileHash -LiteralPath $originalLock -Algorithm SHA256).Hash
Copy-Item -LiteralPath $originalLock -Destination $runtimeLock

Push-Location $projectRoot
try {
    & $dotnetPath restore $projectPath --locked-mode *> (Join-Path $buildRoot 'restore.log')
    if ($LASTEXITCODE -ne 0) { throw "Locked restore failed. See $buildRoot\restore.log" }
    & $dotnetPath publish $projectPath -c Release -r win-x64 --self-contained true -o $packageRoot `
        '-p:PublishSingleFile=false' '-p:PublishTrimmed=false' '-p:DebugType=None' '-p:DebugSymbols=false' `
        "-p:NuGetLockFilePath=$runtimeLock" '-p:RestoreLockedMode=false' *> (Join-Path $buildRoot 'publish.log')
    if ($LASTEXITCODE -ne 0) { throw "Publish failed. See $buildRoot\publish.log" }
} finally { Pop-Location }
if ((Get-FileHash -LiteralPath $originalLock -Algorithm SHA256).Hash -ne $originalLockHash) {
    throw 'Publishing unexpectedly changed the source package lock file.'
}

# Read the exact restored package versions, rather than copying SDK-wide notices
# and implying that they describe the smaller application runtime distribution.
$assets = Get-Content -LiteralPath (Join-Path $projectRoot 'apps\windows\obj\project.assets.json') -Raw | ConvertFrom-Json
$packageCache = [string]$assets.project.restore.packagesPath
$packageRecords = @()
$downloadPackages = @($assets.project.frameworks.PSObject.Properties | ForEach-Object { $_.Value.downloadDependencies })
foreach ($dependency in $downloadPackages) {
    if ($dependency.name -notmatch '^Microsoft\.(NETCore\.App\.Runtime\.win-x64|WindowsDesktop\.App\.Runtime\.win-x64|Windows\.SDK\.NET\.Ref)$') { continue }
    $dependencyVersion = ([regex]::Match([string]$dependency.version, '[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?')).Value
    $dependencyPath = Join-Path $packageCache ($dependency.name.ToLowerInvariant() + '\' + $dependencyVersion)
    if (-not (Test-Path -LiteralPath $dependencyPath)) { throw "Missing restored package: $($dependency.name) $dependencyVersion" }
    $noticeRoot = Join-Path $packageRoot ("licenses\" + $dependency.name + '\' + $dependencyVersion)
    New-Item -ItemType Directory -Path $noticeRoot -Force | Out-Null
    $noticeFiles = @(Get-ChildItem -LiteralPath $dependencyPath -Recurse -File | Where-Object {
        $_.Name -match '(?i)(license|notice)' -or $_.Extension -eq '.nuspec'
    })
    foreach ($noticeFile in $noticeFiles) {
        $relative = $noticeFile.FullName.Substring($dependencyPath.Length).TrimStart('\')
        $destination = Join-Path $noticeRoot $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $noticeFile.FullName -Destination $destination
    }
    if ($dependency.name -ne 'Microsoft.Windows.SDK.NET.Ref' -and -not ($noticeFiles.Name -match '(?i)license')) {
        throw "The runtime package has no license file: $($dependency.name)"
    }
    $packageRecords += [pscustomobject]@{ name=$dependency.name; version=$dependencyVersion; bundledNoticeFiles=$noticeFiles.Count }
}
foreach ($requiredPackage in @('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64', 'Microsoft.Windows.SDK.NET.Ref')) {
    if ($requiredPackage -notin $packageRecords.name) { throw "No package provenance found for $requiredPackage" }
}

# The Windows SDK targeting package supplies its license as a URL in its nuspec.
# Save the official response, plus the CsWinRT MIT notice for WinRT.Runtime.dll.
$sdkNotice = Join-Path $packageRoot 'licenses\windows-sdk'
New-Item -ItemType Directory -Path $sdkNotice | Out-Null
$winRtProductVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $packageRoot 'WinRT.Runtime.dll')).ProductVersion
if ($winRtProductVersion -notmatch '\+([a-f0-9]{40})$') { throw 'WinRT.Runtime.dll has no source commit in its product version; review its license source before packaging.' }
$csWinRtCommit = $Matches[1]
$licenseSources = @(
    @{ url='https://aka.ms/WinSDKLicenseURL'; file='Windows-SDK-license.rtf' },
    @{ url="https://raw.githubusercontent.com/microsoft/CsWinRT/$csWinRtCommit/LICENSE"; file='CsWinRT-LICENSE.txt' }
)
$sourceRecords = @()
foreach ($source in $licenseSources) {
    $destination = Join-Path $sdkNotice $source.file
    # The SDK URL returns application/octet-stream with an RTF body. Save the
    # raw response so PowerShell cannot stringify a byte array into numbers.
    Invoke-WebRequest -Uri $source.url -UseBasicParsing -OutFile $destination
    if ((Get-Item -LiteralPath $destination).Length -lt 500) { throw "License response unexpectedly short: $($source.url)" }
    if ($source.file.EndsWith('.rtf') -and (Get-Content -LiteralPath $destination -Raw) -notmatch '^\{\\rtf') {
        throw 'The Windows SDK license response is no longer the expected RTF document.'
    }
    $sourceRecords += [pscustomobject]@{ url=$source.url; savedAs=('licenses/windows-sdk/' + $source.file); sha256=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash }
}

$upstreamLicense = Join-Path $packageRoot 'licenses\windows-wifidirect'
New-Item -ItemType Directory -Path $upstreamLicense | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'references\upstream\windows-wifidirect\LICENSE') -Destination $upstreamLicense
Copy-Item -LiteralPath (Join-Path $projectRoot 'references\upstream\windows-wifidirect\source-manifest.json') -Destination $upstreamLicense
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md') -Destination (Join-Path $packageRoot 'SOURCE-THIRD-PARTY-NOTICES.md')
if (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'LICENSE'))) { throw 'Project LICENSE is required before distributing the package.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $packageRoot
if (Test-Path -LiteralPath (Join-Path $projectRoot 'NOTICE')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'NOTICE') -Destination $packageRoot
}

$readme = @"
万维计划 Windows 直连实验室 $version（便携实验版 / win-x64）

使用：先完整解压 ZIP，再双击 Wanwei.Windows.exe。不要只复制 EXE。
本包包含所需 .NET 运行时，不要求另装 .NET SDK；优先支持 Windows 11 x64。
Windows 的无线网卡、驱动和系统接口仍须支持对应功能。

已实现：本机能力检查、发现、系统配对/直连、诊断，以及固定 1 MiB 合成文件实验。
文件实验使用 TLS 1.3 双向认证、完整公钥指纹绑定、实际 P2P 地址和安全保存。
Windows 与手机的物理文件互传、普通确认流程和负载下保网仍待验收。
尚不支持选择用户文件；速度测试、iOS 互通、多链路聚合和投屏仍待开发或验证。
它是实验室工具，不是已经完成的隔空投送产品；不能保证所有网卡保留原 Wi-Fi。
若系统提示连接将断开现有 WLAN，请拒绝并结束该轮实验。

普通文件实验需线下核对完整 64 位公钥指纹、交换本次监听端口，并逐次接受接收请求。
开发调试入口 --usb-p0-lab 需显式固定数据预授权，仅用于已核对对端的 1 MiB 合成文件。
USB 只交换控制信息；文件仍必须走原生无线 Direct 路径，不能改走普通局域网。
辅助实验的预授权在报告中与人工点击区分，不代表一键发现、确认和收发已经验收。

程序不安装驱动、服务或开机启动项。关闭窗口后程序退出。
报告默认保存在 %LOCALAPPDATA%\WanweiLab\Reports，包含诊断和匿名观测信息。
原始设备报告、受保护身份文件、私钥、实验 nonce 和个人媒体留在本地，不公开上传。
本包未作代码签名，Windows 可能显示未知发布者；没有伪造签名或绕过安全设置。

LICENSE 为项目许可；SOURCE-THIRD-PARTY-NOTICES.md 是原工程来源记录，
其中源码相对链接应在源码仓库查看。实际随包依赖的许可位于 licenses 目录，
DEPENDENCIES.json 记录本次运行时版本及获取的许可来源。
内置运行时不会自动随系统 .NET 更新；后续安全更新需要重新发布本包。
"@
[IO.File]::WriteAllText((Join-Path $packageRoot '使用说明.txt'), $readme, [Text.UTF8Encoding]::new($true))
$runtimeConfig = Get-Content -LiteralPath (Join-Path $packageRoot 'Wanwei.Windows.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtimeConfig.runtimeOptions.framework -or $runtimeConfig.runtimeOptions.frameworks) { throw 'The output is framework-dependent, not self-contained.' }
foreach ($requiredFile in @('Wanwei.Windows.exe', 'Wanwei.Windows.dll', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'PresentationFramework.dll', 'WinRT.Runtime.dll', 'Microsoft.Windows.SDK.NET.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageRoot $requiredFile))) { throw "Missing required file: $requiredFile" }
}
$dependencyManifest = [ordered]@{
    generatedAtUtc=[DateTime]::UtcNow.ToString('o'); appVersion=$version; runtimeIdentifier='win-x64'; selfContained=$true
    includedFrameworks=$runtimeConfig.runtimeOptions.includedFrameworks; restoredPackages=$packageRecords; extraLicenseSources=$sourceRecords
    winRtProductVersion=$winRtProductVersion; csWinRtSourceCommit=$csWinRtCommit
    microsoftSampleCommit='4eb2fcb499c5bc549e918920cfd2b64396a650d9'
}
$dependencyManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $packageRoot 'DEPENDENCIES.json') -Encoding UTF8
$fileManifest = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{ path=$_.FullName.Substring($packageRoot.Length).TrimStart('\').Replace('\','/'); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$fileManifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $packageRoot 'FILES-SHA256.json') -Encoding UTF8

$zipPath = Join-Path $runRoot ($packageName + '.zip')
Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal
$verificationRoot = Join-Path $runRoot 'unpacked-verification'
Expand-Archive -LiteralPath $zipPath -DestinationPath $verificationRoot
$unpackedRoot = Join-Path $verificationRoot $packageName
foreach ($entry in $fileManifest) {
    if ((Get-FileHash -LiteralPath (Join-Path $unpackedRoot $entry.path) -Algorithm SHA256).Hash -ne $entry.sha256) { throw "ZIP verification failed for $($entry.path)" }
}

# Run local checks, including bounded loopback TLS/fixture checks. They do not
# start wireless discovery, advertisements, pairing, a GUI, or device transfers.
# Use the extracted EXE.
$tracePath = Join-Path $buildRoot 'self-test-host-trace.txt'
$startInfo = New-Object Diagnostics.ProcessStartInfo
$startInfo.FileName = Join-Path $unpackedRoot 'Wanwei.Windows.exe'
$startInfo.Arguments = '--self-test'
$startInfo.WorkingDirectory = $unpackedRoot
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$emptyRuntime = Join-Path $buildRoot 'empty-runtime'
New-Item -ItemType Directory -Path $emptyRuntime | Out-Null
$startInfo.EnvironmentVariables['DOTNET_ROOT'] = $emptyRuntime
$startInfo.EnvironmentVariables['DOTNET_ROOT_X64'] = $emptyRuntime
$startInfo.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
$startInfo.EnvironmentVariables['DOTNET_HOST_TRACE'] = '1'
$startInfo.EnvironmentVariables['DOTNET_HOST_TRACEFILE'] = $tracePath
$process = New-Object Diagnostics.Process
$process.StartInfo = $startInfo
[void]$process.Start()
$stdoutTask = $process.StandardOutput.ReadToEndAsync()
$stderrTask = $process.StandardError.ReadToEndAsync()
if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'Extracted EXE self-test timed out.' }
$stdout = $stdoutTask.GetAwaiter().GetResult()
$stderr = $stderrTask.GetAwaiter().GetResult()
[IO.File]::WriteAllText((Join-Path $buildRoot 'self-test.stdout.txt'), $stdout, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $buildRoot 'self-test.stderr.txt'), $stderr, [Text.UTF8Encoding]::new($false))
if ($process.ExitCode -ne 0 -or $stdout -notmatch '(\d+) local (?:logic )?checks passed') { throw "Extracted EXE self-test failed. See $buildRoot" }
$testCount = [int]$Matches[1]
$hostTrace = Get-Content -LiteralPath $tracePath -Raw
if (-not $hostTrace.Contains((Join-Path $unpackedRoot 'coreclr.dll'))) { throw 'The host trace did not confirm the bundled runtime was loaded.' }
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
"$zipHash  $($packageName).zip" | Set-Content -LiteralPath ($zipPath + '.sha256') -Encoding ASCII
$verification = [ordered]@{
    verifiedAtUtc=[DateTime]::UtcNow.ToString('o'); archive=(Split-Path -Leaf $zipPath); archiveBytes=(Get-Item -LiteralPath $zipPath).Length; sha256=$zipHash
    payloadFileCount=$fileManifest.Count; payloadBytes=($fileManifest | Measure-Object -Property bytes -Sum).Sum; extractedFileHashesVerified=$true
    localChecksPassed=$testCount; testExitCode=$process.ExitCode; launchedExtractedExe=$true; bundledCoreClrConfirmedByHostTrace=$true
    wirelessOperationsPerformed=$false; networkWasDisabled=$false; guiAcceptance='not_run'; cleanMachineAcceptance='not_run'
    executableSignature=(Get-AuthenticodeSignature -LiteralPath (Join-Path $unpackedRoot 'Wanwei.Windows.exe')).Status.ToString()
}
$verification | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runRoot 'verification.json') -Encoding UTF8
Write-Output ([pscustomobject]@{ Archive=$zipPath; Sha256=$zipHash; Bytes=$verification.archiveBytes; LocalChecksPassed=$testCount; Verification=(Join-Path $runRoot 'verification.json') })
