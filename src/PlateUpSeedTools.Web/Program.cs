using Microsoft.Extensions.Options;
using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Infrastructure.Cache;
using PlateUpSeedTools.Web.Contracts;
using PlateUpSeedTools.Web.Endpoints;
using PlateUpSeedTools.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<CacheOptions>(builder.Configuration.GetSection(CacheOptions.SectionName));
builder.Services.AddSingleton<ICacheStore<LayoutResponse>, SqliteCacheStore<LayoutResponse>>();
builder.Services.AddSingleton<ICacheStore<CachedSearchResult>, SqliteCacheStore<CachedSearchResult>>();
builder.Services.AddSingleton<ICacheStore<ExplorerPreload>, SqliteCacheStore<ExplorerPreload>>();
builder.Services.AddSingleton(sp => new CacheAdmin(sp.GetRequiredService<IOptions<CacheOptions>>().Value));

builder.Services.Configure<WebPushOptions>(builder.Configuration.GetSection(WebPushOptions.SectionName));
builder.Services.AddSingleton<PushNotificationSender>();

var dataRoot = builder.Configuration["DataRoot"] ?? Path.Combine(AppContext.BaseDirectory, "data", "settings");
builder.Services.AddSingleton(new GameDataRepository(dataRoot));
builder.Services.AddSingleton<SearchJobManager>();
builder.Services.AddHostedService<SearchJobHeartbeatMonitor>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapLayoutEndpoints();
app.MapSearchEndpoints();
app.MapExplorerEndpoints();
app.MapPushEndpoints();

// Clean-URL routing for the previewer: /preview, /preview/{setting}, and /preview/{setting}/{seed}
// all serve the same static preview.html - it reads setting/seed out of the path client-side (see
// wwwroot/js/app.js). Fallback endpoints only kick in when no static file/API route already
// matched, so this doesn't shadow /preview.html, /api/*, etc.
app.MapFallbackToFile("/preview/{*path}", "preview.html");

// Same pattern for the search page: /search and /search/... serve the static search.html, which
// reads no path state itself - the form starts empty each visit UNLESS the URL also carries a
// #<hash> fragment (/search/cached#hash - also used for a still-running notify-enabled job's
// "live" link, not just a finished one's), which search.js reads and resolves client-side via
// GET /api/search/link/{hash}; fragments never reach the server, so this route doesn't need to
// know about that case at all, it's still just "serve search.html for anything under /search".
app.MapFallbackToFile("/search/{*path}", "search.html");

// Same pattern for the explorer page: /explorer, /explorer/{setting}, /explorer/{setting}/{seed},
// and /explorer/{setting}/{seed}/{hash} all serve the static explorer.html, which prefills
// setting+seed (and, if a hash is present, the full dish/heat/tier/franchise setup + explored path
// via GET /api/explorer/preload/{hash}) client-side (js/explorer.js) - everything else starts
// blank each visit with no hash, same rationale as /search.
app.MapFallbackToFile("/explorer/{*path}", "explorer.html");

app.Run();
