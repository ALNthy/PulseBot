#!/usr/bin/env bash
#
# 一键启动 AnBot 开发环境：后端宿主 + Blazor 管理界面。
#
#   ./scripts/start-dev.sh [--no-browser]
#
# 后端日志写入 logs/dev-backend.log，前端日志写入 logs/dev-frontend.log。
# 按 Ctrl+C 停止本次脚本启动的进程；已在运行的端口会被跳过。
#
set -euo pipefail

no_browser=0
if [[ "${1:-}" == "--no-browser" ]]; then
    no_browser=1
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

command -v dotnet >/dev/null 2>&1 || {
    echo "未找到 dotnet，请先安装 .NET 10 SDK。" >&2
    exit 1
}

mkdir -p logs

started_backend=0
started_frontend=0

# 按监听端口停止服务：dotnet run 会再启动一个应用子进程，
# 只 kill 父进程会把真正监听端口的子进程留在后台。
stop_by_port() {
    local port="$1"

    if command -v powershell.exe >/dev/null 2>&1; then
        powershell.exe -NoProfile -Command "\$pids = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique; foreach (\$id in \$pids) { Stop-Process -Id \$id -Force -ErrorAction SilentlyContinue }" >/dev/null 2>&1 || true
    elif command -v lsof >/dev/null 2>&1; then
        local pids
        pids=$(lsof -ti "tcp:$port" 2>/dev/null || true)
        [[ -n "$pids" ]] && kill $pids 2>/dev/null || true
    fi
}

cleanup() {
    if [[ "$started_frontend" -eq 1 ]]; then
        stop_by_port 5242
    fi

    if [[ "$started_backend" -eq 1 ]]; then
        stop_by_port 5387
    fi
}

trap cleanup INT TERM EXIT

# 用 /dev/tcp 探测端口是否已监听（仅建立连接后立即关闭）。
is_up() {
    (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null
}

if is_up 5387; then
    echo "[1/3] 检测到 5387 已在监听，跳过后端启动。"
else
    echo "[1/3] 启动后端宿主（http://localhost:5387）..."
    dotnet run --project src/AnBot.Cli > logs/dev-backend.log 2>&1 &
    started_backend=1

    for _ in $(seq 1 180); do
        is_up 5387 && break
        sleep 0.5
    done

    if ! is_up 5387; then
        echo "后端未在 90 秒内就绪，请查看 logs/dev-backend.log。" >&2
        exit 1
    fi
fi

echo "[2/3] 启动管理界面（http://localhost:5242）..."

if is_up 5242; then
    echo "      检测到 5242 已在监听，跳过前端启动。"
else
    dotnet run --project src/AnBot.Admin.Web > logs/dev-frontend.log 2>&1 &
    started_frontend=1

    for _ in $(seq 1 120); do
        is_up 5242 && break
        sleep 0.5
    done
fi

if [[ "$no_browser" -eq 0 ]]; then
    echo "[3/3] 打开浏览器..."

    if command -v cmd.exe >/dev/null 2>&1; then
        cmd.exe /c start "" "http://localhost:5242" >/dev/null 2>&1 || true
    elif command -v open >/dev/null 2>&1; then
        open "http://localhost:5242" || true
    elif command -v xdg-open >/dev/null 2>&1; then
        xdg-open "http://localhost:5242" || true
    fi
else
    echo "[3/3] 已跳过浏览器（--no-browser）。"
fi

echo
echo "已启动。默认账号：admin / anbot-dev（仅在账号表为空时创建）。"
echo "日志：logs/dev-backend.log / logs/dev-frontend.log"
echo "按 Ctrl+C 停止。"

wait
