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

app.Run();
