using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using BarcodeTenko.Client.Models;

namespace BarcodeTenko.Client.Services;

public sealed class ApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http;

    public ApiClient(AppConfig config)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(10)
        };

        if (!string.IsNullOrWhiteSpace(config.ClientToken))
        {
            _http.DefaultRequestHeaders.Add("X-Client-Token", config.ClientToken);
        }
        if (!string.IsNullOrWhiteSpace(config.ClientId))
        {
            _http.DefaultRequestHeaders.Add("X-Client-Id", config.ClientId);
        }
    }

    public async Task<List<Location>> GetLocationsAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<Location>>("api/locations", JsonOptions, ct).ConfigureAwait(false);
        return result ?? new List<Location>();
    }

    public async Task<ScanResponse?> SendScanAsync(ScanRecord record, CancellationToken ct = default)
    {
        var payload = new
        {
            clientScanId = record.ClientScanId,
            locationId = record.LocationId,
            code = record.StudentNumber.ToString("D5"),
            clientTime = record.CreatedAt.ToString("o")
        };

        using var response = await _http.PostAsJsonAsync("api/scan", payload, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ScanResponse>(JsonOptions, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 点呼完了の報告。最善努力で1回だけ送り、失敗しても再送しない (呼び出し側で握りつぶす)。
    /// </summary>
    public async Task CompleteSessionAsync(int? sessionId, int scanCount, string binName, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("api/session/complete",
            new { sessionId, scanCount, binName }, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task CancelAsync(string clientScanId, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("api/cancel", new { clientScanId }, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<StatusResponse?> GetStatusAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<StatusResponse>("api/status", JsonOptions, ct).ConfigureAwait(false);
}
