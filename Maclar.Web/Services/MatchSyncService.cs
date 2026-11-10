using Maclar.Web.Models;

namespace Maclar.Web.Services;

/// <summary>
/// Siteden maçları çeker, veritabanına yazar ve bellek içi cache'i veritabanındaki
/// tüm maçlarla (eski maçlar dahil) yeniler. Background servis, elle yenileme ve
/// boş cache durumu bu sınıfı kullanır.
/// </summary>
public class MatchSyncService
{
    private readonly IMatchScraperService _scraperService;
    private readonly MatchStore _store;
    private readonly CachedMatchService _cache;
    private readonly MatchChangeNotifier _notifier;
    private readonly ILogger<MatchSyncService> _logger;

    // Aynı anda birden fazla senkronizasyon çalışmasın (background + elle yenileme).
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public MatchSyncService(
        IMatchScraperService scraperService,
        MatchStore store,
        CachedMatchService cache,
        MatchChangeNotifier notifier,
        ILogger<MatchSyncService> logger)
    {
        _scraperService = scraperService;
        _store = store;
        _cache = cache;
        _notifier = notifier;
        _logger = logger;
    }

    /// <summary>
    /// Arşivden (PuanDurumu) gelen maçları veritabanına yazar ve cache'i yeniler.
    /// Tarama dakikalar sürdüğü için kilit dışında yapılır; sadece kayıt ve cache yenileme kilitli.
    /// Değişiklik bildirimi gönderilmez.
    /// </summary>
    public Task<ExternalSaveResult> SaveArchiveAsync(IReadOnlyList<MatchDto> matches, CancellationToken cancellationToken = default) =>
        SaveExternalAsync("Arşiv", matches, markArchiveSeen: true, cancellationToken);

    /// <summary>
    /// Haftalık Excel programından gelen maçları kaydeder (Telegram üzerinden gönderilen dosya).
    /// </summary>
    public Task<ExternalSaveResult> SaveProgramAsync(IReadOnlyList<MatchDto> matches, CancellationToken cancellationToken = default) =>
        SaveExternalAsync("Excel programı", matches, markArchiveSeen: false, cancellationToken);

    private async Task<ExternalSaveResult> SaveExternalAsync(
        string source, IReadOnlyList<MatchDto> matches, bool markArchiveSeen, CancellationToken cancellationToken)
    {
        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            var result = await _store.SaveExternalAsync(matches, DateTime.UtcNow, markArchiveSeen, cancellationToken);
            var (all, _) = await _store.GetAllAsync(cancellationToken);
            _cache.ReplaceMatches(all);

            _logger.LogInformation(
                "{Source} kaydedildi. Yeni: {Added}, güncellenen: {Updated}, yeni skor: {Finished}, veritabanında toplam: {Total}.",
                source, result.Added.Count, result.Updated, result.NewlyFinished.Count, all.Count);
            return result;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Siteye gitmeden, veritabanındaki maçları cache'e yükler (uygulama açılışında).
    /// </summary>
    /// <returns>Yüklenen maç sayısı.</returns>
    public async Task<int> LoadFromStoreAsync(CancellationToken cancellationToken = default)
    {
        var (matches, lastSeenAt) = await _store.GetAllAsync(cancellationToken);
        if (matches.Count > 0)
        {
            _cache.UpdateMatches(matches, lastSeenAt ?? DateTime.UtcNow);
        }

        _logger.LogInformation("Veritabanından {Count} maç yüklendi ({Path}).", matches.Count, _store.DatabasePath);
        return matches.Count;
    }

    /// <summary>
    /// Siteden çek, veritabanına kaydet, cache'i yenile. Hata olursa exception fırlatır;
    /// bu durumda veritabanı ve cache değişmeden kalır.
    /// </summary>
    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            var scraped = await _scraperService.GetMatchesAsync(cancellationToken);
            var now = DateTime.UtcNow;
            var result = await _store.SaveAsync(scraped, now, cancellationToken);

            var (all, _) = await _store.GetAllAsync(cancellationToken);
            _cache.UpdateMatches(all, now);

            _logger.LogInformation(
                "Maçlar senkronize edildi. Sitede: {Scraped}, yeni: {Added}, saat/salon değişen: {Changed}, " +
                "siteden kaybolan: {Disappeared}, veritabanında toplam: {Total}.",
                scraped.Count, result.Added.Count, result.Changed.Count, result.Disappeared.Count, all.Count);

            // Bildirim hatası senkronizasyonu bozmasın.
            try
            {
                await _notifier.NotifyAsync(result, scraped.Count, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Değişiklik bildirimi hazırlanırken hata oluştu.");
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }
}
