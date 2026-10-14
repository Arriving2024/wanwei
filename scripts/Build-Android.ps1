$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$javaDir = Get-ChildItem -LiteralPath (Join-Path $projectRoot '.tools\java') -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'bin\java.exe') } | Select-Object -First 1
if (-not $javaDir) { throw 'Project JDK missing. Use the standard SDK and Gradle wrapper described in docs/building.md.' }
$env:JAVA_HOME = $javaDir.FullName
$env:ANDROID_HOME = Join-Path $projectRoot '.tools\android-sdk'
$env:GRADLE_USER_HOME = Join-Path $projectRoot '.tools\gradle-cache'
$gradlePath = Join-Path $projectRoot '.tools\gradle\gradle-8.13\bin\gradle.bat'
if (-not (Test-Path -LiteralPath $gradlePath)) { throw 'Project Gradle 8.13 missing.' }
if (-not (Test-Path -LiteralPath (Join-Path $env:ANDROID_HOME 'platforms\android-36\android.jar'))) { throw 'Android SDK platform 36 missing.' }
$proxyArguments = @()
$proxySettings = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction SilentlyContinue
# Honor the existing Windows proxy for build downloads only. Never change system settings.
if ($proxySettings.ProxyEnable -eq 1 -and $proxySettings.ProxyServer -match '^([^:;/]+):(\d+)$') {
    $proxyArguments = @("-Dhttp.proxyHost=$($Matches[1])", "-Dhttp.proxyPort=$($Matches[2])", "-Dhttps.proxyHost=$($Matches[1])", "-Dhttps.proxyPort=$($Matches[2])")
}
Push-Location (Join-Path $projectRoot 'apps\android')
try {
    # Compile host tests with Gradle, run them directly below. Gradle's test-worker argfile
    # mishandles this workspace's Unicode path on this Windows/JDK combination.
    & $gradlePath @proxyArguments --no-daemon --console=plain ':app:assembleDebug' ':app:lintDebug' ':app:compileDebugUnitTestKotlin'
    if ($LASTEXITCODE -ne 0) { throw 'Android build or lint failed.' }
    $dependencyCache = Join-Path $env:GRADLE_USER_HOME 'caches\modules-2\files-2.1'
    $junitJar = Get-ChildItem -LiteralPath (Join-Path $dependencyCache 'junit\junit\4.13.2') -Filter 'junit-4.13.2.jar' -File -Recurse | Select-Object -First 1
    $hamcrestJar = Get-ChildItem -LiteralPath (Join-Path $dependencyCache 'org.hamcrest\hamcrest-core\1.3') -Filter 'hamcrest-core-1.3.jar' -File -Recurse | Select-Object -First 1
    $kotlinJar = Get-ChildItem -LiteralPath (Join-Path $dependencyCache 'org.jetbrains.kotlin\kotlin-stdlib') -Filter 'kotlin-stdlib-*.jar' -File -Recurse | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $junitJar -or -not $hamcrestJar -or -not $kotlinJar) { throw 'Host test runtime dependency missing.' }
    $testClasspath = @($junitJar.FullName, $hamcrestJar.FullName, $kotlinJar.FullName,
        (Join-Path $projectRoot 'apps\android\app\build\tmp\kotlin-classes\debug'),
        (Join-Path $projectRoot 'apps\android\app\build\tmp\kotlin-classes\debugUnitTest')) -join ';'
    & (Join-Path $javaDir.FullName 'bin\java.exe') '-cp' $testClasspath 'org.junit.runner.JUnitCore' 'dev.wanwei.androidlab.PairingSafetyTest'
    if ($LASTEXITCODE -ne 0) { throw 'Android host policy tests failed.' }
    $apk = Join-Path $projectRoot 'apps\android\app\build\outputs\apk\debug\app-debug.apk'
    $outputDirectory = Join-Path $projectRoot 'artifacts\android'
    New-Item -ItemType Directory -Force $outputDirectory | Out-Null
    Copy-Item -LiteralPath $apk -Destination (Join-Path $outputDirectory 'wanwei-android-lab-0.2.5-debug.apk')
    $signer = Join-Path $env:ANDROID_HOME 'build-tools\36.0.0\apksigner.bat'
    & $signer verify --verbose $apk
    if ($LASTEXITCODE -ne 0) { throw 'Android APK signature validation failed.' }
    Get-FileHash -LiteralPath (Join-Path $outputDirectory 'wanwei-android-lab-0.2.5-debug.apk') -Algorithm SHA256
} finally { Pop-Location }
