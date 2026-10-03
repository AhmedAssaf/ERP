# syntax=docker/dockerfile:1
# W-19: container images of the web host, the worker and the migrator for the pilot (one Arm VM, docs/19).
# One build stage publishes all three; each runtime target copies its own output.
#
# Build from the repository root (the context must be the root; this file's .dockerignore keeps it to src/):
#   docker buildx build --platform linux/arm64 -f infra/pilot/docker/app.Dockerfile --target web -t waslabid/web:<tag> --load .
# infra/pilot/build-images.sh builds all three and ships them to the VM.
#
# The build stage runs on the BUILD platform and publishes framework-dependent output for the TARGET's runtime identifier
# (no app host, nothing compiled natively), so an amd64 laptop or CI runner builds arm64 images without emulating the
# SDK. A build ON an arm64 machine (the VM) works too: Platform.UI's Tailwind step picks the standalone CLI by
# operating system and architecture, each pinned by SHA-256 (src/UI/Platform.UI/Platform.UI.csproj).
#
# Base images are pinned by version and multi-architecture index digest (amd64 and arm64), checked 2026-10-03.
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.12-noble@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4

FROM --platform=$BUILDPLATFORM ${SDK_IMAGE} AS build
# The target architecture picks the runtime identifier, so only that platform's native assets ship (SkiaSharp alone
# brings about 480 MB for every platform in a portable publish).
ARG TARGETARCH
ENV DOTNET_NOLOGO=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=true \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true
WORKDIR /src
# .editorconfig matters: the build treats warnings as errors and the analyzer severities live there.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/ src/
# Warnings are errors here too (Directory.Build.props), so an image never holds code CI would refuse.
RUN --mount=type=cache,id=waslabid-nuget,target=/root/.nuget/packages \
    --mount=type=cache,id=waslabid-tools,target=/src/.tools \
    set -e; \
    case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) echo "unsupported $TARGETARCH"; exit 1 ;; esac; \
    for project in Platform.Web Platform.Worker Platform.Migrator; do \
      dotnet publish "src/$project/$project.csproj" -c Release -r "$rid" --self-contained false -o "/out/$project" \
        -p:UseAppHost=false; \
    done

# Ubuntu 24.04 runtime: glibc, ICU (ar-SA formatting) and tzdata (Asia/Riyadh in alert emails) are present; bash is there
# for the health check, which needs no curl. The image's non-root user is APP_UID 1654. No TZ: the hosts work in UTC and
# convert to Asia/Riyadh where they show a time, as in development and the tests.
FROM ${RUNTIME_IMAGE} AS runtime
ENV DOTNET_NOLOGO=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=true
WORKDIR /app

FROM runtime AS web
# Owned by root and only readable by the app user: the process cannot change its own binaries.
COPY --from=build /out/Platform.Web /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
# Liveness only (/alive runs no check, W-10 O-15): a database outage must not make Docker or Compose restart the host.
# Readiness (/health) is checked by deploy.sh and by the worker every minute (F-51).
HEALTHCHECK --interval=15s --timeout=5s --start-period=60s --retries=4 \
  CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /alive HTTP/1.1\\r\\nHost: localhost\\r\\nConnection: close\\r\\n\\r\\n' >&3 && head -n 1 <&3 | grep -q ' 200 '"]
ENTRYPOINT ["dotnet", "Platform.Web.dll"]

FROM runtime AS worker
COPY --from=build /out/Platform.Worker /app
USER $APP_UID
# The worker opens no port, so there is no HTTP probe. Its readiness is its Hangfire heartbeat in PostgreSQL, checked by
# deploy.sh and by watchdog.sh every five minutes (docs/19 section 6); this check only says the process is not a zombie.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD ["bash", "-c", "read -r _ _ state _ < /proc/1/stat && [ \"$state\" != Z ]"]
ENTRYPOINT ["dotnet", "Platform.Worker.dll"]

FROM runtime AS migrator
COPY --from=build /out/Platform.Migrator /app
USER $APP_UID
# One-shot (docker compose run --rm migrator): exits 0 when every migration is applied and the role logins are set.
HEALTHCHECK NONE
ENTRYPOINT ["dotnet", "Platform.Migrator.dll"]
