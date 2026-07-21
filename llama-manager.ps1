param(
    [switch]$Service,
    [switch]$Start,
    [switch]$Stop,
    [switch]$Status,
    [switch]$Open,
    [switch]$EnableAutostart,
    [switch]$DisableAutostart,
    [switch]$PrintPreset,
    [string]$ModelPath,
    [int]$Port = 0
)

$ErrorActionPreference = 'Stop'
$Script:ScriptPath = $PSCommandPath
if (-not $Script:ScriptPath) {
    $Script:ScriptPath = $MyInvocation.MyCommand.Path
}
$Script:AppDir = Split-Path -Parent $Script:ScriptPath
$parentDir = Split-Path -Parent $Script:AppDir
if (Test-Path -LiteralPath (Join-Path $Script:AppDir 'llama-server.exe')) {
    $Script:Root = $Script:AppDir
} elseif ($parentDir -and (Test-Path -LiteralPath (Join-Path $parentDir 'llama-server.exe'))) {
    $Script:Root = $parentDir
} else {
    $Script:Root = $Script:AppDir
}
$Script:LlamaServer = Join-Path $Script:Root 'llama-server.exe'
$Script:ConfigPath = Join-Path $Script:AppDir 'llama-manager.config.json'
$Script:LogPath = Join-Path $Script:AppDir 'llama-manager.log'
$Script:ServerStdoutPath = Join-Path $Script:AppDir 'llama-server.stdout.log'
$Script:ServerStderrPath = Join-Path $Script:AppDir 'llama-server.stderr.log'
$Script:TaskName = 'LlamaCppModelServer'
$Script:RunRegistryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$Script:RunValueName = 'LlamaCppModelServer'
$Script:StartupApprovedRunPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
$Script:StartupFilePath = Join-Path ([Environment]::GetFolderPath('Startup')) 'LlamaCppModelServer.cmd'

function New-DefaultConfig {
    [pscustomobject][ordered]@{
        ModelPath = ''
        Args = ''
        Port = 8080
        AutoStartEnabled = $false
        LastPid = 0
        LastStartedAt = ''
        CreatedAt = (Get-Date).ToString('o')
        UpdatedAt = (Get-Date).ToString('o')
    }
}

function Read-ManagerConfig {
    $config = New-DefaultConfig
    if (Test-Path -LiteralPath $Script:ConfigPath) {
        try {
            $raw = Get-Content -LiteralPath $Script:ConfigPath -Raw | ConvertFrom-Json
            foreach ($name in $config.PSObject.Properties.Name) {
                if ($raw.PSObject.Properties.Name -contains $name) {
                    $config.$name = $raw.$name
                }
            }
        } catch {
            Write-ManagerLog "读取配置失败，使用默认配置：$($_.Exception.Message)"
        }
    }
    if (-not $config.Port) {
        $config.Port = 8080
    }
    return $config
}

function Save-ManagerConfig {
    param([Parameter(Mandatory)][object]$Config)

    if (-not $Config.CreatedAt) {
        $Config.CreatedAt = (Get-Date).ToString('o')
    }
    $Config.UpdatedAt = (Get-Date).ToString('o')
    $Config | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $Script:ConfigPath -Encoding UTF8
}

function Write-ManagerLog {
    param([Parameter(Mandatory)][string]$Message)

    $line = '{0} {1}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $Message
    Add-Content -LiteralPath $Script:LogPath -Value $line -Encoding UTF8
}

function Format-Bytes {
    param([Parameter(Mandatory)][long]$Bytes)

    if ($Bytes -ge 1GB) {
        return ('{0:N2} GB' -f ($Bytes / 1GB))
    }
    if ($Bytes -ge 1MB) {
        return ('{0:N1} MB' -f ($Bytes / 1MB))
    }
    return ('{0:N0} B' -f $Bytes)
}

function Get-CudaInfo {
    if (-not (Test-Path -LiteralPath $Script:LlamaServer)) {
        return [pscustomobject]@{ Available = $false; Text = '未找到 llama-server.exe。' }
    }

    try {
        $output = & $Script:LlamaServer --list-devices 2>&1
        $text = ($output | Out-String).Trim()
        $available = $text -match 'CUDA\d+:'
        return [pscustomobject]@{ Available = $available; Text = $text }
    } catch {
        return [pscustomobject]@{ Available = $false; Text = $_.Exception.Message }
    }
}

