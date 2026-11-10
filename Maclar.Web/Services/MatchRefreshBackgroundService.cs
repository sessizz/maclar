using Microsoft.Extensions.Hosting;

namespace Maclar.Web.Services;

public class MatchRefreshBackgroundService : BackgroundService
{
    private readonly MatchSyncService _syncService;
    private readonly CachedMatchService _cachedMatchService;
    private readonly ILogger<MatchRefreshBackgroundService> _logger;
    private readonly TimeSpan _refreshInterval;

    public MatchRefreshBackgroundService(
        MatchSyncService syncService,
        CachedMatchService cachedMatchService,
        ILogger<MatchRefreshBackgroundService> logger,
        IConfiguration configuration)
    {
        _syncService = syncService;
        _cachedMatchService = cachedMatchService;
        _logger = logger;

        // appsettings ile ayarlanabilir; varsayılan 60 dakika
        var minutes = configuration.GetValue<int?>("Matches:RefreshMinutes") ?? 60;
        _refreshInterval = TimeSpan.FromMinutes(minutes);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MatchRefreshBackgroundService başlatıldı. Interval: {Interval} dakika", _refreshInterval.TotalMinutes);

        // Önce veritabanındakileri göster; site yavaş/erişilemez olsa da liste boş kalmasın.
        try
        {
            await _syncService.LoadFromStoreAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Maçlar veritabanından yüklenemedi.");
        }

        // Ardından hemen bir kere siteden çek
        await RefreshOnceAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_refreshInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }

            await RefreshOnceAsync(stoppingToken);
        }
    }

    private async Task RefreshOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Maç verileri çekiliyor...");
            await _syncService.SyncAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Maç verileri güncellenirken hata oluştu.");
            _cachedMatchService.SetError("Maç verileri güncellenirken bir hata oluştu.");
        }
    }
}
