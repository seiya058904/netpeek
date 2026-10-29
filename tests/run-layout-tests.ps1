param([string]$Executable)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Executable) { $Executable = Join-Path $repo 'dist\NetPeek\NetPeek.exe' }
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$output = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$testExecutable = Join-Path $output 'LayoutTests.exe'
$references = @('/reference:System.dll', '/reference:System.Core.dll', "/reference:$framework\System.Xaml.dll", "/reference:$framework\WPF\WindowsBase.dll", "/reference:$framework\WPF\PresentationCore.dll", "/reference:$framework\WPF\PresentationFramework.dll")
& $compiler /nologo /target:exe /platform:x64 /optimize+ /warnaserror+ /langversion:5 "/out:$testExecutable" $references (Join-Path $PSScriptRoot 'LayoutTests.cs')
if ($LASTEXITCODE -ne 0) { throw "布局测试编译失败: $LASTEXITCODE" }
# Only loads the assembly and its embedded XAML. Never invokes NetPeek.Main.
& $testExecutable $Executable
if ($LASTEXITCODE -ne 0) { throw "布局测试失败: $LASTEXITCODE" }
