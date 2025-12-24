using Maclar.Web.Models;

namespace Maclar.Web.Services;

public interface IMatchScraperService
{
    Task<IReadOnlyList<MatchDto>> GetMatchesAsync(CancellationToken cancellationToken = default);
}


