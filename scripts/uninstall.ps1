$ErrorActionPreference = "Stop"

function Write-Info  { param($msg) Write-Host $msg -ForegroundColor Cyan }
function Write-Ok    { param($msg) Write-Host $msg -ForegroundColor Green }
function Write-Warn  { param($msg) Write-Host $msg -ForegroundColor Yellow }
function Write-Err   { param($msg) Write-Host $msg -ForegroundColor Red }

Write-Host ""
Write-Info "═══ FMO SAS Uninstaller ═══"
Write-Host ""

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Err "请以管理员身份运行 PowerShell"
    Write-Err "用法: irm https://bg5esn.com/share/fmo/sas-installer/uninstall.ps1 | iex"
    exit 1
}

$TaskName = "fmo-sas"
$InstallDir = "$env:LOCALAPPDATA\fmo-sas"

Write-Info "[1/2] 停止并移除计划任务..."
$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($task) {
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Ok "      完成"
} else {
    Write-Ok "      未找到计划任务 (已跳过)"
}

Write-Info "[2/2] 删除程序和配置..."
if (Test-Path $InstallDir) {
    Remove-Item -Recurse -Force $InstallDir
    Write-Ok "      已删除: $InstallDir"
} else {
    Write-Ok "      目录不存在 (已跳过)"
}

Write-Host ""
Write-Ok "═══ FMO SAS 已卸载 ═══"
Write-Host ""
Write-Info "以下内容未删除："

$dataDirs = @(
    "$env:USERPROFILE\.sas",
    "$env:SystemRoot\System32\config\systemprofile\.sas"
) | Select-Object -Unique

$found = $false
foreach ($dir in $dataDirs) {
    if (Test-Path $dir) {
        $found = $true
        Write-Warn "  数据目录: $dir"
        Write-Warn "  彻底删除: Remove-Item -Recurse -Force '$dir'"
    }
}
if (-not $found) {
    Write-Ok "  数据目录: 无"
}

Write-Host ""
