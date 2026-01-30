# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY allstarr.sln .
COPY allstarr/allstarr.csproj allstarr/
COPY allstarr.Tests/allstarr.Tests.csproj allstarr.Tests/

RUN dotnet restore

COPY allstarr/ allstarr/
COPY allstarr.Tests/ allstarr.Tests/

RUN dotnet publish allstarr/allstarr.csproj -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# Install curl for health checks
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /app/downloads

COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "allstarr.dll"]
