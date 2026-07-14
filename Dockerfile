# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0.301@sha256:ea8bde36c11b6e7eec2656d0e59101d4462f6bd630730f2c8201ed0572b295d5 AS build
WORKDIR /src

COPY allstarr.sln .
COPY Directory.Build.props .
COPY allstarr/allstarr.csproj allstarr/
COPY allstarr/AppVersion.cs allstarr/
COPY allstarr.Tests/allstarr.Tests.csproj allstarr.Tests/

RUN dotnet restore

COPY allstarr/ allstarr/
COPY allstarr.Tests/ allstarr.Tests/

RUN dotnet publish allstarr/allstarr.csproj -c Release -o /app/publish
COPY .env.example /app/publish/

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0.9@sha256:7644f992230d35cf230017189d4038c0ae0f7388b13f4f7ae1900a155bafb597
WORKDIR /app

# curl powers container health checks; PostgreSQL 18 pg_dump/pg_restore match the
# PostgreSQL 18 server pinned in docker-compose.yml.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl \
    && install -d /usr/share/postgresql-common/pgdg \
    && curl --fail --silent --show-error \
        --output /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc \
        https://www.postgresql.org/media/keys/ACCC4CF8.asc \
    && . /etc/os-release \
    && printf '%s\n' \
        'Types: deb' \
        'URIs: https://apt.postgresql.org/pub/repos/apt' \
        "Suites: ${VERSION_CODENAME}-pgdg" \
        "Architectures: $(dpkg --print-architecture)" \
        'Components: main' \
        'Signed-By: /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc' \
        > /etc/apt/sources.list.d/pgdg.sources \
    && apt-get update \
    && apt-get install -y --no-install-recommends postgresql-client-18 \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /app/downloads /app/kept /app/cache /app/state/backups

COPY --from=build /app/publish .

EXPOSE 8080 5275

ENTRYPOINT ["dotnet", "allstarr.dll"]
