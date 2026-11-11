# AGENTS.md

Bu dosya, bu depoda çalışan yapay zekâ ajanları (Claude Code, Codex vb.) ve geliştiriciler için rehberdir.

## Proje özeti

**Maclar.Web**, İstanbul Voleybol İl Temsilciliği'nin maç takvimini
(`https://istanbul.voleyboliltemsilciligi.com/MacTakvimi`) HTML scraping ile çekip
aranabilir tek sayfalık bir tablo olarak gösteren ASP.NET Core MVC uygulamasıdır.

- Framework: .NET 8 (`net8.0`), ASP.NET Core MVC (Razor views)
- Bağımlılıklar: `HtmlAgilityPack` (HTML parse), `Microsoft.Data.Sqlite`, `ClosedXML` (Excel okuma)
- UI: shadcn/ui görünümü, elle yazılmış CSS (`wwwroot/css/app.css`, zinc teması değişkenleri) +
  küçük vanilla JS. React, Tailwind, Bootstrap veya jQuery **kullanılmıyor** (`wwwroot/lib` altındaki
  Bootstrap/jQuery şablondan kalma, hiçbir sayfa yüklemiyor). Font: Geist / Geist Mono (Google Fonts).
- Veritabanı: **SQLite** (`Microsoft.Data.Sqlite`, ham SQL, EF Core yok). Kimlik doğrulama **yok**.
- Deploy: Docker (`Dockerfile`, kökte) — Coolify üzerinde çalışıyor.
- Kod yorumları, log mesajları ve UI metinleri **Türkçe**. Bu geleneği koru.

## Dizin yapısı

