ARG SANDBOX_VERSION=2321.2.0
ARG GIT_COMMIT=unknown
ARG BUILD_DATE=unknown
ARG WEBGL_DIST=unity/Builds/WebGL

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG SANDBOX_VERSION
WORKDIR /src

COPY VERSION ./VERSION
COPY server/ ./server/

RUN VERSION_PREFIX="$(printf '%s' "${SANDBOX_VERSION}" | sed -E 's/^([0-9]+\.[0-9]+\.[0-9]+).*/\1/')" \
    && dotnet restore server/src/Code2Create.UnitySandbox.Server/Code2Create.UnitySandbox.Server.csproj \
    && dotnet publish server/src/Code2Create.UnitySandbox.Server/Code2Create.UnitySandbox.Server.csproj \
        -c Release \
        --no-restore \
        -o /app/publish \
        "/p:VersionPrefix=${VERSION_PREFIX}" \
        "/p:InformationalVersion=${SANDBOX_VERSION}" \
        "/p:IncludeSourceRevisionInInformationalVersion=false"

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
ARG SANDBOX_VERSION
ARG GIT_COMMIT
ARG BUILD_DATE
ARG WEBGL_DIST

LABEL org.opencontainers.image.title="mc-remote-unity-sandbox" \
      org.opencontainers.image.description="Minecraft Remote Unity Sandbox Server and Unity WebGL Client" \
      org.opencontainers.image.source="https://github.com/Naohiro2g/mc-remote-unity-sandbox" \
      org.opencontainers.image.version="${SANDBOX_VERSION}" \
      org.opencontainers.image.revision="${GIT_COMMIT}" \
      org.opencontainers.image.created="${BUILD_DATE}" \
      org.opencontainers.image.licenses="MIT"

ENV UNITY_SANDBOX_VERSION="${SANDBOX_VERSION}" \
    UNITY_SANDBOX_GIT_COMMIT="${GIT_COMMIT}" \
    ASPNETCORE_URLS="http://0.0.0.0:8080"

WORKDIR /app
COPY LICENSE ./LICENSE
COPY THIRD-PARTY-NOTICES.txt ./THIRD-PARTY-NOTICES.txt
COPY --from=build /app/publish/ ./
COPY ${WEBGL_DIST}/ ./wwwroot/
COPY THIRD-PARTY-NOTICES.txt ./wwwroot/THIRD-PARTY-NOTICES.txt

USER $APP_UID

EXPOSE 8080/tcp 25585/tcp

ENTRYPOINT ["dotnet", "/app/Code2Create.UnitySandbox.Server.dll", "--url", "http://0.0.0.0:8080", "--web-root", "/app/wwwroot", "--tcp-host", "0.0.0.0", "--tcp-port", "25585"]
