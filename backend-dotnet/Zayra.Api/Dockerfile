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
# PORT is injected at runtime by Railway; 8080 is the Railway default
EXPOSE 8080
ENTRYPOINT ["dotnet", "Zayra.Api.dll"]
