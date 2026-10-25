# KynexOne API image — the one API image definition (client installations, the docker-compose dev stack and CI).
#
#   docker build -t kynexone-api:<version> --build-arg VERSION=<MAJOR.MINOR.PATCH> \
#     --build-arg BUILD_COMMIT=$(git rev-parse HEAD) .
#
# The same image serves the API and runs the one-shot commands: --migrate, --setup-organisation,
# --import-licence, --licence-status and --maintenance (deploy/client/README.md).

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
# ── BUILD-TIME MEMORY CONTAINMENT ──
# The project carries many EF migrations whose Designer.cs files each embed the full model. Compiling them under
# the default SERVER GC (one managed heap per core) needs far more memory than a workstation GC with a single
# heap, no analyzers and one MSBuild node. These settings affect the BUILD stage only.
ENV DOTNET_gcServer=0
ENV DOTNET_GCHeapCount=1
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
ENV DOTNET_NOLOGO=1
WORKDIR /src
COPY backend-dotnet/Zayra.Api/Zayra.Api.csproj ./
RUN dotnet restore
COPY backend-dotnet/Zayra.Api/ ./

# The migration ids this build carries, embedded as Migrations.manifest. The migrations themselves stay compiled
# in — the image's own `--migrate` applies them — and /health/ready compares this list with
# __EFMigrationsHistory, so an image never serves traffic on a database it has not migrated. `test -s` means the
# image can never ship without one.
RUN ls Migrations/*.cs \
      | grep -v '\.Designer\.cs$' \
      | grep -v 'ModelSnapshot\.cs$' \
      | xargs -n1 basename \
      | sed 's/\.cs$//' \
      | sort > Migrations.manifest \
    && test -s Migrations.manifest \
    && echo "Recorded $(wc -l < Migrations.manifest) migration ids into Migrations.manifest"

# The release version (semantic, WP10) and commit, baked into the assembly and reported by /health/live.
ARG VERSION=0.0.0-dev
ARG BUILD_COMMIT=
# CI only (browser suites): also trust the per-run key scripts/ci/ci-licence.sh generated. Never set for a release.
ARG CI_LICENCE_KEY=false
RUN dotnet publish Zayra.Api.csproj -c Release -o /app/publish --no-restore \
    -p:Version=${VERSION} -p:SourceRevisionId=${BUILD_COMMIT} -p:KynexOneCiLicenceKey=${CI_LICENCE_KEY} \
    -p:RunAnalyzers=false -p:UseSharedCompilation=false -maxcpucount:1

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish ./

# Workstation GC and no diagnostic pipes. There is no managed-heap cap: memory is limited by the container
# (deploy/client/docker-compose.yml gives the API at least 2 GB), so a large payroll run is not starved.
ENV DOTNET_gcServer=0
ENV DOTNET_EnableDiagnostics=0
ENV ASPNETCORE_URLS=http://0.0.0.0:5117

# ── RUN AS A NON-ROOT USER ──
# The aspnet:8.0 base ships an unprivileged `app` user and exports its uid as APP_UID (1654).
# The published app stays root-owned and is therefore read-only to the process: a compromised
# request handler cannot rewrite the binaries it runs from. The only directories the process needs
# to write are storage/ (LocalDocumentStorage without Storage__LocalRoot: Development/compose) and
# /var/lib/kynexone/documents (the client kit's durable volume when Storage__Provider=local); both are
# created here and handed to `app`, so a fresh named volume mounted on them inherits that ownership. The
# DataProtection key ring lives in PostgreSQL (PersistKeysToDbContext), and captured mail goes to /tmp.
# Port 5117 is above 1024, so binding it needs no capability.
RUN mkdir -p /app/storage /var/lib/kynexone/documents \
    && chown "$APP_UID" /app/storage /var/lib/kynexone/documents
USER $APP_UID

EXPOSE 5117
ENTRYPOINT ["dotnet", "Zayra.Api.dll"]
