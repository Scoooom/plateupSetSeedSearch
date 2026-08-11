using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Infrastructure.Cache;
using PlateUpSeedTools.Web.Contracts;
using PlateUpSeedTools.Web.Endpoints;
using PlateUpSeedTools.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<CacheOptions>(builder.Configuration.GetSection(CacheOptions.SectionName));
builder.Services.AddSingleton<ICacheStore<LayoutResponse>, SqliteCacheStore<LayoutResponse>>();

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

// Clean-URL routing for the previewer: /preview, /preview/{setting}, and /preview/{setting}/{seed}
// all serve the same static preview.html - it reads setting/seed out of the path client-side (see
// wwwroot/js/app.js). Fallback endpoints only kick in when no static file/API route already
// matched, so this doesn't shadow /preview.html, /api/*, etc.
app.MapFallbackToFile("/preview/{*path}", "preview.html");

// Same pattern for the search page: /search and /search/... serve the static search.html, which
// reads no path state itself (the form starts empty each visit - unlike /preview, a search isn't
// meant to be hotlinked to a specific in-progress job).
app.MapFallbackToFile("/search/{*path}", "search.html");

// Same pattern for the explorer page: /explorer, /explorer/{setting}, and /explorer/{setting}/{seed}
// all serve the static explorer.html, which prefills only setting+seed from the path client-side
// (js/explorer.js) - everything else (dish/heat/tier/franchise cards, the in-progress branch tree)
// starts blank each visit, same rationale as /search.
app.MapFallbackToFile("/explorer/{*path}", "explorer.html");

app.Run();
