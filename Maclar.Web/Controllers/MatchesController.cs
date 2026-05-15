using System.Globalization;
using Maclar.Web.Models;
using Maclar.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Maclar.Web.Controllers;

public class MatchesController : Controller
{
    private static readonly CultureInfo TurkishCulture = new("tr-TR");

    private readonly ICachedMatchService _cachedMatchService;
    private readonly IMatchScraperService _scraperService;
    private readonly CachedMatchService _cacheUpdater;

    public MatchesController(
        ICachedMatchService cachedMatchService,
        IMatchScraperService scraperService,
        CachedMatchService cacheUpdater)
    {
        _cachedMatchService = cachedMatchService;
        _scraperService = scraperService;
        _cacheUpdater = cacheUpdater;
    }

    public async Task<IActionResult> Index(string? search, CancellationToken cancellationToken)
    {
        var (matches, lastUpdatedAt, errorMessage) = await _cachedMatchService.GetCurrentMatchesAsync(cancellationToken);

        // Eğer cache boşsa veya hiç güncellenmemişse, kullanıcı boş sayfa görmesin diye
        // bir defaya mahsus doğrudan scraper'dan veri çekmeyi deniyoruz.
        if ((matches == null || matches.Count == 0) && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var fresh = await _scraperService.GetMatchesAsync(cancellationToken);
                if (fresh.Count > 0)
                {
                    matches = fresh;
                    lastUpdatedAt ??= DateTime.UtcNow;
                    _cacheUpdater.UpdateMatches(fresh, lastUpdatedAt.Value);
                    errorMessage = null;
                }
            }
            catch (Exception)
            {
                // Arka plandaki hata mesajını bozmadan sadece boş liste durumunda
                // kullanıcıya yansıtılmasını sağlıyoruz.
            }
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var tokens = ParseSearchTokens(search);
            matches = matches
                .Where(m => tokens.All(token => GetSearchableText(m).Contains(token, StringComparison.Ordinal)))
                .ToList();
        }

        var viewModel = new MatchesIndexViewModel
        {
            Search = search,
            Matches = matches,
            LastUpdatedAt = lastUpdatedAt,
            ErrorMessage = errorMessage,
        };

        return View(viewModel);
    }

    private static IReadOnlyList<string> ParseSearchTokens(string search)
    {
        return search
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.ToLower(TurkishCulture))
            .ToList();
    }

    private static string GetSearchableText(MatchDto match)
    {
        return string.Join(' ',
            match.Date?.ToString("dd.MM.yyyy") ?? match.RawDateText,
            match.Venue,
            match.HomeTeam,
            match.AwayTeam,
            match.League,
            match.CategoryCode,
            match.CategoryDescription)
            .ToLower(TurkishCulture);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Refresh(CancellationToken cancellationToken)
    {
        var matches = await _scraperService.GetMatchesAsync(cancellationToken);
        _cacheUpdater.UpdateMatches(matches, DateTime.UtcNow);

        return RedirectToAction(nameof(Index));
    }
}


