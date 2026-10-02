FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ENV DOTNET_gcServer=0
ENV DOTNET_GCHeapCount=1
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
ENV DOTNET_NOLOGO=1
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
ENV DOTNET_gcServer=0
ENV DOTNET_GCConserveMemory=9
ENV DOTNET_EnableDiagnostics=0
EXPOSE 5117
ENTRYPOINT ["dotnet", "Zayra.Api.dll"]

