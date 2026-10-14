# Development helper: USB controls only the explicit, fixed-fixture debug lab.
# Dot-source this file; no operation runs until a function is called.
$wanweiP0Root = Split-Path -Parent $PSScriptRoot
$wanweiP0Adb = Join-Path $wanweiP0Root '.tools\android-sdk\platform-tools\adb.exe'

function Invoke-P0LabAdb {
    param([Parameter(Mandatory)][string[]]$Parts, [string]$InputJson, [int]$TimeoutMs = 15000)
    $process = [Diagnostics.Process]::new()
    $process.StartInfo.FileName = $wanweiP0Adb
    $process.StartInfo.ArgumentList.Add('-d')
    foreach ($part in $Parts) { $process.StartInfo.ArgumentList.Add($part) }
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    $process.StartInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false, $true)
    $process.StartInfo.RedirectStandardInput = $null -ne $InputJson -and $InputJson.Length -gt 0
    try {
        [void]$process.Start()
        $read = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        if ($process.StartInfo.RedirectStandardInput) {
            $process.StandardInput.Write($InputJson)
            $process.StandardInput.Close()
        }
        if (-not $process.WaitForExit($TimeoutMs)) {
            $process.Kill(); $process.WaitForExit()
            throw 'The single-device USB lab command timed out.'
        }
        $output = $read.GetAwaiter().GetResult()
        $null = $errors.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw 'The single-device USB lab command failed.' }
        return $output
    } finally { $process.Dispose() }
}

function Write-P0AndroidPrivateJson {
    param([ValidateSet('bootstrap','command')][string]$Kind, [Parameter(Mandatory)]$Value)
    $json = $Value | ConvertTo-Json -Compress -Depth 4
    if ([Text.Encoding]::UTF8.GetByteCount($json) -gt 16384) { throw 'Lab control file is too large.' }
    # Shell text is a fixed app-private path. JSON is sent on stdin, never interpolated as code.
    $file = if ($Kind -eq 'bootstrap') { 'files/p0-lab-bootstrap.json' } else { 'files/p0-lab-command.json' }
    $fixedScript = "'cat > " + $file + '.tmp && mv ' + $file + '.tmp ' + $file + "'"
    # shell -T forwards stdin. exec-out is used only for reads because it does not.
    $null = Invoke-P0LabAdb -Parts @('shell','-T','run-as','dev.wanwei.androidlab','sh','-c',$fixedScript) -InputJson $json
}

function Read-P0AndroidLabJson {
    param([ValidateSet('status','transfer','identity')][string]$Kind)
    $file = switch ($Kind) { 'status' { 'lab-status.json' } 'transfer' { 'latest-transfer.json' } 'identity' { 'public-identity.json' } }
    $raw = Invoke-P0LabAdb -Parts @('exec-out','run-as','dev.wanwei.androidlab','cat',('files/reports/' + $file))
    if ([Text.Encoding]::UTF8.GetByteCount($raw) -gt 262144) { throw 'Unexpected lab report size.' }
    return $raw | ConvertFrom-Json
}

function Enable-P0AndroidUsbLab {
    param([Parameter(Mandatory)][string]$SessionNonce, [Parameter(Mandatory)][string]$PeerPin)
    if ($SessionNonce -cnotmatch '^[0-9a-f]{32}\z' -or $PeerPin -cnotmatch '^[0-9a-f]{64}\z') { throw 'Invalid lab identity or nonce.' }
    Write-P0AndroidPrivateJson -Kind bootstrap -Value @{schema='wanwei.usb-fixture-lab/1';sessionNonce=$SessionNonce;peerPin=$PeerPin;fixtureConsent=$true;fixture='ramp256-1mib-v1'}
    $null = Invoke-P0LabAdb -Parts @('shell','am','start','-W','--activity-single-top','-n','dev.wanwei.androidlab/.MainActivity','--es','wanwei_lab_nonce',$SessionNonce)
    $digest = [Security.Cryptography.SHA256]::Create()
    try { $expectedToken = [BitConverter]::ToString($digest.ComputeHash([Text.Encoding]::ASCII.GetBytes($SessionNonce))).Replace('-','').ToLowerInvariant() }
    finally { $digest.Dispose() }
    $deadline = [datetime]::UtcNow.AddSeconds(3)
    $result = $null
    do {
        try { $result = Read-P0AndroidLabJson -Kind status } catch { $result = $null }
        if ($result -and $result.state -eq 'active' -and $result.sessionToken -ceq $expectedToken) { break }
        Start-Sleep -Milliseconds 100
    } while ([datetime]::UtcNow -lt $deadline)
    if (-not $result -or $result.state -ne 'active' -or $result.sessionToken -cne $expectedToken) { throw 'The current USB fixture session did not activate within its bounded wait.' }
    return [pscustomobject]@{Active=$true;SessionToken=$result.sessionToken;PreauthorizedFixture=$result.fixture}
}

function Send-P0AndroidLabCommand {
    param([Parameter(Mandatory)][string]$SessionNonce,
        [ValidateSet('send','receive','acceptfixedfixture','cancel')][string]$Action,
        [string]$PeerAddress, [int]$Port)
    if ($SessionNonce -cnotmatch '^[0-9a-f]{32}\z') { throw 'Invalid lab nonce.' }
    $Action = $Action.ToLowerInvariant()
    $command = @{sessionNonce=$SessionNonce;commandId=[guid]::NewGuid().ToString('N');action=$Action}
    if ($Action -eq 'send') {
        $parsed = $null
        if (-not [Net.IPAddress]::TryParse($PeerAddress,[ref]$parsed) -or $parsed.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or $Port -notin 1..65535) { throw 'Invalid native peer endpoint.' }
        $command.peerAddress=$parsed.ToString(); $command.port=$Port
    } elseif ($PeerAddress -or $Port) { throw 'This command does not accept an endpoint.' }
    Write-P0AndroidPrivateJson -Kind command -Value $command
    return $command.commandId
}

function Send-P0WindowsLabCommand {
    param([Parameter(Mandatory)][string]$ReportDirectory, [Parameter(Mandatory)][string]$SessionNonce,
        [ValidateSet('send','receive','cancel','end')][string]$Action, [int]$Port)
    if ($SessionNonce -cnotmatch '^[0-9a-f]{32}\z') { throw 'Invalid lab nonce.' }
    $Action = $Action.ToLowerInvariant()
    $command = @{sessionNonce=$SessionNonce;commandId=[guid]::NewGuid().ToString('N');action=$Action}
    if ($Action -eq 'send') {
        if ($Port -notin 1..65535) { throw 'Invalid native peer port.' }
        $command.port=$Port
    } elseif ($Port) { throw 'This command does not accept a port.' }
    $directory = [IO.Path]::GetFullPath($ReportDirectory)
    $temporary = Join-Path $directory 'p0-lab-command.json.tmp'
    [IO.File]::WriteAllText($temporary,($command | ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination (Join-Path $directory 'p0-lab-command.json') -Force
    return $command.commandId
}
