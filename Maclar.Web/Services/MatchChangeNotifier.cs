using System.Text;
using Maclar.Web.Models;

namespace Maclar.Web.Services;

/// <summary>
/// Senkronizasyon farklarından anlamlı değişiklikleri çıkarıp Telegram'a bildirir:
/// - Tarihi değişen maç (eski kayıt kayboldu, aynı takımlarla yeni tarihte kayıt geldi)
/// - Saati veya salonu değişen maç
/// - Siteden kaldırılan (henüz oynanmamış) maç
/// Yeni eklenen fikstürler ve oynanmış eski maçların siteden düşmesi bildirilmez (gürültü olur).
/// </summary>
public class MatchChangeNotifier
{
    /// <summary>
    /// Bir senkronizasyonda bundan fazla maç kaybolursa bu muhtemelen site/scraper sorunudur;
    /// tek tek listelemek yerine tek uyarı gönderilir.
    /// </summary>
    private const int MassDisappearanceThreshold = 30;

    private readonly TelegramNotifier _telegram;
    private readonly ILogger<MatchChangeNotifier> _logger;

    public MatchChangeNotifier(TelegramNotifier telegram, ILogger<MatchChangeNotifier> logger)
    {
        _telegram = telegram;
        _logger = logger;
    }

    public async Task NotifyAsync(MatchSaveResult result, int scrapedCount, CancellationToken cancellationToken = default)
    {
        var message = BuildMessage(result, scrapedCount, IstanbulToday());
        if (message is null)
        {
            return;
        }

        _logger.LogInformation("Maç değişikliği tespit edildi, bildirim gönderiliyor.");
        await _telegram.SendAsync(message, cancellationToken);
    }

    /// <summary>
    /// Bildirilecek bir şey yoksa null döner.
    /// </summary>
    public static string? BuildMessage(MatchSaveResult result, int scrapedCount, DateTime today)
    {
        // İlk senkronizasyonda karşılaştırılacak veri yok; boş çekim büyük ihtimalle site hatası.
        if (result.IsFirstSync || scrapedCount == 0)
        {
            return null;
        }

        if (result.Disappeared.Count > MassDisappearanceThreshold)
        {
            return $"⚠️ <b>Maç takviminde olağandışı değişiklik</b>\n" +
                   $"{result.Disappeared.Count} maç siteden bir anda kayboldu (sitede şu an {scrapedCount} maç var). " +
                   "Site yapısı değişmiş olabilir, kontrol edin.";
        }

        // Kaybolan maçı, aynı takımlarla yeni gelen maçla eşleştir: tarih değişikliği / erteleme.
        var addedByFixture = result.Added
            .GroupBy(MatchStore.BuildFixtureKey)
            .ToDictionary(g => g.Key, g => new Queue<MatchDto>(g));

        var rescheduled = new List<MatchChange>();
        var removed = new List<MatchDto>();

        foreach (var old in result.Disappeared)
        {
            if (addedByFixture.TryGetValue(MatchStore.BuildFixtureKey(old), out var candidates) && candidates.Count > 0)
            {
                rescheduled.Add(new MatchChange(old, candidates.Dequeue()));
            }
            else if (old.Date is null || old.Date.Value.Date >= today)
            {
                // Oynanmış eski maçların siteden düşmesi normal; sadece gelecekteki maçları bildir.
                removed.Add(old);
            }
        }

        var changed = result.Changed
            .Where(c => c.Current.Date is null || c.Current.Date.Value.Date >= today)
            .ToList();

        if (rescheduled.Count == 0 && removed.Count == 0 && changed.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder("🏐 <b>Maç takviminde değişiklik</b>\n");

        if (rescheduled.Count > 0)
        {
            sb.Append("\n📅 <b>Tarihi değişen maçlar</b>\n");
            foreach (var change in rescheduled)
            {
                AppendChange(sb, change);
            }
        }

        if (changed.Count > 0)
        {
            sb.Append("\n🕒 <b>Saati / salonu değişen maçlar</b>\n");
            foreach (var change in changed)
            {
                AppendChange(sb, change);
            }
        }

        if (removed.Count > 0)
        {
            sb.Append("\n❌ <b>Siteden kaldırılan maçlar</b>\n");
            foreach (var match in removed)
            {
                sb.Append("• ").Append(Teams(match)).Append('\n')
                  .Append("   ").Append(When(match)).Append('\n');
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendChange(StringBuilder sb, MatchChange change)
    {
        sb.Append("• ").Append(Teams(change.Current)).Append('\n')
          .Append("   eski: ").Append(When(change.Old)).Append('\n')
          .Append("   yeni: ").Append(When(change.Current)).Append('\n');
    }

    private static string Teams(MatchDto match)
    {
        var detail = string.Join(", ", new[] { match.CategoryCode, match.League }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var text = $"{match.HomeTeam} – {match.AwayTeam}";
        return $"<b>{H(text)}</b>" + (detail.Length > 0 ? $" ({H(detail)})" : string.Empty);
    }

    private static string When(MatchDto match)
    {
        var date = match.Date?.ToString("dd.MM.yyyy") ?? match.RawDateText ?? "?";
        var time = match.Time?.ToString(@"hh\:mm") ?? match.RawTimeText;
        var parts = new[] { date, time, match.Venue }.Where(s => !string.IsNullOrWhiteSpace(s));
        return H(string.Join(" · ", parts));
    }

    // Telegram HTML modunda sadece &, < ve > kaçırılmalı; Türkçe karakterler olduğu gibi kalır.
    private static string H(string? text) =>
        (text ?? string.Empty).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static DateTime IstanbulToday()
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
        }
        catch (TimeZoneNotFoundException)
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time");
        }

        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone).Date;
    }
}