```
Dockerfile                  # Multi-stage build (sdk:8.0 -> aspnet:8.0), port 8080
Maclar.Web/
  Program.cs                # DI kaydı + pipeline. Varsayılan route: Matches/Index
  Controllers/
    MatchesController.cs    # Index "/" (son dört gün ve sonrası), Past "/gecmis", Venues "/salonlar", Refresh (POST)
    HomeController.cs       # Şablondan kalma (Index/Privacy/Error); sadece Error kullanılıyor
  Services/
    IMatchScraperService.cs / MatchScraperService.cs   # Siteyi indirir, <table> satırlarını MatchDto'ya çevirir
    MatchStore.cs           # SQLite: tablo oluşturma, upsert (SaveAsync), listeleme (GetAllAsync)
    MatchSyncService.cs     # Çek -> DB'ye yaz -> cache'i DB'den yenile. Tüm yenilemeler buradan geçer
    ICachedMatchService.cs  / CachedMatchService.cs    # Bellek içi (in-memory) singleton cache (DB'nin kopyası)
    MatchRefreshBackgroundService.cs                   # Açılışta DB'den yükler, sonra her N dakikada senkronize eder
    MatchChangeNotifier.cs  # Senkronizasyon farklarından bildirim metni üretir
    TelegramNotifier.cs     # Telegram Bot API sendMessage
    ArchiveScraperService.cs          # PuanDurumu sayfasını (WebForms postback zinciri) gezip geçmiş maçları toplar
    ArchiveRefreshBackgroundService.cs # Arşivi günde bir tarar (son tarama zamanı DB'den)
    ProgramExcelParser.cs   # Haftalık "X. HAFTA PROGRAM.xlsx" dosyasını MatchDto listesine çevirir
    ProgramImportService.cs # Excel okuma, DB kaydı ve Telegram HTML özeti
    # Controllers/ProgramApiController.cs: fisbot için POST /api/program
    RateLimitPolicies.cs    # Rate limit sabitleri
    IstanbulClock.cs        # İstanbul saat dilimi: Now, Today, FromUtc
  Models/
    MatchDto.cs             # Tek maç kaydı
    MatchesIndexViewModel.cs  # Liste modeli (MatchDayGroup) + VenuesViewModel
    MatchFormat.cs          # Türkçe gösterim: salon/küme başlık biçimi, gün adı, skor, arama metni
  Views/
    Shared/_Layout.cshtml   # Üst bar (menü, arama, tema düğmesi), tema ilk değeri satır içi script ile
    Shared/_PageHead.cshtml # Sayfa başlığı, son güncelleme, "Verileri yenile" formu, uyarılar
    Shared/_Icon.cshtml     # Lucide SVG ikonları (<partial name="_Icon" model="@("map")" />)
    Matches/List.cshtml     # Maçlar ve Geçmiş (ortak): gün grupları, gün/küme filtresi, skor
    Matches/Venues.cshtml   # Salonlar tablosu
  wwwroot/
    css/app.css             # Tüm stiller; renkler :root ve .dark değişkenlerinden
    js/site.js              # Tema düğmesi, "/" kısayolu, yenileme formunun dönüş adresi
    js/matches.js           # Arama + gün + küme filtresi (Maçlar, Geçmiş, Salonlar)
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
- Maç anahtarı (`MatchKey`, UNIQUE): **tarih + ev sahibi + misafir + kategori**,
  `tr-TR` küçük harf, fazla boşluklar temizlenmiş, Türkçe harfler sadeleşmiş (ı/i, ş/s, ğ/g,
  ü/u, ö/o, ç/c; kaynaklar "Medıcana" / "MEDICANA" gibi farklı yazabiliyor, v2 geçişi). Küme anahtarda **yok**: takvim ve arşiv
  sayfaları aynı küme için farklı kod kullanıyor ("2LK" / "MDK1L"), kategori kodları aynı.
- Şema sürümü `PRAGMA user_version` ile tutulur (`MatchStore.Initialize`); yeni geçişi
  `MigrateToV{n}` olarak ekle. v1: `Competition`, `ArchiveSeenAt` sütunları + anahtar yenileme.
- Anahtar zaten varsa yeni satır eklenmez; sadece saat, salon, set skorları,
  set sayıları, `IsFinished` ve `LastSeenAt` güncellenir. Boş gelen değer dolu
  eski değerin üstüne yazılmaz (`COALESCE`/`NULLIF`).
- Siteden silinen maçlar DB'de kalır ve listede görünmeye devam eder.
  `LastSeenAt` sitede en son görüldüğü zamanı tutar; `FirstSeenAt` ilk eklenme.
- Bilinen sınır: bir maçın tarihi, takım adı, küme veya kategorisi sitede
  değişirse yeni kayıt oluşur, eskisi de kalır.
- Sayfalar: **Maçlar** (`/`) son dört gün ve sonrası, eskiden yeniye; tarihi belirsizler en sonda.
  **Geçmiş** (`/gecmis`) bugünden öncekiler, en yeni gün üstte (gün içinde saat artan).
  **Salonlar** (`/salonlar`) salon başına yaklaşan/toplam maç, ilk yaklaşan ve son oynanan
  tarih; salona tıklayınca `/?search=<salon>` açılır. "Bugün" = `IstanbulClock.Today`.
- Gruplama ve sıralama `MatchesController.BuildList`'te; DB zaten tarih+saat artan döner.

## Arşiv (PuanDurumu)

Maç takvimi sayfası sadece yakın tarihli maçları gösterir. Geçmiş maçlar ve skorlar
`https://istanbul.voleyboliltemsilciligi.com/PuanDurumu` sayfasından alınır.

- Sayfa ASP.NET WebForms: her açılır liste seçimi `__VIEWSTATE` ile sayfayı yeniden gönderir.
  Zincir `ddlsbe` (cinsiyet) -> `ddlSkategori` -> `ddlskume` -> `ddlSyarismaadi`; maçlar
  `icerik_gvmusabakaliste` tablosunda, hücreler `span` id son ekiyle (`gtarih`, `gsaat`, `gyer`,
  `gevsahibi`, `gseta`, `gsetb`, `gmisafir`, `gsetsonuclari`).
- Tam tarama ~154 istek, ~133 yarışma/grup sayfası; istekler arası `Archive:RequestDelayMs`
  (varsayılan 800 ms). Günde bir kez (`Archive:RefreshHours`, varsayılan 24); son tarama zamanı
  `MAX(ArchiveSeenAt)`, yani deploy'lar yeniden taramaya yol açmaz. Başarısız tarama 1 saat sonra.
- Sadece gençler (Genç/Küçük/Midi/Yıldız) var; büyükler (BYK) bu sayfada yok.
- Tarihi olmayan fikstür satırları alınmaz. "(H) - Takım" hükmen demek: önek takımdan silinir,
  skora "Hükmen: " eklenir.
- Arşiv kaydı (`SaveArchiveAsync`) skor/set/yarışma adını günceller, takvimden gelmiş saat ve
  salonu korur, **LastSeenAt'e dokunmaz**. Takvimde hiç görülmemiş kayıtlarda
  `LastSeenAt = MatchStore.NeverSeenOnCalendar` (DateTime.MinValue). Değişiklik tespiti ve
  Telegram bildirimi sadece takvimde görülmüş kayıtlara göre yapılır; arşiv bildirim üretmez.
