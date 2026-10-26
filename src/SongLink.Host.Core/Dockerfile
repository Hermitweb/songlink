# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Host.Core 引用了 SongLink.Protocol 与 SongLink.Host.AudioStream 两个工程，
# 镜像里必须按原始相对目录结构一起拷进来，ProjectReference 才解析得到。
# （之前只拷 Host.Core 自己，publish 全线 CS0234。）
COPY src/SongLink.Protocol/SongLink.Protocol.csproj src/SongLink.Protocol/
COPY src/SongLink.Host.AudioStream/SongLink.Host.AudioStream.csproj src/SongLink.Host.AudioStream/
COPY src/SongLink.Host.Core/SongLink.Host.Core.csproj src/SongLink.Host.Core/

# 两点：
#   1. restore 不支持 -o/--output（那是 build/publish 的开关，restore 用了会报
#      MSB1001 Unknown switch）；缓存包目录用 --packages。
#   2. EnableWindowsTargeting 在 Linux 上构建 net8.0-windows 目标是必须的。
RUN dotnet restore src/SongLink.Host.Core/SongLink.Host.Core.csproj \
    --packages /app/dependencies -p:EnableWindowsTargeting=true

# Copy source code and publish
COPY src/SongLink.Protocol/ src/SongLink.Protocol/
COPY src/SongLink.Host.AudioStream/ src/SongLink.Host.AudioStream/
COPY src/SongLink.Host.Core/ src/SongLink.Host.Core/
RUN dotnet publish src/SongLink.Host.Core/SongLink.Host.Core.csproj -c Release -o /app/publish --no-restore -p:EnableWindowsTargeting=true

# ⚠️ 这个镜像的定位：**构建冒烟验证**，不是部署形态。
#
#   - EnableWindowsTargeting=true 是必须的：本工程目标框架 net8.0-windows
#     （引用了 Windows 专用的音频/媒体会话模块），而 build stage 跑在 Linux 上，
#     不加这个开关 SDK 会报 NETSDK1100 直接拒绝构建。
#   - 产出的程序本身仍是 **Windows 程序**，而下面的 runtime stage 是 Linux 镜像，
#     所以它跑不起来。真正的部署形态是 Windows 上的单文件 exe
#     （见 scripts/publish-desktop.ps1 与 installer/）。

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Install dependencies for Bluetooth support
RUN apt-get update && \
    apt-get install -y bluetooth bluez && \
    rm -rf /var/lib/apt/lists/*

# Copy published output
COPY --from=build /app/publish .

# Configure environment variables
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
ENV SONGLINK_PORT=51999
ENV SONGLINK_ADAPTER=fake

EXPOSE 51999

ENTRYPOINT ["./SongLink.Host"]
