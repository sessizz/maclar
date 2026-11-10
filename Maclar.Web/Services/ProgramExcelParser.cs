using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Maclar.Web.Models;

namespace Maclar.Web.Services;

/// <summary>
/// Gençlik ve Spor İl Müdürlüğü'nün haftalık "X. HAFTA PROGRAM.xlsx" dosyasını okur.
///
/// Dosya düzeni: gün + salon başlık satırı ("26 Ekim 2026 Pazartesi" ... "Tozkoparan Spor Salonu"),
/// altında maç satırları: saat | ev sahibi | misafir | küme | (grup, örn. "4B C").
/// Skor yoktur; sadece program. "S.KOM.", "GDS" ve duyuru satırları yok sayılır.
/// </summary>
public static partial class ProgramExcelParser
{
    private static readonly CultureInfo Turkish = new("tr-TR");

    private static readonly Dictionary<string, (string Code, string Name)> YouthCategories = new(StringComparer.Ordinal)
    {
        ["GE"] = ("GE", "Genç Erkek"),
        ["GK"] = ("GK", "Genç Kız"),
        ["KE"] = ("KE", "Küçük Erkek"),
        ["KK"] = ("KK", "Küçük Kız"),
        ["YE"] = ("YE", "Yıldız Erkek"),
        ["YK"] = ("YK", "Yıldız Kız"),
        ["MDE"] = ("MdE", "Midi Erkek"),
        ["MDK"] = ("MdK", "Midi Kız"),
    };

    public sealed record ParseResult(IReadOnlyList<MatchDto> Matches, IReadOnlyList<string> Warnings, string? Period);

    public static ParseResult Parse(Stream stream)
    {
        using var workbook = new XLWorkbook(stream);
        var matches = new List<MatchDto>();
        var warnings = new List<string>();
        string? period = null;

        foreach (var sheet in workbook.Worksheets)
        {
            DateTime? date = null;
            string? venue = null;

            foreach (var row in sheet.RowsUsed())
            {
                var cells = row.CellsUsed().OrderBy(c => c.Address.ColumnNumber).ToList();
                if (cells.Count == 0)
                {
                    continue;
                }

                var texts = cells.Select(c => c.GetFormattedString().Trim()).ToList();

                // "İlimizde 9 - 15 Kasım 2026 tarihleri arasında..." cümlesinden hafta aralığı.
                var periodMatch = cells.Select(c => PeriodRegex().Match(c.GetString())).FirstOrDefault(m => m.Success);
                if (periodMatch is not null && period is null)
                {
                    period = periodMatch.Groups[1].Value.Trim();
                }

                // Gün + salon başlığı.
                var dateIndex = texts.FindIndex(t => DateRegex().IsMatch(t));
                if (dateIndex >= 0 && TryParseDate(texts[dateIndex], out var headerDate))
                {
                    date = headerDate;
                    venue = texts.Skip(dateIndex + 1).LastOrDefault(t => t.Length > 0);
                    continue;
                }

                // Maç satırı: ilk hücre saat.
                var time = ReadTime(cells[0]);
                if (time is null)
                {
                    continue;
                }

                var rest = texts.Skip(1).Where(t => t.Length > 0).ToList();
                if (rest.Count < 3)
                {
                    warnings.Add($"{sheet.Name} satır {row.RowNumber()}: eksik bilgi ({string.Join(" / ", texts)})");
                    continue;
                }

                if (date is null)
                {
                    warnings.Add($"{sheet.Name} satır {row.RowNumber()}: tarihi bulunamadı ({rest[0]} - {rest[1]})");
                    continue;
                }

                var (league, category, categoryName, competition) = MapLeague(rest[2], rest.Count > 3 ? rest[3] : null);

                matches.Add(new MatchDto
                {
                    Date = date,
                    RawDateText = date.Value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
                    Time = time,
                    RawTimeText = time.Value.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
                    Venue = venue,
                    HomeTeam = rest[0],
                    AwayTeam = rest[1],
                    League = league,
                    CategoryCode = category,
                    CategoryDescription = categoryName,
                    Competition = competition,
                });
            }
        }

        return new ParseResult(matches, warnings, period);
    }

