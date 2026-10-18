param(
    [Parameter(Mandatory=$true)]
    [ValidateSet('TrustPeer','PrepareSender','PrepareSend','PrepareReceive','PrepareGroupProbe','StartGroupProbe','StopGroupProbe','PrepareLocalHotspot','StartLocalHotspot','StopLocalHotspot','SetDestination','Send','Receive','Accept','Reject','Cancel')][string]$Action,
    [string]$PeerFingerprint,
    [string]$PeerAddress,
    [int]$PeerPort
)
$ErrorActionPreference = 'Stop'
$wanweiRoot = Split-Path -Parent $PSScriptRoot
$wanweiAdb = Join-Path $wanweiRoot '.tools\android-sdk\platform-tools\adb.exe'
$wanweiPackage = 'dev.wanwei.androidlab'
function Invoke-P0UiAdb([string[]]$Parts) {
    foreach ($part in $Parts) { if ($part -notmatch '^[A-Za-z0-9/_.-]+$') { throw 'Unexpected UI argument.' } }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo.FileName = $wanweiAdb
    $process.StartInfo.EnvironmentVariables['ADB_LIBUSB'] = '1'
    $process.StartInfo.Arguments = (@('-d') + $Parts) -join ' '
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    $process.StartInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false, $true)
    try {
        [void]$process.Start()
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(8000)) { $process.Kill(); $process.WaitForExit(); throw 'Phone UI operation timed out.' }
        $result = $output.GetAwaiter().GetResult(); $null = $errors.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw 'Phone UI operation failed.' }
        return $result
    } finally { $process.Dispose() }
}
function Read-P0Ui {
    $null = Invoke-P0UiAdb @('shell','uiautomator','dump','/data/local/tmp/wanwei-p0-ui.xml')
    [xml]$ui = Invoke-P0UiAdb @('exec-out','cat','/data/local/tmp/wanwei-p0-ui.xml')
    if ($ui.SelectNodes("//node[@package='$wanweiPackage']").Count -eq 0) { throw 'Unlock the phone and keep Wanwei visible.' }
    return $ui
}
function Tap-P0Node($node) {
    if ($null -eq $node -or $node.package -ne $wanweiPackage -or $node.enabled -ne 'true') { throw 'Expected an enabled Wanwei control.' }
    if ($node.bounds -notmatch '^\[(\d+),(\d+)\]\[(\d+),(\d+)\]$') { throw 'Invalid control bounds.' }
    $x = [int](([int]$Matches[1]+[int]$Matches[3])/2)
    $y = [int](([int]$Matches[2]+[int]$Matches[4])/2)
    $null = Invoke-P0UiAdb @('shell','input','tap',"$x","$y")
}
function Find-P0Control([string]$Name) {
    $id = "$wanweiPackage`:id/$Name"
    $lastBounds = 'not_observed'; $lastEnabled = 'not_observed'
    for ($try = 0; $try -lt 8; $try++) {
        $ui = Read-P0Ui
        $nodes = $ui.SelectNodes("//node[@package='$wanweiPackage' and @resource-id='$id']")
        if ($nodes.Count -gt 1) { throw 'Ambiguous Wanwei control.' }
        if ($nodes.Count -eq 1) {
            # Report only bounded geometry and state, never UI text or identities.
            $lastBounds = if ($nodes[0].bounds -match '^\[(\d+),(\d+)\]\[(\d+),(\d+)\]$') { [string]$nodes[0].bounds } else { 'invalid' }
            $lastEnabled = if ($nodes[0].enabled -in @('true','false')) { [string]$nodes[0].enabled } else { 'unknown' }
        }
        $content = $ui.SelectSingleNode("//node[@package='$wanweiPackage' and @resource-id='android:id/content']")
        if ($null -eq $content -or $content.bounds -notmatch '^\[0,0\]\[(\d+),(\d+)\]$') { throw 'App viewport unavailable.' }
        $x = [int]([int]$Matches[1]*.5); $height = [int]$Matches[2]
        if ($try -eq 0 -and $nodes.Count -eq 0) {
            # The requested field may be above a previous card. Reset the own-app
            # scroll before searching down, rather than endlessly scrolling bottom.
            for ($i=0; $i -lt 6; $i++) {
                $null = Invoke-P0UiAdb @('shell','input','swipe',"$x",([string][int]($height*.25)),"$x",([string][int]($height*.85)),'250')
            }
            continue
        }
        $from = [int]($height*.75); $to = [int]($height*.35)
        if ($nodes.Count -eq 1 -and $nodes[0].bounds -match '^\[(\d+),(\d+)\]\[(\d+),(\d+)\]$') {
            $center = ([int]$Matches[2]+[int]$Matches[4])/2
            $hasArea = [int]$Matches[3] -gt [int]$Matches[1] -and [int]$Matches[4] -gt [int]$Matches[2]
            if ($hasArea -and $center -ge $height*.15 -and $center -le $height*.85) { return $nodes[0] }
            # Small corrections for an already visible control avoid bouncing
            # between Honor's status capsule and the bottom of a long page.
            if ($hasArea) {
                $step = [int]($height*.08)
                $from = [int]($height*.5)
                $to = if ($center -lt $height*.15) { $from+$step } else { $from-$step }
            }
        }
        $null = Invoke-P0UiAdb @('shell','input','swipe',"$x","$from","$x","$to",'400')
    }
    throw "Requested P0 control could not be positioned. ControlId=$id; LastObservedBounds=$lastBounds; LastObservedEnabled=$lastEnabled; Attempts=8."
}
function Set-P0Text([string]$Name,[string]$Value) {
    $node = Find-P0Control $Name
    if ($node.class -ne 'android.widget.EditText' -or $node.enabled -ne 'true') { throw 'Expected editable Wanwei text field.' }
    if ($node.text -eq $Value) { return }
    $oldLength = $node.text.Length
    if ($oldLength -gt 64) { throw 'Unexpected existing input length.' }
    Tap-P0Node $node
    $ui = Read-P0Ui
    $focused = $ui.SelectSingleNode("//node[@resource-id='$($node.'resource-id')' and @focused='true']")
    if ($null -eq $focused) { throw 'Wanwei input focus was not confirmed.' }
    # Move to end and erase only the observed field's text, never use a clipboard.
    $keys = @('shell','input','keyevent','123') + @('67') * $oldLength
    $null = Invoke-P0UiAdb $keys
    $null = Invoke-P0UiAdb @('shell','input','text',$Value)
    $ui = Read-P0Ui
    $verified = $ui.SelectSingleNode("//node[@resource-id='$($node.'resource-id')']")
    if ($null -eq $verified -or $verified.text -ne $Value) { throw 'Input value was not verified.' }
    $null = Invoke-P0UiAdb @('shell','input','keyevent','4')
}
$policy = Invoke-P0UiAdb @('shell','dumpsys','window','policy')
if ($policy -notmatch '(?m)^\s+showing=false\s*$') { throw 'Device owner must unlock the phone.' }
switch ($Action) {
    'PrepareSender' { $null = Find-P0Control 'transfer_peer_port' }
    # Preparation only scrolls the ordinary controls into view; it never taps or
    # starts a transfer, and does not depend on the hidden manual port fields.
    'PrepareSend' {
        if ((Find-P0Control 'transfer_send').enabled -ne 'true') { throw 'Send is unavailable; no transfer was started.' }
    }
    'PrepareReceive' {
        if ((Find-P0Control 'transfer_listen').enabled -ne 'true') { throw 'Receive is unavailable; no transfer was started.' }
    }
    'PrepareGroupProbe' {
        if ((Find-P0Control 'group_probe_start').enabled -ne 'true') { throw 'Standalone group probe is unavailable; no probe was started.' }
    }
    'StartGroupProbe' {
        Tap-P0Node (Find-P0Control 'group_probe_start')
        $ui = Read-P0Ui
        if ($null -eq $ui.SelectSingleNode("//node[@package='$wanweiPackage' and @text='检测手机的 2.4 GHz 独立直连？']")) {
            throw 'Expected standalone group confirmation is absent; no other prompt will be accepted.'
        }
        Tap-P0Node ($ui.SelectSingleNode("//node[@package='$wanweiPackage' and @resource-id='android:id/button1' and @text='开始本次检测']"))
    }
    'StopGroupProbe' { Tap-P0Node (Find-P0Control 'group_probe_stop') }
    'PrepareLocalHotspot' {
        if ((Find-P0Control 'local_hotspot_start').enabled -ne 'true') { throw 'Local Wi-Fi lab is unavailable; no network was created.' }
    }
    'StartLocalHotspot' {
        Tap-P0Node (Find-P0Control 'local_hotspot_start')
        $ui = Read-P0Ui
        if ($null -eq $ui.SelectSingleNode("//node[@package='$wanweiPackage' and @text='创建本次 5 GHz 临时直连？']")) {
            throw 'Expected local Wi-Fi lab confirmation is absent; no other prompt will be accepted.'
        }
        Tap-P0Node ($ui.SelectSingleNode("//node[@package='$wanweiPackage' and @resource-id='android:id/button1' and @text='创建本次直连']"))
    }
    'StopLocalHotspot' { Tap-P0Node (Find-P0Control 'local_hotspot_stop') }
    'TrustPeer' {
        if ($PeerFingerprint -notmatch '^[0-9a-f]{64}$') { throw 'A verified full lowercase fingerprint is required.' }
        Set-P0Text 'transfer_peer_pin' $PeerFingerprint
        Tap-P0Node (Find-P0Control 'transfer_trust_peer')
        $ui = Read-P0Ui
        $confirm = $ui.SelectSingleNode("//node[@package='$wanweiPackage' and @resource-id='android:id/button1' and @text='已核对，确认信任']")
        Tap-P0Node $confirm
    }
    'SetDestination' {
        $parsed=$null
        if (-not [Net.IPAddress]::TryParse($PeerAddress,[ref]$parsed) -or $parsed.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or $PeerPort -lt 1 -or $PeerPort -gt 65535) { throw 'Use the peer native Direct address and displayed dynamic port.' }
        Set-P0Text 'transfer_remote_address' $PeerAddress
        Set-P0Text 'transfer_peer_port' ([string]$PeerPort)
    }
    default {
        $name = @{Send='transfer_send';Receive='transfer_listen';Accept='transfer_accept';Reject='transfer_reject';Cancel='transfer_cancel'}[$Action]
        Tap-P0Node (Find-P0Control $name)
    }
}
$null = Read-P0Ui
if ($Action -in @('PrepareSend','PrepareReceive','PrepareGroupProbe','PrepareLocalHotspot')) {
    Write-Output "Visible Wanwei P0 preparation completed: $Action. No transfer was started."
} elseif ($Action -in @('StartGroupProbe','StopGroupProbe')) {
    Write-Output "Visible standalone group probe action completed: $Action. Inspect standaloneGroupProbe in the fresh group-probe report; this is not file-transfer evidence."
} elseif ($Action -in @('StartLocalHotspot','StopLocalHotspot')) {
    Write-Output "Visible local Wi-Fi lab action completed: $Action. Read the local-hotspot report; this is not file or MLO evidence."
} else {
    Write-Output "Visible Wanwei P0 action completed: $Action. Inspect the fresh transfer report for actual results."
}
