#Requires -Version 5.1
<#
  編譯 battery-tray.exe（.NET Framework 4.8 / csc.exe，免安裝 SDK）
  用法：powershell -ExecutionPolicy Bypass -File build.ps1
#>
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw '找不到 csc.exe' }

$src = Join-Path $root 'src\Program.cs'
$manifest = Join-Path $root 'src\app.manifest'
$out = Join-Path $root 'battery-tray.exe'

if (Test-Path $out) { Remove-Item $out -Force }

$compilerArgs = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    ('/win32manifest:' + $manifest),
    ('/out:' + $out),
    '/r:System.dll',
    '/r:System.Core.dll',
    '/r:System.Drawing.dll',
    '/r:System.Windows.Forms.dll',
    $src
)

& $csc $compilerArgs
if ($LASTEXITCODE -ne 0) { throw "csc 失敗，exit=$LASTEXITCODE" }
if (-not (Test-Path $out)) { throw '沒有產生 exe' }

$fi = Get-Item $out
'OK  {0}  ({1:N0} bytes)' -f $fi.FullName, $fi.Length
