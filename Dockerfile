# syntax=docker/dockerfile:1

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the project file alone first, so layer caching survives source edits.
COPY src/Matterhorn/Matterhorn.csproj src/Matterhorn/
RUN dotnet restore src/Matterhorn/Matterhorn.csproj -p:NuGetAudit=false

COPY src/Matterhorn/ src/Matterhorn/
COPY contracts/ contracts/
RUN dotnet publish src/Matterhorn/Matterhorn.csproj -c Release -o /app -p:NuGetAudit=false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

ENV ASPNETCORE_URLS=http://+:80
EXPOSE 80

# aspnet images ship a non-root 'app' user.
USER app
ENTRYPOINT ["dotnet", "Matterhorn.dll"]
