FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ENV DOTNET_gcServer=0
ENV DOTNET_GCHeapCount=1
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
ENV DOTNET_NOLOGO=1
WORKDIR /src
COPY Zayra.Api.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish Zayra.Api.csproj -c Release -o /app/publish --no-restore \
    -p:RunAnalyzers=false -p:UseSharedCompilation=false -maxcpucount:1

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish ./
ENV DOTNET_gcServer=0
ENV DOTNET_GCConserveMemory=9
ENV DOTNET_EnableDiagnostics=0
EXPOSE 5117
ENTRYPOINT ["dotnet", "Zayra.Api.dll"]

