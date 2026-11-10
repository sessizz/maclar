using System.Text;
using Maclar.Web.Models;

namespace Maclar.Web.Services;

/// <summary>
/// Haftalık Excel programını okur, veritabanına yazar ve Telegram'a uygun (HTML) bir özet üretir.
/// Dosya fisbot üzerinden <c>POST /api/program</c> ile gelir (bkz. ProgramApiController).
/// </summary>
public class ProgramImportService
{
    private readonly MatchSyncService _syncService;
    private readonly ILogger<ProgramImportService> _logger;

    public ProgramImportService(MatchSyncService syncService, ILogger<ProgramImportService> logger)
    {
        _syncService = syncService;
        _logger = logger;
    }

    public sealed record ImportResult(bool Ok, int Matches, int Added, string Message);

    public async Task<ImportResult> ImportAsync(Stream stream, string fileName, CancellationToken cancellationToken = default)
    {
        var safeName = TelegramNotifier.Escape(fileName);

        ProgramExcelParser.ParseResult parsed;
        try
        {
            parsed = ProgramExcelParser.Parse(stream);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Excel programı okunamadı: {File}", fileName);
            return new ImportResult(false, 0, 0, $"⚠️ <b>{safeName}</b> okunamadı: geçerli bir Excel (.xlsx) dosyası değil gibi.");
        }

        if (parsed.Matches.Count == 0)
        {
            return new ImportResult(false, 0, 0,
                $"📎 <b>{safeName}</b>: dosyada maç bulunamadı. Haftalık program dosyası mı? Düzen değişmiş olabilir.");
        }

        var result = await _syncService.SaveProgramAsync(parsed.Matches, cancellationToken);
        _logger.LogInformation("Excel programı işlendi: {File}, {Count} maç, {Added} yeni.", fileName, parsed.Matches.Count, result.Added.Count);
        return new ImportResult(true, parsed.Matches.Count, result.Added.Count, BuildSummary(safeName, parsed, result));
    }

    private static string BuildSummary(string safeName, ProgramExcelParser.ParseResult parsed, ExternalSaveResult result)
    {
        var sb = new StringBuilder();
        sb.Append("📥 <b>").Append(safeName).Append("</b>");
        if (parsed.Period is not null)
        {
            sb.Append(" (").Append(TelegramNotifier.Escape(parsed.Period)).Append(')');
        }

        sb.Append('\n')
          .Append(parsed.Matches.Count).Append(" maç okundu: ")
          .Append(result.Added.Count).Append(" yeni, ")
          .Append(result.Updated).Append(" zaten vardı");
        if (result.DateDiffers.Count > 0)
        {
            sb.Append(", ").Append(result.DateDiffers.Count).Append(" tanesi başka günde kayıtlı");
        }

        sb.Append('.');

        if (result.Replaced > 0)
        {
            sb.Append("\nÖnceki programa göre tarihi değişen ").Append(result.Replaced).Append(" maç yeni tarihine taşındı.");
        }

        AppendList(sb, result.DateDiffers,
            "📅", "Tarihi farklı", "ertelenmiş olabilir; sitedeki tarih korundu, yeni kayıt açılmadı",
            c => $": sitede {c.Old.Date?.ToString("dd.MM", MatchFormat.Turkish)}, Excel {c.Current.Date?.ToString("dd.MM", MatchFormat.Turkish)}");

        AppendList(sb, result.TimeDiffers,
            "🕒", "Saati farklı", "sitedeki saat korundu, kontrol edin",
            c => $", {c.Current.Date?.ToString("dd.MM", MatchFormat.Turkish)}: sitede {c.Old.Time?.ToString(@"hh\:mm")}, Excel {c.Current.Time?.ToString(@"hh\:mm")}");

        if (parsed.Warnings.Count > 0)
        {
            sb.Append("\n\n⚠️ Okunamayan ").Append(parsed.Warnings.Count).Append(" satır:\n");
            foreach (var warning in parsed.Warnings.Take(5))
            {
                sb.Append("• ").Append(TelegramNotifier.Escape(warning)).Append('\n');
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendList(StringBuilder sb, IReadOnlyList<MatchChange> changes, string icon, string title, string note, Func<MatchChange, string> detail)
    {
        if (changes.Count == 0)
        {
            return;
        }

        sb.Append("\n\n").Append(icon).Append(" <b>").Append(title).Append(' ').Append(changes.Count)
          .Append(" maç</b> (").Append(note).Append("):\n");
        foreach (var change in changes.Take(10))
        {
            sb.Append("• ")
              .Append(TelegramNotifier.Escape($"{MatchFormat.Team(change.Current.HomeTeam)} - {MatchFormat.Team(change.Current.AwayTeam)}"))
              .Append(TelegramNotifier.Escape(detail(change)))
              .Append('\n');
        }

        if (changes.Count > 10)
        {
            sb.Append("… ve ").Append(changes.Count - 10).Append(" maç daha\n");
        }
    }
}
