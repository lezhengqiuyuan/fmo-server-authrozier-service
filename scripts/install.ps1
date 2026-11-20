$ErrorActionPreference = "Stop"

$SasJsonUrl = "https://bg5esn.com/share/fmo/sas.json"
$ScriptVersion = "1.1.0"
$Rid = "win-x64"
$InstallDir = "$env:LOCALAPPDATA\fmo-sas"
$TaskName = "fmo-sas"

function Write-Info  { param($msg) Write-Host $msg -ForegroundColor Cyan }
function Write-Ok    { param($msg) Write-Host $msg -ForegroundColor Green }
function Write-Warn  { param($msg) Write-Host $msg -ForegroundColor Yellow }
function Write-Err   { param($msg) Write-Host $msg -ForegroundColor Red }

# ═══════════════════════════════════════════════════════════════
Write-Host ""
Write-Info "═══ FMO SAS Installer v$ScriptVersion ═══"
Write-Host ""

# ── 管理员检查 ──
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Err "请以管理员身份运行 PowerShell"
    Write-Err "右键 PowerShell -> 以管理员身份运行，然后执行:"
    Write-Err "  irm https://bg5esn.com/share/fmo/sas-installer/install.ps1 | iex"
    exit 1
}

# ── 环境预检 ──
$hasCurl = [bool](Get-Command curl.exe -ErrorAction SilentlyContinue)
if (-not $hasCurl) {
    Write-Warn "      未检测到 curl.exe，下载将不显示进度条"
}

# ═══════════════════════════════════════════════════════════════
# STEP 1: 平台检测
# ═══════════════════════════════════════════════════════════════
Write-Info "[1/5] 检测平台... Windows x64 -> $Rid"

# ═══════════════════════════════════════════════════════════════
# STEP 2: 获取版本 + 下载
# ═══════════════════════════════════════════════════════════════
Write-Info "[2/5] 获取最新版本..."

$meta = Invoke-RestMethod -Uri $SasJsonUrl
$version = $meta.version
$downloadUrl = $meta.assets.'win-x64'

if (-not $version -or -not $downloadUrl) {
    Write-Err "无法解析版本信息，请检查网络连接"
    exit 1
}

Write-Info "      版本: v$version | 下载: $downloadUrl"

$zipPath = "$env:TEMP\sas-install-$version.zip"
if ($hasCurl) {
    & curl.exe -fL --progress-bar $downloadUrl -o $zipPath
    if ($LASTEXITCODE -ne 0) {
        Write-Err "下载失败，请检查网络连接"
        Write-Err "您可以尝试安装组件后重试，或手动部署，参考: https://bg5esn.com/docs/fmo-server-construction/"
        exit 1
    }
} else {
    Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath -UseBasicParsing
}

# ── SHA-256 校验 ──
$expectedSha = $meta.assets.'win-x64_sha256'
if (-not $expectedSha) {
    Write-Err "版本清单缺少 win-x64_sha256，拒绝安装未校验的安装包"
    Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
    exit 1
}

$actualSha = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualSha -ne $expectedSha.ToLowerInvariant()) {
    Write-Err "安装包校验失败"
    Write-Err "  expected=$($expectedSha.ToLowerInvariant())"
    Write-Err "  actual  =$actualSha"
    Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
    exit 1
}

Write-Ok "      SHA-256 校验通过"

# ═══════════════════════════════════════════════════════════════
# STEP 3: 安装
# ═══════════════════════════════════════════════════════════════
Write-Info "[3/5] 安装到 $InstallDir ..."

# 停止旧任务
$existingTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($existingTask) {
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Get-Process -Name "sas" -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
    Write-Warn "      已停止旧版 fmo-sas 任务"
}

if (Test-Path $InstallDir) {
    $configBackup = $null
    $configPath = "$InstallDir\config.json"
    if (Test-Path $configPath) {
        $configBackup = Get-Content -Raw $configPath
    }
    Remove-Item -Recurse -Force $InstallDir
}

Expand-Archive -Path $zipPath -DestinationPath $InstallDir -Force
Remove-Item $zipPath -Force

if (-not (Test-Path "$InstallDir\sas.exe")) {
    Write-Err "下载文件解压异常，未找到 sas.exe 主程序"
    Write-Err "您可以尝试安装组件后重试，或手动部署，参考: https://bg5esn.com/docs/fmo-server-construction/"
    exit 1
}

# 恢复旧配置
if ($configBackup) {
    Set-Content -Path "$InstallDir\config.json" -Value $configBackup -NoNewline
    Write-Warn "      已恢复旧配置文件"
}

Write-Ok "      完成"

# ═══════════════════════════════════════════════════════════════
# STEP 4: 配置 SAS 参数
# ═══════════════════════════════════════════════════════════════
Write-Info "[4/5] 配置 SAS..."

$serverUid = $null
$serverCall = $null
$mqttHost = $null
$mqttPort = $null
$certFp = $null
$httpPort = "8080"
$skipConfig = $false

$configPath = "$InstallDir\config.json"
$sasExe = "$InstallDir\sas.exe"

