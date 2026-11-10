namespace Maclar.Web.Services;

/// <summary>
/// Arşivi (PuanDurumu) belirli aralıklarla tarar: oynanmış maçların skorlarını ve
/// takvim sayfasında görünmeyen maçları veritabanına ekler.
/// Son tarama zamanı veritabanından okunur; böylece her deploy'da baştan taranmaz.
/// </summary>
public class ArchiveRefreshBackgroundService : BackgroundService
{
    private readonly ArchiveScraperService _scraper;
    private readonly MatchSyncService _syncService;
    private readonly MatchStore _store;
    private readonly TelegramNotifier _telegram;
    private readonly ILogger<ArchiveRefreshBackgroundService> _logger;
    private readonly bool _notify;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _startupDelay;

    public ArchiveRefreshBackgroundService(
        ArchiveScraperService scraper,
        MatchSyncService syncService,
        MatchStore store,
        TelegramNotifier telegram,
        ILogger<ArchiveRefreshBackgroundService> logger,
        IConfiguration configuration)
    {
        _scraper = scraper;
        _syncService = syncService;
        _store = store;
        _telegram = telegram;
        _logger = logger;
        // Tarama sonucunu Telegram'a bildir (varsayılan açık). Kapatmak için: Telegram__ArchiveMessages=false
        _notify = configuration.GetValue<bool?>("Telegram:ArchiveMessages") ?? true;
        _enabled = configuration.GetValue<bool?>("Archive:Enabled") ?? true;
        _interval = TimeSpan.FromHours(configuration.GetValue<double?>("Archive:RefreshHours") ?? 24);
        // Açılışta takvim senkronizasyonu ile aynı anda siteye yüklenmesin.
        _startupDelay = TimeSpan.FromSeconds(configuration.GetValue<int?>("Archive:StartupDelaySeconds") ?? 60);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("Arşiv taraması kapalı (Archive:Enabled=false).");
            return;
        }

        try
        {
            await Task.Delay(_startupDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var last = await _store.GetLastArchiveSeenAtAsync(stoppingToken);
                var due = last is null || DateTime.UtcNow - last.Value >= _interval;

                if (due)
                {
                    if (!await RunOnceAsync(stoppingToken))
                    {
                        // Başarısız taramayı hemen tekrarlama; siteye yük bindirmeyelim.
                        await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
                        continue;
                    }

                    last = DateTime.UtcNow;
                }

                // Bir sonraki taramaya kadar bekle (en az 10 dakika, en fazla aralık kadar).
                var wait = (last ?? DateTime.UtcNow) + _interval - DateTime.UtcNow;
                if (wait < TimeSpan.FromMinutes(10))
                {
                    wait = TimeSpan.FromMinutes(10);
                }

                await Task.Delay(wait, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Uygulama kapanıyor.
        }
    }

    private async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Arşiv taraması başladı.");
            var result = await _scraper.GetMatchesAsync(cancellationToken);
            if (result.Matches.Count == 0)
            {
                // Site yapısı değişmiş ya da erişilemiyor olabilir; boş sonucu kaydetme.
                _logger.LogWarning("Arşiv taramasında hiç maç bulunamadı ({Pages} sayfa). Kayıt yapılmadı.", result.Pages);
                await NotifyAsync(
                    $"⚠️ <b>Arşiv taraması</b>: {result.Pages} sayfa gezildi ama hiç maç bulunamadı. " +
                    "Site yapısı değişmiş olabilir; kayıt yapılmadı, 1 saat sonra tekrar denenecek.", cancellationToken);
                return false;
            }

            var saved = await _syncService.SaveArchiveAsync(result.Matches, cancellationToken);
            await NotifyAsync(
                "📚 <b>Arşiv taraması bitti</b>\n" +
                $"{result.Pages} yarışma sayfası, {result.Matches.Count} tarihli maç.\n" +
                $"Yeni eklenen: {saved.Added.Count}, zaten olan: {saved.Updated}, skoru yeni girilen: {saved.NewlyFinished.Count}." +
                (saved.Replaced > 0 ? $"\nTarihi değişmiş {saved.Replaced} maçın Excel'deki eski tarihli kaydı silindi." : string.Empty),
                cancellationToken);
            return true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Arşiv taraması başarısız oldu; bir saat sonra tekrar denenecek.");
            await NotifyAsync(
                $"⚠️ <b>Arşiv taraması başarısız</b>: {TelegramNotifier.Escape(ex.Message)}\n1 saat sonra tekrar denenecek.",
                cancellationToken);
            return false;
        }
    }

    private async Task NotifyAsync(string message, CancellationToken cancellationToken)
    {
        if (_notify && _telegram.IsConfigured)
        {
            await _telegram.SendAsync(message, cancellationToken);
        }
    }
}
