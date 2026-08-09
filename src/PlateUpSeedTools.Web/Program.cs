using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Infrastructure.Cache;
using PlateUpSeedTools.Web.Contracts;
using PlateUpSeedTools.Web.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<CacheOptions>(builder.Configuration.GetSection(CacheOptions.SectionName));
builder.Services.AddSingleton<ICacheStore<LayoutResponse>, SqliteCacheStore<LayoutResponse>>();

var dataRoot = builder.Configuration["DataRoot"] ?? Path.Combine(AppContext.BaseDirectory, "data", "settings");
builder.Services.AddSingleton(new GameDataRepository(dataRoot));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapLayoutEndpoints();

// Clean-URL routing for the previewer: /preview, /preview/{setting}, and /preview/{setting}/{seed}
// all serve the same static preview.html - it reads setting/seed out of the path client-side (see
// wwwroot/js/app.js). Fallback endpoints only kick in when no static file/API route already
// matched, so this doesn't shadow /preview.html, /api/*, etc.
app.MapFallbackToFile("/preview/{*path}", "preview.html");

app.Run();
