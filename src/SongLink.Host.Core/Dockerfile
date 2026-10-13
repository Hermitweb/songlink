# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files and restore dependencies
COPY src/SongLink.Host.Core/*.csproj ./
RUN dotnet restore -o /app/dependencies

# Copy source code and publish
COPY src/SongLink.Host.Core/ .
RUN dotnet publish SongLink.Host.Core.csproj -c Release -o /app/publish --no-restore

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
