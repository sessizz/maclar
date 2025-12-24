using Maclar.Web.Models;
using Microsoft.Extensions.Hosting;

namespace Maclar.Web.Services;

public class MatchRefreshBackgroundService : BackgroundService
{
    private readonly IMatchScraperService _scraperService;
    private readonly CachedMatchService _cachedMatchService;
    private readonly ILogger<MatchRefreshBackgroundService> _logger;
    private readonly TimeSpan _refreshInterval;

    public MatchRefreshBackgroundService(
        IMatchScraperService scraperService,
        CachedMatchService cachedMatchService,
        ILogger<MatchRefreshBackgroundService> logger,
        IConfiguration configuration)
    {
        _scraperService = scraperService;
        _cachedMatchService = cachedMatchService;
        _logger = logger;

        // appsettings ile ayarlanabilir; varsayılan 60 dakika
        var minutes = configuration.GetValue<int?>("Matches:RefreshMinutes") ?? 60;
        _refreshInterval = TimeSpan.FromMinutes(minutes);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MatchRefreshBackgroundService başlatıldı. Interval: {Interval} dakika", _refreshInterval.TotalMinutes);

        // Uygulama ilk açıldığında hemen bir kere veri çekmeye çalış
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
            IReadOnlyList<MatchDto> matches = await _scraperService.GetMatchesAsync(cancellationToken);
            _cachedMatchService.UpdateMatches(matches, DateTime.UtcNow);
            _logger.LogInformation("Maç verileri başarıyla güncellendi. Toplam {Count} kayıt.", matches.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Maç verileri güncellenirken hata oluştu.");
            _cachedMatchService.SetError("Maç verileri güncellenirken bir hata oluştu.");
        }
    }
}


