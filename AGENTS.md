# AGENTS.md

Bu dosya, bu depoda çalışan yapay zekâ ajanları (Claude Code, Codex vb.) ve geliştiriciler için rehberdir.

## Proje özeti

**Maclar.Web**, İstanbul Voleybol İl Temsilciliği'nin maç takvimini
(`https://istanbul.voleyboliltemsilciligi.com/MacTakvimi`) HTML scraping ile çekip
aranabilir tek sayfalık bir tablo olarak gösteren ASP.NET Core MVC uygulamasıdır.

- Framework: .NET 8 (`net8.0`), ASP.NET Core MVC (Razor views)
- Tek dış bağımlılık: `HtmlAgilityPack` (HTML parse)
- UI: Bootstrap 5 + jQuery (`wwwroot/lib` altında, depoya dahil)
- Veritabanı: **SQLite** (`Microsoft.Data.Sqlite`, ham SQL, EF Core yok). Kimlik doğrulama **yok**.
- Deploy: Docker (`Dockerfile`, kökte) — Coolify üzerinde çalışıyor.
- Kod yorumları, log mesajları ve UI metinleri **Türkçe**. Bu geleneği koru.

## Dizin yapısı

```
Dockerfile                  # Multi-stage build (sdk:8.0 -> aspnet:8.0), port 8080
Maclar.Web/
  Program.cs                # DI kaydı + pipeline. Varsayılan route: Matches/Index
  Controllers/
    MatchesController.cs    # Index (listeleme + sunucu tarafı arama), Refresh (POST, elle yenileme)
    HomeController.cs       # Şablondan kalma (Index/Privacy/Error); sadece Error kullanılıyor
  Services/
    IMatchScraperService.cs / MatchScraperService.cs   # Siteyi indirir, <table> satırlarını MatchDto'ya çevirir
    MatchStore.cs           # SQLite: tablo oluşturma, upsert (SaveAsync), listeleme (GetAllAsync)
    MatchSyncService.cs     # Çek -> DB'ye yaz -> cache'i DB'den yenile. Tüm yenilemeler buradan geçer
    ICachedMatchService.cs  / CachedMatchService.cs    # Bellek içi (in-memory) singleton cache (DB'nin kopyası)
    MatchRefreshBackgroundService.cs                   # Açılışta DB'den yükler, sonra her N dakikada senkronize eder
    MatchChangeNotifier.cs  # Senkronizasyon farklarından bildirim metni üretir
    TelegramNotifier.cs     # Telegram Bot API sendMessage
    RateLimitPolicies.cs    # Rate limit sabitleri
  Models/
    MatchDto.cs             # Tek maç kaydı
    MatchesIndexViewModel.cs
  Views/Matches/Index.cshtml  # Tablo + istemci tarafı canlı filtreleme (inline JS)
  wwwroot/                  # Statik dosyalar, lib/ altında vendor kütüphaneler
```

## Veri akışı ve maç verilerinin saklandığı yer

Kalıcı kaynak **SQLite veritabanıdır**; bellek içi cache onun kopyasıdır.

