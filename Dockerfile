# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so dependency layers are cached until a csproj changes.
COPY global.json Directory.Build.props OpsFlow.slnx ./
COPY src/OpsFlow.Core/OpsFlow.Core.csproj src/OpsFlow.Core/
COPY src/OpsFlow.Infrastructure/OpsFlow.Infrastructure.csproj src/OpsFlow.Infrastructure/
COPY src/OpsFlow.API/OpsFlow.API.csproj src/OpsFlow.API/
COPY src/OpsFlow.Tests/OpsFlow.Tests.csproj src/OpsFlow.Tests/
RUN dotnet restore

COPY src/ src/
RUN dotnet publish src/OpsFlow.API -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# Run as the unprivileged user that ships with the base image.
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
ENTRYPOINT ["dotnet", "OpsFlow.API.dll"]

