using System.Net;
using System.Text.Json;

namespace DiscordIndexer;

// Discovery only performs GETs. The supplied client is the shared Discord rate limiter.
internal sealed class ThreadDiscovery(
    Func<string, string, Task<HttpResponseMessage>> get,
    Func<JsonElement, string, Task> seed,
    Action<string> log,
    Func<TimeSpan, Task>? delay = null)
{
    private readonly Func<TimeSpan, Task> _delay = delay ?? (duration => Task.Delay(duration));

    public static bool IsThreadParent(int type) => type is 0 or 5 or 15 or 16;
    public static bool IsThread(int type) => type is 10 or 11 or 12;

    public async Task DiscoverGuild(string apiBase, string guildId, IReadOnlyList<JsonElement> parents)
    {
        var seen = new HashSet<string>();
        using (var active = await Fetch($"{apiBase}/guilds/{guildId}/threads/active", "GET:/guilds/:guildId/threads/active"))
        {
            if (active != null) await SeedPage(active.RootElement, guildId, seen);
        }

        foreach (var parent in parents)
        {
            var type = parent.GetProperty("type").GetInt32();
            if (!IsThreadParent(type)) continue;
            var channelId = parent.GetProperty("id").GetString()!;
            await DiscoverArchive(apiBase, guildId, channelId, "threads/archived/public", false, seen);
            // Private threads only exist under text channels. Joined archives work without
            // MANAGE_THREADS; the wider private endpoint is best-effort with existing permissions.
            if (type == 0)
            {
                await DiscoverArchive(apiBase, guildId, channelId, "users/@me/threads/archived/private", true, seen);
                await DiscoverArchive(apiBase, guildId, channelId, "threads/archived/private", false, seen);
            }
        }
    }

    private async Task DiscoverArchive(string apiBase, string guildId, string channelId, string path,
        bool snowflakeCursor, HashSet<string> seen)
    {
        string? before = null;
        while (true)
        {
            var url = $"{apiBase}/channels/{channelId}/{path}?limit=100";
            if (before != null) url += $"&before={Uri.EscapeDataString(before)}";
            using var page = await Fetch(url, $"GET:/channels/:channelId/{path}");
            if (page == null) return;
            var root = page.RootElement;
            await SeedPage(root, guildId, seen);
            if (!root.TryGetProperty("has_more", out var more) || !more.GetBoolean()) return;
            var threads = root.GetProperty("threads");
            if (threads.GetArrayLength() == 0)
                throw new InvalidDataException($"Thread archive {channelId}/{path} returned has_more without a cursor.");
            var last = threads[threads.GetArrayLength() - 1];
            var next = snowflakeCursor ? last.GetProperty("id").GetString()
                : last.GetProperty("thread_metadata").GetProperty("archive_timestamp").GetString();
            if (string.IsNullOrEmpty(next) || next == before)
                throw new InvalidDataException($"Thread archive {channelId}/{path} did not advance its cursor.");
            before = next;
        }
    }

    private async Task SeedPage(JsonElement root, string guildId, HashSet<string> seen)
    {
        foreach (var thread in root.GetProperty("threads").EnumerateArray())
        {
            var id = thread.GetProperty("id").GetString();
            if (id != null && IsThread(thread.GetProperty("type").GetInt32()) && seen.Add(id))
                await seed(thread, guildId);
        }
    }

    private async Task<JsonDocument?> Fetch(string url, string route)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var response = await get(url, route);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                log($"Thread discovery skipped inaccessible endpoint ({(int)response.StatusCode}): {url}");
                return null;
            }
            if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
            {
                if (attempt >= 4) throw new HttpRequestException($"Thread discovery retry limit: {(int)response.StatusCode} {url}");
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                if ((int)response.StatusCode == 429)
                {
                    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    if (body.RootElement.TryGetProperty("retry_after", out var retry))
                        wait = TimeSpan.FromSeconds(retry.GetDouble());
                }
                await _delay(wait < TimeSpan.FromMilliseconds(250) ? TimeSpan.FromMilliseconds(250) : wait);
                continue;
            }
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }
    }
}
