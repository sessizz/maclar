namespace Maclar.Web.Services;

public static class RateLimitPolicies
{
    /// <summary>
    /// "Verileri yenile" (POST /Matches/Refresh) için IP başına sabit pencere politikası.
    /// </summary>
    public const string Refresh = "refresh";

    /// <summary>
    /// Bir IP'nin dakikada yapabileceği en fazla yenileme isteği.
    /// </summary>
    public const int RefreshPermitLimit = 20;
}
