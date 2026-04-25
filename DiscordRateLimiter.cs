using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace DiscordIndexer;

/// <summary>
/// Header-driven Discord API rate limiter.
///
/// Discord rate limits are bucketed per-route. The server returns:
/// - X-RateLimit-Bucket (bucket id)
/// - X-RateLimit-Remaining
/// - X-RateLimit-Reset-After (seconds)
/// - Retry-After (on 429)
///
/// We serialize requests per bucket to avoid multiple workers hammering the same bucket.
/// This is intentionally conservative (favor fewer 429s over max throughput).
/// </summary>
public sealed class DiscordRateLimiter
{
    private readonly HttpClient _http;

    public DiscordRateLimiter()
        : this(Program.Http)
    {
    }

    public DiscordRateLimiter(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    private sealed class Bucket
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public long NextAllowedUtcMs;
        public string? BucketId;
    }

    // Map routeKey -> bucket id once learned
    private readonly ConcurrentDictionary<string, string> _routeToBucket = new();
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();

    // Global rate limit (rare, but Discord can set it). When set, blocks all requests.
    private long _globalNextAllowedUtcMs;

    public async Task<HttpResponseMessage> GetAsync(string url, string routeKey)
    {
        // Wait for any global limit
        await WaitGlobalAsync();

        // Bucket key: use learned bucket id if known, else routeKey.
        var bucketKey = _routeToBucket.TryGetValue(routeKey, out var b) ? b : routeKey;
        var bucket = _buckets.GetOrAdd(bucketKey, _ => new Bucket());

        await bucket.Gate.WaitAsync();
        try
        {
            // If bucket has a delay, honor it.
            await WaitUntilAsync(bucket.NextAllowedUtcMs);

            var resp = await _http.GetAsync(url);

            // Update limiter state from headers/body.
            await ObserveAsync(routeKey, bucket, resp);

            return resp;
        }
        finally
        {
            bucket.Gate.Release();
        }
    }

    private async Task ObserveAsync(string routeKey, Bucket bucket, HttpResponseMessage resp)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Learn bucket id
        if (resp.Headers.TryGetValues("X-RateLimit-Bucket", out var bucketVals))
        {
            var bucketId = bucketVals.FirstOrDefault();
            if (!string.IsNullOrEmpty(bucketId))
            {
                bucket.BucketId = bucketId;
                _routeToBucket.TryAdd(routeKey, bucketId);
                _buckets.TryAdd(bucketId, bucket);
            }
        }

        // 429 handling: use Retry-After.
        if ((int)resp.StatusCode == 429)
        {
            var retryMs = 1000;

            var retry = resp.Headers.RetryAfter?.Delta;
            if (retry != null)
            {
                retryMs = (int)Math.Ceiling(retry.Value.TotalMilliseconds);
            }
            else
            {
                // Some Discord responses include retry_after (seconds) in JSON body
                try
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    var root = JsonDocument.Parse(body).RootElement;
                    if (root.TryGetProperty("retry_after", out var ra) && ra.TryGetDouble(out var secs))
                        retryMs = (int)Math.Ceiling(secs * 1000);

                    // Global rate limit flag
                    if (root.TryGetProperty("global", out var gl) && gl.ValueKind == JsonValueKind.True)
                    {
                        Interlocked.Exchange(ref _globalNextAllowedUtcMs, nowMs + retryMs);
                    }
                }
                catch
                {
                    // ignore
                }
            }

            if (retryMs < 250) retryMs = 250;
            bucket.NextAllowedUtcMs = Math.Max(bucket.NextAllowedUtcMs, nowMs + retryMs);
            return;
        }

        // Respect remaining/reset-after if present
        try
        {
            int? remaining = null;
            double? resetAfterSecs = null;

            if (resp.Headers.TryGetValues("X-RateLimit-Remaining", out var remVals))
            {
                var s = remVals.FirstOrDefault();
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r)) remaining = r;
            }

            if (resp.Headers.TryGetValues("X-RateLimit-Reset-After", out var resetVals))
            {
                var s = resetVals.FirstOrDefault();
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var ra)) resetAfterSecs = ra;
            }

            if (remaining != null && remaining <= 0 && resetAfterSecs != null)
            {
                var delayMs = (long)Math.Ceiling(resetAfterSecs.Value * 1000);
                if (delayMs < 250) delayMs = 250;
                bucket.NextAllowedUtcMs = Math.Max(bucket.NextAllowedUtcMs, nowMs + delayMs);
            }

            // Global rate-limit header (rare)
            if (resp.Headers.TryGetValues("X-RateLimit-Global", out var globVals))
            {
                var g = globVals.FirstOrDefault();
                if (!string.IsNullOrEmpty(g))
                {
                    // If reset-after present, treat as global.
                    if (resetAfterSecs != null)
                    {
                        var delayMs = (long)Math.Ceiling(resetAfterSecs.Value * 1000);
                        Interlocked.Exchange(ref _globalNextAllowedUtcMs, nowMs + delayMs);
                    }
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private async Task WaitGlobalAsync()
    {
        var until = Interlocked.Read(ref _globalNextAllowedUtcMs);
        await WaitUntilAsync(until);
    }

    private static async Task WaitUntilAsync(long utcMs)
    {
        if (utcMs <= 0) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var delay = utcMs - now;
        if (delay > 0)
            await Task.Delay((int)Math.Min(delay, int.MaxValue));
    }
}
