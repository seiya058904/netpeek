param([string]$Executable = (Join-Path $PSScriptRoot '..\dist\NetPeek\NetPeek.exe'))
$ErrorActionPreference = 'Stop'
$artifact = (Resolve-Path -LiteralPath $Executable).Path
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$output = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$testExecutable = Join-Path $output 'ControllerStateArtifactTests.exe'
$beforeHash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash
Write-Host ("ARTIFACT controller target: {0}; SHA256={1}" -f $artifact,$beforeHash)
$references = @('/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Xaml.dll', "/reference:$framework\WPF\WindowsBase.dll", "/reference:$framework\WPF\PresentationCore.dll", "/reference:$framework\WPF\PresentationFramework.dll", "/reference:$artifact")
& $compiler /nologo /target:exe /platform:x64 /optimize+ /warnaserror+ /main:ArtifactTestBootstrap "/out:$testExecutable" $references (Join-Path $PSScriptRoot 'ArtifactTestBootstrap.cs') (Join-Path $PSScriptRoot 'ControllerStateTests.cs')
if ($LASTEXITCODE -ne 0) { throw "Controller artifact tests compilation failed: $LASTEXITCODE" }
& $testExecutable $artifact ControllerStateTests
if ($LASTEXITCODE -ne 0) { throw "Controller artifact tests failed: $LASTEXITCODE" }
if ((Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash -ne $beforeHash) { throw 'Target EXE changed during controller tests.' }
