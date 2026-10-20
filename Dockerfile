# syntax=docker/dockerfile:1

# ============================================================================
# 构建阶段：还原依赖并发布 PulseBot.Cli（含内核、适配器、插件与后台）。
# ============================================================================

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 构建配置与 NuGet 配置先入镜像：它们变化频率远低于源码，可复用还原缓存。
COPY Directory.Build.props Directory.Packages.props global.json nuget.config ./
COPY PulseBot.slnx ./

# 只复制宿主需要的源码；tests/ 不参与发布。
COPY src/ ./src/

# 还原 Cli 及其全部 ProjectReference（Core、适配器、插件、Admin.*）。
RUN dotnet restore src/PulseBot.Cli/PulseBot.Cli.csproj

# 发布为框架依赖部署：运行阶段由 aspnet 共享框架提供运行时。
# UseAppHost=false 不生成原生可执行文件，入口统一用 `dotnet PulseBot.Cli.dll`。
RUN dotnet publish src/PulseBot.Cli/PulseBot.Cli.csproj \
        --configuration Release \
        --no-restore \
        --output /app/publish \
        /p:UseAppHost=false

# ============================================================================
# 运行阶段：只保留运行时与发布产物。
# ============================================================================

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# curl 供 HEALTHCHECK / docker compose 健康检查使用；
# tzdata 让 TZ 环境变量生效（日志与邮件时间按本地时区显示）。
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl tzdata \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

# SQLite 数据库目录。生产部署应把它挂成命名卷或宿主目录，
# 否则容器重建时账号、审计日志与运行时名单会一起丢失。
RUN mkdir -p /app/data && chown -R app:app /app/data

# .NET 8+ 的 aspnet 镜像内置非 root 用户 app（APP_UID）。
USER $APP_UID

# 5187 = 后台管理；6700 = OneBot 12 反向 WebSocket。
# 控制台适配器不占端口：容器内没有标准输入，它在启动后会自动停止读取。
EXPOSE 5187 6700

# 内容根是程序所在目录 /app，appsettings.json 随发布产物一同复制。
ENTRYPOINT ["dotnet", "PulseBot.Cli.dll"]
