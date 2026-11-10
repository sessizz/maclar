using System.Globalization;
using Maclar.Web.Models;
using Microsoft.Data.Sqlite;

namespace Maclar.Web.Services;

/// <summary>
/// Maçları SQLite veritabanında kalıcı olarak saklar. Kaynak siteden silinen maçlar
/// veritabanında kalır, böylece geçmiş maçlar da görülebilir.
///
/// İki kaynak yazar:
/// - Maç takvimi (MacTakvimi sayfası, <see cref="SaveAsync"/>): LastSeenAt güncellenir; değişiklik
///   tespiti ve Telegram bildirimleri sadece bu kaynağa göre yapılır.
/// - Arşiv (PuanDurumu sayfası, <see cref="SaveArchiveAsync"/>): geçmiş maçlar ve skorlar. LastSeenAt'e
///   dokunmaz; takvimde hiç görülmemiş kayıtlarda LastSeenAt = <see cref="NeverSeenOnCalendar"/>.
/// </summary>
public class MatchStore
{
    private const string DateFormat = "yyyy-MM-dd";
    private const string TimeFormat = @"hh\:mm";
    private static readonly CultureInfo TurkishCulture = new("tr-TR");

    /// <summary>
    /// Sadece arşivden gelmiş, takvim sayfasında hiç görülmemiş kayıtların LastSeenAt değeri.
    /// </summary>
    public static readonly DateTime NeverSeenOnCalendar = DateTime.MinValue;

    private static readonly string NeverSeenText = NeverSeenOnCalendar.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Şema sürümü (PRAGMA user_version). Yeni bir geçiş eklerken artır.</summary>
    private const int SchemaVersion = 1;

    private readonly string _connectionString;
    private readonly ILogger<MatchStore> _logger;

    public MatchStore(IConfiguration configuration, IHostEnvironment environment, ILogger<MatchStore> logger)
    {
        _logger = logger;
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
        Execute(connection, null, """
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
            """);

        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);