    /// <summary>
    /// Küme hücresini veritabanındaki biçime çevirir.
    /// Gençler: "MDK1L" + "4B C" -> küme MDK1L, kategori MdK, yarışma "Midi Kızlar 1. Lig 4. Bölge C Grubu" benzeri.
    /// Büyükler: sponsorlu lig adı takvim sayfasındaki koda çevrilir (örn. "UZMAN POSTA 2.LİG KADIN" -> 2LK).
    /// </summary>
    private static (string League, string Category, string CategoryName, string? Competition) MapLeague(string raw, string? group)
    {
        var code = raw.Trim().ToUpper(Turkish);
        var youth = YouthRegex().Match(code);
        if (youth.Success && YouthCategories.TryGetValue(youth.Groups[1].Value, out var cat))
        {
            var competition = string.IsNullOrWhiteSpace(group) ? null : GroupName(group);
            return (code, cat.Code, cat.Name, competition);
        }

        var league = code switch
        {
            _ when code.Contains("2.LİG KADIN") || code.Contains("2. LİG KADIN") => "2LK",
            _ when code.Contains("2.LİG ERKEK") || code.Contains("2. LİG ERKEK") => "2LE",
            _ when code.Contains("1.LİG KADIN") || code.Contains("1. LİG KADIN") => "1LK",
            _ when code.Contains("1.LİG ERKEK") || code.Contains("1. LİG ERKEK") => "1LE",
            "VSL" => "SULTANLAR",
            "SGEL" => "EFELER",
            "ASKV" => "ASKVK",
            _ => code,
        };

        return (league, "BYK", "Büyükler", raw.Trim());
    }

    /// <summary>"4B C" -> "4. Bölge C Grubu", "A" -> "A Grubu".</summary>
    private static string GroupName(string group)
    {
        var m = GroupRegex().Match(group.Trim());
        if (m.Success)
        {
            return $"{m.Groups[1].Value}. Bölge {m.Groups[2].Value} Grubu";
        }

        return group.Trim().Length <= 2 ? $"{group.Trim()} Grubu" : group.Trim();
    }

    private static TimeSpan? ReadTime(IXLCell cell)
    {
        var value = cell.Value;
        if (value.IsTimeSpan)
        {
            return Round(value.GetTimeSpan());
        }

        if (value.IsDateTime)
        {
            return Round(value.GetDateTime().TimeOfDay);
        }

        if (value.IsNumber)
        {
            var n = value.GetNumber();
            return n is > 0 and < 1 ? Round(TimeSpan.FromDays(n)) : null;
        }

        var text = cell.GetString().Trim();
        return TimeRegex().IsMatch(text) && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var t) ? t : null;
    }

    // Excel saatleri kesirli gün olarak tutar (0.7916666 = 19:00); kayan nokta hatasını dakikaya yuvarla.
    private static TimeSpan Round(TimeSpan t) => TimeSpan.FromMinutes(Math.Round(t.TotalMinutes));

    private static bool TryParseDate(string text, out DateTime date)
    {
        var m = DateRegex().Match(text);
        date = default;
        return m.Success && DateTime.TryParseExact(
            $"{m.Groups[1].Value} {m.Groups[2].Value} {m.Groups[3].Value}", "d MMMM yyyy", Turkish, DateTimeStyles.None, out date);
    }

    [GeneratedRegex(@"^(\d{1,2})\s+(Ocak|Şubat|Mart|Nisan|Mayıs|Haziran|Temmuz|Ağustos|Eylül|Ekim|Kasım|Aralık)\s+(\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"^\d{1,2}[:.]\d{2}$")]
    private static partial Regex TimeRegex();

    [GeneratedRegex(@"^(GE|GK|KE|KK|YE|YK|MDE|MDK)(\d+L|SL)$")]
    private static partial Regex YouthRegex();

    [GeneratedRegex(@"^(\d+)\s*B\s+([A-ZÇĞİÖŞÜ])$", RegexOptions.IgnoreCase)]
    private static partial Regex GroupRegex();

    [GeneratedRegex(@"[İi]limizde\s+(.+?)\s+tarihleri", RegexOptions.IgnoreCase)]
    private static partial Regex PeriodRegex();
}
