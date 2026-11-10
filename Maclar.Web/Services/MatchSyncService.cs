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
    private readonly ILogger<MatchSyncService> _logger;

    // Aynı anda birden fazla senkronizasyon çalışmasın (background + elle yenileme).
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public MatchSyncService(
        IMatchScraperService scraperService,
        MatchStore store,
        CachedMatchService cache,
        ILogger<MatchSyncService> logger)
    {
        _scraperService = scraperService;
        _store = store;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Siteye gitmeden, veritabanındaki maçları cache'e yükler (uygulama açılışında).
    /// </summary>
    public async Task LoadFromStoreAsync(CancellationToken cancellationToken = default)
    {
        var (matches, lastSeenAt) = await _store.GetAllAsync(cancellationToken);
        if (matches.Count > 0)
        {
            _cache.UpdateMatches(matches, lastSeenAt ?? DateTime.UtcNow);
        }

        _logger.LogInformation("Veritabanından {Count} maç yüklendi ({Path}).", matches.Count, _store.DatabasePath);
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
            var added = await _store.SaveAsync(scraped, now, cancellationToken);

            var (all, _) = await _store.GetAllAsync(cancellationToken);
            _cache.UpdateMatches(all, now);

            _logger.LogInformation(
                "Maçlar senkronize edildi. Sitede: {Scraped}, yeni eklenen: {Added}, veritabanında toplam: {Total}.",
                scraped.Count, added, all.Count);
        }
        finally
        {
            _syncLock.Release();
        }
    }
}
