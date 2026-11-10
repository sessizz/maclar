using System.Globalization;
using System.Net;
using HtmlAgilityPack;
using Maclar.Web.Models;

namespace Maclar.Web.Services;

/// <summary>
/// İl temsilciliği sitesinin PuanDurumu sayfasını gezerek geçmiş (ve tarihi belli gelecek) maçları toplar.
///
/// Sayfa ASP.NET WebForms: her açılır liste seçimi viewstate ile sayfayı yeniden gönderir (postback).
/// Zincir: cinsiyet -> kategori -> küme -> yarışma; son sayfadaki "icerik_gvmusabakaliste" tablosu maçları içerir.
/// Bir tam tarama ~150 istek; siteye yük bindirmemek için istekler arasında bekleme var.
/// </summary>
public class ArchiveScraperService
{
    private const string PageUrl = "https://istanbul.voleyboliltemsilciligi.com/PuanDurumu";
    private const string Prefix = "ctl00$icerik$";
    private const string GenderField = Prefix + "ddlsbe";
    private const string CategoryField = Prefix + "ddlSkategori";
    private const string LeagueField = Prefix + "ddlskume";
    private const string CompetitionField = Prefix + "ddlSyarismaadi";
    private const string HomeWalkoverPrefix = "(H) - ";

    private static readonly CultureInfo Turkish = new("tr-TR");

    private readonly HttpClient _httpClient;
    private readonly ILogger<ArchiveScraperService> _logger;
    private readonly TimeSpan _delay;

    public ArchiveScraperService(HttpClient httpClient, IConfiguration configuration, ILogger<ArchiveScraperService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _delay = TimeSpan.FromMilliseconds(configuration.GetValue<int?>("Archive:RequestDelayMs") ?? 800);
    }

    public sealed record ArchiveResult(IReadOnlyList<MatchDto> Matches, int Pages, int Requests);

    public async Task<ArchiveResult> GetMatchesAsync(CancellationToken cancellationToken = default)
    {
        var requests = 0;
        var pages = 0;
        var matches = new List<MatchDto>();

        async Task<HtmlDocument> Load(HtmlDocument? from, string? target, string? value)
        {
            if (requests > 0)
            {
                await Task.Delay(_delay, cancellationToken);
            }

            requests++;
            using var request = new HttpRequestMessage(from is null ? HttpMethod.Get : HttpMethod.Post, PageUrl);
            request.Headers.Referrer = new Uri(PageUrl);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            if (from is not null)
            {
                var fields = FormFields(from);
                fields[target!] = value!;
                fields["__EVENTTARGET"] = target!;
                fields["__EVENTARGUMENT"] = string.Empty;
                request.Content = new FormUrlEncodedContent(fields);
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var doc = new HtmlDocument();
            doc.LoadHtml(await response.Content.ReadAsStringAsync(cancellationToken));
            return doc;
        }

        var start = await Load(null, null, null);
        foreach (var (genderValue, _) in Options(start, GenderField))
        {
            var byGender = await Load(start, GenderField, genderValue);
            foreach (var (categoryValue, categoryName) in Options(byGender, CategoryField))
            {
                var byCategory = await Load(byGender, CategoryField, categoryValue);
                foreach (var (leagueValue, _) in Options(byCategory, LeagueField))
                {
                    var byLeague = await Load(byCategory, LeagueField, leagueValue);
                    var selected = Selected(byLeague, CompetitionField);
                    foreach (var (competitionValue, competitionName) in Options(byLeague, CompetitionField))
                    {
                        // Küme seçilince ilk yarışma zaten seçili gelir; onun için ayrıca istek atma.
                        var page = competitionValue == selected
                            ? byLeague
                            : await Load(byLeague, CompetitionField, competitionValue);
                        pages++;
                        matches.AddRange(ParseMatches(page, categoryValue, categoryName, leagueValue, competitionName));
                    }
                }
            }
        }

        _logger.LogInformation("Arşiv tarandı: {Pages} yarışma sayfası, {Requests} istek, {Count} tarihli maç.", pages, requests, matches.Count);
        return new ArchiveResult(matches, pages, requests);
    }

    private static IEnumerable<MatchDto> ParseMatches(HtmlDocument page, string categoryCode, string categoryName, string leagueCode, string competition)
    {
        var rows = page.DocumentNode.SelectNodes("//table[@id='icerik_gvmusabakaliste']//tr");
        if (rows is null)
        {
            yield break;
        }

        foreach (var row in rows)
        {
            var spans = row.SelectNodes(".//span[@id]");
            if (spans is null)
            {
                continue;
            }

            // Hücre kimlikleri: icerik_gvmusabakaliste_<alan>_<satır>
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var span in spans)
            {
                var parts = span.Id.Split('_');
                if (parts.Length >= 3)
                {
                    fields[parts[^2]] = Clean(span.InnerText);
                }
            }

            // Tarihi belli olmayan fikstür satırları atlanır.
            if (!DateTime.TryParseExact(fields.GetValueOrDefault("gtarih"), "dd.MM.yyyy", Turkish, DateTimeStyles.None, out var date))
            {
                continue;
            }

            var home = fields.GetValueOrDefault("gevsahibi") ?? string.Empty;
            var away = fields.GetValueOrDefault("gmisafir") ?? string.Empty;
            var setScores = fields.GetValueOrDefault("gsetsonuclari");

            // "(H) - Takım" hükmen yenik sayılan takımı gösterir; adı sade tut, bilgiyi skora yaz.
            var walkover = home.StartsWith(HomeWalkoverPrefix, StringComparison.Ordinal) || away.StartsWith(HomeWalkoverPrefix, StringComparison.Ordinal);
            home = StripWalkover(home);
            away = StripWalkover(away);
            if (walkover && !string.IsNullOrEmpty(setScores))
            {
                setScores = "Hükmen: " + setScores;
            }

            int? homeSets = int.TryParse(fields.GetValueOrDefault("gseta"), out var hs) ? hs : null;
            int? awaySets = int.TryParse(fields.GetValueOrDefault("gsetb"), out var aws) ? aws : null;
            var time = TimeSpan.TryParse(fields.GetValueOrDefault("gsaat"), CultureInfo.InvariantCulture, out var t) ? t : (TimeSpan?)null;

            yield return new MatchDto
            {
                Date = date,
                RawDateText = fields.GetValueOrDefault("gtarih"),
                Time = time,
                RawTimeText = fields.GetValueOrDefault("gsaat"),
                Venue = NullIfEmpty(fields.GetValueOrDefault("gyer")),
                HomeTeam = home,
                AwayTeam = away,
                HomeSets = homeSets,
                AwaySets = awaySets,
                SetScores = NullIfEmpty(setScores),
                IsFinished = homeSets.HasValue && awaySets.HasValue && homeSets + awaySets > 0 ? true : (bool?)null,
                League = leagueCode,
                CategoryCode = categoryCode,
                CategoryDescription = categoryName,
                Competition = competition,
            };
        }
    }

