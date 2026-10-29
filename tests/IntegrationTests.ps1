param(
    [string]$Executable = (Join-Path $PSScriptRoot '..\dist\NetPeek\NetPeek.exe'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\integration'),
    [ValidateRange(8,512)][int]$StressConnections = 96,
    [string]$ExternalHost = '',
    [ValidateRange(1,65535)][int]$ExternalPort = 443,
    [switch]$LiveSockets,
    [switch]$SkipGuiSmoke
)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$checks = New-Object 'System.Collections.Generic.List[object]'
$ownedSockets = New-Object 'System.Collections.Generic.List[System.IDisposable]'
$child = $null
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd_HHmmss_fff')

function Assert-Check([bool]$Condition, [string]$Name, [string]$Detail) {
    $checks.Add([pscustomobject]@{ Name=$Name; Passed=$Condition; Detail=$Detail })
    Write-Host ('{0} {1}: {2}' -f $(if ($Condition) {'PASS'} else {'FAIL'}), $Name, $Detail)
    if (-not $Condition) { throw $Name + ': ' + $Detail }
}
function Get-Snapshot([string]$Label) {
    $path = Join-Path $OutputDirectory ($runId + '_' + $Label + '.json')
    $p = Start-Process -FilePath $Executable -ArgumentList @('--snapshot', ('"{0}"' -f $path)) -PassThru
    if (-not $p.WaitForExit(30000)) { $p.Kill(); throw 'Owned snapshot process exceeded 30 seconds.' }
    Assert-Check ($p.ExitCode -eq 0) ($Label + ' complete exit') ('ExitCode=' + $p.ExitCode)
    $result = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-Check ([bool]$result.IsComplete) ($Label + ' all tables') (($result.Warnings | Out-String).Trim())
    return $result
}
function Find-Endpoint($Snapshot, [string]$Protocol, [string]$Address, [int]$Port, [int]$OwnerPid) {
    return @($Snapshot.Endpoints | Where-Object { $_.Protocol -eq $Protocol -and $_.LocalAddress -eq $Address -and $_.LocalPort -eq $Port -and $_.Process.ProcessId -eq $OwnerPid })
}
function Add-TcpFixture([System.Net.IPAddress]$Address, [int]$Count) {
    $listener = New-Object System.Net.Sockets.TcpListener($Address, 0)
    $listener.Start($Count + 8)
    $ownedSockets.Add($listener.Server)
    $port = $listener.LocalEndpoint.Port
    for ($i=0; $i -lt $Count; $i++) {
        $client = New-Object System.Net.Sockets.TcpClient($Address.AddressFamily)
        $client.Connect($Address, $port)
        $ownedSockets.Add($client)
        $ownedSockets.Add($listener.AcceptTcpClient())
    }
    return [pscustomobject]@{ Address=$Address.ToString(); Port=$port; Count=$Count }
}
function Add-UdpFixture([System.Net.IPAddress]$Address) {
    $endpoint = New-Object System.Net.IPEndPoint($Address, 0)
    $socket = New-Object System.Net.Sockets.UdpClient($endpoint)
    $ownedSockets.Add($socket)
    return [pscustomobject]@{ Address=$Address.ToString(); Port=$socket.Client.LocalEndPoint.Port }
}
function Get-ComparableKey($Row, [string]$Protocol, [bool]$SystemRow) {
    if ($SystemRow) {
        $state = if ($Protocol -eq 'UDP') { 'BOUND' } else { $Row.State.ToString().ToUpperInvariant().Replace('_','') }
        $ownerPid = $Row.OwningProcess
    } else {
        $state = $Row.State.ToString().ToUpperInvariant().Replace('_','')
        $ownerPid = $Row.Process.ProcessId
    }
    $remoteAddress = if ($Protocol -eq 'UDP' -or $state -eq 'LISTEN') { '' } else { $Row.RemoteAddress }
    $remotePort = if ($Protocol -eq 'UDP' -or $state -eq 'LISTEN') { 0 } else { $Row.RemotePort }
    return (@($Protocol,$Row.LocalAddress,$Row.LocalPort,$remoteAddress,$remotePort,$ownerPid,$state) -join '|')
}
function Test-PassiveSnapshot {
    $beforeTcp = @(Get-NetTCPConnection -ErrorAction Stop)
    $beforeUdp = @(Get-NetUDPEndpoint -ErrorAction Stop)
    $passive = Get-Snapshot 'passive'
    $afterTcp = @(Get-NetTCPConnection -ErrorAction Stop)
    $afterUdp = @(Get-NetUDPEndpoint -ErrorAction Stop)
    foreach ($source in @(@{Name='tcp_before'; Rows=$beforeTcp},@{Name='udp_before'; Rows=$beforeUdp},@{Name='tcp_after'; Rows=$afterTcp},@{Name='udp_after'; Rows=$afterUdp})) {
        $source.Rows | Select-Object LocalAddress,LocalPort,RemoteAddress,RemotePort,State,OwningProcess | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory ($runId + '_passive_system_' + $source.Name + '.json')) -Encoding UTF8
    }
    $ours = @{}
    foreach ($row in $passive.Endpoints) { $ours[(Get-ComparableKey $row $row.Protocol $false)] = $true }
    foreach ($protocol in @('TCP','UDP')) {
        $beforeRows = if ($protocol -eq 'TCP') { $beforeTcp } else { $beforeUdp }
        $afterRows = if ($protocol -eq 'TCP') { $afterTcp } else { $afterUdp }
        $afterKeys = @{}
        foreach ($row in $afterRows) { $afterKeys[(Get-ComparableKey $row $protocol $true)] = $true }
        $matched = @{IPv4=0; IPv6=0}
        $missing = New-Object 'System.Collections.Generic.List[string]'
        foreach ($row in $beforeRows) {
            # Get-NetTCPConnection can include BOUND rows outside the native TCP connection table.
            if ($protocol -eq 'TCP' -and $row.State.ToString() -eq 'Bound') { continue }
            $key = Get-ComparableKey $row $protocol $true
            if ($afterKeys.ContainsKey($key)) {
                if (-not $ours.ContainsKey($key)) { $missing.Add($key) }
                else { $family = if ($row.LocalAddress.Contains(':')) {'IPv6'} else {'IPv4'}; $matched[$family]++ }
            }
        }
        Assert-Check ($missing.Count -eq 0) ('Passive stable ' + $protocol + ' tuple/PID/state comparison') ('IPv4=' + $matched.IPv4 + '; IPv6=' + $matched.IPv6 + '; missing=' + ($missing -join ', '))
        Assert-Check (($matched.IPv4 + $matched.IPv6) -gt 0) ('Passive ' + $protocol + ' evidence present') 'At least one stable existing system endpoint was compared; zero in a family does not establish family coverage.'
    }
    $invalidUdp = @($passive.Endpoints | Where-Object { $_.Protocol -eq 'UDP' -and ($null -ne $_.RemoteAddress -or $_.RemotePort -ne 0 -or $_.State -ne 'BOUND') })
    Assert-Check ($invalidUdp.Count -eq 0) 'Passive UDP semantics' 'Local bindings have no invented remote or Internet-active state.'
}

