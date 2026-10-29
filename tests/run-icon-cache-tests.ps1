param([string]$Executable)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$output = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $output -Force | Out-Null
if ($Executable) {
    $artifact = (Resolve-Path -LiteralPath $Executable).Path
    $beforeHash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash
    Write-Host ("ARTIFACT icon target: {0}; SHA256={1}" -f $artifact,$beforeHash)
    $testCopy = Join-Path $output 'IconArtifactTests.cs'
    # Adapt only the test's internal-cache calls; the adapter delegates to final-EXE reflection.
    (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'IconCacheTests.cs') -Encoding UTF8 -Raw).Replace('IconCache<','ArtifactIconCache<') | Set-Content -LiteralPath $testCopy -Encoding UTF8
    $testExecutable = Join-Path $output 'IconArtifactTests.exe'
    $references = @('/reference:System.dll', '/reference:System.Core.dll', "/reference:$framework\WPF\WindowsBase.dll", "/reference:$framework\WPF\PresentationCore.dll", "/reference:$framework\WPF\PresentationFramework.dll", "/reference:$artifact")
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /warnaserror+ /main:ArtifactTestBootstrap "/out:$testExecutable" $references (Join-Path $PSScriptRoot 'ArtifactTestBootstrap.cs') (Join-Path $PSScriptRoot 'IconArtifactAdapter.cs') $testCopy
    if ($LASTEXITCODE -ne 0) { throw "Artifact icon test compilation failed: $LASTEXITCODE" }
    & $testExecutable $artifact IconCacheTests
    if ($LASTEXITCODE -ne 0) { throw "Artifact icon tests failed: $LASTEXITCODE" }
    if ((Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash -ne $beforeHash) { throw 'Target EXE changed during artifact tests.' }
    return
}
Write-Host 'SOURCE UNIT mode: business source is compiled with tests; use -Executable for final-artifact verification.'
$testExecutable = Join-Path $output 'IconCacheTests.exe'
$sources = @(Get-ChildItem -LiteralPath (Join-Path $repo 'src\NetPeek.Core') -Filter '*.cs' | ForEach-Object { $_.FullName })
$sources += @('src\NetPeek.App\CaptureHistory.cs','src\NetPeek.App\ViewModels.cs','src\NetPeek.App\IconCache.cs','tests\IconCacheTests.cs') | ForEach-Object { Join-Path $repo $_ }
$references = @('/reference:System.dll', '/reference:System.Core.dll', "/reference:$framework\WPF\WindowsBase.dll", "/reference:$framework\WPF\PresentationCore.dll", "/reference:$framework\WPF\PresentationFramework.dll")
& $compiler /nologo /target:exe /platform:anycpu /optimize+ /warnaserror+ "/out:$testExecutable" $references $sources
if ($LASTEXITCODE -ne 0) { throw "图标缓存测试编译失败: $LASTEXITCODE" }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw "图标缓存测试失败: $LASTEXITCODE" }