function Get-ModelFiles {
    $modelsDir = Join-Path $Script:Root 'models'
    if (-not (Test-Path -LiteralPath $modelsDir)) {
        return @()
    }

    @(Get-ChildItem -LiteralPath $modelsDir -Recurse -Filter '*.gguf' -File |
        Where-Object { $_.Name -notlike 'mmproj-*' } |
        Sort-Object FullName)
}

function Resolve-ModelPath {
    param([Parameter(Mandatory)][string]$Path)

    $trimmed = $Path.Trim('"')
    if (Test-Path -LiteralPath $trimmed) {
        return [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $trimmed).Path)
    }

    if (-not [System.IO.Path]::IsPathRooted($trimmed)) {
        $rootRelative = Join-Path $Script:Root $trimmed
        if (Test-Path -LiteralPath $rootRelative) {
            return [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $rootRelative).Path)
        }
    }

    throw "未找到模型文件：$trimmed"
}

function Get-PortFromArgs {
    param(
        [string]$Args,
        [int]$Fallback = 8080
    )

    if ($Args -match '(?:^|\s)--port\s+(\d+)') {
        return [int]$Matches[1]
    }
    return $Fallback
}

function Test-MoEModel {
    param([Parameter(Mandatory)][string]$Path)

    $filename = [System.IO.Path]::GetFileNameWithoutExtension($Path)
    return $filename -match '(?i)(A3B|moe|mixture\.of\.experts)'
}

function Get-MoEPresetArgs {
    param(
        [Parameter(Mandatory)][string]$Path,
        [int]$Port = 8080
    )

    $size = (Get-Item -LiteralPath $Path).Length
    $threads = [Math]::Min(8, [Environment]::ProcessorCount)
    if ($threads -lt 1) {
        $threads = 1
    }

    $gpuArgs = if ((Get-CudaInfo).Available) {
        '-ngl all --fit on --fit-target 512'
    } else {
        '-ngl 0'
    }

    $parts = @(
        '--host 127.0.0.1',
        "--port $Port",
        "-t $threads",
        "-tb $threads",
        '-c 8192',
        '-np 1',
        '-b 1024',
        '-ub 512',
        $gpuArgs,
        '--cache-prompt',
        '--flash-attn on',
        '--cpu-moe',
        '--reasoning off'
    )

    $freeRAM = (Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory * 1KB
    if ($freeRAM - $size -gt 2GB) {
        $parts += '--no-mmap'
        $parts += '--mlock'
    }

    return ($parts -join ' ')
}

function Get-PresetArgs {
    param(
        [Parameter(Mandatory)][string]$Path,
        [int]$Port = 8080
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "未找到模型文件：$Path"
    }

    $size = (Get-Item -LiteralPath $Path).Length
    $threads = [Math]::Min(8, [Environment]::ProcessorCount)
    if ($threads -lt 1) {
        $threads = 1
    }

    if (Test-MoEModel -Path $Path) {
        return Get-MoEPresetArgs -Path $Path -Port $Port
    }

    if ($size -le 1GB) {
        $ctx = 40960
        $batch = 2048
        $ubatch = 512
        $extra = '--reasoning off'
    } elseif ($size -le 3.5GB) {
        $ctx = 20480
        $batch = 2048
        $ubatch = 512
        $extra = '--reasoning off'
    } elseif ($size -le 5GB) {
        $ctx = 10240
        $batch = 1024
        $ubatch = 512
        $extra = '--reasoning off'
    } elseif ($size -le 6GB) {
        $ctx = 8192
        $batch = 1024
        $ubatch = 512
        $extra = '--reasoning off'
    } else {
        $ctx = 4096
        $batch = 512
        $ubatch = 256
        $extra = '--reasoning off'
    }

    $cuda = Get-CudaInfo
    if ($cuda.Available) {
        if ($size -le 3.5GB) {
            $gpu = '-ngl all --fit on --fit-target 1024'
        } else {
            $gpu = '-ngl auto --fit on --fit-target 1024'
        }
    } else {
        $gpu = '-ngl 0'
    }

    $parts = @(
        '--host 127.0.0.1',
        "--port $Port",
        "-t $threads",
        "-tb $threads",
        "-c $ctx",
        '-np 1',
        "-b $batch",
        "-ub $ubatch",
        $gpu,
        '--cache-prompt'
    )
    if ($extra) {
        $parts += $extra
    }

    return ($parts -join ' ')
}

function Get-ManagedServerProcess {
    param([Parameter(Mandatory)][object]$Config)

    if (-not $Config.LastPid -or [int]$Config.LastPid -le 0) {
        return $null
    }

    try {
        $process = Get-Process -Id ([int]$Config.LastPid) -ErrorAction Stop
    } catch {
        return $null
    }

    if ($process.ProcessName -ne 'llama-server') {
        return $null
    }

    try {
        $expected = [System.IO.Path]::GetFullPath($Script:LlamaServer)
        $actual = [System.IO.Path]::GetFullPath($process.Path)
        if ($actual -ne $expected) {
            return $null
        }
    } catch {
        return $null
    }

    return $process
}

function Test-PortInUse {
    param([int]$Port)

    try {
        $connection = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
        return $null -ne $connection
    } catch {
        return $false
    }
}

function Get-ListeningProcessByPort {
    param([int]$Port)

    if ($Port -le 0) {
        return $null
    }

    try {
        $connection = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $connection) {
            return $null
        }
        return Get-Process -Id ([int]$connection.OwningProcess) -ErrorAction SilentlyContinue
    } catch {
        return $null
    }
}

