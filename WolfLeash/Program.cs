using Microsoft.EntityFrameworkCore;
using WolfLeash.Components.Classes;
using WolfLeash.Database;
using Api = WolfLeash.Components.Classes.Api;
using GamesOnWhales.Extensions;
using GamesOnWhales.SSE;
using Microsoft.AspNetCore.Components.Server.Circuits;
using WolfLeash.Components.Classes.DecompressionStrategy;
using App = WolfLeash.Components.App;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables("WOLF_");
// Accept both documented filesystem paths and the unix:// URI used by the image.
var wolfSocketPath = builder.Configuration["SOCKET_PATH"] ?? "/var/run/wolf/wolf.sock";
if (wolfSocketPath.StartsWith('/')) builder.Configuration["SOCKET_PATH"] = "unix://" + wolfSocketPath;

builder.WebHost.UseStaticWebAssets();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        if(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") != "Development")
            options.DetailedErrors = false;
        
        options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromSeconds(30);
    });

builder.Services.AddLogging(configure =>
{
    configure.AddConsole();
#if DEBUG
    configure.AddFilter("GamesOnWhales", LogLevel.Debug);
#endif
});

builder.Services.AddTransient<ColorGenerator>();

builder.Services.AddSingleton<ActiveUserManager>();
builder.Services.AddScoped<CircuitHandler, BlazorConnectionTracker>();

builder.Services.AddTransient<ISseEventHandler, PairSignalEventHandler>();
builder.Services.AddWolfApi<Api>();

builder.Services.AddSingleton<DefaultAppLoader>();
builder.Services.AddSingleton<WolfLeash.Configuration.DenStorage>();
builder.Services.AddSingleton<WolfLeash.Configuration.WolfConfigurationClient>();
builder.Services.AddSingleton<WolfLeash.Configuration.AppCatalog>();
builder.Services.AddHostedService<WolfLeash.Configuration.CatalogInitializer>();

builder.Services.AddDbContext<WolfLeashDbContext>();

builder.Services.AddBlazorBootstrap();
builder.Services.AddScoped<EventLogger>();

builder.Services.AddDecompressionStrategies();

builder.Services.Configure<CompatibilityToolsOptions>(
    builder.Configuration.GetSection(CompatibilityToolsOptions.SectionName));
builder.Services.AddSingleton<CompatibilityToolsService>();

var app = builder.Build();

app.Services.GetRequiredService<CompatibilityToolsService>().ProvisionTargets();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
    
    // Update Database
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<WolfLeashDbContext>();
    await db.Database.MigrateAsync();
}
else
{
    // Recreate Database for Development
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<WolfLeashDbContext>();
    //db.Database.EnsureDeleted();
    await db.Database.MigrateAsync();
}

//app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapGet("/api/icons", async (string path, WolfLeash.Configuration.WolfConfigurationClient wolf) =>
{
    try { return Results.File(await wolf.GetIconAsync(path), "image/png"); }
    catch { return Results.Redirect("/default-icon.png"); }
});
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
