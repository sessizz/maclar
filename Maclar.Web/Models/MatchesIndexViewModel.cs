namespace Maclar.Web.Models;

/// <summary>
/// Bir güne ait maçlar.
/// </summary>
public sealed record MatchDayGroup(DateTime? Date, IReadOnlyList<MatchDto> Matches);

/// <summary>
/// "Maçlar" (son dört gün ve sonrası) ve "Geçmiş" sayfalarının ortak modeli.
/// </summary>
public class MatchesIndexViewModel
{
    public string Title { get; set; } = "Maç Takvimi";

    /// <summary>Üst menüde seçili sekme: "matches" veya "past".</summary>
    public string Nav { get; set; } = "matches";

    public string? Search { get; set; }

    public IReadOnlyList<MatchDayGroup> Groups { get; set; } = Array.Empty<MatchDayGroup>();

    public int TotalCount { get; set; }

    /// <summary>Küme filtresindeki seçenekler.</summary>
    public IReadOnlyList<string> Leagues { get; set; } = Array.Empty<string>();

    /// <summary>Gün düğmeleri gösterilsin mi (Geçmiş'te gün çok olacağı için kapalı).</summary>
    public bool ShowDayFilter { get; set; }

    public string EmptyMessage { get; set; } = "Gösterilecek maç yok.";

    /// <summary>
    /// Verilerin en son ne zaman güncellendiği (cache zamanı, UTC).
    /// </summary>
    public DateTime? LastUpdatedAt { get; set; }

    /// <summary>
    /// Arka planda veri çekerken hata olduysa kısa bir mesaj göstermek için.
    /// </summary>
    public string? ErrorMessage { get; set; }
}

public sealed record VenueRow(string Name, int UpcomingCount, int TotalCount, DateTime? NextDate, DateTime? LastDate);

public class VenuesViewModel
{
    public IReadOnlyList<VenueRow> Venues { get; set; } = Array.Empty<VenueRow>();

    public DateTime? LastUpdatedAt { get; set; }

    public string? ErrorMessage { get; set; }
}
