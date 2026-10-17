[CmdletBinding()]
param(
    [switch]$Service, [switch]$Start, [switch]$Stop, [switch]$Status,
    [switch]$Open, [switch]$EnableAutostart, [switch]$DisableAutostart,
    [switch]$PrintPreset, [switch]$Bootstrap, [switch]$Doctor,
    [switch]$All, [switch]$Offline, [switch]$AutoBootstrap,
    [string]$Backend, [string]$ModelPath, [int]$Port
)

$ErrorActionPreference = 'Stop'
$launcherExecutable = Join-Path $PSScriptRoot 'dist\LlamaManager.exe'
if (-not (Test-Path -LiteralPath $launcherExecutable)) {
    throw "未找到 $launcherExecutable。请先运行 dotnet publish gui/LlamaManager/LlamaManager.csproj -c Release -o dist"
}

$launcherArguments = @()
$commands = @(@(
    @{ Enabled = $Service; Name = '--service' },
    @{ Enabled = $Start; Name = '--start' },
    @{ Enabled = $Stop; Name = '--stop' },
    @{ Enabled = $Status; Name = '--status' },
    @{ Enabled = $Open; Name = '--open' },
    @{ Enabled = $EnableAutostart; Name = '--enable-autostart' },
    @{ Enabled = $DisableAutostart; Name = '--disable-autostart' },
    @{ Enabled = $PrintPreset; Name = '--print-preset' },
    @{ Enabled = $Bootstrap; Name = '--bootstrap' },
    @{ Enabled = $Doctor; Name = '--doctor' }
) | Where-Object { $_.Enabled })
if ($commands.Count -gt 1) { throw '一次只能使用一个操作开关。' }
if ($commands.Count -eq 1) { $launcherArguments += $commands[0].Name }
if ($Backend) { $launcherArguments += @('--backend', $Backend) }
if ($ModelPath) { $launcherArguments += @('--model', $ModelPath) }
if ($PSBoundParameters.ContainsKey('Port')) { $launcherArguments += @('--port', "$Port") }
if ($All) { $launcherArguments += '--all' }
if ($Offline) { $launcherArguments += '--offline' }
if ($AutoBootstrap) { $launcherArguments += '--auto-bootstrap' }
if ($launcherArguments.Count -gt 0 -and $commands.Count -eq 0) { throw '指定模型、端口或引擎时，需要 -Start 或其他操作开关。' }

if ($launcherArguments.Count -eq 0) {
    Start-Process -FilePath $launcherExecutable
} else {
    # Piping causes PowerShell to wait for the WinExe command and retains its output/exit code.
    & $launcherExecutable @launcherArguments | Out-Host
    exit $LASTEXITCODE
}