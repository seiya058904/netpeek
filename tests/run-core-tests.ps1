param(
    [switch]$LiveSockets,
    [switch]$InterfaceSockets
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
$executable = Join-Path $output 'CoreTests.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $repo 'src\NetPeek.Core') -Filter '*.cs' | ForEach-Object { $_.FullName })
$sources += Join-Path $PSScriptRoot 'CoreTests.cs'
& $compiler /nologo /target:exe /platform:anycpu /optimize+ /warnaserror+ "/out:$executable" /reference:System.dll /reference:System.Core.dll $sources
if ($LASTEXITCODE -ne 0) { throw "核心测试编译失败: $LASTEXITCODE" }
$testArguments = @()
if ($LiveSockets) { $testArguments += '--live' }
if ($InterfaceSockets) { $testArguments += '--interfaces' }
& $executable @testArguments
if ($LASTEXITCODE -ne 0) { throw "核心测试失败: $LASTEXITCODE" }
