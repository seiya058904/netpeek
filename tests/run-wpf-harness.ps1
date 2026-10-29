param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [ValidateSet('benchmark','gui','dpi')][string]$Mode='benchmark',
    [ValidateRange(5,120)][int]$SampleSeconds=15,
    [ValidateRange(1,5)][int]$Runs=3,
    [int]$FixtureProcessId=0,
    [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$Label='current'
)
$ErrorActionPreference='Stop'
$Executable=(Resolve-Path -LiteralPath $Executable).Path
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory=(Resolve-Path -LiteralPath $OutputDirectory).Path
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$bin=Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force -Path $bin | Out-Null
$harness=Join-Path $bin 'WpfHarness.exe'
$references=@('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Xaml.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll') | ForEach-Object { '/reference:'+(Join-Path $framework $_) }
$inputFingerprint=('winexe-v1-'+(Get-FileHash (Join-Path $PSScriptRoot 'WpfHarness.cs')).Hash+(Get-FileHash (Join-Path $PSScriptRoot 'NetPeekFixture.cs')).Hash)
$fingerprintPath=Join-Path $bin 'WpfHarness.inputs.sha256'
if(-not (Test-Path -LiteralPath $harness) -or -not (Test-Path -LiteralPath $fingerprintPath) -or (Get-Content -LiteralPath $fingerprintPath -Raw -Encoding UTF8).Trim() -ne $inputFingerprint) {
& (Join-Path $framework 'csc.exe') /nologo /target:winexe /platform:x64 /optimize+ /langversion:5 /warnaserror+ "/out:$harness" $references (Join-Path $PSScriptRoot 'WpfHarness.cs')
if($LASTEXITCODE -ne 0) {throw 'Harness compilation failed.'}
& (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /optimize+ /langversion:5 /warnaserror+ ('/out:'+(Join-Path $bin 'NetPeekFixture.exe')) ('/win32icon:'+(Join-Path (Split-Path -Parent $PSScriptRoot) 'assets\NetPeek.ico')) (Join-Path $PSScriptRoot 'NetPeekFixture.cs')
if($LASTEXITCODE -ne 0) {throw 'Fixture compilation failed.'}
$inputFingerprint | Set-Content -LiteralPath $fingerprintPath -Encoding UTF8
}
Copy-Item -LiteralPath ($Executable+'.config') -Destination ($harness+'.config')
$tag=[DateTime]::UtcNow.ToString('yyyyMMdd_HHmmss_fff')+'_'+$Label
$reportPath=Join-Path $OutputDirectory ($tag+'_harness.json')
$hostProcess=Start-Process -FilePath $harness -ArgumentList @(('"{0}"' -f $Executable),('"{0}"' -f $reportPath),$Mode,$SampleSeconds.ToString(),$Runs.ToString(),$FixtureProcessId.ToString()) -PassThru
Write-Host ('Test-owned WPF host started: PID '+$hostProcess.Id)
if(-not $hostProcess.WaitForExit(630000)) {Stop-Process -Id $hostProcess.Id -Force;throw 'Test-owned WPF host timed out.'}
if($hostProcess.ExitCode -ne 0) {throw ('WPF harness failed; inspect '+$reportPath)}
$report=Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json
if(-not $report.Success) {throw 'WPF harness returned invalid result.'}
$os=Get-CimInstance Win32_OperatingSystem
$cpu=Get-CimInstance Win32_Processor | Select-Object -First 1
$environment=[pscustomobject]@{OS=$os.Caption;OSVersion=$os.Version;CPU=$cpu.Name.Trim();LogicalProcessors=[Environment]::ProcessorCount;PowerShell=$PSVersionTable.PSVersion.ToString()}
foreach($scenario in @('overview','expanded','background')) {
    $scenarioRuns=@($report.Runs | Where-Object {$_.Scenario -eq $scenario})
    if($scenarioRuns.Count -eq 0) {continue}
    $normalized=[pscustomobject]@{SchemaVersion=2;TimestampUtc=[DateTime]::UtcNow.ToString('o');Label=$Label;Scenario=$scenario;Executable=$Executable;ExecutableSha256=$report.TargetSha256;HarnessSha256=$report.HarnessSha256;Environment=$environment;RequestedRuns=$Runs;SampleSeconds=$SampleSeconds;Failure=$report.Failure;Method=$report.Method;Expansion=$report.Expansion;Runs=$scenarioRuns;InvalidRuns=@($report.InvalidRuns | Where-Object {$_.Scenario -eq $scenario})}
    $normalized | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $OutputDirectory ($tag+'_'+$scenario+'_attached.json')) -Encoding UTF8
}
Write-Host ('WPF host result: '+$reportPath)
