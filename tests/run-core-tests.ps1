param(
    [switch]$LiveSockets,
    [switch]$InterfaceSockets,
    [string]$Executable
)

$ErrorActionPreference = 'Stop'
if ($InterfaceSockets -and -not $LiveSockets) { throw '-InterfaceSockets 必须同时指定 -LiveSockets。' }
if ($LiveSockets) { Write-Warning '已明确启用真实回环 TCP/UDP 套接字和子进程测试；Windows 可能显示网络权限提示。不需要启用任何防火墙权限。' }
if ($InterfaceSockets) { Write-Warning '已明确启用活动网卡 IPv6 link-local 监听测试；可能触发 Windows 防火墙提示。仅在允许此类交互的环境运行。' }
$repo = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw "找不到 .NET Framework C# 编译器: $compiler" }
$output = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $output -Force | Out-Null
if ($Executable) {
    if ($LiveSockets -or $InterfaceSockets) { throw 'Artifact core mode is offline. Use IntegrationTests.ps1 -Executable for final-EXE live socket checks.' }
    $artifact = (Resolve-Path -LiteralPath $Executable).Path
    $beforeHash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash
    Write-Host ("ARTIFACT core target: {0}; SHA256={1}" -f $artifact,$beforeHash)
    $testExecutable = Join-Path $output 'CoreArtifactTests.exe'
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /warnaserror+ /main:ArtifactTestBootstrap "/out:$testExecutable" /reference:System.dll /reference:System.Core.dll "/reference:$artifact" (Join-Path $PSScriptRoot 'ArtifactTestBootstrap.cs') (Join-Path $PSScriptRoot 'CoreArtifactProbe.cs')
    if ($LASTEXITCODE -ne 0) { throw "Artifact core probe compilation failed: $LASTEXITCODE" }
    & $testExecutable $artifact CoreArtifactProbe
    if ($LASTEXITCODE -ne 0) { throw "Artifact core probes failed: $LASTEXITCODE" }
    if ((Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash -ne $beforeHash) { throw 'Target EXE changed during artifact tests.' }
    return
}
Write-Host 'SOURCE UNIT mode: business source is compiled with tests; use -Executable for final-artifact verification.'
$testExecutable = Join-Path $output 'CoreTests.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $repo 'src\NetPeek.Core') -Filter '*.cs' | ForEach-Object { $_.FullName })
$sources += Join-Path $PSScriptRoot 'CoreTests.cs'
& $compiler /nologo /target:exe /platform:anycpu /optimize+ /warnaserror+ "/out:$testExecutable" /reference:System.dll /reference:System.Core.dll $sources
if ($LASTEXITCODE -ne 0) { throw "核心测试编译失败: $LASTEXITCODE" }
$testArguments = @()
if ($LiveSockets) { $testArguments += '--live' }
if ($InterfaceSockets) { $testArguments += '--interfaces' }
& $testExecutable @testArguments
if ($LASTEXITCODE -ne 0) { throw "核心测试失败: $LASTEXITCODE" }
