# syntax=docker/dockerfile:1
# WAN Safety Studio server (.NET 10 + React UI) for a Linux container App Service.
# Build from the repo root (context = repo root):
#   docker build --platform linux/amd64 -f src/VideoSafetyStudio.Server/Dockerfile -t wan-safety-studio-server:dev .
# Optional credentials for an authenticated package feed (never baked into a layer):
#   --secret id=npmrc,src=$HOME/.npmrc  --secret id=nugetconfig,src=<user NuGet.Config>
# Docs: docs/app-service-docker.md

FROM mcr.microsoft.com/azurelinux/base/nodejs:24 AS web
WORKDIR /repo/web
COPY web/package.json web/package-lock.json web/.npmrc ./
RUN --mount=type=secret,id=npmrc,target=/root/.npmrc \
    npm ci --no-audit --no-fund
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS publish
WORKDIR /repo
COPY nuget.config ./
COPY src/ src/
# The web build already ran in the previous stage, so skip the project's BuildWeb target.
# Publishing for linux-x64 makes the Copilot SDK build step download the Linux Copilot CLI/runtime
# (the video helper's agent loop). Infrastructure and Server both stage it, so allow the duplicate outputs.
RUN --mount=type=secret,id=nugetconfig,target=/root/.nuget/NuGet/NuGet.Config \
    dotnet publish src/VideoSafetyStudio.Server/VideoSafetyStudio.Server.csproj \
      -c Release -r linux-x64 --self-contained false -p:SkipWebBuild=true \
      -p:ErrorOnDuplicatePublishOutputFiles=false -o /app/publish
COPY --from=web /repo/web/dist /app/publish/wwwroot
# Native executables can lose their mode through package/copy paths; the non-root user must be able to run them.
RUN find /app/publish -type f \( -name copilot -o -name copilot-runtime -o -path '*/ripgrep/bin/*' -o -path '*/tgrep/bin/*' \) -exec chmod 0755 {} + \
    && find /app/publish -type f \( -name copilot -o -name copilot-runtime \) | grep .

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0 \
    HOME=/home/app
COPY --from=publish --chown=app:app /app/publish ./
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "VideoSafetyStudio.Server.dll"]
