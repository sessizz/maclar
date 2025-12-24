namespace Maclar.Web.Models;

public class MatchesIndexViewModel
{
    public string? Search { get; set; }

    public IReadOnlyList<MatchDto> Matches { get; set; } = Array.Empty<MatchDto>();

    /// <summary>
    /// Verilerin en son ne zaman güncellendiği (cache zamanı).
    /// </summary>
    public DateTime? LastUpdatedAt { get; set; }

    /// <summary>
    /// Arka planda veri çekerken hata olduysa kısa bir mesaj göstermek için.
    /// </summary>
    public string? ErrorMessage { get; set; }
}


