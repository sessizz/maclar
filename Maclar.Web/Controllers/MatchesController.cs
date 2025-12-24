using Maclar.Web.Models;
using Maclar.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Maclar.Web.Controllers;

public class MatchesController : Controller
{
    private readonly ICachedMatchService _cachedMatchService;

    public MatchesController(ICachedMatchService cachedMatchService)
    {
        _cachedMatchService = cachedMatchService;
    }

    public async Task<IActionResult> Index(string? search, CancellationToken cancellationToken)
    {
        var (matches, lastUpdatedAt, errorMessage) = await _cachedMatchService.GetCurrentMatchesAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            matches = matches
                .Where(m =>
                    (!string.IsNullOrEmpty(m.HomeTeam) && m.HomeTeam.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(m.AwayTeam) && m.AwayTeam.Contains(term, StringComparison.OrdinalIgnoreCase)))
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
}


