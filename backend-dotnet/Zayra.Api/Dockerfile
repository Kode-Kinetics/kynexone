FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Zayra.Api.csproj ./
RUN dotnet restore
COPY . ./
# The commit this image is built from, baked into the assembly's informational version and reported
# by /health/live, so the e2e preflight can refuse a stale image. Empty unless the builder passes it
# (docker compose forwards BUILD_COMMIT).
ARG BUILD_COMMIT=
RUN dotnet publish Zayra.Api.csproj -c Release -o /app/publish -p:SourceRevisionId=${BUILD_COMMIT}

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish ./
# ── RUN AS A NON-ROOT USER ──
# The aspnet:8.0 base ships an unprivileged `app` user and exports its uid as APP_UID (1654).
# The published app stays root-owned and is therefore read-only to the process: a compromised
# request handler cannot rewrite the binaries it runs from. The only directory the process needs
# to write under /app is storage/ (LocalDocumentStorage, Development/compose only — production
# documents go to S3); it is created here and handed to `app`, so a fresh named volume mounted on
# it inherits that ownership. The DataProtection key ring lives in PostgreSQL
# (PersistKeysToDbContext), and captured mail goes to /tmp, so nothing else needs write access.
# Port 8080 is above 1024, so binding it needs no capability.
RUN mkdir -p /app/storage && chown "$APP_UID" /app/storage
USER $APP_UID

# PORT is injected at runtime by Railway; 8080 is the Railway default
EXPOSE 8080
ENTRYPOINT ["dotnet", "Zayra.Api.dll"]
