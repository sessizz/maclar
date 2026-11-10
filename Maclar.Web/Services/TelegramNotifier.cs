using System.Net.Http.Json;

namespace Maclar.Web.Services;

/// <summary>
/// Telegram Bot API ile mesaj gönderir. Yapılandırma:
/// <c>Telegram:BotToken</c> ve <c>Telegram:ChatId</c> (env: <c>Telegram__BotToken</c>, <c>Telegram__ChatId</c>).
/// İkisinden biri yoksa gönderim yapılmaz, mesaj sadece log'a yazılır.
/// </summary>
public class TelegramNotifier
{
    // Telegram tek mesajda en fazla 4096 karakter kabul eder; HTML etiketleri için pay bırakıyoruz.
    private const int MaxMessageLength = 3800;

    private readonly HttpClient _httpClient;
    private readonly ILogger<TelegramNotifier> _logger;
    private readonly string? _botToken;
    private readonly string? _chatId;

    public TelegramNotifier(HttpClient httpClient, IConfiguration configuration, ILogger<TelegramNotifier> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _botToken = configuration["Telegram:BotToken"];
        _chatId = configuration["Telegram:ChatId"];
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_botToken) && !string.IsNullOrWhiteSpace(_chatId);

    /// <summary>
    /// HTML biçimli metni gönderir (parse_mode = HTML). Uzun metinler satır sınırından bölünür.
    /// Hata fırlatmaz; sorunları log'a yazar.
    /// </summary>
    public async Task SendAsync(string htmlText, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogInformation("Telegram yapılandırılmamış, bildirim gönderilmedi:\n{Message}", htmlText);
            return;
        }

        foreach (var chunk in SplitMessage(htmlText))
        {
            try
            {
                // Not: Token URL'de olduğu için URL'yi asla log'a yazma.
                using var response = await _httpClient.PostAsJsonAsync(
                    $"https://api.telegram.org/bot{_botToken}/sendMessage",
                    new
                    {
                        chat_id = _chatId,
                        text = chunk,
                        parse_mode = "HTML",
                        disable_web_page_preview = true,
                    },
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogWarning("Telegram bildirimi gönderilemedi. HTTP {Status}: {Body}", (int)response.StatusCode, body);
                    return;
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Telegram bildirimi gönderilemedi: {Error}", ex.Message);
                return;
            }
        }

        _logger.LogInformation("Telegram bildirimi gönderildi.");
    }

    private static IEnumerable<string> SplitMessage(string text)
    {
        if (text.Length <= MaxMessageLength)
        {
            yield return text;
            yield break;
        }

        var current = new System.Text.StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (current.Length > 0 && current.Length + line.Length + 1 > MaxMessageLength)
            {
                yield return current.ToString();
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append('\n');
            }

            current.Append(line.Length > MaxMessageLength ? line[..MaxMessageLength] : line);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }
}
