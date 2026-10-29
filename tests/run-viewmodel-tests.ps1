$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw "找不到 .NET Framework C# 编译器: $compiler" }
$output = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$executable = Join-Path $output 'ViewModelTests.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $repo 'src\NetPeek.Core') -Filter '*.cs' | ForEach-Object { $_.FullName })
$sources += Join-Path $repo 'src\NetPeek.App\CaptureHistory.cs'
$sources += Join-Path $repo 'src\NetPeek.App\ViewModels.cs'
$sources += Join-Path $PSScriptRoot 'ViewModelTests.cs'
$references = @('/reference:System.dll', '/reference:System.Core.dll', "/reference:$framework\WPF\WindowsBase.dll", "/reference:$framework\WPF\PresentationCore.dll", "/reference:$framework\WPF\PresentationFramework.dll")
& $compiler /nologo /target:exe /platform:anycpu /optimize+ /warnaserror+ "/out:$executable" $references $sources
if ($LASTEXITCODE -ne 0) { throw "视图模型测试编译失败: $LASTEXITCODE" }
& $executable
if ($LASTEXITCODE -ne 0) { throw "视图模型测试失败: $LASTEXITCODE" }
