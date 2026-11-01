[CmdletBinding()]
param(
    [switch]$Service, [switch]$Start, [switch]$Stop, [switch]$Status,
    [switch]$Open, [switch]$EnableAutostart, [switch]$DisableAutostart,
    [switch]$PrintPreset, [switch]$Bootstrap, [switch]$Doctor, [switch]$Update,
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
    @{ Enabled = $Doctor; Name = '--doctor' },
    @{ Enabled = $Update; Name = '--update' }
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
    # Windows PowerShell 5.1 refuses a WinExe in a pipeline and does not wait for one.
    # Start-Process redirects both streams; explicit WaitForExit captures its exit code.
    function Quote-WindowsArgument([string]$Value) {
        $quoted = New-Object System.Text.StringBuilder
        [void]$quoted.Append('"')
        $backslashes = 0
        foreach ($character in $Value.ToCharArray()) {
            if ($character -eq [char]'\') {
                $backslashes++
                continue
            }
            if ($character -eq [char]'"') {
                [void]$quoted.Append(('\' * (($backslashes * 2) + 1)))
                [void]$quoted.Append('"')
                $backslashes = 0
                continue
            }
            [void]$quoted.Append(('\' * $backslashes))
            [void]$quoted.Append($character)
            $backslashes = 0
        }
        [void]$quoted.Append(('\' * ($backslashes * 2)))
        [void]$quoted.Append('"')
        return $quoted.ToString()
    }

    $argumentLine = ($launcherArguments | ForEach-Object { Quote-WindowsArgument $_ }) -join ' '
    $stdoutPath = [IO.Path]::GetTempFileName()
    $stderrPath = [IO.Path]::GetTempFileName()
    try {
        $process = Start-Process -FilePath $launcherExecutable -ArgumentList $argumentLine `
            -NoNewWindow -PassThru `
            -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
        # WaitForExit on the Process object waits for only the launcher. Start-Process -Wait
        # would also wait for a detached model server and make --start appear to hang.
        $process.WaitForExit()
        [Console]::Out.Write([IO.File]::ReadAllText($stdoutPath, [Text.Encoding]::UTF8))
        [Console]::Error.Write([IO.File]::ReadAllText($stderrPath, [Text.Encoding]::UTF8))
        exit $process.ExitCode
    } finally {
        if ($null -ne $process) { $process.Dispose() }
        Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
    }
}
