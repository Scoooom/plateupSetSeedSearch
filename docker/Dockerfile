FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/PlateUpSeedTools.Web/PlateUpSeedTools.Web.csproj -c Release -o /app/web
RUN dotnet publish src/PlateUpSeedTools.Cli/PlateUpSeedTools.Cli.csproj -c Release -o /app/cli

# Minifies wwwroot/js + wwwroot/css for the shipped image only - source in the repo stays
# untouched/readable (still "no build step" for local editing/dev - docker compose still rebuilds
# through this same Dockerfile though, so a local `docker compose up` also serves minified assets,
# not just an actual prod deploy). Goal is obscuring the source, not a perf win (the files are
# small - see project-history.md's minification note if one gets added) - esbuild --minify strips
# comments/whitespace and renames LOCAL (function-scope) variables, but deliberately does NOT
# rename top-level function/const names: search.js/explorer.js/home.js/app.js are plain
# `<script src>` tags (not ES modules) that rely on shared.js's top-level declarations as implicit
# globals (see e.g. search.js's "VALID_SEED/fetchSettings/populateSettingSelect come from
# shared.js" comment) - per-file minification without bundling is what keeps that safe; renaming
# top-level identifiers independently per file would break those cross-file references.
FROM node:22-alpine AS assets
WORKDIR /assets
COPY src/PlateUpSeedTools.Web/wwwroot/js ./js
COPY src/PlateUpSeedTools.Web/wwwroot/css ./css
RUN npx --yes esbuild js/*.js --minify --outdir=js --allow-overwrite \
 && npx --yes esbuild css/*.css --minify --outdir=css --allow-overwrite

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS web
# libfontconfig1: SkiaSharp's native lib needs it on Linux even for the small amount of text
# rendering LayoutImageRenderer does (the abbreviated-text fallback for appliances with no icon).
RUN apt-get update && apt-get install -y --no-install-recommends libfontconfig1 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/web .
COPY --from=assets /assets/js ./wwwroot/js
COPY --from=assets /assets/css ./wwwroot/css
COPY data/settings ./data/settings
ENV CacheStore__SqlitePath=/data/cache.db
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
VOLUME /data
ENTRYPOINT ["dotnet", "PlateUpSeedTools.Web.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS cli
WORKDIR /app
COPY --from=build /app/cli .
COPY data/settings ./data/settings
ENV CacheStore__SqlitePath=/data/cache.db
VOLUME /data
ENTRYPOINT ["dotnet", "PlateUpSeedTools.Cli.dll"]
