# ---------------------------------------------------------------------------
#  Battery Care tray - PowerShell host
#  Windows 11 Smart App Control blocks unsigned .exe files, so the same C#
#  logic is compiled in memory (Add-Type) and run inside a signed host.
#  Usage:  powershell -NoProfile -STA -ExecutionPolicy Bypass -File start.ps1 [--status|--set 80|--install|...]
# ---------------------------------------------------------------------------
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Remaining = @())

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $root 'Program.cs'
if (-not (Test-Path $source)) { $source = Join-Path $root 'src\Program.cs' }
if (-not (Test-Path $source)) { throw "找不到 Program.cs：$source" }

$loaded = $false
try { $loaded = ($null -ne [BatteryCare.Program]) } catch { $loaded = $false }

if (-not $loaded) {
    $code = [System.IO.File]::ReadAllText($source, [System.Text.Encoding]::UTF8)
    Add-Type -TypeDefinition $code -Language CSharp -ReferencedAssemblies @(
        'System.dll',
        'System.Core.dll',
        'System.Drawing.dll',
        'System.Windows.Forms.dll'
    ) | Out-Null
}

$self = Join-Path $root 'start.ps1'
$command = 'powershell.exe -NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $self + '" --autostart'
[BatteryCare.Program]::AutostartCommand = $command

$exitCode = [BatteryCare.Program]::Main([string[]]$Remaining)
exit $exitCode