- Arşivde salon adları kısa ("50. Yıl Deniz Esinduy"), takvimde uzun; Salonlar sayfasında
  aynı salon iki satır görünebilir. Takım adları iki sayfada farklı yazılırsa aynı maç iki
  kayıt olur (bilinen sınır).

## Haftalık Excel programı (Telegram ile)

İl müdürlüğünün haftalık program dosyası bota gönderilince veritabanına eklenir.

- Telegram'ı yalnızca **FişBot** dinler; fotoğrafları kendi işler, `.xlsx` belgelerini
  `POST /api/program` adresine `file` ve `chat_id` alanlarıyla iletir. Maclar polling yapmaz.
- İstek `Authorization: Bearer <Program:UploadToken>` başlığı taşır; anahtar yoksa adres
  kapalıdır (404), yanlışsa 401 döner. `Telegram:ChatId` tanımlıysa yalnızca o sohbet
  kabul edilir (diğerleri 403). `.xlsx`, en fazla 10 MB.
- Coolify: Maclar'da `Program__UploadToken`, FişBot'ta aynı değerle `MACLAR_UPLOAD_TOKEN`
  ve `MACLAR_UPLOAD_URL=https://maclar.bahceliler.net/api/program` ayarlanır.
- API'nin `{ ok, matches, added, message }` yanıtındaki HTML özeti FişBot sohbete yazar.
- Dosya düzeni (`ProgramExcelParser`): gün + salon başlık satırı ("26 Ekim 2026 Pazartesi" ...
  salon), altında saat | ev sahibi | misafir | küme | grup ("4B C" -> "4. Bölge C Grubu").
  Saatler Excel'de kesirli gün; dakikaya yuvarlanır.
- Gençler küme kodundan kategori alır (MDK1L -> MdK). Büyükler sponsorlu lig adıyla gelir,
  takvim koduna çevrilir: "UZMAN POSTA 2.LİG KADIN" -> 2LK, "... ERKEK" -> 2LE, "ACH 1.LİG
  KADIN/ERKEK" -> 1LK/1LE, VSL -> SULTANLAR, SGEL -> EFELER, ASKV -> ASKVK (son üçü tahmin), kategori BYK.
- Kayıt `SaveExternalAsync(markArchiveSeen: false)`: skor yok, var olan kayıtta saat/salon korunur.
  Bot cevabında okunan/yeni/var olan sayısı, saati farklı ve tarihi farklı maçlar listelenir.

Erteleme ve kopya kuralları (aynı ev sahibi + misafir + kategori, 14 gün içinde başka tarih):
- Excel gelince başka tarihli kayıt arşiv/takvimdense: yeni kayıt açılmaz, "tarihi farklı" bildirilir.
- Excel gelince başka tarihli kayıt önceki bir Excel'dense: yeni tarih geçerli, eskisi silinir.
- Arşiv veya takvim gelince: sadece Excel'den gelmiş başka tarihli kopyalar silinir.
- "Sadece Excel'den" = `LastSeenAt = NeverSeenOnCalendar` ve `ArchiveSeenAt IS NULL`.

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
- Arşiv taraması da sonucu bildirir (bitti: sayfa/maç/yeni/skoru yeni girilen; boş sonuç; hata).
  Kapatmak: `Telegram__ArchiveMessages=false`.
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
- Önce ana sayfa aynı çerezli HttpClient ile ziyaret edilir (`RootVisited` / ASP.NET oturumu).
  Doğrudan takvim isteğinde "Bu Hafta" tablosu eksik gelebilir; bu adımı kaldırma.
- `id` içinde `gvmusabakaliste` geçen **tüm** tablolar okunur (Bu Hafta + Gelecek Hafta).
  Her tabloda ilk satır başlık kabul edilip atlanır; tarih/salon devri tablo başında sıfırlanır.
- Her satırda saat formatına uyan ilk hücre "saat" çapası olur. Diğer kolonlar
  ona göre bulunur: solunda salon, onun solunda tarih (`dd.MM.yyyy`); sağında
  ev sahibi, A set, B set, misafir, set sonuçları.
