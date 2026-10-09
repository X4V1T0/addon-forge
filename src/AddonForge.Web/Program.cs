using AddonForge.Core.Abstractions;
using AddonForge.Core.Orchestration;
using AddonForge.Core.Versioning;
using AddonForge.Infrastructure.Data;
using AddonForge.Infrastructure.Git;
using AddonForge.Infrastructure.Llm;
using AddonForge.Infrastructure.Publishing;
using AddonForge.Infrastructure.Registry;
using AddonForge.Web.Options;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Home Assistant writes the addon's configured `options:` (llm/scheduler/github,
// matching config.yaml) to /data/options.json — we don't use bashio/s6, so we
// read it ourselves. Outside a HAOS container this file simply doesn't exist,
// and appsettings.json's defaults apply instead.
builder.Configuration.AddJsonFile("/data/options.json", optional: true, reloadOnChange: true);

// Every addon gets /data as persistent storage automatically (no `map:` entry
// needed). Falling back to a local ./data folder makes `dotnet run` outside
// HAOS work out of the box for development.
var dataRoot = Directory.Exists("/data") ? "/data" : Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataRoot);

// /addons only exists when this addon has been installed with the `addons:rw`
// map. Outside HAOS, fall back to a local folder so "publish" is still
// exercisable end-to-end during development.
var publishedRoot = Directory.Exists("/addons") ? "/addons" : Path.Combine(dataRoot, "addons-dev");
Directory.CreateDirectory(publishedRoot);

builder.Services.AddDbContext<AddonForgeDbContext>(o =>
    o.UseSqlite($"Data Source={Path.Combine(dataRoot, "addon-forge.db")}"));

builder.Services.Configure<OpenAiCompatibleOptions>(builder.Configuration.GetSection("llm"));
builder.Services.Configure<SchedulerOptions>(builder.Configuration.GetSection("scheduler"));
builder.Services.Configure<GitOptions>(builder.Configuration.GetSection("github"));
builder.Services.Configure<FileSystemAddonFileWriterOptions>(o =>
{
    o.StagingRoot = Path.Combine(dataRoot, "staging");
    o.PublishedRoot = publishedRoot;
    o.IconsRoot = Path.Combine(dataRoot, "icons");
});

builder.Services.AddSingleton<IGitClient, GitCliClient>();
builder.Services.AddSingleton<IAddonFileWriter, FileSystemAddonFileWriter>();
builder.Services.AddHttpClient<ILlmClient, OpenAiCompatibleClient>();
builder.Services.AddHttpClient<IContainerRegistryClient, DockerRegistryClient>();
builder.Services.AddScoped<IManagedAddonRepository, EfManagedAddonRepository>();
builder.Services.AddSingleton<AddonGenerationOrchestrator>();
builder.Services.AddSingleton<VersionCheckService>();
builder.Services.AddScoped<AddonUpdateService>();
builder.Services.AddHostedService<AddonForge.Web.Services.VersionCheckScheduler>();

builder.Services.AddRazorPages();

// Home Assistant's ingress proxy strips its `/api/hassio_ingress/<token>`
// prefix and forwards to the addon with a bare path (see the PathBase
// middleware below). Any link/redirect this app generates back to the root
// page ("/Index") then collapses to exactly that prefix with no trailing
// slash — and the proxy 404s on that, since it expects the prefix boundary
// to end in "/". AppendTrailingSlash makes every generated URL end in "/",
// which fixes that without touching each redirect/link individually.
builder.Services.Configure<RouteOptions>(o => o.AppendTrailingSlash = true);

// CookieBuilder defaults an unset Path to the request's PathBase, which means
// the antiforgery/TempData cookies set while PathBase="/api/hassio_ingress/<token>"
// only get sent back by the browser for requests under that exact prefix. Pinning
// Path to "/" avoids any chance of that scoping mismatching a real request.
builder.Services.Configure<AntiforgeryOptions>(o => o.Cookie.Path = "/");
builder.Services.Configure<CookieTempDataProviderOptions>(o => o.Cookie.Path = "/");

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AddonForgeDbContext>();
    db.Database.Migrate();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

// Home Assistant's ingress proxy serves this addon under a per-session,
// randomly generated path (e.g. /api/hassio_ingress/<token>/) rather than at
// the root, and forwards it as X-Ingress-Path. There is no HTTPS to redirect
// to either — ingress talks to the addon over plain HTTP on the internal
// Docker network.
app.Use(async (context, next) =>
{
    if (context.Request.Headers.TryGetValue("X-Ingress-Path", out var ingressPath) &&
        !string.IsNullOrEmpty(ingressPath))
    {
        context.Request.PathBase = ingressPath.ToString();
    }

    await next();
});

app.UseRouting();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