    private static string StripWalkover(string team) =>
        team.StartsWith(HomeWalkoverPrefix, StringComparison.Ordinal) ? team[HomeWalkoverPrefix.Length..].Trim() : team;

    /// <summary>
    /// Formun postback'te geri gönderilmesi gereken alanları: gizli alanlar ve açılır listelerin seçili değerleri.
    /// </summary>
    private static Dictionary<string, string> FormFields(HtmlDocument doc)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var input in doc.DocumentNode.SelectNodes("//input[@type='hidden'][@name]") ?? Enumerable.Empty<HtmlNode>())
        {
            fields[input.GetAttributeValue("name", "")] = WebUtility.HtmlDecode(input.GetAttributeValue("value", ""));
        }

        foreach (var select in doc.DocumentNode.SelectNodes("//select[@name]") ?? Enumerable.Empty<HtmlNode>())
        {
            var option = select.SelectSingleNode(".//option[@selected]") ?? select.SelectSingleNode(".//option");
            if (option is not null)
            {
                fields[select.GetAttributeValue("name", "")] = WebUtility.HtmlDecode(option.GetAttributeValue("value", ""));
            }
        }

        return fields;
    }

    /// <summary>
    /// Bir açılır listenin gerçek seçenekleri ("-Seçiniz-" gibi boş değerler hariç).
    /// </summary>
    private static List<(string Value, string Text)> Options(HtmlDocument doc, string name)
    {
        var select = doc.DocumentNode.SelectSingleNode($"//select[@name='{name}']");
        var result = new List<(string, string)>();
        foreach (var option in select?.SelectNodes(".//option") ?? Enumerable.Empty<HtmlNode>())
        {
            var value = WebUtility.HtmlDecode(option.GetAttributeValue("value", ""));
            if (value is "" or "0" or "-1")
            {
                continue;
            }

            result.Add((value, Clean(option.InnerText)));
        }

        return result;
    }

    private static string? Selected(HtmlDocument doc, string name)
    {
        var option = doc.DocumentNode.SelectSingleNode($"//select[@name='{name}']//option[@selected]");
        return option is null ? null : WebUtility.HtmlDecode(option.GetAttributeValue("value", ""));
    }

    private static string Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty : HtmlEntity.DeEntitize(text).Trim();

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