function Get-AnyLlamaServerProcess {
    $processes = @(Get-Process -Name 'llama-server' -ErrorAction SilentlyContinue)
    if ($processes.Count -eq 0) {
        return $null
    }

    try {
        $expected = [System.IO.Path]::GetFullPath($Script:LlamaServer)
        foreach ($process in $processes) {
            try {
                if ($process.Path) {
                    $actual = [System.IO.Path]::GetFullPath($process.Path)
                    if ($actual -eq $expected) {
                        return $process
                    }
                }
            } catch {
            }
        }
    } catch {
    }

    return $processes[0]
}

function Test-ServerApiReady {
    param([int]$Port)

    try {
        Invoke-RestMethod -Uri "http://127.0.0.1:$Port/v1/models" -TimeoutSec 2 -ErrorAction Stop | Out-Null
        return $true
    } catch {
        return $false
    }
}

function Start-ModelServer {
    param(
        [Parameter(Mandatory)][object]$Config,
        [switch]$OpenBrowser
    )

    if (-not (Test-Path -LiteralPath $Script:LlamaServer)) {
        throw "未找到 llama-server.exe：$Script:LlamaServer"
    }
    if (-not $Config.ModelPath -or -not (Test-Path -LiteralPath $Config.ModelPath)) {
        throw '未选择有效模型。'
    }
    if (-not $Config.Args) {
        $Config.Args = Get-PresetArgs -Path $Config.ModelPath -Port $Config.Port
    }

    $running = Get-ManagedServerProcess -Config $Config
    if ($running) {
        Write-ManagerLog "服务已经在运行，PID $($running.Id)。"
        if ($OpenBrowser) {
            Open-WebUi -Config $Config
        }
        return $running
    }

    $Config.Port = Get-PortFromArgs -Args $Config.Args -Fallback $Config.Port
    if (Test-PortInUse -Port $Config.Port) {
        $owner = Get-ListeningProcessByPort -Port $Config.Port
        if ($owner -and $owner.ProcessName -eq 'llama-server') {
            throw "端口 $($Config.Port) 已被另一个 llama-server 进程占用（PID $($owner.Id)）。请先停止后重试。"
        }
        throw "端口 $($Config.Port) 已被占用。"
    }

    $quotedModel = '"' + ($Config.ModelPath -replace '"', '\"') + '"'
    $argumentLine = "-m $quotedModel $($Config.Args)"
    Write-ManagerLog "正在启动 llama-server.exe $argumentLine"

    $process = Start-Process -FilePath $Script:LlamaServer `
        -ArgumentList $argumentLine `
        -WorkingDirectory $Script:Root `
        -WindowStyle Hidden `
        -RedirectStandardOutput $Script:ServerStdoutPath `
        -RedirectStandardError $Script:ServerStderrPath `
        -PassThru

    $Config.LastPid = $process.Id
    $Config.LastStartedAt = (Get-Date).ToString('o')
    $Config.Port = Get-PortFromArgs -Args $Config.Args -Fallback $Config.Port
    Save-ManagerConfig -Config $Config

    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline) {
        if ($process.HasExited) {
            $err = ''
            if (Test-Path -LiteralPath $Script:ServerStderrPath) {
                $err = (Get-Content -LiteralPath $Script:ServerStderrPath -Tail 40 | Out-String).Trim()
            }
            $Config.LastPid = 0
            Save-ManagerConfig -Config $Config
            throw "llama-server 启动失败或提前退出。$err"
        }

        if (Test-ServerApiReady -Port $Config.Port) {
            Write-ManagerLog "服务已启动，PID $($process.Id)，端口 $($Config.Port)。"
            if ($OpenBrowser) {
                Open-WebUi -Config $Config
            }
            return $process
        }

        Start-Sleep -Seconds 2
    }

    Write-ManagerLog "服务进程已启动，PID $($process.Id)，但 90 秒内 API 未就绪；请查看 llama-server.stderr.log。"
    return $process
}

