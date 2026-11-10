using Microsoft.Extensions.Hosting;

namespace Maclar.Web.Services;

public class MatchRefreshBackgroundService : BackgroundService
{
    private readonly MatchSyncService _syncService;
    private readonly CachedMatchService _cachedMatchService;
    private readonly TelegramNotifier _telegram;
    private readonly ILogger<MatchRefreshBackgroundService> _logger;
    private readonly TimeSpan _refreshInterval;
    private readonly bool _sendStartupMessage;

    public MatchRefreshBackgroundService(
        MatchSyncService syncService,
        CachedMatchService cachedMatchService,
        TelegramNotifier telegram,
        ILogger<MatchRefreshBackgroundService> logger,
        IConfiguration configuration)
    {
        _syncService = syncService;
        _cachedMatchService = cachedMatchService;
        _telegram = telegram;
        _logger = logger;

        // Açılışta Telegram'a durum mesajı (varsayılan açık). Kapatmak için: Telegram__StartupMessage=false
        _sendStartupMessage = configuration.GetValue<bool?>("Telegram:StartupMessage") ?? true;

        // appsettings ile ayarlanabilir; varsayılan 60 dakika
        var minutes = configuration.GetValue<int?>("Matches:RefreshMinutes") ?? 60;
        _refreshInterval = TimeSpan.FromMinutes(minutes);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MatchRefreshBackgroundService başlatıldı. Interval: {Interval} dakika", _refreshInterval.TotalMinutes);

        // Önce veritabanındakileri göster; site yavaş/erişilemez olsa da liste boş kalmasın.
        int? loadedCount = null;
        try
        {
            loadedCount = await _syncService.LoadFromStoreAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Maçlar veritabanından yüklenemedi.");
        }

        // Ardından hemen bir kere siteden çek
        var firstSyncSucceeded = await RefreshOnceAsync(stoppingToken);

        await SendStartupMessageAsync(loadedCount, firstSyncSucceeded, stoppingToken);

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

    private async Task<bool> RefreshOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Maç verileri çekiliyor...");
            await _syncService.SyncAsync(cancellationToken);
            return true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Maç verileri güncellenirken hata oluştu.");
            _cachedMatchService.SetError("Maç verileri güncellenirken bir hata oluştu.");
            return false;
        }
    }

    /// <summary>
    /// Telegram ayarlarının çalıştığını ve verinin kalıcı olduğunu her açılışta doğrulamak için durum mesajı.
    /// </summary>
    private async Task SendStartupMessageAsync(int? loadedCount, bool firstSyncSucceeded, CancellationToken cancellationToken)
    {
        if (!_sendStartupMessage || !_telegram.IsConfigured || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var (matches, _, _) = await _cachedMatchService.GetCurrentMatchesAsync(cancellationToken);

        var loadedText = loadedCount switch
        {
            null => "okunamadı ⚠️",
            0 => "0 maç (ilk kurulum değilse kalıcı depolama çalışmıyor olabilir ⚠️)",
            _ => $"{loadedCount} maç",
        };

        var syncText = firstSyncSucceeded
            ? $"başarılı, toplam {matches.Count} maç"
            : "başarısız ⚠️ (log'a bakın)";

        var message = "✅ <b>Maç takvimi servisi başladı</b>\n" +
                      $"Veritabanından yüklenen: {loadedText}\n" +
                      $"İlk senkronizasyon: {syncText}";

        await _telegram.SendAsync(message, cancellationToken);
    }
}
