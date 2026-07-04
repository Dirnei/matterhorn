# syntax=docker/dockerfile:1

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the project file alone first, so layer caching survives source edits.
COPY src/Matter2Mqtt/Matter2Mqtt.csproj src/Matter2Mqtt/
RUN dotnet restore src/Matter2Mqtt/Matter2Mqtt.csproj -p:NuGetAudit=false

COPY src/Matter2Mqtt/ src/Matter2Mqtt/
RUN dotnet publish src/Matter2Mqtt/Matter2Mqtt.csproj -c Release -o /app -p:NuGetAudit=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

ENV ASPNETCORE_URLS=http://+:8090
EXPOSE 8090

# aspnet images ship a non-root 'app' user.
USER app
ENTRYPOINT ["dotnet", "Matter2Mqtt.dll"]
