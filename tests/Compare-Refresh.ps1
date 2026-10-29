param(
    [Parameter(Mandatory=$true)][string]$BaselineExecutable,
    [Parameter(Mandatory=$true)][string]$CandidateExecutable,
    [Parameter(Mandatory=$true)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$bin = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force -Path $bin | Out-Null
function Run-Probe([string]$Target,[string]$Label) {
    $targetPath = (Resolve-Path -LiteralPath $Target).Path
    $sha = (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash
    $probe = Join-Path $bin ('RefreshProbe_' + $Label + '.exe')
    $references = @('System.dll','System.Core.dll','System.Web.Extensions.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
    & (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /optimize+ /warnaserror+ /main:ArtifactTestBootstrap "/out:$probe" "/reference:$targetPath" $references (Join-Path $PSScriptRoot 'ArtifactTestBootstrap.cs') (Join-Path $PSScriptRoot 'RefreshProbe.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Refresh probe compilation failed.' }
    $output = @(& $probe $targetPath RefreshProbe)
    if ($LASTEXITCODE -ne 0) { throw 'Refresh probe failed.' }
    if ((Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash -ne $sha) { throw 'Artifact changed during probe.' }
    return [pscustomobject]@{ExecutableSha256=$sha;Result=($output[-1] | ConvertFrom-Json)}
}
$baseline = Run-Probe $BaselineExecutable 'baseline'
$candidate = Run-Probe $CandidateExecutable 'candidate'
$result = $candidate.Result
$passed = $result.ApplicationNotifications -eq 0 -and $result.ProcessNotifications -eq 0 -and $result.EndpointNotifications -eq 0 -and $result.CollectionNotifications -eq 0 -and $result.LastSeenAccurate -and $result.UnknownApplicationReused -and $result.UnknownProcessReused -and $result.UnknownEndpointReused -and $result.UnknownExpansionRetained -and $result.UnknownRawEpochStillDistinct -and $result.UnknownRemainsUnverified
$report = [pscustomobject]@{
    SchemaVersion=1;GeneratedAtUtc=[DateTime]::UtcNow.ToString('o');Passed=$passed
    Method='Same offline synthetic fixture against independently loaded real baseline and candidate EXEs. One verified process with 512 unchanged endpoints, second snapshot two seconds later; counts include all INotifyPropertyChanged and collection events after the first sample. Unknown identity probe advances raw epoch while retaining the same PID and endpoint tuple. This is not live collector or CPU evidence.'
    Baseline=$baseline;Candidate=$candidate
}
$fullOutput = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $fullOutput) | Out-Null
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $fullOutput -Encoding UTF8
if (-not $passed) { throw 'Candidate refresh regression failed.' }
Write-Host ($report | ConvertTo-Json -Depth 8)
