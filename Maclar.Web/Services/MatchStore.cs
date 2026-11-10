using System.Globalization;
using Maclar.Web.Models;
using Microsoft.Data.Sqlite;

namespace Maclar.Web.Services;

/// <summary>
/// Maçları SQLite veritabanında kalıcı olarak saklar. Kaynak siteden silinen maçlar
/// veritabanında kalır, böylece geçmiş maçlar da görülebilir.
/// </summary>
public class MatchStore
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string TimeFormat = @"hh\:mm";
    private static readonly CultureInfo TurkishCulture = new("tr-TR");

    private readonly string _connectionString;

    public MatchStore(IConfiguration configuration, IHostEnvironment environment)
    {
        var builder = new SqliteConnectionStringBuilder(
            configuration.GetConnectionString("Matches") ?? "Data Source=data/maclar.db");

        // Göreli yol verilirse uygulamanın content root'una göre çöz (Docker'da /app/data/maclar.db).
        if (!Path.IsPathRooted(builder.DataSource))
        {
            builder.DataSource = Path.GetFullPath(Path.Combine(environment.ContentRootPath, builder.DataSource));
        }

        var directory = Path.GetDirectoryName(builder.DataSource);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        builder.DefaultTimeout = 30;
        _connectionString = builder.ToString();
        DatabasePath = builder.DataSource;
    }

    public string DatabasePath { get; }

    public void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS Matches (
                Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                MatchKey            TEXT NOT NULL UNIQUE,
                Date                TEXT NULL,
                RawDateText         TEXT NULL,
                Time                TEXT NULL,
                RawTimeText         TEXT NULL,
                Venue               TEXT NULL,
                HomeTeam            TEXT NULL,
                AwayTeam            TEXT NULL,
                SetScores           TEXT NULL,
                League              TEXT NULL,
                CategoryCode        TEXT NULL,
                CategoryDescription TEXT NULL,
                HomeSets            INTEGER NULL,
                AwaySets            INTEGER NULL,
                IsFinished          INTEGER NULL,
                FirstSeenAt         TEXT NOT NULL,
                LastSeenAt          TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_Matches_Date ON Matches (Date);
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Maçları kaydeder. Aynı maç (bkz. <see cref="BuildMatchKey"/>) zaten varsa yeni kayıt
    /// eklenmez; sadece saat, salon ve skor gibi değişebilen alanları güncellenir.
    /// Dönen sonuç, bir önceki senkronizasyona göre farkları (yeni, değişen, kaybolan) içerir.
    /// </summary>
    public async Task<MatchSaveResult> SaveAsync(IReadOnlyList<MatchDto> matches, DateTime seenAtUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        // Değişiklik tespiti için mevcut kayıtları al. Veri küçük (binler mertebesi), tamamını okumak sorun değil.
        var existing = new Dictionary<string, StoredMatch>();
        foreach (var stored in await ReadAllAsync(connection, transaction, cancellationToken))
        {
            existing[stored.Key] = stored;
        }

        // Bir önceki senkronizasyonda sitede görülen kayıtlar = LastSeenAt'i en büyük olanlar.
        DateTime? previousSeenAt = existing.Count == 0 ? null : existing.Values.Max(s => s.LastSeenAt);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Var olan kayıtta boş gelen skor/set değerleri eski değerin üstüne yazılmaz.
        command.CommandText = """
            INSERT INTO Matches (
                MatchKey, Date, RawDateText, Time, RawTimeText, Venue, HomeTeam, AwayTeam, SetScores,
                League, CategoryCode, CategoryDescription, HomeSets, AwaySets, IsFinished, FirstSeenAt, LastSeenAt)
            VALUES (
                $key, $date, $rawDate, $time, $rawTime, $venue, $home, $away, $setScores,
                $league, $category, $categoryDescription, $homeSets, $awaySets, $isFinished, $seenAt, $seenAt)
            ON CONFLICT (MatchKey) DO UPDATE SET
                Time                = COALESCE(excluded.Time, Matches.Time),
                RawTimeText         = COALESCE(NULLIF(excluded.RawTimeText, ''), Matches.RawTimeText),
                Venue               = COALESCE(NULLIF(excluded.Venue, ''), Matches.Venue),
                SetScores           = COALESCE(NULLIF(excluded.SetScores, ''), Matches.SetScores),
                CategoryDescription = COALESCE(NULLIF(excluded.CategoryDescription, ''), Matches.CategoryDescription),
                HomeSets            = COALESCE(excluded.HomeSets, Matches.HomeSets),
                AwaySets            = COALESCE(excluded.AwaySets, Matches.AwaySets),
                IsFinished          = COALESCE(excluded.IsFinished, Matches.IsFinished),
                LastSeenAt          = excluded.LastSeenAt;
            """;

        var key = command.Parameters.Add("$key", SqliteType.Text);
        var date = command.Parameters.Add("$date", SqliteType.Text);
        var rawDate = command.Parameters.Add("$rawDate", SqliteType.Text);
        var time = command.Parameters.Add("$time", SqliteType.Text);
        var rawTime = command.Parameters.Add("$rawTime", SqliteType.Text);
        var venue = command.Parameters.Add("$venue", SqliteType.Text);
        var home = command.Parameters.Add("$home", SqliteType.Text);
        var away = command.Parameters.Add("$away", SqliteType.Text);
        var setScores = command.Parameters.Add("$setScores", SqliteType.Text);
        var league = command.Parameters.Add("$league", SqliteType.Text);
        var category = command.Parameters.Add("$category", SqliteType.Text);
        var categoryDescription = command.Parameters.Add("$categoryDescription", SqliteType.Text);
        var homeSets = command.Parameters.Add("$homeSets", SqliteType.Integer);
        var awaySets = command.Parameters.Add("$awaySets", SqliteType.Integer);
        var isFinished = command.Parameters.Add("$isFinished", SqliteType.Integer);
        command.Parameters.AddWithValue("$seenAt", seenAtUtc.ToString("O", CultureInfo.InvariantCulture));

        var currentKeys = new HashSet<string>();
        var added = new List<MatchDto>();
        var changed = new List<MatchChange>();

        foreach (var match in matches)
        {
            var matchKey = BuildMatchKey(match);

            // Aynı çekimde aynı maç iki kez gelirse farkı bir kez hesapla.
            if (currentKeys.Add(matchKey))
            {
                if (existing.TryGetValue(matchKey, out var old))
                {
                    if (HasTimeOrVenueChanged(old.Match, match))
                    {
                        changed.Add(new MatchChange(old.Match, match));
                    }
                }
                else
                {
                    added.Add(match);
                }
            }

            key.Value = matchKey;
            date.Value = Db(match.Date?.ToString(DateFormat, CultureInfo.InvariantCulture));
            rawDate.Value = Db(match.RawDateText);
            time.Value = Db(match.Time?.ToString(TimeFormat, CultureInfo.InvariantCulture));
            rawTime.Value = Db(match.RawTimeText);
            venue.Value = Db(match.Venue);
            home.Value = Db(match.HomeTeam);
            away.Value = Db(match.AwayTeam);
            setScores.Value = Db(match.SetScores);
            league.Value = Db(match.League);
            category.Value = Db(match.CategoryCode);
            categoryDescription.Value = Db(match.CategoryDescription);
            homeSets.Value = Db(match.HomeSets);
            awaySets.Value = Db(match.AwaySets);
            isFinished.Value = match.IsFinished.HasValue ? (match.IsFinished.Value ? 1 : 0) : DBNull.Value;

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // Önceki senkronizasyonda sitede olup bu sefer gelmeyenler.
        var disappeared = previousSeenAt is null
            ? new List<MatchDto>()
            : existing.Values
                .Where(s => s.LastSeenAt == previousSeenAt && !currentKeys.Contains(s.Key))
                .Select(s => s.Match)
                .ToList();

        await transaction.CommitAsync(cancellationToken);

        return new MatchSaveResult(
            IsFirstSync: existing.Count == 0,
            Added: added,
            Changed: changed,
            Disappeared: disappeared);
    }

    /// <summary>
    /// Tüm maçları döner: eskiden yeniye (tarih, sonra saat artan).
    /// </summary>
    public async Task<(IReadOnlyList<MatchDto> Matches, DateTime? LastSeenAtUtc)> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        var all = await ReadAllAsync(connection, null, cancellationToken);
        DateTime? lastSeenAt = all.Count == 0 ? null : all.Max(s => s.LastSeenAt);
        return (all.Select(s => s.Match).ToList(), lastSeenAt);
    }

    private static async Task<List<StoredMatch>> ReadAllAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT Date, RawDateText, Time, RawTimeText, Venue, HomeTeam, AwayTeam, SetScores,
                   League, CategoryCode, CategoryDescription, HomeSets, AwaySets, IsFinished, LastSeenAt, MatchKey
            FROM Matches
            ORDER BY Date IS NULL, Date, Time IS NULL, Time, Id;
            """;

        var result = new List<StoredMatch>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var match = new MatchDto
            {
                Date = reader.IsDBNull(0) ? null : DateTime.ParseExact(reader.GetString(0), DateFormat, CultureInfo.InvariantCulture),
                RawDateText = GetString(reader, 1),
                Time = reader.IsDBNull(2) ? null : TimeSpan.ParseExact(reader.GetString(2), TimeFormat, CultureInfo.InvariantCulture),
                RawTimeText = GetString(reader, 3),
                Venue = GetString(reader, 4),
                HomeTeam = GetString(reader, 5),
                AwayTeam = GetString(reader, 6),
                SetScores = GetString(reader, 7),
                League = GetString(reader, 8),
                CategoryCode = GetString(reader, 9),
                CategoryDescription = GetString(reader, 10),
                HomeSets = reader.IsDBNull(11) ? null : reader.GetInt32(11),
                AwaySets = reader.IsDBNull(12) ? null : reader.GetInt32(12),
                IsFinished = reader.IsDBNull(13) ? null : reader.GetInt32(13) != 0,
            };

            var lastSeenAt = DateTime.Parse(reader.GetString(14), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            result.Add(new StoredMatch(reader.GetString(15), match, lastSeenAt));
        }

        return result;
    }

    /// <summary>
    /// Saat veya salon değişti mi? Yeni değer boşsa değişiklik sayılmaz (kayıtta da eskisi korunur).
    /// </summary>
    private static bool HasTimeOrVenueChanged(MatchDto old, MatchDto current)
    {
        var timeChanged = current.Time.HasValue && old.Time.HasValue && current.Time != old.Time;
        var venueChanged = !string.IsNullOrWhiteSpace(current.Venue)
            && !string.IsNullOrWhiteSpace(old.Venue)
            && Normalize(current.Venue) != Normalize(old.Venue);
        return timeChanged || venueChanged;
    }

    /// <summary>
    /// Takım/küme/kategori aynı ama tarihi farklı olabilecek maçları eşleştirmek için (erteleme tespiti).
    /// </summary>
    public static string BuildFixtureKey(MatchDto match) =>
        string.Join('|',
            Normalize(match.HomeTeam),
            Normalize(match.AwayTeam),
            Normalize(match.League),
            Normalize(match.CategoryCode));

    private sealed record StoredMatch(string Key, MatchDto Match, DateTime LastSeenAt);

    /// <summary>
    /// Bir maçı tekil tanımlayan anahtar: tarih + ev sahibi + misafir + küme + kategori.
    /// Saat, salon ve skor anahtara dahil değil; bunlar değişirse kayıt güncellenir.
    /// </summary>
    public static string BuildMatchKey(MatchDto match)
    {
        var dateKey = match.Date?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? match.RawDateText;
        return string.Join('|',
            Normalize(dateKey),
            Normalize(match.HomeTeam),
            Normalize(match.AwayTeam),
            Normalize(match.League),
            Normalize(match.CategoryCode));
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // Fazla boşlukları teke indir, Türkçe kurallarıyla küçük harfe çevir.
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLower(TurkishCulture);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static object Db(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private static object Db(int? value) => value.HasValue ? value.Value : DBNull.Value;

    private static string? GetString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
