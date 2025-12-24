using Maclar.Web.Models;

namespace Maclar.Web.Services;

public interface ICachedMatchService
{
    Task<(IReadOnlyList<MatchDto> Matches, DateTime? LastUpdatedAt, string? ErrorMessage)> GetCurrentMatchesAsync(
        CancellationToken cancellationToken = default);
}


