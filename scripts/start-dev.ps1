#Requires -Version 5.1
<#
.SYNOPSIS
    一键启动 AnBot 开发环境：后端宿主 + Blazor 管理界面。

.DESCRIPTION
    在两个独立的 PowerShell 窗口中分别运行：
      - src/AnBot.Cli        → http://localhost:5387
      - src/AnBot.Admin.Web  → http://localhost:5242（开发服务器，自动代理配置到 5387）

    如果对应端口已在监听，则跳过启动（便于在调试时只重启其中一个）。
    关闭两个子窗口即可停止服务。

.PARAMETER NoBrowser
    不自动打开浏览器。

.EXAMPLE
    .\scripts\start-dev.ps1
#>
[CmdletBinding()]
param(
    [switch]$NoBrowser
)

$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $root

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "未找到 dotnet，请先安装 .NET 10 SDK。"
}

function Test-Port([int]$Port) {
    $client = [System.Net.Sockets.TcpClient]::new()

    try {
        $client.Connect("127.0.0.1", $Port)
        return $true
    }
    catch {
        return $false
    }
    finally {
        $client.Dispose()
    }
}

function Start-DevWindow([string]$Title, [string]$Command) {
    Start-Process powershell -ArgumentList @(
        "-NoExit",
        "-Command",
        "`$Host.UI.RawUI.WindowTitle = '$Title'; Set-Location '$root'; $Command"
    ) | Out-Null
}

$backendReady = Test-Port 5387

if ($backendReady) {
    Write-Host "[1/3] 检测到 5387 已在监听，跳过后端启动。" -ForegroundColor Yellow
}
else {
    Write-Host "[1/3] 启动后端宿主（http://localhost:5387）..." -ForegroundColor Cyan
    Start-DevWindow "AnBot 宿主" "dotnet run --project src/AnBot.Cli"

    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline -and -not (Test-Port 5387)) {
        Start-Sleep -Milliseconds 500
    }

    $backendReady = Test-Port 5387
}

if (-not $backendReady) {
    Write-Warning "后端未在 90 秒内就绪，请查看「AnBot 宿主」窗口的日志。"
}

Write-Host "[2/3] 启动管理界面（http://localhost:5242）..." -ForegroundColor Cyan

if (Test-Port 5242) {
    Write-Host "      检测到 5242 已在监听，跳过前端启动。" -ForegroundColor Yellow
}
else {
    Start-DevWindow "AnBot 管理界面" "dotnet run --project src/AnBot.Admin.Web"

    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and -not (Test-Port 5242)) {
        Start-Sleep -Milliseconds 500
    }
}

if (-not $NoBrowser) {
    Write-Host "[3/3] 打开浏览器..." -ForegroundColor Cyan
    Start-Process "http://localhost:5242"
}
else {
    Write-Host "[3/3] 已跳过浏览器（-NoBrowser）。" -ForegroundColor Cyan
}

Write-Host ""
Write-Host "已启动。默认账号：admin / anbot-dev（仅在账号表为空时创建）。" -ForegroundColor Green
Write-Host "关闭两个 PowerShell 窗口即可停止服务。"
