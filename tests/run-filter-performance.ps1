param([string]$Executable = (Join-Path $PSScriptRoot '..\dist\NetPeek\NetPeek.exe'), [Parameter(Mandatory=$true)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
$artifact = (Resolve-Path -LiteralPath $Executable).Path
$before = (Get-FileHash -LiteralPath $artifact).Hash
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$bin = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force -Path $bin | Out-Null
$probe = Join-Path $bin 'FilterPerformanceProbe.exe'
$references = @('System.dll','System.Core.dll','System.Web.Extensions.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
& (Join-Path $framework 'csc.exe') /nologo /target:exe /platform:x64 /optimize+ /warnaserror+ /main:ArtifactTestBootstrap "/out:$probe" "/reference:$artifact" $references (Join-Path $PSScriptRoot 'ArtifactTestBootstrap.cs') (Join-Path $PSScriptRoot 'FilterPerformanceProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Filter performance probe compilation failed.' }
$output = @(& $probe $artifact FilterPerformanceProbe)
$code = $LASTEXITCODE
if ((Get-FileHash -LiteralPath $artifact).Hash -ne $before) { throw 'Artifact changed during filter probe.' }
$report = [pscustomobject]@{ ExecutableSha256 = $before; Result = ($output[-1] | ConvertFrom-Json) }
$destination = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $destination -Encoding UTF8
if ($code -ne 0 -or -not $report.Result.Passed) { throw 'Filter performance regression failed.' }
$report.Result | Format-List
