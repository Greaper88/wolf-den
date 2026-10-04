namespace WolfLeash.Configuration;

public sealed class CatalogInitializer(AppCatalog catalog, ILogger<CatalogInitializer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await catalog.SeedAsync();
        if (catalog.SourceStatus is { } error) logger.LogWarning("{GowCatalogStatus}", error);
    }
}
