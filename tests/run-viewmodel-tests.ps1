param([string]$Executable)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw "找不到 .NET Framework C# 编译器: $compiler" }
$output = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $output -Force | Out-Null
if ($Executable) {
    $artifact = (Resolve-Path -LiteralPath $Executable).Path
    $beforeHash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash
    Write-Host ("ARTIFACT VM target: {0}; SHA256={1}" -f $artifact,$beforeHash)
    $references = @('/reference:System.dll', '/reference:System.Core.dll', "/reference:$framework\WPF\WindowsBase.dll", "/reference:$framework\WPF\PresentationCore.dll", "/reference:$framework\WPF\PresentationFramework.dll", "/reference:$artifact")
    $testExecutable = Join-Path $output 'ViewModelArtifactTests.exe'
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /warnaserror+ /main:ArtifactTestBootstrap "/out:$testExecutable" $references (Join-Path $PSScriptRoot 'ArtifactTestBootstrap.cs') (Join-Path $PSScriptRoot 'ViewModelTests.cs')
    if ($LASTEXITCODE -ne 0) { throw "Artifact VM test compilation failed: $LASTEXITCODE" }
    & $testExecutable $artifact ViewModelTests
    if ($LASTEXITCODE -ne 0) { throw "Artifact VM tests failed: $LASTEXITCODE" }
    if ((Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash -ne $beforeHash) { throw 'Target EXE changed during artifact tests.' }
    return
}
Write-Host 'SOURCE UNIT mode: business source is compiled with tests; use -Executable for final-artifact verification.'
$testExecutable = Join-Path $output 'ViewModelTests.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $repo 'src\NetPeek.Core') -Filter '*.cs' | ForEach-Object { $_.FullName })
$sources += Join-Path $repo 'src\NetPeek.App\CaptureHistory.cs'
$sources += Join-Path $repo 'src\NetPeek.App\ViewModels.cs'
$sources += Join-Path $PSScriptRoot 'ViewModelTests.cs'
$references = @('/reference:System.dll', '/reference:System.Core.dll', "/reference:$framework\WPF\WindowsBase.dll", "/reference:$framework\WPF\PresentationCore.dll", "/reference:$framework\WPF\PresentationFramework.dll")
& $compiler /nologo /target:exe /platform:anycpu /optimize+ /warnaserror+ "/out:$testExecutable" $references $sources
if ($LASTEXITCODE -ne 0) { throw "视图模型测试编译失败: $LASTEXITCODE" }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw "视图模型测试失败: $LASTEXITCODE" }
