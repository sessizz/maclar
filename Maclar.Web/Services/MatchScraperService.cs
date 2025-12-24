using System.Globalization;
using HtmlAgilityPack;
using Maclar.Web.Models;

namespace Maclar.Web.Services;

public class MatchScraperService : IMatchScraperService
{
    private const string MatchesUrl = "https://istanbul.voleyboliltemsilciligi.com/MacTakvimi";
    private readonly HttpClient _httpClient;

    public MatchScraperService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<MatchDto>> GetMatchesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(MatchesUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(cancellationToken);

        var htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(html);

        // Sayfadaki ana tabloyu bulmaya çalışıyoruz; ilerde ihtiyaç olursa daha spesifik bir selector kullanılabilir.
        var table = htmlDoc.DocumentNode.SelectSingleNode("//table");
        if (table is null)
        {
            return Array.Empty<MatchDto>();
        }

        var rows = table.SelectNodes(".//tr");
        if (rows is null)
        {
            return Array.Empty<MatchDto>();
        }

        var matches = new List<MatchDto>();

        string? currentDateText = null;
        DateTime? currentDate = null;
        string? currentVenueText = null;

        foreach (var row in rows.Skip(1)) // ilk satır çoğunlukla header
        {
            var cells = row.SelectNodes(".//td");
            if (cells is null || cells.Count < 5)
            {
                continue;
            }

            // Önce saat hücresini bul (her satırda olması bekleniyor)
            int timeIndex = -1;
            string? timeText = null;
            for (int i = 0; i < cells.Count; i++)
            {
                var t = CleanText(cells[i].InnerText);
                if (IsTime(t))
                {
                    timeIndex = i;
                    timeText = t;
                    break;
                }
            }

            if (timeIndex == -1)
            {
                // Bu satır gerçek maç satırı olmayabilir, atla.
                continue;
            }

            // Saat hücresinin solundaki hücre salon, onun da solundaki tarih (varsa).
            string? venue = null;
            string? dateText = null;
            DateTime? date = null;

            int venueIndex = timeIndex - 1;
            if (venueIndex >= 0)
            {
                var venueCandidate = CleanText(cells[venueIndex].InnerText);
                // Venue, saat veya tarih formatında olmamalı.
                if (!string.IsNullOrWhiteSpace(venueCandidate) && !IsTime(venueCandidate) && !TryParseDate(venueCandidate, out _))
                {
                    venue = venueCandidate;
                    currentVenueText = venueCandidate;
                }
            }

            if (venue is null)
            {
                // Bu satırda salon yoksa, bir önceki satırın salonunu kullan.
                venue = currentVenueText ?? string.Empty;
            }

            int dateIndex = venueIndex - 1;
            if (dateIndex >= 0)
            {
                var dateCandidate = CleanText(cells[dateIndex].InnerText);
                if (TryParseDate(dateCandidate, out var parsedDate))
                {
                    currentDateText = dateCandidate;
                    currentDate = parsedDate;
                }
            }

            dateText = currentDateText ?? string.Empty;
            date = currentDate;

            // Kolon sırası, saat indexine göre relatif:
            // timeIndex: Saat
            // timeIndex +1: Ev Sahibi (A)
            // timeIndex +2: A (Ev sahibi set)
            // timeIndex +3: B (Misafir set)
            // timeIndex +4: Misafir (B)
            // timeIndex +5: Set Sonuçları

            int homeIndex = timeIndex + 1;
            int homeSetsIndex = timeIndex + 2;
            int awaySetsIndex = timeIndex + 3;
            int awayIndex = timeIndex + 4;
            int setIndex = timeIndex + 5;

            if (setIndex >= cells.Count)
            {
                continue;
            }

            var homeTeam = CleanText(cells.ElementAtOrDefault(homeIndex)?.InnerText ?? string.Empty);
            var homeSetsText = CleanText(cells.ElementAtOrDefault(homeSetsIndex)?.InnerText ?? string.Empty);
            var awaySetsText = CleanText(cells.ElementAtOrDefault(awaySetsIndex)?.InnerText ?? string.Empty);
            var awayTeam = CleanText(cells.ElementAtOrDefault(awayIndex)?.InnerText ?? string.Empty);
            var setScores = CleanText(cells.ElementAtOrDefault(setIndex)?.InnerText ?? string.Empty);

            // Küme ve Ktg sütunları – genelde sondan birkaç önceki kolonlarda
            // Küme genelde Ktg'den bir önceki kolonda (sondan 4.)
            var league = CleanText(cells.ElementAtOrDefault(cells.Count - 4)?.InnerText ?? string.Empty);
            var categoryCode = CleanText(cells[^3].InnerText);

            var match = new MatchDto
            {
                RawDateText = dateText,
                RawTimeText = timeText,
                Venue = venue,
                HomeTeam = homeTeam,
                AwayTeam = awayTeam,
                SetScores = setScores,
                League = league,
                CategoryCode = categoryCode,
                Date = date,
                Time = ParseTime(timeText ?? string.Empty),
            };

            if (int.TryParse(homeSetsText, out var hSets))
            {
                match.HomeSets = hSets;
            }

            if (int.TryParse(awaySetsText, out var aSets))
            {
                match.AwaySets = aSets;
            }

            FillSetsAndStatus(match);

            matches.Add(match);
        }

        return matches;
    }

    private static string CleanText(string? text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : HtmlEntity.DeEntitize(text).Trim();
    }

    private static bool TryParseDate(string text, out DateTime? date)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            date = null;
            return false;
        }

        // Örn: 22.12.2025
        if (DateTime.TryParseExact(text.Trim(), "dd.MM.yyyy", new CultureInfo("tr-TR"), DateTimeStyles.None, out var dt))
        {
            date = dt;
            return true;
        }

        date = null;
        return false;
    }

    private static bool IsTime(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return TimeSpan.TryParse(text.Trim(), CultureInfo.InvariantCulture, out _);
    }

    private static TimeSpan? ParseTime(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (TimeSpan.TryParse(text.Trim(), CultureInfo.InvariantCulture, out var time))
        {
            return time;
        }

        return null;
    }

    private static void FillSetsAndStatus(MatchDto match)
    {
        // Eğer A/B kolonlarından set sayıları dolu geldiyse, doğrudan bunları kullan.
        if (match.HomeSets.HasValue || match.AwaySets.HasValue)
        {
            match.IsFinished = (match.HomeSets.GetValueOrDefault() + match.AwaySets.GetValueOrDefault()) > 0;
            return;
        }

        if (string.IsNullOrWhiteSpace(match.SetScores))
        {
            match.IsFinished = null;
            return;
        }

        // Örn: (25-11) (12-25) (25-23) (25-15)
        var setParts = match.SetScores
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Trim('(', ')'))
            .Where(p => p.Contains('-'))
            .ToList();

        if (setParts.Count == 0)
        {
            match.IsFinished = null;
            return;
        }

        var homeWon = 0;
        var awayWon = 0;

        foreach (var part in setParts)
        {
            var scores = part.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (scores.Length != 2)
            {
                continue;
            }

            if (!int.TryParse(scores[0], out var homeScore) || !int.TryParse(scores[1], out var awayScore))
            {
                continue;
            }

            if (homeScore > awayScore)
            {
                homeWon++;
            }
            else if (awayScore > homeScore)
            {
                awayWon++;
            }
        }

        match.HomeSets = homeWon;
        match.AwaySets = awayWon;

        // Basit varsayım: En az bir set oynandıysa maç bitmiş kabul edelim.
        match.IsFinished = setParts.Count > 0;
    }
}


