FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
# Cache bust: increment when a stale registry cache must be forced to rebuild.
ARG CACHE_BUST=3
# ── BUILD-TIME MEMORY CONTAINMENT (fixes Render free-tier "ran out of memory >8GB") ──
# The project carries 38+ EF migrations, each Designer.cs embedding the full ~7,400-line model
# snapshot (~280k lines of near-duplicate model-builder code). Compiling that under the .NET
# default SERVER GC — which allocates one managed heap PER build-host core — exceeded 8 GB on
# Render's builder. Workstation GC (single heap) + disabling analyzers + a single MSBuild node
# keep peak build memory well under the limit. These settings affect the BUILD stage only;
# runtime GC is tuned separately in the final image below.
ENV DOTNET_gcServer=0
ENV DOTNET_GCHeapCount=1
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
ENV DOTNET_NOLOGO=1
WORKDIR /src
COPY backend-dotnet/Zayra.Api/Zayra.Api.csproj ./
RUN dotnet restore
COPY backend-dotnet/Zayra.Api/ ./
# ── DECISIVE OOM FIX: do not compile EF migrations into the RUNTIME image ──
# DO NOT REMOVE without first proving the build fits in Render's builder. This was reverted once
# (Aug 2026) on the theory that workstation GC alone bounded the build; the repo's own history
# disproves it — #40 "workstation GC + trimmed Docker build" came FIRST and was insufficient, which
# is why #41 landed this line and called it "decisive". Migrations have since grown 38 -> 59, each
# Designer ~23k lines, so the compile load is now LARGER than when GC tuning already failed.
# The Designer.cs files embed ~1.4M lines of duplicate model snapshots — the bulk of the compiler's
# memory. The RUNTIME app never applies migrations (Database__RunMigrationsOnStartup is "false";
# schema is applied by the Render pre-deploy `--migrate` and CI's `dotnet ef database update` — both
# SEPARATE builds that keep the migrations). EF builds its runtime model from ZayraDbContext
# .OnModelCreating, NOT from these snapshots, so dropping them here is safe and cuts peak build
# memory from >8GB (Server GC) to ~2GB.
#
# ── WHAT THIS USED TO BREAK, AND WHY IT NO LONGER DOES ──
# Deleting the directory left the image with ZERO migrations, and GetPendingMigrationsAsync() is
# "migrations in the assembly MINUS applied history" — so it returned 0 pending for every database,
# forever. /health/ready reported `ready` against a database missing twelve migrations, which is how
# a release was promoted onto an un-migrated schema. The old comment here called that "acceptable
# because CI applies migrations ahead of deploy"; the incident proved otherwise, and render.yaml's
# promotion guarantee was false for as long as this line existed unaccompanied.
#
# The manifest below is the fix: record the migration ids (a few KB of text) BEFORE deleting the
# classes, embed them, and let ProductionReadinessEvidence diff them against __EFMigrationsHistory.
# The gate becomes real without reintroducing the compile cost. `test -s` means the image can never
# ship without one — and if a manifest is somehow absent, the readiness check fails CLOSED rather
# than reporting a comfortable zero. Squashing the migrations is still tracked and would let this
# whole block go away.
RUN ls Migrations/*.cs \
      | grep -v '\.Designer\.cs$' \
      | grep -v 'ModelSnapshot\.cs$' \
      | xargs -n1 basename \
      | sed 's/\.cs$//' \
      | sort > Migrations.manifest \
    && test -s Migrations.manifest \
    && echo "Recorded $(wc -l < Migrations.manifest) migration ids into Migrations.manifest"
RUN rm -rf Migrations
RUN dotnet publish Zayra.Api.csproj -c Release -o /app/publish --no-restore \
    -p:RunAnalyzers=false -p:UseSharedCompilation=false -maxcpucount:1

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish ./

# Memory/GC tuning for 512 MB containers (Render free/starter tier).
# GCConserveMemory=9: most aggressive heap trimming after each GC cycle.
# EnableDiagnostics=0: skip diagnostic pipes/sockets (-3 MB baseline).
# GCHeapHardLimit: cap managed heap at 380 MB, leaving headroom for native/stack.
ENV DOTNET_GCConserveMemory=9
ENV DOTNET_EnableDiagnostics=0
ENV DOTNET_GCHeapHardLimit=398458880

EXPOSE 8080
ENTRYPOINT ["dotnet", "Zayra.Api.dll"]
