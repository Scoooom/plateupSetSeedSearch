FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/PlateUpSeedTools.Web/PlateUpSeedTools.Web.csproj -c Release -o /app/web
RUN dotnet publish src/PlateUpSeedTools.Cli/PlateUpSeedTools.Cli.csproj -c Release -o /app/cli

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS web
WORKDIR /app
COPY --from=build /app/web .
COPY data/settings ./data/settings
ENV CacheStore__SqlitePath=/data/cache.db
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
VOLUME /data
ENTRYPOINT ["dotnet", "PlateUpSeedTools.Web.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS cli
WORKDIR /app
COPY --from=build /app/cli .
ENV CacheStore__SqlitePath=/data/cache.db
VOLUME /data
ENTRYPOINT ["dotnet", "PlateUpSeedTools.Cli.dll"]