function Stop-ProcessTree {
    param([Parameter(Mandatory)][int]$ProcessId)

    $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $ProcessId" -ErrorAction SilentlyContinue)
    foreach ($child in $children) {
        Stop-ProcessTree -ProcessId ([int]$child.ProcessId)
    }
    try {
        Stop-Process -Id $ProcessId -Force -ErrorAction Stop
    } catch {
        Write-ManagerLog "停止 PID ${ProcessId} 失败：$($_.Exception.Message)"
    }
}

function Stop-ModelServer {
    param([Parameter(Mandatory)][object]$Config)

    $process = Get-ActiveLlamaServerProcess -Config $Config
    if (-not $process) {
        Write-ManagerLog '当前没有由本工具管理的 llama-server 进程在运行。'
        $Config.LastPid = 0
        Save-ManagerConfig -Config $Config
        return $false
    }

    if ($Config.LastPid -and [int]$Config.LastPid -eq $process.Id) {
        Write-ManagerLog "正在停止由本工具管理的 llama-server，PID $($process.Id)。"
    } else {
        Write-ManagerLog "正在停止检测到的 llama-server，PID $($process.Id)。"
    }
    Stop-ProcessTree -ProcessId $process.Id
    Start-Sleep -Seconds 1
    $Config.LastPid = 0
    Save-ManagerConfig -Config $Config
    return $true
}

function Open-WebUi {
    param([Parameter(Mandatory)][object]$Config)

    $portToOpen = Get-PortFromArgs -Args $Config.Args -Fallback $Config.Port
    $url = "http://127.0.0.1:$portToOpen"
    Write-ManagerLog "正在打开 Web UI：$url"
    Start-Process $url | Out-Null
}

function Get-PowerShellExe {
    $pwsh = Get-Command pwsh.exe -ErrorAction SilentlyContinue
    if ($pwsh) {
        return $pwsh.Source
    }
    return (Get-Command powershell.exe -ErrorAction Stop).Source
}

function Get-AutostartRunCommand {
    try {
        $entry = Get-ItemProperty -LiteralPath $Script:RunRegistryPath -Name $Script:RunValueName -ErrorAction Stop
        $property = $entry.PSObject.Properties[$Script:RunValueName]
        if ($property -and $property.Value) {
            return [string]$property.Value
        }
    } catch {
    }
    return ''
}

function Set-AutostartRunCommand {
    param([Parameter(Mandatory)][string]$CommandLine)

    New-Item -Path $Script:RunRegistryPath -Force | Out-Null
    New-ItemProperty -LiteralPath $Script:RunRegistryPath -Name $Script:RunValueName -PropertyType String -Value $CommandLine -Force | Out-Null
}

function Remove-AutostartRunCommand {
    Remove-ItemProperty -LiteralPath $Script:RunRegistryPath -Name $Script:RunValueName -ErrorAction SilentlyContinue
}

function Test-AutostartStartupFile {
    return (Test-Path -LiteralPath $Script:StartupFilePath)
}

function Set-AutostartStartupFile {
    param([Parameter(Mandatory)][string]$PowerShellExe)

    $content = @(
        '@echo off',
        'start "" /min "{0}" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{1}" -Service' -f $PowerShellExe, $Script:ScriptPath
    )
    Set-Content -LiteralPath $Script:StartupFilePath -Value $content -Encoding ASCII
}

function Remove-AutostartStartupFile {
    Remove-Item -LiteralPath $Script:StartupFilePath -Force -ErrorAction SilentlyContinue
}

function Clear-StartupApprovedRunState {
    try {
        Remove-ItemProperty -LiteralPath $Script:StartupApprovedRunPath -Name $Script:RunValueName -ErrorAction SilentlyContinue
    } catch {
    }
}

