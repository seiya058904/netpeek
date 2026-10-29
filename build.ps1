param([switch]$Package, [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = ([regex]::Match((Get-Content -LiteralPath (Join-Path $root 'src\NetPeek.App\AssemblyInfo.cs') -Raw -Encoding UTF8), 'AssemblyFileVersion\("([0-9]+\.[0-9]+\.[0-9]+)\.[0-9]+"\)')).Groups[1].Value
if (!$version) { throw 'Cannot determine the package version from AssemblyInfo.cs.' }
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw '需要 Windows x64 和 .NET Framework 4.8。' }
$release = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue
if (!$release -or $release.Release -lt 528040) { throw '请先安装 .NET Framework 4.8。构建不会自动下载运行时。' }
$output = Join-Path $root 'dist\NetPeek'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$files = @(Get-ChildItem (Join-Path $root 'src\NetPeek.Core\*.cs')) + @(Get-ChildItem (Join-Path $root 'src\NetPeek.App\*.cs'))
$references = @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Xaml.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll')
$arguments = @('/nologo','/target:winexe','/platform:x64','/optimize+','/debug-','/utf8output','/langversion:5',('/out:' + (Join-Path $output 'NetPeek.exe')),('/win32manifest:' + (Join-Path $root 'src\NetPeek.App\app.manifest')),('/resource:' + (Join-Path $root 'src\NetPeek.App\MainWindow.xaml') + ',NetPeek.MainWindow.xaml'))
foreach ($reference in $references) { $arguments += '/reference:' + (Join-Path $framework $reference) }
$icon = Join-Path $root 'assets\NetPeek.ico'
if (Test-Path -LiteralPath $icon) { $arguments += '/win32icon:' + $icon; $arguments += '/resource:' + $icon + ',NetPeek.ico' }
$arguments += $files.FullName
if (!$SkipBuild) {
  & $compiler $arguments
  if ($LASTEXITCODE -ne 0) { throw 'C# compilation failed.' }
} elseif (!(Test-Path -LiteralPath (Join-Path $output 'NetPeek.exe'))) { throw 'SkipBuild requires an existing executable.' }
Copy-Item -LiteralPath (Join-Path $root 'src\NetPeek.App\App.config') -Destination (Join-Path $output 'NetPeek.exe.config')
foreach ($name in @('LICENSE','THIRD_PARTY_NOTICES.md','README.md')) { if (Test-Path -LiteralPath (Join-Path $root $name)) { Copy-Item -LiteralPath (Join-Path $root $name) -Destination $output } }
if ($Package) {
  Add-Type -AssemblyName System.IO.Compression
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $sourceZip = Join-Path $output 'NetPeek_source.zip'
  $sourcePaths = @('.gitignore','README.md','PRODUCT.md','DESIGN.md','LICENSE','THIRD_PARTY_NOTICES.md','build.ps1','src','assets','scripts','tests','docs')
  $stream = [System.IO.File]::Open($sourceZip,[System.IO.FileMode]::Create)
  $zip = New-Object System.IO.Compression.ZipArchive($stream,[System.IO.Compression.ZipArchiveMode]::Create)
  try {
    foreach ($relative in $sourcePaths) {
      $path = Join-Path $root $relative
      if (!(Test-Path -LiteralPath $path)) { continue }
      $entries = if (Test-Path -LiteralPath $path -PathType Container) { @(Get-ChildItem -LiteralPath $path -File -Recurse | Where-Object { $_.FullName -notmatch '[\\/]tests[\\/]bin[\\/]' }) } else { @(Get-Item -LiteralPath $path) }
      foreach ($entry in $entries) {
        $entryName = $entry.FullName.Substring($root.Length + 1).Replace('\','/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$entry.FullName,$entryName,[System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
      }
    }
  } finally { $zip.Dispose(); $stream.Dispose() }
  Copy-Item -LiteralPath $sourceZip -Destination (Join-Path $root ('dist\NetPeek_' + $version + '_source.zip'))
  $archive = Join-Path $root ('dist\NetPeek_' + $version + '_windows_x64.zip')
  # Build from an explicit current-file list. Old files in dist must never leak
  # into a later release after documentation or screenshots have been removed.
  $stream = [System.IO.File]::Open($archive,[System.IO.FileMode]::Create)
  $zip = New-Object System.IO.Compression.ZipArchive($stream,[System.IO.Compression.ZipArchiveMode]::Create)
  try {
    foreach ($name in @('NetPeek.exe','NetPeek.exe.config','NetPeek_source.zip')) {
      [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $output $name),('NetPeek/' + $name),[System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    foreach ($relative in @('README.md','LICENSE','THIRD_PARTY_NOTICES.md','assets','docs')) {
      $path = Join-Path $root $relative
      $entries = if (Test-Path -LiteralPath $path -PathType Container) { @(Get-ChildItem -LiteralPath $path -File -Recurse) } else { @(Get-Item -LiteralPath $path) }
      foreach ($entry in $entries) {
        $entryName = 'NetPeek/' + $entry.FullName.Substring($root.Length + 1).Replace('\','/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$entry.FullName,$entryName,[System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
      }
    }
  } finally { $zip.Dispose(); $stream.Dispose() }
  Get-FileHash -LiteralPath $archive -Algorithm SHA256 | Format-List
}
Get-Item -LiteralPath (Join-Path $output 'NetPeek.exe') | Select-Object FullName,Length