- Tarih/salon hücresi boşsa (rowspan benzeri yapı) önceki satırın değeri kullanılır.
- Küme = sondan 4. hücre, Ktg (kategori kodu) = sondan 3. hücre.
- `CategoryDescription` şu an hiç doldurulmuyor (ileride kod → açıklama sözlüğü için ayrıldı).

Sitenin HTML'i değişirse ilk bakılacak yer burası. Değişiklik yapmadan önce
gerçek sayfa HTML'ini indirip yapıyı doğrula; tahmine dayalı selector yazma.

## Arama

Arama ve filtreler **yalnızca tarayıcı tarafında** yapılır (`wwwroot/js/matches.js`).
Sunucuya filtreleme ekleme.

- Sunucu her zaman sayfanın tüm listesini gönderir. Her satırda `data-search`
  (sunucuda `MatchFormat.SearchText` ile `tr-TR` küçük harf), `data-day="yyyy-MM-dd"` ve
  `data-league` var; JS bunlara bakar, hücre metnine bakmaz. Aranabilir yeni alan
  eklersen `SearchText`'e ekle.
- Kurallar: boşlukla ayrılmış tüm kelimeler eşleşmeli (AND), `"tırnaklı ifade"`
  tek kelime sayılır, `toLocaleLowerCase('tr-TR')` ile karşılaştırılır.
  Alanlar: tarih, gün adı, salon, ev sahibi, misafir, küme, kategori.
- Filtre durumu adres çubuğunda: `?search=`, `?gun=yyyy-MM-dd`, `?kume=` (`replaceState`,
  paylaşılabilir link). Gün başlığı filtreye uyan maç yoksa gizlenir, sayısı güncellenir.
- Gün düğmeleri sadece Maçlar'da (`ShowDayFilter`); son dört gündeki maç günleri solda,
  ardından "Tümü", bugün ve sonraki maç günleri sağda. "Tümü" sayfadaki tüm aralığı gösterir.
  Geçmiş'te gün sayısı büyüyeceği için gün düğmeleri yok.
- Küme menüsünde yıldızlanan kümeler `localStorage.favoriteLeagues` içinde saklanır.
  Favoriler menünün solunda hızlı filtre badge'leri olarak görünür; seçili badge'e tekrar
  tıklamak küme filtresini kaldırır. Gün ve arama filtresi korunur. Tercihler Maçlar ve
  Geçmiş arasında paylaşılır; o sayfada olmayan favoriler silinmez, sadece gösterilmez.
- "Verileri yenile" formu `returnUrl` (sayfa + filtreler) gönderir; hem `Refresh` hem rate
  limit redirect'i sadece site içi adrese döner (`//` ve `/\` ile başlayanlar reddedilir).

## Arayüz

- Renkler yalnızca `app.css`'teki `:root` / `.dark` değişkenlerinden; sayfaya sabit renk yazma.
- Koyu tema: `<html class="dark">`. İlk değer localStorage `theme` yoksa sistem tercihi.
- Satır vurgusu sadece fare üstündeyken (`--row-hover`): açık temada açık yeşil, koyu temada
  turuncu. Gün başlığı satırları gri (`--muted`); ikisini aynı renk yapma.
- Takım adları yan yana ve eşit ağırlıkta (ev sahibi sağa, misafir sola dayalı, arada `-`).
  Takımlardan birini soluk/ikincil gösterme. "Sıradaki maç" vurgusu ve özet kartları
  kullanıcı isteğiyle kaldırıldı; geri ekleme.
- Telefonda (<760px) tablo satırları kartlaşır (CSS grid); menü ve arama ikinci/üçüncü satıra iner.

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
| `Telegram:StartupMessage` | `true` | Açılışta (ilk senkronizasyondan sonra) Telegram'a durum mesajı: DB'den yüklenen sayı + ilk senkronizasyon sonucu. Env: `Telegram__StartupMessage`. |
| `Archive:Enabled` | `true` | Arşiv taraması. Env: `Archive__Enabled`. |
| `Archive:RefreshHours` | `24` | Arşiv tarama aralığı (saat). |
| `Archive:RequestDelayMs` | `800` | Arşiv taramasında istekler arası bekleme. |
| `Archive:StartupDelaySeconds` | `60` | Açılıştan sonra ilk kontrol için bekleme. |
| `Program:UploadToken` | — | FişBot Excel yükleme anahtarı. Env: `Program__UploadToken`. Boşsa API kapalı. |
| `Telegram:ArchiveMessages` | `true` | Arşiv taraması sonucunu Telegram'a bildir. |
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