- Dosya: `ConnectionStrings:Matches` (varsayılan `Data Source=data/maclar.db`,
  content root'a göre). Yerelde `Maclar.Web/data/maclar.db`, Docker'da
  `/app/data/maclar.db`. `data/` git'e ve Docker build context'ine dahil değil.
- **Docker'da `/app/data` kalıcı volume olmalı** (Coolify: Persistent Storage).
  Olmazsa her deploy'da veritabanı sıfırlanır.
- Data Protection anahtarları da `data/keys` altında tutulur (antiforgery ve
  TempData cookie'leri bunlarla şifrelenir). Kalıcı olmazsa her deploy sonrası
  açık kalan sayfalardaki "Verileri yenile" 400 verir.
- Tablo uygulama açılışında `MatchStore.Initialize()` ile oluşturulur
  (`CREATE TABLE IF NOT EXISTS`). Migration sistemi yok; şema değişikliğinde
  `Initialize()` içine geriye uyumlu `ALTER TABLE` ekle, var olan veriyi silme.

Akış:
1. Açılışta `MatchRefreshBackgroundService` önce DB'deki tüm maçları cache'e
   yükler (`MatchSyncService.LoadFromStoreAsync`), sonra siteyi çeker ve
   `Matches:RefreshMinutes` (varsayılan **60**) dakikada bir tekrarlar.
2. Her senkronizasyon (`MatchSyncService.SyncAsync`, `SemaphoreSlim` ile tekil):
   scraper -> `MatchStore.SaveAsync` (upsert) -> `GetAllAsync` ile cache yenilenir.
3. `MatchesController.Index` cache'i okur. Cache boşsa (DB de boş) bir kereliğine
   `SyncAsync` çağırır.
4. `POST /Matches/Refresh` `SyncAsync` çağırır. Hata olursa DB ve cache korunur,
   kullanıcıya `TempData["RefreshMessage"]` ile bilgi verilir; arama korunarak
   `/?search=...`'e dönülür.

Tekillik ve güncelleme kuralları (`MatchStore`):
- Maç anahtarı (`MatchKey`, UNIQUE): **tarih + ev sahibi + misafir + küme + kategori**,
  `tr-TR` küçük harf, fazla boşluklar temizlenmiş.
- Anahtar zaten varsa yeni satır eklenmez; sadece saat, salon, set skorları,
  set sayıları, `IsFinished` ve `LastSeenAt` güncellenir. Boş gelen değer dolu
  eski değerin üstüne yazılmaz (`COALESCE`/`NULLIF`).
- Siteden silinen maçlar DB'de kalır ve listede görünmeye devam eder.
  `LastSeenAt` sitede en son görüldüğü zamanı tutar; `FirstSeenAt` ilk eklenme.
- Bilinen sınır: bir maçın tarihi, takım adı, küme veya kategorisi sitede
  değişirse yeni kayıt oluşur, eskisi de kalır.
- Liste sırası: eskiden yeniye (tarih, sonra saat artan). Sayfa açılınca istemci
  bugün veya sonrasındaki ilk maça kaydırır, bugünün satırları vurgulanır
  ("Bugüne git" butonu aynı işi yapar). Bugün = `Europe/Istanbul` tarihine göre,
  satırlardaki `data-date="yyyy-MM-dd"` ile karşılaştırılır.

## Değişiklik bildirimi (Telegram)

`MatchStore.SaveAsync` her senkronizasyonda bir öncekine göre farkları döner
(`MatchSaveResult`: yeni, saat/salon değişen, kaybolan). "Önceki senkronizasyonda
görülen" = `LastSeenAt` değeri en büyük olan kayıtlar. `MatchChangeNotifier`
bunlardan mesaj üretir, `TelegramNotifier` gönderir:

- **Tarihi değişen:** kaybolan bir maç ile aynı ev sahibi + misafir + küme +
  kategoride yeni gelen maç eşleşirse (`MatchStore.BuildFixtureKey`).
- **Saati / salonu değişen:** aynı anahtarlı maçta saat veya salon farklı (bugün ve sonrası).
- **Siteden kaldırılan:** eşleşmeyen kaybolan maç, tarihi bugün veya sonrası.
- Bildirilmeyenler: ilk senkronizasyon, boş çekim, yeni fikstürler, oynanmış
  eski maçların siteden düşmesi. 30'dan fazla maç birden kaybolursa tek bir
  "olağandışı değişiklik" uyarısı gider (site/scraper bozulmuş olabilir).
- Tarihi değişen maçta eski tarihli kayıt DB'de kalır (listede iki satır görünür).
- Bildirim hatası senkronizasyonu bozmaz. Telegram yapılandırılmamışsa mesaj log'a yazılır.
- **Güvenlik:** bot token istek URL'sinde. Telegram HttpClient'ında
  `RemoveAllLoggers()` bu yüzden var; kaldırma, URL'yi asla log'a yazma.

## Rate limiting (flood koruması)

- `POST /Matches/Refresh`, `[EnableRateLimiting(RateLimitPolicies.Refresh)]` ile
  korunur: **IP başına dakikada 20 istek** (fixed window, kuyruk yok).
  Sabitler `Services/RateLimitPolicies.cs`'te, politika `Program.cs`'te.
- Limit aşılınca 429 yerine `/?search=<formdaki arama>`'ya 303 redirect yapılır
  ve mesaj TempData ile gösterilir.
- Gerçek istemci IP'si için `UseForwardedHeaders` açık (`ForwardLimit = 1`,
  bilinen proxy listesi boş). Bu, uygulamanın Coolify/Traefik arkasında olmasına
  dayanır; uygulama proxy'siz doğrudan internete açılırsa `X-Forwarded-For`
  sahtelenebilir, o durumda `KnownProxies`/`KnownNetworks` ayarlanmalı.
- Rate limiter routing'den sonra, antiforgery doğrulamasından önce çalışır;
  geçersiz token'lı istekler de limite sayılır.

Sonuçları:
- Uygulama/container yeniden başlayınca veri kaybolur, ilk istek veya background
  servis yeniden çekene kadar liste boş olabilir.
- Birden fazla instance çalışırsa her birinin ayrı cache'i olur.
- Kalıcı saklama gerekirse `CachedMatchService`'i genişlet (ör. JSON dosyası
  veya SQLite); `ICachedMatchService` arayüzü okuma tarafını zaten soyutluyor.

## Scraper notları (en kırılgan kısım)

`MatchScraperService` hedef sitenin HTML yapısına sıkı bağlıdır:
- Sayfadaki **ilk** `<table>` alınır, ilk satır başlık kabul edilip atlanır.
- Her satırda saat formatına uyan ilk hücre "saat" çapası olur. Diğer kolonlar
  ona göre bulunur: solunda salon, onun solunda tarih (`dd.MM.yyyy`); sağında
  ev sahibi, A set, B set, misafir, set sonuçları.
