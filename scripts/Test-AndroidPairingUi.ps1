param([switch]$ConfirmPairing, [switch]$PrepareOnly, [switch]$SelectOnly, [switch]$ConnectSelected, [switch]$PreferPhoneGroupOwner, [switch]$FileTransferLab, [string]$ExpectedVisiblePeerName)
$ErrorActionPreference = 'Stop'
if ((@($ConfirmPairing,$PrepareOnly,$SelectOnly,$ConnectSelected) | Where-Object {$_}).Count -ne 1) { throw 'Choose exactly one preparation, selection or connection mode.' }
if ($ExpectedVisiblePeerName -and ($ExpectedVisiblePeerName.Length -gt 48 -or $ExpectedVisiblePeerName -match '[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]')) { throw 'Expected peer name must be a short, single-line display label.' }
$wanweiRoot = Split-Path -Parent $PSScriptRoot
$wanweiAdb = Join-Path $wanweiRoot '.tools\android-sdk\platform-tools\adb.exe'
$wanweiPackage = 'dev.wanwei.androidlab'

function Invoke-WanweiUiAdb([string[]]$Parts) {
    $process = [Diagnostics.Process]::new()
    $process.StartInfo.FileName = $wanweiAdb
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    $process.StartInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false, $true)
    # Only fixed command words, numeric coordinates, and our own fixed XML path.
    foreach ($part in $Parts) { if ($part -notmatch '^[A-Za-z0-9/_.-]+$') { throw 'Unexpected ADB UI argument.' } }
    $process.StartInfo.Arguments = (@('-d') + $Parts) -join ' '
    try {
        [void]$process.Start()
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(8000)) { $process.Kill(); $process.WaitForExit(); throw 'Phone UI command timed out.' }
        $text = $output.GetAwaiter().GetResult()
        $null = $errors.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw 'Phone UI command failed.' }
        return $text
    } finally { $process.Dispose() }
}
function Read-WanweiUi {
    $null = Invoke-WanweiUiAdb @('shell','uiautomator','dump','/data/local/tmp/wanwei-ui.xml')
    [xml]$xml = Invoke-WanweiUiAdb @('exec-out','cat','/data/local/tmp/wanwei-ui.xml')
    if ($xml.SelectNodes("//node[@package='$wanweiPackage']").Count -eq 0) {
        throw 'Wanwei is not visible. Unlock the phone and open Wanwei; no other UI will be operated.'
    }
    return $xml
}
function Tap-WanweiNode($Node) {
    if ($null -eq $Node -or $Node.package -ne $wanweiPackage -or $Node.enabled -ne 'true') { throw 'Expected enabled Wanwei control.' }
    if ($Node.bounds -notmatch '^\[(\d+),(\d+)\]\[(\d+),(\d+)\]$') { throw 'Invalid control bounds.' }
    $x = [int](([int]$Matches[1] + [int]$Matches[3]) / 2)
    $y = [int](([int]$Matches[2] + [int]$Matches[4]) / 2)
    $null = Invoke-WanweiUiAdb @('shell','input','tap',"$x","$y")
}
function Find-WanweiControl([string]$Id) {
    for ($index = 0; $index -lt 5; $index++) {
        $xml = Read-WanweiUi
        $nodes = $xml.SelectNodes("//node[@package='$wanweiPackage' and @resource-id='$Id']")
        if ($nodes.Count -gt 1) { throw 'Ambiguous app control.' }
        $content = $xml.SelectSingleNode("//node[@package='$wanweiPackage' and @resource-id='android:id/content']")
        if ($null -eq $content -or $content.bounds -notmatch '^\[0,0\]\[(\d+),(\d+)\]$') { throw 'Cannot scroll this app surface.' }
        $x = [int]([int]$Matches[1] * .5)
        $height = [int]$Matches[2]
        $from = [int]($height * .7)
        $to = [int]($height * .4)
        if ($nodes.Count -eq 1 -and $nodes[0].bounds -match '^\[(\d+),(\d+)\]\[(\d+),(\d+)\]$') {
            $centerY = ([int]$Matches[2] + [int]$Matches[4]) / 2
            $hasArea = [int]$Matches[3] -gt [int]$Matches[1] -and [int]$Matches[4] -gt [int]$Matches[2]
            # A partially scrolled button can lie under Honor's status capsule.
            # Place controls inside the app viewport before tapping; do not tap system overlays.
            if ($hasArea -and $centerY -ge $height * .15 -and $centerY -le $height * .85) { return $nodes[0] }
            if ($centerY -lt $height * .15) { $from = [int]($height * .35); $to = [int]($height * .65) }
        }
        $null = Invoke-WanweiUiAdb @('shell','input','swipe',"$x","$from","$x","$to",'700')
    }
    throw 'Requested Wanwei control is not visible.'
}
function Move-WanweiToTop {
    $xml = Read-WanweiUi
    $content = $xml.SelectSingleNode("//node[@package='$wanweiPackage' and @resource-id='android:id/content']")
    if ($null -eq $content -or $content.bounds -notmatch '^\[0,0\]\[(\d+),(\d+)\]$') { throw 'App viewport unavailable.' }
    $x = [int]([int]$Matches[1] * .5); $height = [int]$Matches[2]
    for ($i = 0; $i -lt 6; $i++) {
        $null = Invoke-WanweiUiAdb @('shell','input','swipe',"$x",([string][int]($height*.25)),"$x",([string][int]($height*.85)),'250')
    }
}