function Test-ManagerAutostartEnabled {
    $runEnabled = [bool](Get-AutostartRunCommand)
    $startupFileEnabled = Test-AutostartStartupFile
    $taskEnabled = [bool](Get-ScheduledTask -TaskName $Script:TaskName -ErrorAction SilentlyContinue)
    return ($runEnabled -or $startupFileEnabled -or $taskEnabled)
}

function Test-PrimaryAutostartEnabled {
    $runEnabled = [bool](Get-AutostartRunCommand)
    $startupFileEnabled = Test-AutostartStartupFile
    return ($runEnabled -or $startupFileEnabled)
}

function Enable-ManagerAutostart {
    param([Parameter(Mandatory)][object]$Config)

    if (-not $Config.ModelPath -or -not (Test-Path -LiteralPath $Config.ModelPath)) {
        throw '开启自启动前请先选择有效模型。'
    }
    if (-not $Config.Args) {
        $Config.Args = Get-PresetArgs -Path $Config.ModelPath -Port $Config.Port
    }

    $psExe = Get-PowerShellExe
    $runArgs = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}" -Service' -f $Script:ScriptPath
    $runCommand = '"{0}" {1}' -f $psExe, $runArgs

    $enabledRun = $false
    $enabledStartup = $false
    $errors = @()

    try {
        Set-AutostartRunCommand -CommandLine $runCommand
        $enabledRun = $true
    } catch {
        $errors += "注册表启动项失败：$($_.Exception.Message)"
    }

    try {
        Set-AutostartStartupFile -PowerShellExe $psExe
        $enabledStartup = $true
    } catch {
        $errors += "启动文件失败：$($_.Exception.Message)"
    }

    if (-not ($enabledRun -or $enabledStartup)) {
        throw ("写入登录自启动项失败：" + ($errors -join '；'))
    }
    Clear-StartupApprovedRunState

    $task = Get-ScheduledTask -TaskName $Script:TaskName -ErrorAction SilentlyContinue
    if ($task) {
        try {
            Unregister-ScheduledTask -TaskName $Script:TaskName -Confirm:$false
            Write-ManagerLog "检测到旧版计划任务自启动，已迁移为注册表登录项。"
        } catch {
            Write-ManagerLog "检测到旧版计划任务自启动，但删除失败：$($_.Exception.Message)。已保留注册表登录项作为主入口。"
        }
    }

    $Config.AutoStartEnabled = ($enabledRun -or $enabledStartup)
    Save-ManagerConfig -Config $Config
    if ($enabledRun -and $enabledStartup) {
        Write-ManagerLog "已开启登录后自启动（注册表项 + 启动文件）。"
    } elseif ($enabledRun) {
        Write-ManagerLog "已开启登录后自启动（注册表项）。"
    } elseif ($enabledStartup) {
        Write-ManagerLog "已开启登录后自启动（启动文件）。"
    }
    if ($errors.Count -gt 0) {
        Write-ManagerLog ("自启动启用部分失败：" + ($errors -join '；'))
    }
}

function Disable-ManagerAutostart {
    param([Parameter(Mandatory)][object]$Config)

    Remove-AutostartRunCommand
    Remove-AutostartStartupFile
    Clear-StartupApprovedRunState

    $task = Get-ScheduledTask -TaskName $Script:TaskName -ErrorAction SilentlyContinue
    if ($task) {
        try {
            Unregister-ScheduledTask -TaskName $Script:TaskName -Confirm:$false
        } catch {
            Write-ManagerLog "删除旧版计划任务自启动失败：$($_.Exception.Message)。如果需要完全关闭，请以管理员权限运行一次后再关闭。"
        }
    }
    $Config.AutoStartEnabled = Test-PrimaryAutostartEnabled
    Save-ManagerConfig -Config $Config
    if ($Config.AutoStartEnabled) {
        Write-ManagerLog '已关闭注册表登录自启动项，但仍检测到计划任务自启动。'
    } else {
        Write-ManagerLog '已关闭登录后自启动。'
    }
}

