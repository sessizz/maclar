using Maclar.Web.Models;

namespace Maclar.Web.Services;

public class CachedMatchService : ICachedMatchService
{
    private readonly object _lock = new();
    private IReadOnlyList<MatchDto> _matches = Array.Empty<MatchDto>();
    private DateTime? _lastUpdatedAt;
    private string? _lastError;

    public Task<(IReadOnlyList<MatchDto> Matches, DateTime? LastUpdatedAt, string? ErrorMessage)> GetCurrentMatchesAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult((_matches, _lastUpdatedAt, _lastError));
        }
    }

    public void UpdateMatches(IReadOnlyList<MatchDto> matches, DateTime updatedAt)
    {
        lock (_lock)
        {
            _matches = matches;
            _lastUpdatedAt = updatedAt;
            _lastError = null;
        }
    }

    public void SetError(string errorMessage)
    {
        lock (_lock)
        {
            _lastError = errorMessage;
        }
    }
}


