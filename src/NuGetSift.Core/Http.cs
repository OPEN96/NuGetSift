using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace NuGetSift.Core;

/// <summary>带重试与鉴权的 HTTP 访问封装（CLI 与桌面客户端共用）。</summary>
internal static class Http
{
    public static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 64
    })
    { Timeout = TimeSpan.FromSeconds(60) };

    public static void ApplyAuth(HttpRequestMessage req, BaGetConfig? cfg)
    {
        if (cfg == null || string.IsNullOrEmpty(cfg.ApiKey)) return;

        if (string.Equals(cfg.AuthType, "bearer", StringComparison.OrdinalIgnoreCase))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
        }
        else if (string.Equals(cfg.AuthType, "basic", StringComparison.OrdinalIgnoreCase))
        {
            var token = Convert.ToBase64String(Encoding.ASCII.GetBytes(cfg.ApiKey));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
    }

    public static async Task<JsonDocument> GetJsonAsync(
        string url, BaGetConfig? cfg, int retries, CancellationToken ct)
    {
        Exception? last = null;
        for (var i = 0; i <= retries; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyAuth(req, cfg);
                req.Headers.Add("Accept", "application/json");
                using var resp = await Client.SendAsync(req, ct).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                    throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.StatusCode}");

                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (i < retries)
            {
                last = ex;
                await DelayAsync(i, ct).ConfigureAwait(false);
            }
        }
        throw new Exception($"HTTP 请求失败(已重试 {retries} 次): {url} — {last?.Message}");
    }

    public static async Task<HttpStatusCode> GetStatusAsync(string url, int retries, CancellationToken ct)
    {
        for (var i = 0; i <= retries; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await Client.SendAsync(req, ct).ConfigureAwait(false);

                if (resp.StatusCode == HttpStatusCode.NotFound) return HttpStatusCode.NotFound;
                if ((int)resp.StatusCode >= 500 || resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (i < retries) { await DelayAsync(i, ct).ConfigureAwait(false); continue; }
                }
                return resp.StatusCode;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) when (i < retries)
            {
                await DelayAsync(i, ct).ConfigureAwait(false);
            }
        }
        return HttpStatusCode.ServiceUnavailable;
    }

    public static async Task<string> GetTextAsync(
        string url, BaGetConfig? cfg, int retries, CancellationToken ct)
    {
        for (var i = 0; i <= retries; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyAuth(req, cfg);
                req.Headers.Add("Accept", "application/atom+xml,application/xml,xml");
                using var resp = await Client.SendAsync(req, ct).ConfigureAwait(false);

                if (resp.StatusCode == HttpStatusCode.NotFound) return "";
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) when (i < retries)
            {
                await DelayAsync(i, ct).ConfigureAwait(false);
            }
        }
        return "";
    }

    public static async Task<byte[]?> GetBytesAsync(
        string url, BaGetConfig? cfg, int retries, CancellationToken ct)
    {
        for (var i = 0; i <= retries; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyAuth(req, cfg);
                using var resp = await Client.SendAsync(
                    req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    if (i < retries && ((int)resp.StatusCode >= 500 ||
                                        resp.StatusCode == HttpStatusCode.TooManyRequests))
                    {
                        await DelayAsync(i, ct).ConfigureAwait(false);
                        continue;
                    }
                    return null;
                }
                return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) when (i < retries)
            {
                await DelayAsync(i, ct).ConfigureAwait(false);
            }
        }
        return null;
    }

    private static Task DelayAsync(int attempt, CancellationToken ct)
        => Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
}