function Get-AutostartText {
    $runCommand = Get-AutostartRunCommand
    $startupFileEnabled = Test-AutostartStartupFile
    $task = Get-ScheduledTask -TaskName $Script:TaskName -ErrorAction SilentlyContinue

    if ($runCommand -and $startupFileEnabled) {
        if ($task) {
            return "已开启（注册表登录项 + 启动文件 + 计划任务兼容项）"
        }
        return '已开启（注册表登录项 + 启动文件）'
    }
    if ($runCommand -and $task) {
        return "已开启（注册表登录项 + 计划任务兼容项）"
    }
    if ($runCommand) {
        return '已开启（注册表登录项）'
    }
    if ($startupFileEnabled) {
        if ($task) {
            return "已开启（启动文件 + 计划任务兼容项）"
        }
        return '已开启（启动文件）'
    }
    if ($task) {
        return "已开启（$($task.State)）"
    }
    return '已关闭'
}

function Show-Header {
    param([Parameter(Mandatory)][object]$Config)

    Clear-Host
    Write-Host 'llama.cpp 管理器'
    Write-Host '================'
    Write-Host "管理器目录: $($Script:AppDir)"
    Write-Host "根目录    : $($Script:Root)"
    Write-Host "模型      : $($Config.ModelPath)"
    Write-Host "端口      : $(Get-PortFromArgs -Args $Config.Args -Fallback $Config.Port)"
    Write-Host "开机自启  : $(Get-AutostartText)"
    $running = Get-ManagedServerProcess -Config $Config
    if ($running) {
        Write-Host "服务      : 运行中（PID $($running.Id)）"
    } else {
        Write-Host '服务      : 已停止'
    }
    $cuda = Get-CudaInfo
    if ($cuda.Available) {
        Write-Host 'CUDA      : 可用'
    } else {
        Write-Host 'CUDA      : 不可用'
    }
    Write-Host ''
}

function Pause-Menu {
    Write-Host ''
    Read-Host '按 Enter 继续' | Out-Null
}

function Get-ActiveLlamaServerProcess {
    param(
        [Parameter(Mandatory)][object]$Config,
        [int]$AdditionalPort = 0
    )

    $managed = Get-ManagedServerProcess -Config $Config
    if ($managed) {
        return $managed
    }

    $ports = @()
    $primaryPort = Get-PortFromArgs -Args $Config.Args -Fallback $Config.Port
    if ($primaryPort -gt 0) {
        $ports += $primaryPort
    }
    if ($AdditionalPort -gt 0 -and $ports -notcontains $AdditionalPort) {
        $ports += $AdditionalPort
    }

    foreach ($port in $ports) {
        $owner = Get-ListeningProcessByPort -Port $port
        if (-not $owner) {
            continue
        }
        if ($owner.ProcessName -eq 'llama-server') {
            return $owner
        }
    }

    $anyLlamaServer = Get-AnyLlamaServerProcess
    if ($anyLlamaServer) {
        return $anyLlamaServer
    }

    return $null
}

function Restart-ModelServerIfRunning {
    param(
        [Parameter(Mandatory)][object]$Config,
        [int]$PreviousPort = 0
    )

    $running = Get-ActiveLlamaServerProcess -Config $Config -AdditionalPort $PreviousPort
    if (-not $running) {
        return $false
    }

    $isManaged = ($Config.LastPid -and [int]$Config.LastPid -eq $running.Id)
    Write-Host '检测到服务正在运行，正在重启以应用新模型...'
    Write-ManagerLog '检测到模型配置变更，正在重启服务以应用新模型。'

    if ($isManaged) {
        Stop-ModelServer -Config $Config | Out-Null
    } else {
        Write-ManagerLog "检测到未登记的 llama-server 进程，PID $($running.Id)，将直接停止后切换模型。"
        Stop-ProcessTree -ProcessId $running.Id
        Start-Sleep -Seconds 1
        $Config.LastPid = 0
        Save-ManagerConfig -Config $Config
    }

    Start-ModelServer -Config $Config | Out-Null
    Write-ManagerLog '模型切换已生效，服务已重启。'
    return $true
}

