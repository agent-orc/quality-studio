# syntax=docker/dockerfile:1
#
# One image, one port: the API serves /api and the built browser bundle from the same host.
# Build:  docker build -t quality-studio:local .
# Run:    see docs/deployment.md
#
# The image was authored and reviewed but never built on the machine that wrote it (no Docker
# available there). The pieces it assembles are proven separately: `dotnet publish` of the API and
# the static-hosting contract covered by QualityStudio.Api.Tests/StaticUiHostingTests.

# ---------------------------------------------------------------- browser bundle
FROM node:22-bookworm-slim AS ui
WORKDIR /build
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build -- --configuration production

# ---------------------------------------------------------------- API publish
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY Directory.Build.props ./
COPY src/ ./src/
RUN dotnet publish src/QualityStudio.Api/QualityStudio.Api.csproj \
        --configuration Release \
        --output /publish \
        /p:UseAppHost=true

# ---------------------------------------------------------------- pinned gitleaks
# Downloaded once at build time and checked against the digest tracked in
# src/AgentOrchestrator.CodeQuality/gitleaks-binaries.json, so no scan ever downloads at runtime.
FROM debian:bookworm-slim AS gitleaks
ARG GITLEAKS_VERSION=8.24.2
ARG GITLEAKS_SHA256=fa0500f6b7e41d28791ebc680f5dd9899cd42b58629218a5f041efa899151a8e
RUN apt-get update \
 && apt-get install --yes --no-install-recommends ca-certificates curl \
 && rm -rf /var/lib/apt/lists/*
RUN curl --fail --silent --show-error --location \
        --output /tmp/gitleaks.tar.gz \
        "https://github.com/gitleaks/gitleaks/releases/download/v${GITLEAKS_VERSION}/gitleaks_${GITLEAKS_VERSION}_linux_x64.tar.gz" \
 && echo "${GITLEAKS_SHA256}  /tmp/gitleaks.tar.gz" | sha256sum --check - \
 && tar --extract --gzip --file /tmp/gitleaks.tar.gz --directory /usr/local/bin gitleaks \
 && chmod 0755 /usr/local/bin/gitleaks \
 && rm /tmp/gitleaks.tar.gz

# ---------------------------------------------------------------- runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# git: the API starts git processes for hierarchy state, churn and change sets.
# curl: the container health check.
RUN apt-get update \
 && apt-get install --yes --no-install-recommends ca-certificates curl git \
 && rm -rf /var/lib/apt/lists/* \
 && git config --system --add safe.directory '*'

COPY --from=gitleaks /usr/local/bin/gitleaks /usr/local/bin/gitleaks
COPY --from=api /publish /app
COPY --from=ui /build/dist/frontend/browser /app/wwwroot

# Non-root. /repositories holds the reviewed working copies, /app/.quality-studio the server-owned
# registry, /data the future home of the relocated .quality outputs (QS-102).
RUN useradd --system --home-dir /home/quality --uid 10001 --user-group quality \
 && mkdir -p /repositories /data /app/.quality-studio /home/quality \
 && chown -R quality:quality /repositories /data /app/.quality-studio /home/quality
USER quality
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_URLS=http://0.0.0.0:8080 \
    QUALITY_GITLEAKS_PATH=/usr/local/bin/gitleaks \
    QualityStudio__Security__Mode=Hosted \
    QualityStudio__Security__RequireHttps=false \
    QualityStudio__RepositoryRoot=/repositories \
    QualityStudio__AllowedRoots__0=/repositories \
    QualityStudio__DataRoot=/data \
    QualityStudio__AllowedOrigins__0=http://localhost:8080

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl --fail --silent --show-error http://127.0.0.1:8080/health || exit 1

ENTRYPOINT ["dotnet", "/app/QualityStudio.Api.dll"]