try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    $elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    Assert-Check (-not $elevated) 'Ordinary permissions' 'This acceptance run must use a non-elevated shell.'
    Get-Command Get-NetTCPConnection,Get-NetUDPEndpoint -ErrorAction Stop | Out-Null
    Test-PassiveSnapshot
    if ($LiveSockets) {
    $tcp4 = Add-TcpFixture ([System.Net.IPAddress]::Loopback) $StressConnections
    $tcp6 = Add-TcpFixture ([System.Net.IPAddress]::IPv6Loopback) 8
    $udp4 = Add-UdpFixture ([System.Net.IPAddress]::Loopback)
    $udp6 = Add-UdpFixture ([System.Net.IPAddress]::IPv6Loopback)
    $snapshot = Get-Snapshot 'controlled'
    $systemTcp = @(Get-NetTCPConnection -OwningProcess $PID -ErrorAction Stop)
    $systemUdp = @(Get-NetUDPEndpoint -OwningProcess $PID -ErrorAction Stop)
    $systemTcp | Select-Object LocalAddress,LocalPort,RemoteAddress,RemotePort,State,OwningProcess | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory ($runId + '_system_tcp.json')) -Encoding UTF8
    $systemUdp | Select-Object LocalAddress,LocalPort,OwningProcess | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory ($runId + '_system_udp.json')) -Encoding UTF8
    foreach ($fixture in @($tcp4,$tcp6)) {
        $ours = @(Find-Endpoint $snapshot 'TCP' $fixture.Address $fixture.Port $PID)
        $system = @($systemTcp | Where-Object { $_.LocalAddress -eq $fixture.Address -and $_.LocalPort -eq $fixture.Port })
        Assert-Check ($system.Count -eq ($fixture.Count + 1)) ('System TCP fixture ' + $fixture.Address) ('Expected listener + ' + $fixture.Count + ' accepted connections; actual=' + $system.Count)
        Assert-Check ($ours.Count -eq $system.Count) ('TCP count ' + $fixture.Address) ('NetPeek=' + $ours.Count + '; system=' + $system.Count)
        foreach ($row in $system) {
            $expectedState = if ($row.State.ToString() -eq 'Listen') { 'LISTEN' } else { 'ESTABLISHED' }
            $expectedRemote = if ($expectedState -eq 'LISTEN') { $null } else { $row.RemoteAddress }
            $matches = @($ours | Where-Object { $_.RemoteAddress -eq $expectedRemote -and $_.RemotePort -eq $row.RemotePort })
            if ($matches.Count -ne 1 -or $matches[0].State -ne $expectedState) { throw 'TCP tuple/state mismatch against Windows: ' + ($row | ConvertTo-Json -Compress) }
        }
        Assert-Check $true ('TCP tuples/state/PID ' + $fixture.Address) ($system.Count.ToString() + ' stable owned endpoints matched')
    }
    foreach ($fixture in @($udp4,$udp6)) {
        $ours = @(Find-Endpoint $snapshot 'UDP' $fixture.Address $fixture.Port $PID)
        $system = @($systemUdp | Where-Object { $_.LocalAddress -eq $fixture.Address -and $_.LocalPort -eq $fixture.Port })
        Assert-Check ($system.Count -eq 1 -and $ours.Count -eq 1) ('UDP endpoint/PID ' + $fixture.Address) 'Owned local endpoint agrees with Windows.'
        Assert-Check ($null -eq $ours[0].RemoteAddress -and $ours[0].RemotePort -eq 0 -and $ours[0].State -eq 'BOUND') ('UDP semantics ' + $fixture.Address) 'No invented remote peer or active Internet state.'
    }
    }
    if ($ExternalHost) {
        $external = New-Object System.Net.Sockets.TcpClient
        $ownedSockets.Add($external)
        $connect = $external.ConnectAsync($ExternalHost, $ExternalPort)
        if (-not $connect.Wait(10000)) { throw 'Requested external TCP probe timed out.' }
        $local = $external.Client.LocalEndPoint
        $remote = $external.Client.RemoteEndPoint
        $externalSnapshot = Get-Snapshot 'external'
        $externalSystemRows = @(Get-NetTCPConnection -OwningProcess $PID -LocalPort $local.Port -ErrorAction Stop)
        $externalSystemRows | Select-Object LocalAddress,LocalPort,RemoteAddress,RemotePort,State,OwningProcess | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory ($runId + '_system_external.json')) -Encoding UTF8
        $externalSystem = @($externalSystemRows | Where-Object { $_.LocalAddress -eq $local.Address.ToString() -and $_.RemoteAddress -eq $remote.Address.ToString() -and $_.RemotePort -eq $remote.Port })
        $externalOurs = @(Find-Endpoint $externalSnapshot 'TCP' $local.Address.ToString() $local.Port $PID)
        Assert-Check ($externalSystem.Count -eq 1 -and $externalSystem[0].State.ToString() -eq 'Established') 'External system connection' ($ExternalHost + ' -> ' + $remote.ToString() + '; system=' + ($externalSystemRows | Select-Object LocalAddress,LocalPort,RemoteAddress,RemotePort,State,OwningProcess | ConvertTo-Json -Compress))
        Assert-Check ($externalOurs.Count -eq 1 -and $externalOurs[0].RemoteAddress -eq $remote.Address.ToString() -and $externalOurs[0].RemotePort -eq $remote.Port -and $externalOurs[0].State -eq 'ESTABLISHED') 'External tuple/PID/state' ($local.ToString() + ' -> ' + $remote.ToString())
        $external.Dispose()
    }
    if ($LiveSockets) {
    $fixtureScript = Join-Path $OutputDirectory ($runId + '_child.ps1')
    $readyPath = Join-Path $OutputDirectory ($runId + '_child_ready.json')
    $stopPath = Join-Path $OutputDirectory ($runId + '_child_stop.txt')
    @'
param([string]$ReadyPath,[string]$StopPath)
$ErrorActionPreference='Stop'
$listener=New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback,0)
try {
  $listener.Start()
  @{ ProcessId=$PID; Port=$listener.LocalEndpoint.Port } | ConvertTo-Json | Set-Content -LiteralPath $ReadyPath -Encoding UTF8
  $deadline=[DateTime]::UtcNow.AddSeconds(120)
  while (-not (Test-Path -LiteralPath $StopPath) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
} finally { $listener.Stop() }
'@ | Set-Content -LiteralPath $fixtureScript -Encoding UTF8
    $child = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"{0}"' -f $fixtureScript),'-ReadyPath',('"{0}"' -f $readyPath),'-StopPath',('"{0}"' -f $stopPath)) -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while (-not (Test-Path -LiteralPath $readyPath) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    $ready = Get-Content -LiteralPath $readyPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $beforeExit = Get-Snapshot 'before_exit'
    Assert-Check (@(Find-Endpoint $beforeExit 'TCP' '127.0.0.1' $ready.Port $ready.ProcessId).Count -eq 1) 'Child before exit' ('PID=' + $ready.ProcessId)
    'stop' | Set-Content -LiteralPath $stopPath -Encoding UTF8
    Assert-Check ($child.WaitForExit(10000)) 'Child fixture exited' 'Graceful fixture shutdown.'
    $afterExit = Get-Snapshot 'after_exit'
    Assert-Check (@(Find-Endpoint $afterExit 'TCP' '127.0.0.1' $ready.Port $ready.ProcessId).Count -eq 0) 'Exited process disappears' 'No stale listening endpoint retained.'
    }
    if (-not $SkipGuiSmoke) {
        $smokePath = Join-Path $OutputDirectory ($runId + '_smoke.json')
        $gui = Start-Process -FilePath $Executable -ArgumentList @('--smoke-test', ('"{0}"' -f $smokePath)) -PassThru
        $descendants = New-Object 'System.Collections.Generic.HashSet[int]'
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while (-not $gui.HasExited -and $watch.Elapsed.TotalSeconds -lt 25) {
            $processes = @(Get-CimInstance Win32_Process -Property ProcessId,ParentProcessId)
            do {
                $added = $false
                foreach ($process in $processes) {
                    if (($process.ParentProcessId -eq $gui.Id -or $descendants.Contains([int]$process.ParentProcessId)) -and $descendants.Add([int]$process.ProcessId)) { $added = $true }
                }
            } while ($added)
            Start-Sleep -Milliseconds 200
            $gui.Refresh()
        }
        if (-not $gui.HasExited) { Stop-Process -Id $gui.Id -Force -ErrorAction SilentlyContinue; throw 'GUI smoke did not exit within 25 seconds.' }
        Assert-Check ($gui.ExitCode -eq 0) 'GUI smoke exit' ('ExitCode=' + $gui.ExitCode)
        Assert-Check (Test-Path -LiteralPath $smokePath) 'GUI smoke evidence' $smokePath
        $survivors = @($descendants | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
        Assert-Check ($survivors.Count -eq 0) 'Exit process cleanup' ('Observed child processes=' + $descendants.Count + '; surviving=' + $survivors.Count)
    }
} catch {
    $checks.Add([pscustomobject]@{ Name='Unhandled test failure'; Passed=$false; Detail=$_.Exception.Message })
    Write-Error -ErrorRecord $_ -ErrorAction Continue
} finally {
    if ($child -and -not $child.HasExited) {
        'stop' | Set-Content -LiteralPath $stopPath -Encoding UTF8
        if (-not $child.WaitForExit(10000)) { $child.Kill() }
    }
    foreach ($socket in $ownedSockets) { $socket.Dispose() }
    $report = [pscustomobject]@{ TimestampUtc=[DateTime]::UtcNow.ToString('o'); Executable=$Executable; ExecutableSha256=(Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash; Elevated=$elevated; LiveSocketsEnabled=[bool]$LiveSockets; StressConnections=$(if ($LiveSockets) {$StressConnections} else {0}); ExternalHost=$ExternalHost; ExternalPort=$ExternalPort; ExternalProbeSkipped=(-not [bool]$ExternalHost); GuiSmokeSkipped=[bool]$SkipGuiSmoke; Passed=(@($checks | Where-Object {-not $_.Passed}).Count -eq 0); Checks=@($checks.ToArray()) }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory ($runId + '_results.json')) -Encoding UTF8
}
if (-not $report.Passed) { exit 1 }
