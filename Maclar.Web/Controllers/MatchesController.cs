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

    public async Task<IActionResult> Index(string? search, CancellationToken cancellationToken)
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

        // Arama tamamen tarayıcı tarafında yapılıyor (Views/Matches/Index.cshtml).
        // Sunucu her zaman tüm listeyi gönderir; "search" sadece arama kutusunu doldurmak için
        // view'a aktarılır, sayfa açılınca istemci filtresi bu değerle bir kez çalışır.
        var viewModel = new MatchesIndexViewModel
        {
            Search = search,
            Matches = matches ?? Array.Empty<MatchDto>(),
            LastUpdatedAt = lastUpdatedAt,
            ErrorMessage = errorMessage,
        };

        return View(viewModel);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting(RateLimitPolicies.Refresh)]
    public async Task<IActionResult> Refresh(string? search, CancellationToken cancellationToken)
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

        return RedirectToAction(nameof(Index), new { search = string.IsNullOrWhiteSpace(search) ? null : search });
    }
}


