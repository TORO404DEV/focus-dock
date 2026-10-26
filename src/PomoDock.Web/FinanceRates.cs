using System.Text.Json;

namespace PomoDock.Web;

/// <summary>Pulls USD→MXN from Frankfurter (ECB). Offline, the book keeps the last stored rate.</summary>
internal static class FinanceRates
{
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(8) };
    // The desktop's frankfurter.app address now redirects without CORS headers; the browser needs the new host.
    private static readonly Uri endpoint = new("https://api.frankfurter.dev/v1/latest?from=USD&to=MXN");

    public static async Task<decimal?> FetchUsdToMxnAsync(CancellationToken cancellation = default)
    {
        try
        {
            using var response = await http.GetAsync(endpoint, cancellation);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
            if (!doc.RootElement.TryGetProperty("rates", out var rates)) return null;
            if (!rates.TryGetProperty("MXN", out var mxn)) return null;
            decimal value = mxn.GetDecimal();
            return value > 0 ? decimal.Round(value, 4, MidpointRounding.AwayFromZero) : null;
        }
        catch (Exception) { return null; }
    }
}