function Select-ModelMenu {
    param([Parameter(Mandatory)][object]$Config)

    $models = @(Get-ModelFiles)
    if ($models.Count -eq 0) {
        Write-Host 'models/ 目录下没有找到 GGUF 模型文件。'
        Pause-Menu
        return
    }

    Write-Host '可用模型：'
    for ($i = 0; $i -lt $models.Count; $i++) {
        $item = $models[$i]
        Write-Host ('[{0}] {1}  {2}' -f ($i + 1), (Format-Bytes -Bytes $item.Length), $item.FullName)
    }
    Write-Host ''
    $choice = Read-Host '请输入模型编号'
    $index = 0
    if (-not [int]::TryParse($choice, [ref]$index) -or $index -lt 1 -or $index -gt $models.Count) {
        Write-Host '无效选择。'
        Pause-Menu
        return
    }

    $previousPort = Get-PortFromArgs -Args $Config.Args -Fallback $Config.Port
    $Config.ModelPath = $models[$index - 1].FullName
    $Config.Args = Get-PresetArgs -Path $Config.ModelPath -Port $Config.Port
    $Config.Port = Get-PortFromArgs -Args $Config.Args -Fallback $Config.Port
    Save-ManagerConfig -Config $Config
    Write-ManagerLog "已选择模型 $($Config.ModelPath)。"
    if (Restart-ModelServerIfRunning -Config $Config -PreviousPort $previousPort) {
        Write-Host '已选择模型，并自动重启服务完成切换。'
    } else {
        Write-Host '已选择模型，并生成预设启动参数。'
    }
    Pause-Menu
}

function Enter-CustomModelMenu {
    param([Parameter(Mandatory)][object]$Config)

    $path = Read-Host '请输入 .gguf 模型的完整路径'
    try {
        $path = Resolve-ModelPath -Path $path
    } catch {
        Write-Host $_.Exception.Message
        Pause-Menu
        return
    }
    if ([System.IO.Path]::GetExtension($path) -ne '.gguf') {
        Write-Host '选择的文件不是 .gguf 文件。'
        Pause-Menu
        return
    }

    $previousPort = Get-PortFromArgs -Args $Config.Args -Fallback $Config.Port
    $Config.ModelPath = [System.IO.Path]::GetFullPath($path)
    $Config.Args = Get-PresetArgs -Path $Config.ModelPath -Port $Config.Port
    $Config.Port = Get-PortFromArgs -Args $Config.Args -Fallback $Config.Port
    Save-ManagerConfig -Config $Config
    Write-ManagerLog "已选择自定义模型 $($Config.ModelPath)。"
    if (Restart-ModelServerIfRunning -Config $Config -PreviousPort $previousPort) {
        Write-Host '已选择自定义模型，并自动重启服务完成切换。'
    } else {
        Write-Host '已选择自定义模型，并生成预设启动参数。'
    }
    Pause-Menu
}

function Edit-ArgsMenu {
    param([Parameter(Mandatory)][object]$Config)

    Write-Host '当前启动参数：'
    Write-Host $Config.Args
    Write-Host ''
    $newArgs = Read-Host '请输入新的启动参数，直接回车则保持不变'
    if ($newArgs.Trim()) {
        $Config.Args = $newArgs.Trim()
        $Config.Port = Get-PortFromArgs -Args $Config.Args -Fallback $Config.Port
        Save-ManagerConfig -Config $Config
        Write-ManagerLog '已更新启动参数。'
        Write-Host '启动参数已更新。'
    } else {
        Write-Host '启动参数未修改。'
    }
    Pause-Menu
}

function Regenerate-PresetMenu {
    param([Parameter(Mandatory)][object]$Config)

    if (-not $Config.ModelPath -or -not (Test-Path -LiteralPath $Config.ModelPath)) {
        Write-Host '请先选择有效模型。'
        Pause-Menu
        return
    }
    $Config.Args = Get-PresetArgs -Path $Config.ModelPath -Port $Config.Port
    $Config.Port = Get-PortFromArgs -Args $Config.Args -Fallback $Config.Port
    Save-ManagerConfig -Config $Config
    Write-Host '已重新生成预设启动参数：'
    Write-Host $Config.Args
    Pause-Menu
}

function Show-StatusMenu {
    param([Parameter(Mandatory)][object]$Config)

    Write-Status -Config $Config
    Pause-Menu
}

function Write-Status {
    param([Parameter(Mandatory)][object]$Config)

    Write-Host '当前启动参数：'
    Write-Host $Config.Args
    Write-Host ''
    Write-Host 'CUDA 设备：'
    Write-Host (Get-CudaInfo).Text
    Write-Host ''
    if (Test-Path -LiteralPath $Script:LogPath) {
        Write-Host '管理器日志最近记录：'
        Get-Content -LiteralPath $Script:LogPath -Tail 20 | ForEach-Object { Write-Host $_ }
    } else {
        Write-Host '暂无管理器日志。'
    }
}