if (Test-Path $configPath) {
    Write-Ok "      检测到已有配置文件，跳过参数获取"
} else {
    Write-Host ""
    Write-Host "      请在浏览器中打开 FMO 后台获取 SAS 启动命令:"
    Write-Host "      http://fmo.local/config.html -> [SAS 搭建快捷方式] -> [生成命令] -> 复制"
    Write-Host ""
    Write-Host "      [1] 粘贴 FMO 生成的命令 (推荐)"
    Write-Host "      [2] 手动输入各参数"
    Write-Host "      [3] 跳过配置 (稍后双击 sas.exe 进入交互向导)"
    $choice = Read-Host "      请选择 [1/2/3]"

    switch ($choice) {
        "1" {
            $cmdLine = Read-Host "      请粘贴命令"
            if ($cmdLine -match '--server-uid\s+(\S+)') { $serverUid = $Matches[1] }
            if ($cmdLine -match '--server-callsign\s+(\S+)') { $serverCall = $Matches[1] }
            if ($cmdLine -match '--mqtt-host\s+(\S+)') { $mqttHost = $Matches[1] }
            if ($cmdLine -match '--mqtt-port\s+(\S+)') { $mqttPort = $Matches[1] }
            if ($cmdLine -match '--cert-fingerprint\s+(\S+)') { $certFp = $Matches[1] }
            if ($cmdLine -match '--http-port\s+(\S+)') { $httpPort = $Matches[1] }
            if (-not $mqttPort) { $mqttPort = "1883" }
            if (-not $httpPort) { $httpPort = "8080" }
        }
        "2" {
            $serverUid = Read-Host "      server-uid"
            $serverCall = Read-Host "      server-callsign"
            $mqttHost = Read-Host "      mqtt-host"
            $mqttPort = Read-Host "      mqtt-port [1883]"
            if (-not $mqttPort) { $mqttPort = "1883" }
            $certFp = Read-Host "      cert-fingerprint"
            $httpPort = Read-Host "      http-port [8080]"
            if (-not $httpPort) { $httpPort = "8080" }
        }
        default {
            $skipConfig = $true
            Write-Warn "      跳过配置"
        }
    }

    if (-not $skipConfig -and $serverUid) {
        Write-Info "      生成配置文件..."
        $proc = Start-Process -FilePath $sasExe -ArgumentList @(
            "--config", $configPath,
            "--server-uid", "$serverUid",
            "--server-callsign", "$serverCall",
            "--mqtt-host", "$mqttHost",
            "--mqtt-port", "$mqttPort",
            "--cert-fingerprint", "$certFp",
            "--http-port", "$httpPort"
        ) -PassThru -WindowStyle Hidden
        Start-Sleep -Seconds 2
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500

        if (Test-Path $configPath) {
            Write-Ok "      配置文件已生成: $configPath"
        } else {
            Write-Warn "      配置文件生成失败，请稍后手动运行 sas.exe"
            $skipConfig = $true
        }
    }
}

# ═══════════════════════════════════════════════════════════════
# STEP 5: 注册计划任务 (作为服务替代)
# ═══════════════════════════════════════════════════════════════
Write-Info "[5/5] 注册系统任务..."

$sasExe = "$InstallDir\sas.exe"

# 移除旧任务
if ($existingTask) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
}

$logPath = "$InstallDir\sas.log"
$action = New-ScheduledTaskAction `
    -Execute "cmd.exe" `
    -Argument "/c `"`"$sasExe`" --config `"$configPath`" >> `"$logPath`" 2>&1`"" `
    -WorkingDirectory $InstallDir

$trigger = New-ScheduledTaskTrigger -AtStartup

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -RestartCount 3 `
    -ExecutionTimeLimit (New-TimeSpan -Days 9999)

$principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Settings $settings `
    -Principal $principal `
    -Description "FMO Server Authorizer Service" | Out-Null

$canStart = (-not $skipConfig) -and (Test-Path $configPath)

if ($canStart) {
    Start-ScheduledTask -TaskName $TaskName
    Write-Info "      等待服务启动 (5 秒)..."
    Start-Sleep -Seconds 5
    Write-Host ""
    Write-Host "  ── 启动日志 ──"
    if (Test-Path $logPath) {
        Get-Content $logPath | ForEach-Object { Write-Host "  $_" }
    } else {
        Write-Host "  (日志文件尚未生成)"
    }
    Write-Host "  ──────────────"
    Write-Host ""
    Write-Ok "═══ SAS v$version 安装完成并已启动 ═══"
    Write-Host ""
    Write-Host "  日志:     Get-Content -Wait '$logPath'"
    Write-Host ""
    Write-Host "  状态:     Get-ScheduledTask -TaskName fmo-sas"
    Write-Host "  启动:     Start-ScheduledTask -TaskName fmo-sas"
    Write-Host "  停止:     Stop-ScheduledTask -TaskName fmo-sas"
    Write-Host "  重启:     Stop-ScheduledTask fmo-sas; Start-ScheduledTask fmo-sas"
    Write-Host "  升级:     重新运行本安装脚本即可（自动保留配置）"
    Write-Host "  卸载:     irm https://bg5esn.com/share/fmo/sas-installer/uninstall.ps1 | iex"
    Write-Host ""
    Write-Host "  配置文件: $configPath"
    Write-Host "  安装目录: $InstallDir"
} else {
    Write-Host ""
    Write-Ok "═══ SAS v$version 已安装（未启动，需先配置）═══"
    Write-Host ""
    Write-Host "  配置:     双击运行 $sasExe 进入交互向导"
    Write-Host ""
    Write-Host "  状态:     Get-ScheduledTask -TaskName fmo-sas"
    Write-Host "  启动:     Start-ScheduledTask -TaskName fmo-sas"
    Write-Host "  停止:     Stop-ScheduledTask -TaskName fmo-sas"
    Write-Host "  日志:     Get-Content -Wait '$logPath'"
    Write-Host "  升级:     重新运行本安装脚本即可（自动保留配置）"
    Write-Host "  卸载:     irm https://bg5esn.com/share/fmo/sas-installer/uninstall.ps1 | iex"
    Write-Host ""
    Write-Host "  配置文件: $configPath"
    Write-Host "  安装目录: $InstallDir"
}

Write-Host ""
