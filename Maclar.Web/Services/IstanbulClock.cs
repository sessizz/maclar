namespace Maclar.Web.Services;

/// <summary>
/// İstanbul saat dilimi yardımcıları. Veriler UTC saklanır, gösterim ve "bugün" hesabı İstanbul'a göre yapılır.
/// </summary>
public static class IstanbulClock
{
    public static readonly TimeZoneInfo Zone = FindZone();

    public static DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone);

    public static DateTime Today => Now.Date;

    public static DateTime FromUtc(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    private static TimeZoneInfo FindZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time");
        }
    }
}
