## Multi-stage build for FileHorizon (.NET 8)
## Build args (override as needed):
#   BUILD_CONFIGURATION=Release
#   UID=1001
#   GID=1001

ARG BUILD_CONFIGURATION=Release
ARG UID=1001
ARG GID=1001

############################
## Build Stage
############################
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy solution and project files first to leverage layer caching
COPY FileHorizon.sln ./
COPY src/FileHorizon.Application/FileHorizon.Application.csproj src/FileHorizon.Application/
COPY src/FileHorizon.Host/FileHorizon.Host.csproj src/FileHorizon.Host/
COPY test/FileHorizon.Application.Tests/FileHorizon.Application.Tests.csproj test/FileHorizon.Application.Tests/

# Restore
RUN dotnet restore FileHorizon.sln --nologo

# Copy full source
COPY . .

# Build & publish (self-contained trimming not applied yet to keep diagnostics easier)
RUN dotnet publish src/FileHorizon.Host/FileHorizon.Host.csproj \
    -c ${BUILD_CONFIGURATION} \
    -o /app/publish \
    --no-restore \
    --nologo

############################
## Runtime Stage (ASP.NET Core runtime image)
############################
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_EnableDiagnostics=0

# Create non-root user/group
ARG UID=1001
ARG GID=1001
RUN groupadd -g ${GID} appgroup \
    && useradd -u ${UID} -g appgroup -m appuser \
    && mkdir -p /app/data /app/config \
    && chown -R appuser:appgroup /app

WORKDIR /app
COPY --from=build /app/publish ./

# Expose health port (8080) – actual port mapping decided at run
EXPOSE 8080

USER appuser

# Default: run the host
ENTRYPOINT ["dotnet", "FileHorizon.Host.dll"]