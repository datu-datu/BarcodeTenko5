using System.Net;
using System.Net.Http;
using System.IO;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using BarcodeTenko.Viewer.Models;

namespace BarcodeTenko.Viewer.Services;

public sealed class ApiAuthException : Exception
{
    public ApiAuthException(string message)
        : base(message)
    {
    }
}

public sealed class ApiConfigurationException : Exception
{
    public ApiConfigurationException(string message)
        : base(message)
    {
    }
}

public enum LoginOutcome
{
    Success,
    InvalidPassword,
    RateLimited,
    NotConfigured,
    Error
}

public sealed class LoginResult
{
    public LoginOutcome Outcome { get; init; } = LoginOutcome.Error;
    public string Message { get; init; } = "";
    public int RetryAfterSeconds { get; init; }
}

public sealed class ViewerApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClientHandler _handler;
    private readonly HttpClient _http;
    private readonly HttpClient _streamHttp;

    public ViewerApiClient(ViewerConfig config)
    {
        var baseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/");
        if (baseAddress.Scheme != Uri.UriSchemeHttps && !IsLoopback(baseAddress.Host))
        {
            throw new InvalidOperationException("リモートサーバーへの接続には HTTPS が必要です。localhost の HTTP 接続は使用できます。");
        }

        _handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };

        _http = new HttpClient(_handler, disposeHandler: false)
        {
            BaseAddress = baseAddress,
            Timeout = RequestTimeout
        };
        _streamHttp = new HttpClient(_handler, disposeHandler: false)
        {
            BaseAddress = baseAddress,
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static bool IsLoopback(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);

    public async Task<bool> CheckSessionAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("admin/api/me", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return false;
        }

        await EnsureAuthorizedAsync(response, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<LoginResult> LoginAsync(string password, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync("admin/api/login", new { password }, ct).ConfigureAwait(false);
            var (message, retryAfter) = await ReadErrorAsync(response, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return new LoginResult { Outcome = LoginOutcome.Success, Message = "ログインしました" };
            }

            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => new LoginResult
                {
                    Outcome = LoginOutcome.InvalidPassword,
                    Message = string.IsNullOrEmpty(message) ? "パスワードが正しくありません" : message
                },
                HttpStatusCode.TooManyRequests => new LoginResult
                {
                    Outcome = LoginOutcome.RateLimited,
                    RetryAfterSeconds = retryAfter,
                    Message = string.IsNullOrEmpty(message)
                        ? "ログイン試行回数が上限を超えました。約5分後に再試行してください。"
                        : message
                },
                HttpStatusCode.ServiceUnavailable => new LoginResult
                {
                    Outcome = LoginOutcome.NotConfigured,
                    Message = string.IsNullOrEmpty(message)
                        ? "管理APIが有効化されていません (.env の ADMIN_PASSWORD / SESSION_SECRET)。"
                        : message
                },
                _ => new LoginResult
                {
                    Outcome = LoginOutcome.Error,
                    Message = string.IsNullOrEmpty(message) ? $"HTTP {(int)response.StatusCode}" : message
                }
            };
        }
        catch (OperationCanceledException)
        {
            return new LoginResult { Outcome = LoginOutcome.Error, Message = "接続がタイムアウトしました。" };
        }
        catch (HttpRequestException ex)
        {
            return new LoginResult { Outcome = LoginOutcome.Error, Message = "サーバに接続できません: " + ex.Message };
        }
    }

    public Task<SummaryResponse?> GetSummaryAsync(CancellationToken ct = default)
        => GetJsonAsync<SummaryResponse>("admin/api/summary", ct);

    public async Task<List<AttendanceRow>> GetScansAsync(int? sessionId, bool includeDeleted = false, CancellationToken ct = default)
    {
        const int pageSize = 500;
        var all = new List<AttendanceRow>();
        var offset = 0;

        while (true)
        {
            var query = $"limit={pageSize}&offset={offset}&includeDeleted={(includeDeleted ? "1" : "0")}";
            if (sessionId is int id)
            {
                query += $"&sessionId={id}";
            }

            var page = await GetJsonAsync<ScansPageResponse>("admin/api/scans?" + query, ct).ConfigureAwait(false);
            if (page is null || page.Rows.Count == 0)
            {
                break;
            }

            all.AddRange(page.Rows);
            if (all.Count >= page.Total)
            {
                break;
            }

            offset += page.Rows.Count;
        }

        return all;
    }

    public async IAsyncEnumerable<ServerEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "admin/api/stream");
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await _streamHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureAuthorizedAsync(response, ct).ConfigureAwait(false);

        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
        {
            throw new HttpRequestException("SSEエンドポイントから text/event-stream が返されませんでした。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var eventName = "message";
        var data = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                if (data.Length > 0)
                {
                    yield return new ServerEvent { Name = eventName, Data = data.ToString(0, data.Length - 1) };
                }

                yield break;
            }

            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return new ServerEvent { Name = eventName, Data = data.ToString(0, data.Length - 1) };
                }

                eventName = "message";
                data.Clear();
                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            var separator = line.IndexOf(':');
            var field = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? "" : line[(separator + 1)..].TrimStart(' ');
            if (field == "event")
            {
                eventName = value;
            }
            else if (field == "data")
            {
                data.Append(value).Append('\n');
            }
        }
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        await EnsureAuthorizedAsync(response, ct).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(body) ? default : JsonSerializer.Deserialize<T>(body, JsonOptions);
    }

    private static async Task EnsureAuthorizedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var (message, _) = await ReadErrorAsync(response, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new ApiAuthException(string.IsNullOrEmpty(message) ? "管理セッションが無効です。再ログインしてください。" : message);
        }

        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            throw new ApiConfigurationException(string.IsNullOrEmpty(message) ? "サーバーの管理APIが無効です。" : message);
        }

        throw new HttpRequestException(string.IsNullOrEmpty(message)
            ? $"HTTP {(int)response.StatusCode}"
            : $"HTTP {(int)response.StatusCode}: {message}");
    }

    private static async Task<(string Message, int RetryAfter)> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return ("", 0);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return ("", 0);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var message = doc.RootElement.TryGetProperty("error", out var error) ? error.GetString() ?? "" : "";
            var retry = doc.RootElement.TryGetProperty("retryAfter", out var value) && value.TryGetInt32(out var seconds)
                ? seconds
                : 0;
            return (message, retry);
        }
        catch (JsonException)
        {
            return ("", 0);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _streamHttp.Dispose();
        _handler.Dispose();
    }
}
