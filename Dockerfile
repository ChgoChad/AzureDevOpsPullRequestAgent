# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project file and restore as a distinct layer for caching
COPY src/ADOPullRequestAgent/ADOPullRequestAgent.csproj src/ADOPullRequestAgent/
RUN dotnet restore src/ADOPullRequestAgent/ADOPullRequestAgent.csproj

# Copy remaining source and publish
COPY src/ src/
RUN dotnet publish src/ADOPullRequestAgent/ADOPullRequestAgent.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

# Create writable work directory for the non-root user
# /sources is the mount point for the local repository clone
RUN mkdir -p /output /sources && \
    chown "$APP_UID":"$APP_UID" /output /sources && \
    chmod 755 /output /sources

# Run as non-root user for security
USER $APP_UID

ENTRYPOINT ["dotnet", "ADOPullRequestAgent.dll"]
