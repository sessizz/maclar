namespace Maclar.Web.Models;

public class MatchDto
{
    public DateTime? Date { get; set; }
    public string? RawDateText { get; set; }

    public string? Venue { get; set; }

    public TimeSpan? Time { get; set; }
    public string? RawTimeText { get; set; }

    public string? HomeTeam { get; set; }
    public string? AwayTeam { get; set; }

    public string? SetScores { get; set; }

    /// <summary>
    /// Sitedeki "Küme" sütunundaki küme bilgisi.
    /// </summary>
    public string? League { get; set; }

    /// <summary>
    /// Sitedeki "Ktg" sütunundaki kategori kodu (örn. MdK, YE, MdE).
    /// </summary>
    public string? CategoryCode { get; set; }

    /// <summary>
    /// İstenirse UI tarafında gösterilebilecek açıklama (örn. Midi Kız, Yıldız Erkek).
    /// Şimdilik boş bırakılabilir, ileride bir sözlük ile doldurulabilir.
    /// </summary>
    public string? CategoryDescription { get; set; }

    public int? HomeSets { get; set; }
    public int? AwaySets { get; set; }

    public bool? IsFinished { get; set; }
}


