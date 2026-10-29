param(
    [Parameter(Mandatory=$true)][string]$BaselineDirectory,
    [Parameter(Mandatory=$true)][string]$CandidateDirectory,
    [Parameter(Mandatory=$true)][string]$OutputPath
)
$ErrorActionPreference='Stop'
function Read-Scenario([string]$Directory,[string]$Scenario) {
    $files=@(Get-ChildItem -LiteralPath $Directory -Filter ('*_' + $Scenario + '_attached.json') -File)
    if ($files.Count -ne 1) { throw ('Expected one ' + $Scenario + ' report in ' + $Directory + ', found ' + $files.Count) }
    $report=Get-Content -LiteralPath $files[0].FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($report.Failure -or $report.Runs.Count -ne $report.RequestedRuns) { throw 'Cannot compare incomplete measurement.' }
    foreach($run in $report.Runs) {
        if(-not $run.Measurement.WindowStateValid) {throw 'Scenario report lacks validated own-window sample state.'}
        foreach($sample in $run.Measurement.Samples) {
            if(($Scenario -eq 'background' -and -not $sample.WindowMinimized) -or ($Scenario -ne 'background' -and (-not $sample.WindowVisible -or $sample.WindowMinimized))) {throw 'A sample does not match its declared visible/minimized scene.'}
        }
    }
    return $report
}
function Summarize($Report) {
    $measurements=@($Report.Runs | ForEach-Object Measurement)
    $samples=@($measurements | ForEach-Object Samples)
    $cpuSeconds=($measurements | Measure-Object CpuSeconds -Sum).Sum
    $elapsed=($measurements | Measure-Object ElapsedSeconds -Sum).Sum
    $foreground=($measurements | Measure-Object ForegroundSamples -Sum).Sum
    $counts=@($Report.Runs | ForEach-Object { $_.Before.EndpointCount; $_.After.EndpointCount })
    $fixtureCounts=@($Report.Runs | ForEach-Object { $_.Before.FixtureEndpointCount; $_.After.FixtureEndpointCount })
    $naturalCounts=@($Report.Runs | ForEach-Object { $_.Before.EndpointCount-$_.Before.FixtureEndpointCount; $_.After.EndpointCount-$_.After.FixtureEndpointCount })
    $descendants=@($samples | ForEach-Object { @($_.Processes).Count - 1 })
    return [pscustomobject]@{
        ExecutableSha256=$Report.ExecutableSha256;Runs=$Report.Runs.Count;RequestedSampleSeconds=$Report.SampleSeconds;MeasuredSeconds=$elapsed
        CpuPercentOfMachine=100.0*$cpuSeconds/$elapsed/$Report.Environment.LogicalProcessors
        CpuPercentOfOneCore=100.0*$cpuSeconds/$elapsed
        AverageWorkingSetBytes=($samples | Measure-Object WorkingSetBytes -Average).Average
        PeakWorkingSetBytes=($samples | Measure-Object WorkingSetBytes -Maximum).Maximum
        AveragePrivateBytes=($samples | Measure-Object PrivateBytes -Average).Average
        PeakPrivateBytes=($samples | Measure-Object PrivateBytes -Maximum).Maximum
        ForegroundSamples=$foreground;TotalSamples=$samples.Count
        VisibleSamples=($measurements | Measure-Object VisibleSamples -Sum).Sum;MinimizedSamples=($measurements | Measure-Object MinimizedSamples -Sum).Sum
        WindowStateValidated=$true;ExcludedInvalidAttempts=@($Report.InvalidRuns).Count
        EndpointCountMin=($counts | Measure-Object -Minimum).Minimum;EndpointCountMax=($counts | Measure-Object -Maximum).Maximum
        FixtureEndpointCountMin=($fixtureCounts | Measure-Object -Minimum).Minimum;FixtureEndpointCountMax=($fixtureCounts | Measure-Object -Maximum).Maximum
        NaturalEndpointCountMin=($naturalCounts | Measure-Object -Minimum).Minimum;NaturalEndpointCountMax=($naturalCounts | Measure-Object -Maximum).Maximum
        MaximumObservedDescendants=($descendants | Measure-Object -Maximum).Maximum
    }
}
$comparisons=@()
$environment=$null
foreach ($scenario in @('overview','expanded','background')) {
    $baseline=Read-Scenario $BaselineDirectory $scenario
    $candidate=Read-Scenario $CandidateDirectory $scenario
    if ($baseline.Environment.CPU -ne $candidate.Environment.CPU -or $baseline.Environment.OSVersion -ne $candidate.Environment.OSVersion -or $baseline.Environment.LogicalProcessors -ne $candidate.Environment.LogicalProcessors) { throw 'Machine or OS environment changed.' }
    if ($baseline.RequestedRuns -ne $candidate.RequestedRuns -or $baseline.SampleSeconds -ne $candidate.SampleSeconds) { throw 'Measurement durations or repeats differ.' }
    if (-not $baseline.HarnessSha256 -or $baseline.HarnessSha256 -ne $candidate.HarnessSha256) { throw 'Test host binaries differ or are missing; use the same built harness for both versions.' }
    $environment=$candidate.Environment
    $before=Summarize $baseline
    $after=Summarize $candidate
    if($before.FixtureEndpointCountMin -le 0 -or $before.FixtureEndpointCountMin -ne $before.FixtureEndpointCountMax -or $after.FixtureEndpointCountMin -ne $after.FixtureEndpointCountMax -or $before.FixtureEndpointCountMin -ne $after.FixtureEndpointCountMin) { throw 'Controlled fixture endpoint count missing or drifted.' }
    $comparisons += [pscustomobject]@{Scenario=$scenario;Baseline=$before;Candidate=$after;ExpansionBefore=$baseline.Expansion;ExpansionAfter=$candidate.Expansion;Delta=[pscustomobject]@{
        CpuMachinePercentagePoints=$after.CpuPercentOfMachine-$before.CpuPercentOfMachine
        AverageWorkingSetBytes=$after.AverageWorkingSetBytes-$before.AverageWorkingSetBytes
        AveragePrivateBytes=$after.AveragePrivateBytes-$before.AveragePrivateBytes
    }}
}
$summary=[pscustomobject]@{
    SchemaVersion=1;GeneratedAtUtc=[DateTime]::UtcNow.ToString('o');Environment=$environment;HarnessSha256=$candidate.HarnessSha256
    DetailScene='Overview shows all naturally present apps. Expanded filters by the owned fixture process ID, then expands only that real process with 513 current loopback endpoints; other processes stay collapsed. Background minimizes that same filtered detail scene. Expansion records disclose actual target visible grids and realized rows for each attempt.'
    WindowStateValidation='Each sampled own HWND records native IsWindowVisible and IsIconic. Every retained overview/expanded sample must be visible and not minimized; every retained background sample must be minimized. Each run restores only its own window before sampling. A run with a mismatching sample is excluded, preserved in local raw reports, and retried at most once. States between samples and physical occlusion are not observed. Foreground focus remains a separate measured property.'
    Method='Same-machine, same sampler and host binary, equal requested durations and repetitions; actual sampled durations normalize CPU. With HarnessSha256 present, unchanged actual product entry points run inside that common WPF test host; measured resources include its sampling overhead, so these are not standalone EXE footprints. Overview and expanded are visible own-window Topmost scenes; OS foreground focus is not guaranteed and actual foreground sample counts are disclosed. Background is minimized. Machine CPU is divided by logical CPU count; one-core percentage is also reported. Real controlled loopback endpoints plus natural system load; count ranges disclose load drift. Working set and private bytes cover all observed descendants. Expansion latency is real binding assignment through layout and dispatcher ContextIdle, not mouse-to-screen latency. This summary intentionally excludes local paths, process IDs, addresses and application names.'
    Scenarios=$comparisons
}
$parent=Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Host ('Sanitized comparison: ' + [IO.Path]::GetFullPath($OutputPath))
