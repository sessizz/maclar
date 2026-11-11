using Maclar.Web.Models;
using Maclar.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Maclar.Web.Controllers;

public class MatchesController : Controller
{
    private readonly ICachedMatchService _cachedMatchService;
    private readonly MatchSyncService _syncService;
    private readonly ILogger<MatchesController> _logger;

    public MatchesController(
        ICachedMatchService cachedMatchService,
        MatchSyncService syncService,
        ILogger<MatchesController> logger)
    {
        _cachedMatchService = cachedMatchService;
        _syncService = syncService;
        _logger = logger;
    }

    /// <summary>
    /// Son dört gün ve sonrasındaki maçlar (tarihi belirsiz olanlar en sonda).
    /// </summary>
    public async Task<IActionResult> Index(string? search, CancellationToken cancellationToken)
    {
        var (matches, lastUpdatedAt, errorMessage) = await GetMatchesAsync(cancellationToken);
        var today = IstanbulClock.Today;

        var recentAndUpcoming = matches.Where(m => m.Date is null || m.Date.Value.Date >= today.AddDays(-4)).ToList();

        var viewModel = BuildList(recentAndUpcoming, newestFirst: false);
        viewModel.Title = "İstanbul Voleybol Maç Takvimi";
        viewModel.Nav = "matches";
        viewModel.Search = search;
        viewModel.ShowDayFilter = true;
        viewModel.EmptyMessage = "Son dört gün ve sonrası için kayıtlı maç yok. Daha eski maçlar Geçmiş sekmesinde.";
        viewModel.LastUpdatedAt = lastUpdatedAt;
        viewModel.ErrorMessage = errorMessage;

        return View("List", viewModel);
    }

    /// <summary>
    /// Bugünden önceki maçlar, en yeni gün en üstte. Siteden silinen maçlar da burada kalır.
    /// </summary>
    [HttpGet("gecmis")]
    public async Task<IActionResult> Past(string? search, CancellationToken cancellationToken)
    {
        var (matches, lastUpdatedAt, errorMessage) = await GetMatchesAsync(cancellationToken);
        var today = IstanbulClock.Today;

        var past = matches.Where(m => m.Date.HasValue && m.Date.Value.Date < today).ToList();

        var viewModel = BuildList(past, newestFirst: true);
        viewModel.Title = "Geçmiş maçlar";
        viewModel.Nav = "past";
        viewModel.Search = search;
        viewModel.ShowDayFilter = false;
        viewModel.EmptyMessage = "Henüz geçmiş maç yok. Oynanan maçlar burada birikir.";
        viewModel.LastUpdatedAt = lastUpdatedAt;
        viewModel.ErrorMessage = errorMessage;

        return View("List", viewModel);
    }

    /// <summary>
    /// Salonlar ve her salondaki maç sayıları.
    /// </summary>
    [HttpGet("salonlar")]
    public async Task<IActionResult> Venues(CancellationToken cancellationToken)
    {
        var (matches, lastUpdatedAt, errorMessage) = await GetMatchesAsync(cancellationToken);
        var today = IstanbulClock.Today;
        var comparer = StringComparer.Create(MatchFormat.Turkish, ignoreCase: true);

        var venues = matches
            .Where(m => !string.IsNullOrWhiteSpace(m.Venue))
            .GroupBy(m => MatchFormat.Venue(m.Venue), comparer)
            .Select(g =>
            {
                var upcoming = g.Where(m => m.Date.HasValue && m.Date.Value.Date >= today).ToList();
                return new VenueRow(
                    Name: g.Key,
                    UpcomingCount: upcoming.Count,
                    TotalCount: g.Count(),
                    NextDate: upcoming.Select(m => m.Date).Min(),
                    LastDate: g.Where(m => m.Date.HasValue && m.Date.Value.Date < today).Select(m => m.Date).Max());
            })
            .OrderByDescending(v => v.UpcomingCount)
            .ThenBy(v => v.Name, comparer)
            .ToList();

        return View(new VenuesViewModel { Venues = venues, LastUpdatedAt = lastUpdatedAt, ErrorMessage = errorMessage });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitPolicies.Refresh)]
    public async Task<IActionResult> Refresh(string? search, string? returnUrl, CancellationToken cancellationToken)
    {
        try
        {
            await _syncService.SyncAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Mevcut cache'i bozmadan kullanıcıya bilgi veriyoruz.
            _logger.LogError(ex, "Elle yenileme sırasında maç verileri çekilemedi.");
            TempData["RefreshMessage"] = "Veriler şu an yenilenemedi. Son başarılı veriler gösteriliyor.";
        }

        // Kullanıcı hangi sayfadaysa (filtreleriyle birlikte) oraya dön; dış adreslere asla yönlendirme.
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return LocalRedirect(returnUrl);
        }

        return RedirectToAction(nameof(Index), new { search = string.IsNullOrWhiteSpace(search) ? null : search });
    }

    private async Task<(IReadOnlyList<MatchDto> Matches, DateTime? LastUpdatedAt, string? ErrorMessage)> GetMatchesAsync(
        CancellationToken cancellationToken)
    {
        var (matches, lastUpdatedAt, errorMessage) = await _cachedMatchService.GetCurrentMatchesAsync(cancellationToken);

        // Cache boşsa (veritabanı da boşsa, ör. ilk kurulum) kullanıcı boş sayfa görmesin diye
        // bir defaya mahsus hemen senkronizasyon deniyoruz.
        if ((matches == null || matches.Count == 0) && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _syncService.SyncAsync(cancellationToken);
                (matches, lastUpdatedAt, errorMessage) = await _cachedMatchService.GetCurrentMatchesAsync(cancellationToken);
            }
            catch (Exception)
            {
                // Arka plandaki hata mesajını bozmadan sadece boş liste durumunda
                // kullanıcıya yansıtılmasını sağlıyoruz.
            }
        }

        return (matches ?? Array.Empty<MatchDto>(), lastUpdatedAt, errorMessage);
    }

    /// <summary>
    /// Maçları güne göre gruplar. Gelen liste veritabanından tarih ve saate göre artan sırada gelir.
    /// Arama ve filtreler tarayıcıda yapılır (wwwroot/js/matches.js); sunucu her zaman tüm listeyi gönderir.
    /// </summary>
    private static MatchesIndexViewModel BuildList(IReadOnlyList<MatchDto> matches, bool newestFirst)
    {
        var groups = matches
            .GroupBy(m => m.Date?.Date)
            .Select(g => new MatchDayGroup(g.Key, g.ToList()));

        groups = newestFirst
            ? groups.OrderByDescending(g => g.Date ?? DateTime.MinValue)
            : groups.OrderBy(g => g.Date is null).ThenBy(g => g.Date);

        return new MatchesIndexViewModel
        {
            Groups = groups.ToList(),
            TotalCount = matches.Count,
            Leagues = matches
                .Select(m => MatchFormat.League(m.League))
                .Where(l => l.Length > 0)
                .Distinct()
                .OrderBy(l => l, StringComparer.Create(MatchFormat.Turkish, ignoreCase: true))
                .ToList(),
        };
    }
}