        if (version < 1)
        {
            MigrateToV1(connection);
        }
    }

    /// <summary>
    /// v1: arşiv sütunları eklenir ve maç anahtarından küme çıkarılır (iki kaynak farklı küme kodu kullanıyor).
    /// </summary>
    private void MigrateToV1(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = connection.CreateCommand())
        {
            info.Transaction = transaction;
            info.CommandText = "PRAGMA table_info(Matches);";
            using var reader = info.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }
        }

        if (!columns.Contains("Competition"))
        {
            Execute(connection, transaction, "ALTER TABLE Matches ADD COLUMN Competition TEXT NULL;");
        }

        if (!columns.Contains("ArchiveSeenAt"))
        {
            Execute(connection, transaction, "ALTER TABLE Matches ADD COLUMN ArchiveSeenAt TEXT NULL;");
        }

        // Anahtarları yeni biçime çevir. Aynı yeni anahtara düşen iki kayıt varsa takvimde en son görüleni kalır.
        var rows = new List<(long Id, string OldKey, string NewKey, string LastSeenAt)>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT Id, MatchKey, Date, RawDateText, HomeTeam, AwayTeam, CategoryCode, LastSeenAt FROM Matches;";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                var newKey = BuildKey(
                    reader.IsDBNull(2) ? GetString(reader, 3) : reader.GetString(2),
                    GetString(reader, 4), GetString(reader, 5), GetString(reader, 6));
                rows.Add((reader.GetInt64(0), reader.GetString(1), newKey, reader.GetString(7)));
            }
        }

        var removed = 0;
        foreach (var group in rows.GroupBy(r => r.NewKey))
        {
            var ordered = group.OrderByDescending(r => r.LastSeenAt, StringComparer.Ordinal).ToList();
            foreach (var duplicate in ordered.Skip(1))
            {
                Execute(connection, transaction, "DELETE FROM Matches WHERE Id = $id;", ("$id", duplicate.Id));
                removed++;
            }

            // Geçici anahtar ile iki adımda değiştir; UNIQUE kısıtı ara durumda çakışmasın.
            Execute(connection, transaction, "UPDATE Matches SET MatchKey = $tmp WHERE Id = $id;",
                ("$tmp", "tmp:" + ordered[0].Id), ("$id", ordered[0].Id));
        }

        foreach (var group in rows.GroupBy(r => r.NewKey))
        {
            var keep = group.OrderByDescending(r => r.LastSeenAt, StringComparer.Ordinal).First();
            Execute(connection, transaction, "UPDATE Matches SET MatchKey = $key WHERE Id = $id;",
                ("$key", group.Key), ("$id", keep.Id));
        }

        Execute(connection, transaction, $"PRAGMA user_version = {SchemaVersion};");
        transaction.Commit();

        _logger.LogInformation("Veritabanı v1'e geçirildi: {Count} kaydın anahtarı yenilendi, {Removed} çift kayıt birleştirildi.", rows.Count, removed);
    }

    /// <summary>
    /// Maç takviminden gelen maçları kaydeder. Aynı maç (bkz. <see cref="BuildMatchKey"/>) zaten varsa yeni kayıt
    /// eklenmez; sadece saat, salon ve skor gibi değişebilen alanları güncellenir.
    /// Dönen sonuç, bir önceki takvim senkronizasyonuna göre farkları (yeni, değişen, kaybolan) içerir.
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

        // Bir önceki takvim senkronizasyonunda görülen kayıtlar = LastSeenAt'i en büyük olanlar.
        // Sadece arşivden gelmiş kayıtlar (takvimde hiç görülmemiş) bu hesaba girmez.
        var seenOnCalendar = existing.Values.Where(s => s.LastSeenAt > NeverSeenOnCalendar).ToList();
        DateTime? previousSeenAt = seenOnCalendar.Count == 0 ? null : seenOnCalendar.Max(s => s.LastSeenAt);

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

        var p = new MatchParameters(command);
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
                    // Arşivden gelmiş kayıtta salon kısa adla yazılı; ilk kez takvimde görülünce "salon değişti" sayılmasın.
                    if (old.LastSeenAt > NeverSeenOnCalendar && HasTimeOrVenueChanged(old.Match, match))
                    {
                        changed.Add(new MatchChange(old.Match, match));
                    }
                }
                else
                {
                    added.Add(match);
                }
            }

            p.Set(matchKey, match);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // Önceki takvim senkronizasyonunda sitede olup bu sefer gelmeyenler.
        var disappeared = previousSeenAt is null
            ? new List<MatchDto>()
            : seenOnCalendar
                .Where(s => s.LastSeenAt == previousSeenAt && !currentKeys.Contains(s.Key))
                .Select(s => s.Match)
                .ToList();

        await transaction.CommitAsync(cancellationToken);

        return new MatchSaveResult(
            IsFirstSync: previousSeenAt is null,
            Added: added,
            Changed: changed,
            Disappeared: disappeared);
    }

    /// <summary>
    /// Arşivden (PuanDurumu) gelen maçları kaydeder. Var olan kayıtta skor, set ve yarışma bilgisi güncellenir;
    /// takvimden gelmiş saat ve salon bilgisi korunur. LastSeenAt'e dokunmaz.
    /// </summary>
    /// <returns>Yeni eklenen ve güncellenen kayıt sayısı.</returns>
    public async Task<(int Added, int Updated)> SaveArchiveAsync(IReadOnlyList<MatchDto> matches, DateTime seenAtUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var existingKeys = new HashSet<string>(StringComparer.Ordinal);
        await using (var keys = connection.CreateCommand())
        {
            keys.Transaction = transaction;
            keys.CommandText = "SELECT MatchKey FROM Matches;";
            await using var reader = await keys.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existingKeys.Add(reader.GetString(0));
            }
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Matches (
                MatchKey, Date, RawDateText, Time, RawTimeText, Venue, HomeTeam, AwayTeam, SetScores,
                League, CategoryCode, CategoryDescription, HomeSets, AwaySets, IsFinished,
                Competition, FirstSeenAt, LastSeenAt, ArchiveSeenAt)
            VALUES (
                $key, $date, $rawDate, $time, $rawTime, $venue, $home, $away, $setScores,
                $league, $category, $categoryDescription, $homeSets, $awaySets, $isFinished,
                $competition, $seenAt, $neverSeen, $seenAt)
            ON CONFLICT (MatchKey) DO UPDATE SET
                Time                = COALESCE(Matches.Time, excluded.Time),
                RawTimeText         = COALESCE(Matches.RawTimeText, excluded.RawTimeText),
                Venue               = COALESCE(Matches.Venue, excluded.Venue),
                League              = COALESCE(Matches.League, excluded.League),
                CategoryDescription = COALESCE(Matches.CategoryDescription, excluded.CategoryDescription),
                SetScores           = COALESCE(excluded.SetScores, Matches.SetScores),
                HomeSets            = COALESCE(excluded.HomeSets, Matches.HomeSets),
                AwaySets            = COALESCE(excluded.AwaySets, Matches.AwaySets),
                IsFinished          = COALESCE(excluded.IsFinished, Matches.IsFinished),
                Competition         = COALESCE(excluded.Competition, Matches.Competition),
                ArchiveSeenAt       = excluded.ArchiveSeenAt;
            """;

        var p = new MatchParameters(command);
        var competition = command.Parameters.Add("$competition", SqliteType.Text);
        command.Parameters.AddWithValue("$seenAt", seenAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$neverSeen", NeverSeenText);

        var added = 0;
        var updated = 0;
        var processed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var match in matches)
        {
            var matchKey = BuildMatchKey(match);
            if (!processed.Add(matchKey))
            {
                continue;
            }

            if (existingKeys.Contains(matchKey))
            {
                updated++;
            }
            else
            {
                added++;
            }

            p.Set(matchKey, match);
            competition.Value = Db(match.Competition);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (added, updated);
    }

    /// <summary>
    /// Arşiv taramasının en son ne zaman yapıldığı (hiç yapılmadıysa null).
    /// </summary>
    public async Task<DateTime?> GetLastArchiveSeenAtAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(ArchiveSeenAt) FROM Matches;";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string text
            ? DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            : null;
    }

    /// <summary>
    /// Tüm maçları döner: eskiden yeniye (tarih, sonra saat artan).
    /// LastSeenAtUtc: takvimde en son görülme zamanı (sadece arşiv kayıtları varsa null).
    /// </summary>
    public async Task<(IReadOnlyList<MatchDto> Matches, DateTime? LastSeenAtUtc)> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        var all = await ReadAllAsync(connection, null, cancellationToken);
        var seen = all.Where(s => s.LastSeenAt > NeverSeenOnCalendar).ToList();
        DateTime? lastSeenAt = seen.Count == 0 ? null : seen.Max(s => s.LastSeenAt);
        return (all.Select(s => s.Match).ToList(), lastSeenAt);
    }

    private static async Task<List<StoredMatch>> ReadAllAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT Date, RawDateText, Time, RawTimeText, Venue, HomeTeam, AwayTeam, SetScores,
                   League, CategoryCode, CategoryDescription, HomeSets, AwaySets, IsFinished, LastSeenAt, MatchKey,
                   Competition
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
                Competition = GetString(reader, 16),
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
    /// Bir maçı tekil tanımlayan anahtar: tarih + ev sahibi + misafir + kategori.
    /// Küme anahtarda yok: takvim ve arşiv sayfaları aynı küme için farklı kodlar kullanıyor (ör. "2LK" ve "MDK1L"),
    /// kategori kodları (MdK, YE, BYK...) ise aynı. Saat, salon ve skor değişirse kayıt güncellenir.
    /// </summary>
    public static string BuildMatchKey(MatchDto match) =>
        BuildKey(match.Date?.ToString(DateFormat, CultureInfo.InvariantCulture) ?? match.RawDateText,
            match.HomeTeam, match.AwayTeam, match.CategoryCode);

    private static string BuildKey(string? date, string? home, string? away, string? category) =>
        string.Join('|', Normalize(date), Normalize(home), Normalize(away), Normalize(category));

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

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private static object Db(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private static object Db(int? value) => value.HasValue ? value.Value : DBNull.Value;

    private static string? GetString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>
    /// İki kayıt sorgusunun ortak parametreleri.
    /// </summary>
    private sealed class MatchParameters
    {
        private readonly SqliteParameter _key, _date, _rawDate, _time, _rawTime, _venue, _home, _away, _setScores,
            _league, _category, _categoryDescription, _homeSets, _awaySets, _isFinished;

        public MatchParameters(SqliteCommand command)
        {
            _key = command.Parameters.Add("$key", SqliteType.Text);
            _date = command.Parameters.Add("$date", SqliteType.Text);
            _rawDate = command.Parameters.Add("$rawDate", SqliteType.Text);
            _time = command.Parameters.Add("$time", SqliteType.Text);
            _rawTime = command.Parameters.Add("$rawTime", SqliteType.Text);
            _venue = command.Parameters.Add("$venue", SqliteType.Text);
            _home = command.Parameters.Add("$home", SqliteType.Text);
            _away = command.Parameters.Add("$away", SqliteType.Text);
            _setScores = command.Parameters.Add("$setScores", SqliteType.Text);
            _league = command.Parameters.Add("$league", SqliteType.Text);
            _category = command.Parameters.Add("$category", SqliteType.Text);
            _categoryDescription = command.Parameters.Add("$categoryDescription", SqliteType.Text);
            _homeSets = command.Parameters.Add("$homeSets", SqliteType.Integer);
            _awaySets = command.Parameters.Add("$awaySets", SqliteType.Integer);
            _isFinished = command.Parameters.Add("$isFinished", SqliteType.Integer);
        }

        public void Set(string key, MatchDto match)
        {
            _key.Value = key;
            _date.Value = Db(match.Date?.ToString(DateFormat, CultureInfo.InvariantCulture));
            _rawDate.Value = Db(match.RawDateText);
            _time.Value = Db(match.Time?.ToString(TimeFormat, CultureInfo.InvariantCulture));
            _rawTime.Value = Db(match.RawTimeText);
            _venue.Value = Db(match.Venue);
            _home.Value = Db(match.HomeTeam);
            _away.Value = Db(match.AwayTeam);
            _setScores.Value = Db(match.SetScores);
            _league.Value = Db(match.League);
            _category.Value = Db(match.CategoryCode);
            _categoryDescription.Value = Db(match.CategoryDescription);
            _homeSets.Value = Db(match.HomeSets);
            _awaySets.Value = Db(match.AwaySets);
            _isFinished.Value = match.IsFinished.HasValue ? (match.IsFinished.Value ? 1 : 0) : DBNull.Value;
        }
    }
}
