using System.Threading.RateLimiting;
using Maclar.Web.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

builder.Services.AddHttpClient<IMatchScraperService, MatchScraperService>();
builder.Services.AddSingleton<CachedMatchService>();
builder.Services.AddSingleton<ICachedMatchService>(sp => sp.GetRequiredService<CachedMatchService>());
builder.Services.AddSingleton<MatchStore>();
builder.Services.AddSingleton<MatchSyncService>();
builder.Services.AddHostedService<MatchRefreshBackgroundService>();

// Uygulama Coolify (Traefik) reverse proxy'sinin arkasında çalışıyor. Gerçek istemci IP'sini
// X-Forwarded-For'dan almazsak tüm kullanıcılar proxy IP'si ile aynı rate limit kovasına düşer.
// ForwardLimit = 1: sadece proxy'nin en son eklediği (en sağdaki) değer kullanılır;
// istemcinin kendi gönderdiği sahte X-Forwarded-For değerleri dikkate alınmaz.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// "Verileri yenile" butonu her basışta kaynak siteye istek atıyor. Flood'a karşı
// IP başına dakikada en fazla RefreshPolicyPermitLimit istek.
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy(RateLimitPolicies.Refresh, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = RateLimitPolicies.RefreshPermitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    // Form POST'u olduğu için kullanıcıya boş 429 sayfası yerine mesajla birlikte listeye dön.
    options.OnRejected = async (context, cancellationToken) =>
    {
        var httpContext = context.HttpContext;
        var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("RateLimiting");
        logger.LogWarning("Yenileme isteği rate limit'e takıldı. IP: {Ip}", httpContext.Connection.RemoteIpAddress);

        var tempData = httpContext.RequestServices.GetRequiredService<ITempDataDictionaryFactory>().GetTempData(httpContext);
        tempData["RefreshMessage"] = "Çok sık yenileme isteği gönderdiniz. Lütfen bir dakika sonra tekrar deneyin.";
        tempData.Save();

        // Kullanıcının aramasını kaybetmemek için formdaki "search" değeriyle geri dön.
        string? search = null;
        if (httpContext.Request.HasFormContentType)
        {
            var form = await httpContext.Request.ReadFormAsync(cancellationToken);
            search = form["search"].ToString().Trim();
        }

        httpContext.Response.StatusCode = StatusCodes.Status303SeeOther;
        httpContext.Response.Headers.Location = string.IsNullOrEmpty(search)
            ? "/"
            : $"/{QueryString.Create("search", search)}";
    };
});

var app = builder.Build();

// Veritabanı dosyasını ve tabloyu oluştur (yoksa).
app.Services.GetRequiredService<MatchStore>().Initialize();

app.UseForwardedHeaders();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Matches}/{action=Index}/{id?}");


app.Run();