# Position the visible scan control before starting the Windows discovery timer.
# Never try to operate the application through the phone lock screen.
$lockState = Invoke-WanweiUiAdb @('shell','dumpsys','window','policy')
if ($lockState -notmatch '(?m)^\s+showing=false\s*$') {
    throw 'Phone unlock is not confirmed. Unlock it yourself and keep Wanwei in front.'
}
if (-not $SelectOnly) {
if (-not $ConnectSelected) { Move-WanweiToTop }
$roleControl = Find-WanweiControl 'dev.wanwei.androidlab:id/pair_prefer_phone_go'
$expectedRoleChecked = if ($PreferPhoneGroupOwner) { 'true' } else { 'false' }
if ($roleControl.checkable -ne 'true' -or $roleControl.enabled -ne 'true') { throw 'Role preference control is not ready.' }
if ($roleControl.checked -ne $expectedRoleChecked) {
    if (-not $PrepareOnly) { throw 'Prepare the requested role preference before starting the Windows timer.' }
    Tap-WanweiNode $roleControl
    $roleControl = Find-WanweiControl 'dev.wanwei.androidlab:id/pair_prefer_phone_go'
    if ($roleControl.checked -ne $expectedRoleChecked) { throw 'Role preference was not confirmed.' }
}
$transferControls = (Read-WanweiUi).SelectNodes("//node[@package='$wanweiPackage' and @resource-id='dev.wanwei.androidlab:id/pair_file_experiment']")
if ($FileTransferLab -or $transferControls.Count -gt 0) {
    $transferControl = Find-WanweiControl 'dev.wanwei.androidlab:id/pair_file_experiment'
    $expectedTransferChecked = if ($FileTransferLab) { 'true' } else { 'false' }
    if ($transferControl.checkable -ne 'true' -or $transferControl.enabled -ne 'true') { throw 'File experiment control is not ready.' }
    if ($transferControl.checked -ne $expectedTransferChecked) {
        if (-not $PrepareOnly) { throw 'Prepare the requested file experiment before starting the Windows timer.' }
        Tap-WanweiNode $transferControl
        $transferControl = Find-WanweiControl 'dev.wanwei.androidlab:id/pair_file_experiment'
        if ($transferControl.checked -ne $expectedTransferChecked) { throw 'File experiment was not confirmed.' }
    }
}
}
if ($ConnectSelected) {
    Tap-WanweiNode (Find-WanweiControl 'dev.wanwei.androidlab:id/pair_connect')
    $xml = Read-WanweiUi
    Tap-WanweiNode ($xml.SelectSingleNode("//node[@package='$wanweiPackage' and @resource-id='android:id/button1' and @text='确认连接']"))
    Write-Output 'The already selected candidate was confirmed. Check the Windows prompt.'
    return
}
if (-not $SelectOnly) { Move-WanweiToTop }
$scanControl = Find-WanweiControl 'dev.wanwei.androidlab:id/scan_nearby'
if ($PrepareOnly) {
    if ($scanControl.enabled -ne 'true') { throw 'The scan control is visible but busy.' }
    Write-Output 'The visible Wanwei scan control is ready. No scan or connection was started.'
    return
}
Tap-WanweiNode $scanControl
$deadline = [datetime]::UtcNow.AddSeconds(22)
do {
    Start-Sleep -Milliseconds 500
    $xml = Read-WanweiUi
    $select = $xml.SelectSingleNode("//node[@package='$wanweiPackage' and @resource-id='dev.wanwei.androidlab:id/pair_candidate_select']")
    $scan = $xml.SelectSingleNode("//node[@package='$wanweiPackage' and @resource-id='dev.wanwei.androidlab:id/scan_nearby']")
    if ($null -ne $scan -and $scan.enabled -eq 'true') { break }
} while ([datetime]::UtcNow -lt $deadline)
if ($null -eq $scan -or $scan.enabled -ne 'true') { throw 'Discovery did not settle in time.' }
Tap-WanweiNode (Find-WanweiControl 'dev.wanwei.androidlab:id/pair_candidate_select')
$xml = Read-WanweiUi
$candidates = $xml.SelectNodes("//node[@package='$wanweiPackage' and @resource-id='android:id/text1']")
if ($ExpectedVisiblePeerName) {
    # This is only a visible selection hint for the operator's own test computer.
    # Names are unauthenticated; ordinary pairing, full pin trust and OFFER consent remain required.
    $matched = @($candidates | Where-Object {
        $lines = $_.text -split "`n"
        $lines.Count -eq 3 -and $lines[0] -cmatch '^候选 [1-9][0-9]* · [0-9a-f]{12}$' -and
            $lines[1] -ceq ('系统名称（未认证）：' + $ExpectedVisiblePeerName)
    })
    if ($matched.Count -ne 1) { throw 'Expected exactly one displayed name matching the local test computer; no candidate was selected.' }
    Tap-WanweiNode $matched[0]
} else {
    if ($candidates.Count -ne 1 -or $candidates[0].text -notmatch '^候选 1 · [0-9a-f]{12}') { throw 'Expected exactly one anonymous candidate; manual selection required.' }
    Tap-WanweiNode $candidates[0]
}
if ($SelectOnly) {
    $null = Find-WanweiControl 'dev.wanwei.androidlab:id/pair_connect'
    Write-Output 'A single candidate is selected; no connection request has been sent.'
    return
}
Tap-WanweiNode (Find-WanweiControl 'dev.wanwei.androidlab:id/pair_connect')
$xml = Read-WanweiUi
$confirm = $xml.SelectSingleNode("//node[@package='$wanweiPackage' and @resource-id='android:id/button1' and @text='确认连接']")
Tap-WanweiNode $confirm
Write-Output 'The single candidate was selected and the visible Wanwei confirmation was pressed. Check the Windows request prompt now.'
