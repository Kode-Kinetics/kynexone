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
# Keep compiled EF migrations in the runtime assembly. /health/ready uses EF's migration metadata
# as the independent traffic gate; stripping these sources made every deployed image report zero
# pending migrations even when the database was behind. Build memory remains bounded by workstation
# GC, a single MSBuild node, disabled analyzers, and disabled shared compilation below.
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