- Tarih/salon hücresi boşsa (rowspan benzeri yapı) önceki satırın değeri kullanılır.
- Küme = sondan 4. hücre, Ktg (kategori kodu) = sondan 3. hücre.
- `CategoryDescription` şu an hiç doldurulmuyor (ileride kod → açıklama sözlüğü için ayrıldı).

Sitenin HTML'i değişirse ilk bakılacak yer burası. Değişiklik yapmadan önce
gerçek sayfa HTML'ini indirip yapıyı doğrula; tahmine dayalı selector yazma.

## Arama

Arama **yalnızca tarayıcı tarafında** yapılır (`Views/Matches/Index.cshtml`
içindeki inline script). Sunucuya filtreleme ekleme.

- Sunucu her zaman tüm listeyi gönderir; `?search=` değeri sadece arama kutusunu
  doldurur ve sayfa açılınca filtre bu değerle bir kez çalışır.
- Kurallar: boşlukla ayrılmış tüm kelimeler eşleşmeli (AND), `"tırnaklı ifade"`
  tek kelime sayılır, `toLocaleLowerCase('tr-TR')` ile karşılaştırılır.
  Alanlar: tarih, salon, ev sahibi, misafir, küme, kategori hücreleri.
- Yazdıkça `history.replaceState` ile adres çubuğundaki `?search=` güncellenir
  (paylaşılabilir link). Enter sayfayı yeniden yüklemez.
- "Verileri yenile" ve rate limit redirect'i aramayı `?search=` ile korur.

Türkçe karakterler (İ/ı) nedeniyle karşılaştırmalarda daima `tr-TR` kültürü kullan.

## Saat dilimi

`LastUpdatedAt` UTC saklanır; view'da `Europe/Istanbul`
(Windows'ta fallback `Turkey Standard Time`) ile gösterilir. Yeni tarih/saat
alanlarında aynı kuralı izle: UTC sakla, gösterirken İstanbul'a çevir.

## Komutlar

```bash
# Geliştirme (http://localhost:5169)
dotnet run --project Maclar.Web/Maclar.Web.csproj

# Build
dotnet build Maclar.Web/Maclar.Web.csproj

# Docker
docker build -t maclar .
docker run -p 8080:8080 -v maclar-data:/app/data maclar
```

Test projesi yok. Değişiklik sonrası en azından `dotnet build` çalıştır ve
uygulamayı açıp tabloyu, aramayı ve "Verileri yenile" butonunu kontrol et.

## Yapılandırma

| Anahtar | Varsayılan | Açıklama |
|---|---|---|
| `Matches:RefreshMinutes` | 60 | Background yenileme aralığı (dakika). `appsettings.json`'da tanımlı değil; env ile `Matches__RefreshMinutes` verilebilir. |
| `Telegram:BotToken` | — | Bot token. Env: `Telegram__BotToken`. Gizli; repoya koyma. |
| `Telegram:ChatId` | — | Bildirimin gideceği sohbet/grup id'si. Env: `Telegram__ChatId`. |
| `ConnectionStrings:Matches` | `Data Source=data/maclar.db` | SQLite bağlantısı. Env: `ConnectionStrings__Matches`. |
| `ASPNETCORE_URLS` | `http://0.0.0.0:8080` (Docker) | Dinlenen adres. |

## Kurallar

- Hedef framework `net8.0`; Dockerfile da 8.0 imajlarını kullanıyor. Framework'ü
  yükseltirsen ikisini birlikte güncelle.
- Scraper'ın kullandığı `User-Agent`/`Referer` başlıklarını kaldırma; site basit
  bot korumasına sahip olabilir.
- Hedef siteye yük bindirme: yenileme aralığını çok düşürme, istek başına
  scraping ekleme (cache'i kullan).
- Yeni servisleri `Program.cs`'te kaydet; scraper `AddHttpClient` ile
  typed client olarak kayıtlı, `HttpClient`'ı elle `new`'leme.
- `CachedMatchService` hem `ICachedMatchService` (okuma) hem de somut sınıf
  (yazma) olarak aynı singleton örneğiyle kayıtlı; bu ikiliği bozma.
- Cache'e doğrudan yazma; her yenileme `MatchSyncService.SyncAsync` üzerinden
  geçmeli ki veriler DB'ye de yazılsın.
- SQL'de daima parametre kullan (`$param`), string birleştirme yapma.
- Küçük, odaklı değişiklikler yap; şablondan kalan kodu istenmeden silme.

## Bilinen sorunlar / teknik borç

- `UseHttpsRedirection` container içinde (sadece HTTP 8080) gereksiz uyarı
  üretebilir; TLS reverse proxy (Coolify) tarafından sonlandırılıyor.
- `HomeController` Index/Privacy view'ları kullanılmıyor.
- `IsFinished` mantığı basit: herhangi bir set skoru varsa maç bitti sayılıyor.
