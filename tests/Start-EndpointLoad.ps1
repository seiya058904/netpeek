param(
    [Parameter(Mandatory=$true)][string]$ReadyPath,
    [Parameter(Mandatory=$true)][string]$StopPath,
    [ValidateRange(16,1024)][int]$Connections=256,
    [ValidateRange(60,7200)][int]$MaximumSeconds=3600
)
$ErrorActionPreference='Stop'
$owned=New-Object 'System.Collections.Generic.List[System.IDisposable]'
$listener=New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback,0)
try {
    $listener.Start($Connections+8)
    for ($i=0;$i -lt $Connections;$i++) {
        $client=New-Object System.Net.Sockets.TcpClient
        $client.Connect([System.Net.IPAddress]::Loopback,$listener.LocalEndpoint.Port)
        $owned.Add($client)
        $owned.Add($listener.AcceptTcpClient())
    }
    [pscustomobject]@{ProcessId=$PID;Connections=$Connections;ExpectedEndpoints=2*$Connections+1;LocalPort=$listener.LocalEndpoint.Port;StartedAtUtc=[DateTime]::UtcNow.ToString('o');ExpiresAtUtc=[DateTime]::UtcNow.AddSeconds($MaximumSeconds).ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath $ReadyPath -Encoding UTF8
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $StopPath) -and $watch.Elapsed.TotalSeconds -lt $MaximumSeconds) { Start-Sleep -Milliseconds 500 }
} finally {
    foreach($socket in $owned) { $socket.Dispose() }
    $listener.Stop()
}
