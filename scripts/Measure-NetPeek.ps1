param(
    [string]$Executable = (Join-Path $PSScriptRoot '..\dist\NetPeek\NetPeek.exe'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\performance'),
    [ValidateRange(1,10)][int]$Runs = 3,
    [ValidateRange(5,120)][int]$SampleSeconds = 15,
    [string]$Label = 'current'
)
$ErrorActionPreference = 'Stop'
$Executable = (Resolve-Path -LiteralPath $Executable).Path
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd_HHmmss_fff')
$logicalProcessors = [Environment]::ProcessorCount
if (-not ('NetPeekMeasurementWindow' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class NetPeekMeasurementWindow {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
'@
}
function Get-TreeSample([int]$RootPid) {
    $all = @(Get-CimInstance Win32_Process -Property ProcessId,ParentProcessId -ErrorAction Stop)
    $ids = New-Object 'System.Collections.Generic.HashSet[int]'
    [void]$ids.Add($RootPid)
    do {
        $changed = $false
        foreach ($p in $all) { if ($ids.Contains([int]$p.ParentProcessId) -and $ids.Add([int]$p.ProcessId)) { $changed = $true } }
    } while ($changed)
    $rows = @()
    foreach ($processId in $ids) {
        $p = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($p) {
            try { $rows += [pscustomobject]@{ Id=$p.Id; Name=$p.ProcessName; StartTimeUtc=$p.StartTime.ToUniversalTime().ToString('o'); CpuSeconds=$p.TotalProcessorTime.TotalSeconds; WorkingSetBytes=$p.WorkingSet64; PrivateBytes=$p.PrivateMemorySize64 } } catch { }
        }
    }
    [uint32]$foregroundPid = 0
    [void][NetPeekMeasurementWindow]::GetWindowThreadProcessId([NetPeekMeasurementWindow]::GetForegroundWindow(), [ref]$foregroundPid)
    return [pscustomobject]@{ TimestampUtc=[DateTime]::UtcNow.ToString('o'); Tick=[Diagnostics.Stopwatch]::GetTimestamp(); Processes=$rows; ForegroundPid=$foregroundPid; AppIsForeground=($foregroundPid -eq $RootPid); WorkingSetBytes=($rows | Measure-Object WorkingSetBytes -Sum).Sum; PrivateBytes=($rows | Measure-Object PrivateBytes -Sum).Sum }
}
function Measure-Phase([int]$RootPid, [string]$Phase) {
    $samples = New-Object 'System.Collections.Generic.List[object]'
    $samples.Add((Get-TreeSample $RootPid))
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $SampleSeconds) {
        Start-Sleep -Milliseconds 1000
        if (-not (Get-Process -Id $RootPid -ErrorAction SilentlyContinue)) { throw 'App exited during measurement.' }
        $samples.Add((Get-TreeSample $RootPid))
    }
    $cpuSeconds = 0.0
    for ($i=1; $i -lt $samples.Count; $i++) {
        foreach ($p in $samples[$i].Processes) {
            $previous = @($samples[$i-1].Processes | Where-Object { $_.Id -eq $p.Id -and $_.StartTimeUtc -eq $p.StartTimeUtc })
            if ($previous.Count -eq 1) { $cpuSeconds += [Math]::Max(0.0, [double]($p.CpuSeconds - $previous[0].CpuSeconds)) }
        }
    }
    $elapsed = ($samples[$samples.Count-1].Tick - $samples[0].Tick) / [double][Diagnostics.Stopwatch]::Frequency
    return [pscustomobject]@{
        Phase=$Phase; ElapsedSeconds=$elapsed; CpuSeconds=$cpuSeconds
        CpuPercentOfMachine=(100.0*$cpuSeconds/$elapsed/$logicalProcessors)
        CpuPercentOfOneCore=(100.0*$cpuSeconds/$elapsed)
        AverageWorkingSetBytes=($samples | Measure-Object WorkingSetBytes -Average).Average
        PeakWorkingSetBytes=($samples | Measure-Object WorkingSetBytes -Maximum).Maximum
        AveragePrivateBytes=($samples | Measure-Object PrivateBytes -Average).Average
        PeakPrivateBytes=($samples | Measure-Object PrivateBytes -Maximum).Maximum
        ForegroundSamples=@($samples | Where-Object AppIsForeground).Count
        SampleCount=$samples.Count; Samples=$samples.ToArray()
    }
}
function Get-DirectoryBytes([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return 0 }
    return (Get-ChildItem -LiteralPath $Path -File -Recurse -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
}
$measurements = New-Object 'System.Collections.Generic.List[object]'
$app = $null
$knownChildren = New-Object 'System.Collections.Generic.HashSet[int]'
$failure = $null
try {
    $warmupPath = Join-Path $OutputDirectory ($runId + '_' + $Label + '_warmup.json')
    Write-Host 'Warm-up launch (not included in measured runs).'
    $app = Start-Process -FilePath $Executable -ArgumentList @('--smoke-test', ('"{0}"' -f $warmupPath)) -PassThru
    if (-not $app.WaitForExit(30000)) { throw 'Warm-up smoke test did not exit within 30 seconds.' }
    if ($app.ExitCode -ne 0) { throw 'Warm-up smoke test failed.' }
    $app = $null
    for ($run=1; $run -le $Runs; $run++) {
        $knownChildren.Clear()
        $startupPath = Join-Path $OutputDirectory ($runId + '_' + $Label + '_startup_' + $run + '.json')
        $launchWatch = [Diagnostics.Stopwatch]::StartNew()
        $app = Start-Process -FilePath $Executable -ArgumentList @('--benchmark', ('"{0}"' -f $startupPath), $SampleSeconds.ToString()) -PassThru
        $startup = $null
        while ($launchWatch.Elapsed.TotalSeconds -lt 30) {
            if ($app.HasExited) { throw 'App exited before initial snapshot.' }
            if (Test-Path -LiteralPath $startupPath) {
                try { $startup = Get-Content -LiteralPath $startupPath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $startup=$null }
                if ($startup -and $startup.StartupMilliseconds -gt 0 -and $startup.FirstSnapshotMilliseconds -gt 0) { break }
            }
            Start-Sleep -Milliseconds 100
            $app.Refresh()
        }
        if (-not $startup -or $startup.StartupMilliseconds -le 0 -or $startup.FirstSnapshotMilliseconds -le 0) { throw 'No startup and first-snapshot benchmark metrics within 30 seconds.' }
        if ($startup.Phase -ne 'foreground') { throw 'App was not in its scheduled foreground phase when measurement became ready.' }
        Write-Host ('Run {0}/{1}: foreground ({2}s)' -f $run,$Runs,$SampleSeconds)
        $foreground = Measure-Phase $app.Id 'foreground'
        $phaseWatch = [Diagnostics.Stopwatch]::StartNew()
        $phaseReport = $null
        while ($phaseWatch.Elapsed.TotalSeconds -lt 10) {
            if ($app.HasExited) { throw 'App exited before its scheduled background phase.' }
            try { $phaseReport = Get-Content -LiteralPath $startupPath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $phaseReport = $null }
            if ($phaseReport -and $phaseReport.Phase -eq 'background') { break }
            Start-Sleep -Milliseconds 100
            $app.Refresh()
        }
        if (-not $phaseReport -or $phaseReport.Phase -ne 'background') { throw 'App did not report its scheduled background phase within 10 seconds.' }
        Write-Host ('Run {0}/{1}: minimized background ({2}s)' -f $run,$Runs,$SampleSeconds)
        $background = Measure-Phase $app.Id 'minimized-background'
        foreach ($phase in @($foreground,$background)) {
            foreach ($sample in $phase.Samples) { foreach ($p in $sample.Processes) { if ($p.Id -ne $app.Id) { [void]$knownChildren.Add([int]$p.Id) } } }
        }
        $exitWatch = [Diagnostics.Stopwatch]::StartNew()
        if (-not $app.WaitForExit(15000)) { throw 'App did not finish its scheduled automatic exit within 15 seconds after sampling.' }
        if ($app.ExitCode -ne 0) { throw 'App returned a nonzero benchmark exit code.' }
        $survivors = @($knownChildren | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
        if ($survivors.Count -gt 0) { throw 'Observed child process survived app exit: ' + ($survivors -join ',') }
        $measurements.Add([pscustomobject]@{ Run=$run; Startup=$startup; ForegroundRequestAccepted='N/A: app activates its own benchmark window'; Foreground=$foreground; Background=$background; AutomaticExitWaitMilliseconds=$exitWatch.Elapsed.TotalMilliseconds; ExitCode=$app.ExitCode; ObservedChildren=$knownChildren.Count; SurvivingChildren=$survivors.Count })
        Write-Host ('Run {0}: startup={1:N1}ms; data={2:N1}ms; foreground={3:N3}% CPU; background={4:N3}% CPU' -f $run,$startup.StartupMilliseconds,$startup.FirstSnapshotMilliseconds,$foreground.CpuPercentOfMachine,$background.CpuPercentOfMachine)
        $app = $null
    }
} catch { $failure=$_.Exception.Message; Write-Error -ErrorRecord $_ -ErrorAction Continue }
finally {
    if ($app -and -not $app.HasExited) {
        # Allow the app's own scheduled shutdown first; terminate only this owned process if it fails.
        if (-not $app.WaitForExit(15000)) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    }
    $runtime = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue
    $runtimeDirectories = @()
    foreach ($relative in @('Microsoft.NET\Framework\v4.0.30319','Microsoft.NET\Framework64\v4.0.30319')) {
        $runtimePath = Join-Path $env:WINDIR $relative
        $runtimeDirectories += [pscustomobject]@{ Path=$runtimePath; Bytes=(Get-DirectoryBytes $runtimePath) }
    }
    $os = Get-CimInstance Win32_OperatingSystem
    $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
    $packageDirectory = Split-Path -Parent $Executable
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    $report = [pscustomobject]@{
        TimestampUtc=[DateTime]::UtcNow.ToString('o'); Label=$Label; Executable=$Executable; ExecutableSha256=(Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash
        Environment=[pscustomobject]@{ OS=$os.Caption; OSVersion=$os.Version; OSBuild=$os.BuildNumber; CPU=$cpu.Name; LogicalProcessors=$logicalProcessors; PhysicalMemoryBytes=([int64]$os.TotalVisibleMemorySize*1024); Elevated=$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator); DotNetFrameworkRelease=$runtime.Release; DotNetFrameworkVersion=$runtime.Version; PowerShell=$PSVersionTable.PSVersion.ToString() }
        Package=[pscustomobject]@{ Directory=$packageDirectory; Bytes=(Get-DirectoryBytes $packageDirectory); Files=@(Get-ChildItem -LiteralPath $packageDirectory -Recurse -File | Select-Object FullName,Length); RuntimeRequirement='.NET Framework 4.8 or later (shared Windows runtime; not bundled)'; SharedRuntimeDirectories=$runtimeDirectories; SharedRuntimeDirectoryBytes=($runtimeDirectories | Measure-Object Bytes -Sum).Sum; RuntimeSizeScope='Directory logical sizes only. Shared GAC, Windows component store, native-image caches and hardlink deduplication are not included; this is not an incremental runtime installation size.' }
        Method='One unmeasured warm-up launch then three measured warm launches by default. The app owns its activation, timed minimization, phase reports and automatic exit; this script only reads foreground status and performance counters. Startup is app Stopwatch until first ContentRendered, data is initial snapshot applied. Full discovered process tree sampled about once per second; summed working set can double-count shared pages, private bytes are also reported. CPU deltas only for process instances observed in consecutive samples; sub-second child processes can be missed. Foreground is verified per sample; background means minimized, not tray. AutomaticExitWaitMilliseconds is time spent awaiting scheduled exit after sampling, not shutdown latency. No synthetic before baseline.'
        RequestedRuns=$Runs; SampleSeconds=$SampleSeconds; Failure=$failure; Runs=$measurements.ToArray()
    }
    $reportPath = Join-Path $OutputDirectory ($runId + '_' + $Label + '_results.json')
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    Write-Host ('Results: ' + $reportPath)
}
if ($failure) { exit 1 }