function Show-MainMenu {
    $config = Read-ManagerConfig
    while ($true) {
        Show-Header -Config $config
        Write-Host '[1] 选择本地模型'
        Write-Host '[2] 输入自定义模型路径'
        Write-Host '[3] 重新生成预设启动参数'
        Write-Host '[4] 编辑启动参数'
        Write-Host '[5] 启动模型服务并打开 Web UI'
        Write-Host '[6] 打开 Web UI'
        Write-Host '[7] 停止/卸载当前模型'
        Write-Host '[8] 开启/关闭登录后自启动'
        Write-Host '[9] 查看状态和日志'
        Write-Host '[0] 退出'
        Write-Host ''

        $choice = Read-Host '请选择'
        try {
            switch ($choice) {
                '1' { Select-ModelMenu -Config $config }
                '2' { Enter-CustomModelMenu -Config $config }
                '3' { Regenerate-PresetMenu -Config $config }
                '4' { Edit-ArgsMenu -Config $config }
                '5' {
                    Start-ModelServer -Config $config -OpenBrowser | Out-Null
                    Write-Host '服务已启动。'
                    Pause-Menu
                }
                '6' {
                    Open-WebUi -Config $config
                    Write-Host 'Web UI 已打开。'
                    Pause-Menu
                }
                '7' {
                    if (Stop-ModelServer -Config $config) {
                        Write-Host '服务已停止，模型已从内存/显存中卸载。'
                    } else {
                        Write-Host '当前没有由本工具管理的服务在运行。'
                    }
                    Pause-Menu
                }
                '8' {
                    if (Test-PrimaryAutostartEnabled) {
                        Disable-ManagerAutostart -Config $config
                        Write-Host '登录后自启动已关闭。'
                    } else {
                        Enable-ManagerAutostart -Config $config
                        Write-Host '登录后自启动已开启。'
                    }
                    Pause-Menu
                }
                '9' { Show-StatusMenu -Config $config }
                '0' { return }
                default {
                    Write-Host '无效选择。'
                    Pause-Menu
                }
            }
            $config = Read-ManagerConfig
        } catch {
            Write-ManagerLog "错误：$($_.Exception.Message)"
            Write-Host "错误：$($_.Exception.Message)"
            Pause-Menu
            $config = Read-ManagerConfig
        }
    }
}

try {
    $config = Read-ManagerConfig

    if ($PrintPreset) {
        if (-not $ModelPath) {
            throw '使用 -PrintPreset 时必须提供 -ModelPath。'
        }
        $presetPort = if ($Port -gt 0) { $Port } else { 8080 }
        Get-PresetArgs -Path (Resolve-ModelPath -Path $ModelPath) -Port $presetPort
        exit 0
    }

    if ($ModelPath) {
        $config.ModelPath = Resolve-ModelPath -Path $ModelPath
        $presetPort = if ($Port -gt 0) { $Port } else { $config.Port }
        $config.Args = Get-PresetArgs -Path $config.ModelPath -Port $presetPort
        $config.Port = Get-PortFromArgs -Args $config.Args -Fallback $presetPort
        Save-ManagerConfig -Config $config
    } elseif ($Port -gt 0) {
        $config.Port = $Port
        Save-ManagerConfig -Config $config
    }

    if ($Service) {
        $serviceProcess = Start-ModelServer -Config $config
        if ($serviceProcess) {
            Write-ManagerLog "自启动看护进程正在等待 llama-server PID $($serviceProcess.Id) 结束。"
            Wait-Process -Id $serviceProcess.Id -ErrorAction SilentlyContinue
            Write-ManagerLog "llama-server PID $($serviceProcess.Id) 已结束，自启动看护进程退出。"
        }
        exit 0
    }
    if ($Start) {
        Start-ModelServer -Config $config -OpenBrowser | Out-Null
        exit 0
    }
    if ($Stop) {
        Stop-ModelServer -Config $config | Out-Null
        exit 0
    }
    if ($Open) {
        Open-WebUi -Config $config
        exit 0
    }
    if ($EnableAutostart) {
        Enable-ManagerAutostart -Config $config
        exit 0
    }
    if ($DisableAutostart) {
        Disable-ManagerAutostart -Config $config
        exit 0
    }
    if ($Status) {
        Write-Status -Config $config
        exit 0
    }

    Show-MainMenu
} catch {
    Write-ManagerLog "致命错误：$($_.Exception.Message)"
    Write-Error $_.Exception.Message
    exit 1
}
