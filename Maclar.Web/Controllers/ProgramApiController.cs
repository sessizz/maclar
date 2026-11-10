using System.Security.Cryptography;
using System.Text;
using Maclar.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Maclar.Web.Controllers;

/// <summary>
/// Haftalık Excel programı yükleme adresi. Telegram'ı fisbot dinler (aynı bot token'ı iki uygulama
/// polling ile paylaşamaz); fisbot gelen .xlsx dosyasını buraya iletir ve dönen özeti sohbete yazar.
///
/// İstek: POST /api/program, multipart/form-data: file (.xlsx), chat_id (dosyanın geldiği sohbet).
/// Başlık: Authorization: Bearer &lt;Program:UploadToken&gt;.
/// Yanıt: { ok, matches, added, message } (message: Telegram HTML biçiminde özet).
/// </summary>
[ApiController]
[Route("api/program")]
public class ProgramApiController : ControllerBase
{
    private const long MaxFileBytes = 10 * 1024 * 1024;

    private readonly ProgramImportService _importService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ProgramApiController> _logger;

    public ProgramApiController(ProgramImportService importService, IConfiguration configuration, ILogger<ProgramApiController> logger)
    {
        _importService = importService;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost]
    [RequestSizeLimit(MaxFileBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxFileBytes + 64 * 1024)]
    public async Task<IActionResult> Upload([FromForm] IFormFile? file, [FromForm(Name = "chat_id")] string? chatId, CancellationToken cancellationToken)
    {
        var expectedToken = _configuration["Program:UploadToken"];
        if (string.IsNullOrWhiteSpace(expectedToken))
        {
            // Anahtar tanımlı değilse adres kapalı.
            return NotFound();
        }

        if (!IsAuthorized(expectedToken))
        {
            _logger.LogWarning("Program yükleme reddedildi: geçersiz anahtar. IP: {Ip}", HttpContext.Connection.RemoteIpAddress);
            return Unauthorized(new { ok = false, message = "Yetkisiz." });
        }

        // Sadece Telegram:ChatId sohbetinden gelen dosyalar (fisbot dosyanın geldiği sohbeti gönderir).
        var allowedChat = _configuration["Telegram:ChatId"];
        if (!string.IsNullOrWhiteSpace(allowedChat) && !string.Equals(chatId?.Trim(), allowedChat.Trim(), StringComparison.Ordinal))
        {
            _logger.LogWarning("Program yükleme reddedildi: izin verilmeyen sohbet {ChatId}.", chatId);
            return StatusCode(StatusCodes.Status403Forbidden,
                new { ok = false, message = "Bu sohbetten gönderilen programlar kabul edilmiyor." });
        }

        if (file is null || file.Length == 0)
        {
            return BadRequest(new { ok = false, message = "Dosya yok." });
        }

        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { ok = false, message = "Sadece .xlsx program dosyaları okunabiliyor." });
        }

        await using var stream = new MemoryStream();
        await file.CopyToAsync(stream, cancellationToken);
        stream.Position = 0;

        var result = await _importService.ImportAsync(stream, Path.GetFileName(file.FileName), cancellationToken);
        return Ok(new { ok = result.Ok, matches = result.Matches, added = result.Added, message = result.Message });
    }

    private bool IsAuthorized(string expectedToken)
    {
        var header = Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var given = Encoding.UTF8.GetBytes(header[prefix.Length..].Trim());
        var expected = Encoding.UTF8.GetBytes(expectedToken.Trim());
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }
}
