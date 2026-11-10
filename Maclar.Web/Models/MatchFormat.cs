using System.Globalization;

namespace Maclar.Web.Models;

/// <summary>
/// View'larda kullanılan Türkçe gösterim yardımcıları.
/// </summary>
public static class MatchFormat
{
    public static readonly CultureInfo Turkish = new("tr-TR");

    // Kaynak sitede büyük harfle gelen salon adları başlık biçimine çevrilirken korunacak kısaltmalar.
    private static readonly Dictionary<string, string> Acronyms = new(StringComparer.Ordinal)
    {
        ["tvf"] = "TVF",
        ["hdı"] = "HDI",
        ["hdi"] = "HDI",
        ["ibb"] = "İBB",
        ["ito"] = "İTO",
        ["itü"] = "İTÜ",
    };

    /// <summary>"PAZARTESİ" gibi büyük harfli metni "Pazartesi" biçimine çevirir (Türkçe kurallarıyla).</summary>
    public static string TitleCase(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word =>
            {
                var lower = word.ToLower(Turkish);
                if (Acronyms.TryGetValue(lower, out var acronym))
                {
                    return acronym;
                }

                return char.ToUpper(lower[0], Turkish) + lower[1..];
            });

        return string.Join(' ', words);
    }

    /// <summary>Kaynak büyük harf yazıyorsa başlık biçimine çevir, karışık yazılmışsa olduğu gibi bırak.</summary>
    public static string Venue(string? venue)
    {
        if (string.IsNullOrWhiteSpace(venue))
        {
            return string.Empty;
        }

        return venue == venue.ToUpper(Turkish) ? TitleCase(venue) : venue.Trim();
    }

    /// <summary>"SULTANLAR" gibi uzun büyük harfli küme adlarını "Sultanlar" yapar; "2LK" gibi kodlara dokunmaz.</summary>
    public static string League(string? league)
    {
        if (string.IsNullOrWhiteSpace(league))
        {
            return string.Empty;
        }

        var isCode = league.Length <= 5 || league.Any(char.IsDigit);
        return isCode ? league : TitleCase(league);
    }

    public static string DayTitle(DateTime? date) =>
        date.HasValue ? date.Value.ToString("dddd, d MMMM", Turkish) : "Tarihi belirsiz";

    public static string ShortDay(DateTime date) => date.ToString("ddd d", Turkish);

    public static string Time(MatchDto match) => match.Time?.ToString(@"hh\:mm") ?? match.RawTimeText ?? string.Empty;

    /// <summary>Bitmiş maçta set sayısı ("3 - 1"), değilse boş.</summary>
    public static string Score(MatchDto match) =>
        match.IsFinished == true && match.HomeSets.HasValue && match.AwaySets.HasValue
            ? $"{match.HomeSets} - {match.AwaySets}"
            : string.Empty;

    /// <summary>İstemci tarafı arama için, tüm aranabilir alanların küçük harfli birleşimi.</summary>
    public static string SearchText(MatchDto match) =>
        string.Join(' ',
                match.Date?.ToString("dd.MM.yyyy") ?? match.RawDateText,
                match.Date?.ToString("dddd", Turkish),
                Venue(match.Venue),
                match.HomeTeam,
                match.AwayTeam,
                match.League,
                League(match.League),
                match.CategoryCode,
                match.CategoryDescription)
            .ToLower(Turkish);
}
