using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Driver;

namespace DiscordIndexer;

public class Program
{
    internal static readonly HttpClient Http = new();

    // Discord rate-limit coordination (header-driven). Key goal: avoid 429s by serializing per bucket.
    private static readonly DiscordRateLimiter RateLimiter = new();

    private static IMongoCollection<BsonDocument>? _messages;
    private static IMongoCollection<BsonDocument>? _backfill;
    private static IMongoCollection<BsonDocument>? _users;
    private static IMongoCollection<BsonDocument>? _channels;

    // In-memory channel cache: channel_id -> (guild_id, last_seen_ms)
    private static readonly ConcurrentDictionary<string, (string? guildId, long lastSeenMs)> ChannelCache = new();

    private static int _backfillPageSize = 100; // Discord max
    private static int _backfillWorkers = 2;
    private static int _backfillRequestDelayMs = 500;
    private static TimeSpan _backfillClaimTimeout = BackfillState.DefaultClaimTimeout;

    public static async Task Main(string[] args)
    {
        Console.WriteLine("Starting Discord Indexer (.NET)");

        var token = GetEnv("DISCORD_BOT_TOKEN");
        var apiBase = GetEnv("DISCORD_API_BASE", "https://discord.com/api/v10").TrimEnd('/');
        var gatewayUrl = GetEnv("DISCORD_GATEWAY_URL", "wss://gateway.discord.gg/?v=10&encoding=json");
        var guildIdsCsv = GetEnv("DISCORD_GUILD_IDS", "");
        // Default intents:
        // - GUILDS (1)
        // - GUILD_MESSAGES (512)
        // - DIRECT_MESSAGES (4096)
        // NOTE: MESSAGE_CONTENT (32768) is privileged and must be enabled in the Discord Developer Portal.
        // We do NOT enable it by default.
        var intents = int.Parse(GetEnv("DISCORD_INTENTS", "4609"));

        var mongoUri = GetEnv("MONGODB_URI", "mongodb://localhost:27017");
        var mongoDbName = GetEnv("MONGODB_DB", "discord_index");

        _backfillPageSize = int.Parse(GetEnv("INDEXER_BACKFILL_PAGE_SIZE", _backfillPageSize.ToString()));
        _backfillWorkers = int.Parse(GetEnv("INDEXER_BACKFILL_WORKERS", _backfillWorkers.ToString()));
        _backfillRequestDelayMs = int.Parse(GetEnv("INDEXER_BACKFILL_REQUEST_DELAY_MS", _backfillRequestDelayMs.ToString()));
        var claimTimeoutMs = int.Parse(GetEnv("INDEXER_BACKFILL_CLAIM_TIMEOUT_MS", ((int)BackfillState.DefaultClaimTimeout.TotalMilliseconds).ToString()));
        _backfillClaimTimeout = TimeSpan.FromMilliseconds(Math.Max(1000, claimTimeoutMs));

        if (_backfillPageSize is < 1 or > 100) _backfillPageSize = 100;

        // Mongo
        Console.WriteLine($"Connecting to MongoDB: {mongoUri}");
        var client = new MongoClient(mongoUri);
        var db = client.GetDatabase(mongoDbName);
        _messages = db.GetCollection<BsonDocument>("messages");
        _backfill = db.GetCollection<BsonDocument>("channel_backfill");
        _users = db.GetCollection<BsonDocument>("users");
        _channels = db.GetCollection<BsonDocument>("channels");
        await EnsureIndexes();
        Console.WriteLine("MongoDB indexes ensured.");

        // HTTP auth
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", token);

        // Seed channels for backfill
        var guildIds = guildIdsCsv
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct()
            .ToArray();

        if (guildIds.Length == 0)
        {
            Console.WriteLine("DISCORD_GUILD_IDS not set; discovering guilds via Discord API...");
            guildIds = await ListGuildIds(apiBase);

            if (guildIds.Length == 0)
            {
                Console.WriteLine("WARN: No guilds returned from /users/@me/guilds. Backfill disabled.");
                Console.WriteLine("(Live gateway ingestion will still run.)");
            }
        }

        if (guildIds.Length > 0)
        {
            foreach (var gid in guildIds)
            {
                await SeedGuildChannels(apiBase, gid);
            }

            Console.WriteLine($"Starting backfill workers: {_backfillWorkers} (pageSize={_backfillPageSize})");
            for (var i = 0; i < _backfillWorkers; i++)
            {
                _ = Task.Run(() => BackfillWorkerLoop(apiBase));
            }
        }

        // Live gateway ingestion
        Console.WriteLine("Starting Discord gateway live ingestion...");
        while (true)
        {
            try
            {
                await RunGatewayLoop(gatewayUrl, token, intents);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Gateway loop error: {ex.Message}");
            }

            await Task.Delay(5000);
        }
    }

    private static async Task EnsureIndexes()
    {
        if (_messages == null || _backfill == null || _users == null || _channels == null) return;

        await _messages.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("message_id"),
            new CreateIndexOptions { Unique = true }));

        await _messages.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("channel_id").Descending("timestamp_ms")));

        await _messages.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("author_id").Descending("timestamp_ms")));

        await _messages.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Descending("timestamp_ms")));

        await _messages.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("guild_id").Descending("timestamp_ms")));

        await _backfill.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("channel_id"),
            new CreateIndexOptions { Unique = true }));

        await _backfill.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("done").Ascending("updated_at")));

        await _users.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("user_id"),
            new CreateIndexOptions { Unique = true }));

        await _users.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Descending("last_seen_ms")));

        await _channels.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("channel_id"),
            new CreateIndexOptions { Unique = true }));

        await _channels.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("guild_id").Ascending("channel_id")));

        await _channels.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Descending("last_seen_ms")));
    }

    private static async Task<string[]> ListGuildIds(string apiBase)
    {
        // Uses GET /users/@me/guilds (Bot token)
        // Docs: https://discord.com/developers/docs/resources/user#get-current-user-guilds
        // This is paginated. We request up to 200 per page.

        var ids = new List<string>();
        string? after = null;

        while (true)
        {
            var url = $"{apiBase}/users/@me/guilds?limit=200";
            if (!string.IsNullOrEmpty(after)) url += $"&after={Uri.EscapeDataString(after)}";

            var resp = await RateLimiter.GetAsync(url, routeKey: "GET:/users/@me/guilds");
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"WARN: Failed to list guilds: {(int)resp.StatusCode} {resp.ReasonPhrase}");
                return ids.ToArray();
            }

            var json = await resp.Content.ReadAsStringAsync();
            var arr = JsonDocument.Parse(json).RootElement;
            if (arr.ValueKind != JsonValueKind.Array) break;

            var page = arr.EnumerateArray().ToArray();
            if (page.Length == 0) break;

            foreach (var g in page)
            {
                var id = g.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var name = g.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;

                if (!string.IsNullOrEmpty(id))
                {
                    ids.Add(id);
                    Console.WriteLine($"Discovered guild: {name ?? "(no-name)"} ({id})");
                    after = id;
                }
            }

            // If the API returns less than limit, we're done.
            if (page.Length < 200) break;
        }

        return ids.Distinct().ToArray();
    }

    private static async Task SeedGuildChannels(string apiBase, string guildId)
    {
        if (_backfill == null) return;

        Console.WriteLine($"Fetching channels for guild {guildId}...");
        var url = $"{apiBase}/guilds/{guildId}/channels";
        var resp = await RateLimiter.GetAsync(url, routeKey: "GET:/guilds/:guildId/channels");
        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"WARN: Failed to list channels for guild {guildId}: {(int)resp.StatusCode} {resp.ReasonPhrase}");
            return;
        }

        var json = await resp.Content.ReadAsStringAsync();
        var arr = JsonDocument.Parse(json).RootElement;
        if (arr.ValueKind != JsonValueKind.Array) return;

        foreach (var ch in arr.EnumerateArray())
        {
            var type = ch.GetProperty("type").GetInt32();
            // 0 = GUILD_TEXT, 5 = GUILD_ANNOUNCEMENT
            if (type != 0 && type != 5) continue;

            var channelId = ch.GetProperty("id").GetString();
            if (string.IsNullOrEmpty(channelId)) continue;

            // Keep a lookup of channel_id -> metadata (name/type/parent) for later analysis.
            // This also helps fill guild_id when Discord message payloads omit it.
            await UpsertChannelFromGuildList(ch, guildId);

            await SeedBackfillChannel(channelId!, guildId);
        }
    }

    private static async Task SeedBackfillChannel(string channelId, string guildId)
    {
        if (_backfill == null) return;

        var filter = Builders<BsonDocument>.Filter.Eq("channel_id", channelId);
        var existing = await _backfill.Find(filter).FirstOrDefaultAsync();
        if (existing != null) return;

        var doc = new BsonDocument
        {
            { "channel_id", channelId },
            { "guild_id", guildId },
            { "cursor_before", BsonNull.Value },
            { "done", false },
            { "claimed", false },
            { "created_at", DateTime.UtcNow },
            { "updated_at", DateTime.UtcNow },
            { "error_count", 0 },
        };

        try
        {
            await _backfill.InsertOneAsync(doc);
            Console.WriteLine($"Seeded backfill for channel {channelId}");
        }
        catch
        {
            // ignore races
        }
    }

    private static async Task BackfillWorkerLoop(string apiBase)
    {
        if (_backfill == null) return;

        while (true)
        {
            try
            {
                var claim = await ClaimNextChannel();
                if (claim == null)
                {
                    await Task.Delay(2000);
                    continue;
                }

                var channelId = claim["channel_id"].AsString;
                var cursor = claim.Contains("cursor_before") && !claim["cursor_before"].IsBsonNull
                    ? claim["cursor_before"].AsString
                    : null;

                var (newCursor, done, count, errorDelta, retryAfterMs) = await BackfillOnePage(apiBase, channelId, cursor);

                await UpdateChannelState(channelId, newCursor, done, errorDelta);

                // Throttle: honor Retry-After/rate limit headers when present, otherwise use a small steady delay.
                var delay = retryAfterMs > 0 ? retryAfterMs : _backfillRequestDelayMs;
                if (delay < 0) delay = 0;
                await Task.Delay(delay);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Backfill worker error: {ex.Message}");
                await Task.Delay(2000);
            }
        }
    }

    private static async Task<BsonDocument?> ClaimNextChannel()
    {
        if (_backfill == null) return null;

        var staleCutoff = DateTime.UtcNow.Subtract(_backfillClaimTimeout);
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("done", false),
            Builders<BsonDocument>.Filter.Or(
                Builders<BsonDocument>.Filter.Ne("claimed", true),
                Builders<BsonDocument>.Filter.Lt("updated_at", staleCutoff)
            )
        );

        var update = Builders<BsonDocument>.Update
            .Set("claimed", true)
            .Set("updated_at", DateTime.UtcNow);

        return await _backfill.FindOneAndUpdateAsync(
            filter,
            update,
            new FindOneAndUpdateOptions<BsonDocument>
            {
                ReturnDocument = ReturnDocument.After,
                Sort = Builders<BsonDocument>.Sort.Ascending("updated_at")
            });
    }

    private static async Task UpdateChannelState(string channelId, string? newCursor, bool done, int errorDelta)
    {
        if (_backfill == null) return;

        var filter = Builders<BsonDocument>.Filter.Eq("channel_id", channelId);

        var cursorVal = newCursor == null ? (BsonValue)BsonNull.Value : new BsonString(newCursor);

        var upd = Builders<BsonDocument>.Update
            .Set<BsonValue>("cursor_before", cursorVal)
            .Set("done", done)
            .Set("claimed", false)
            .Set("updated_at", DateTime.UtcNow);

        if (errorDelta > 0)
            upd = upd.Inc("error_count", errorDelta);

        await _backfill.UpdateOneAsync(filter, upd);
    }

    private static async Task<(string? newCursor, bool done, int count, int errorDelta, int retryAfterMs)> BackfillOnePage(string apiBase, string channelId, string? before)
    {
        var url = $"{apiBase}/channels/{channelId}/messages?limit={_backfillPageSize}";
        if (!string.IsNullOrEmpty(before))
            url += $"&before={before}";

        var resp = await RateLimiter.GetAsync(url, routeKey: "GET:/channels/:channelId/messages");

        // Rate limiting
        if ((int)resp.StatusCode == 429)
        {
            // Discord usually returns Retry-After in seconds (as header), sometimes also in JSON body.
            var retry = resp.Headers.RetryAfter?.Delta;
            var retryMs = retry != null ? (int)Math.Ceiling(retry.Value.TotalMilliseconds) : 2000;

            // Try parsing JSON body retry_after (seconds)
            try
            {
                var body = await resp.Content.ReadAsStringAsync();
                var root429 = JsonDocument.Parse(body).RootElement;
                if (root429.TryGetProperty("retry_after", out var ra) && ra.TryGetDouble(out var secs))
                {
                    retryMs = (int)Math.Ceiling(secs * 1000);
                }
            }
            catch
            {
                // ignore
            }

            if (retryMs < 250) retryMs = 250;
            Console.WriteLine($"WARN: Backfill rate-limited for channel {channelId}: 429. Sleeping {retryMs}ms then retrying.");
            return (before, false, 0, 1, retryMs);
        }

        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"WARN: Backfill fetch failed for channel {channelId}: {(int)resp.StatusCode} {resp.ReasonPhrase}");
            return (before, false, 0, 1, _backfillRequestDelayMs);
        }

        // If we are about to hit the limit, honor reset-after headers.
        // Discord headers: X-RateLimit-Remaining, X-RateLimit-Reset-After (seconds)
        var preDelayMs = 0;
        try
        {
            if (resp.Headers.TryGetValues("X-RateLimit-Remaining", out var remVals))
            {
                var remStr = remVals.FirstOrDefault();
                if (int.TryParse(remStr, out var remaining) && remaining <= 0)
                {
                    if (resp.Headers.TryGetValues("X-RateLimit-Reset-After", out var resetVals))
                    {
                        var resetStr = resetVals.FirstOrDefault();
                        if (double.TryParse(resetStr, out var resetAfterSecs))
                        {
                            preDelayMs = (int)Math.Ceiling(resetAfterSecs * 1000);
                        }
                    }
                }
            }
        }
        catch
        {
            // ignore header parse issues
        }

        var json = await resp.Content.ReadAsStringAsync();
        var root = JsonDocument.Parse(json).RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            return (before, false, 0, 1, preDelayMs);

        var msgs = root.EnumerateArray().ToList();
        var messageIds = msgs
            .Select(m => m.TryGetProperty("id", out var idEl) ? idEl.GetString() : null)
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToArray();
        var pageResult = BackfillState.ApplyFetchedPage(before, messageIds);

        if (pageResult.Done)
        {
            Console.WriteLine($"Backfill done for channel {channelId}");
            return (pageResult.NewCursor, true, 0, 0, preDelayMs);
        }

        foreach (var m in msgs)
        {
            await InsertMessage(m, source: "backfill");
        }

        Console.WriteLine($"Backfilled {msgs.Count} messages from channel {channelId}");
        return (pageResult.NewCursor, false, msgs.Count, 0, preDelayMs);
    }

    private static async Task InsertMessage(JsonElement msg, string source)
    {
        if (_messages == null) return;

        var id = msg.GetProperty("id").GetString() ?? "";
        var channelId = msg.TryGetProperty("channel_id", out var cid) ? cid.GetString() : null;
        var timestamp = msg.TryGetProperty("timestamp", out var ts) ? ts.GetString() : null;
        var guildId = msg.TryGetProperty("guild_id", out var gid) ? gid.GetString() : null;

        string? authorId = null;
        string? authorUsername = null;
        string? authorGlobalName = null;
        if (msg.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object)
        {
            authorId = author.TryGetProperty("id", out var aid) ? aid.GetString() : null;
            authorUsername = author.TryGetProperty("username", out var au) ? au.GetString() : null;
            authorGlobalName = author.TryGetProperty("global_name", out var agn) ? agn.GetString() : null;
        }

        long tsMs = 0;
        if (!string.IsNullOrEmpty(timestamp) && DateTimeOffset.TryParse(timestamp, out var dto))
            tsMs = dto.ToUnixTimeMilliseconds();

        // Discord REST backfill payloads often omit guild_id. Fill it from the channels/backfill collections.
        if (string.IsNullOrEmpty(guildId) && !string.IsNullOrEmpty(channelId))
        {
            guildId = await GetGuildIdForChannel(channelId!);
        }

        // Touch channel lookup (helps map channel_id -> guild_id/name over time)
        if (!string.IsNullOrEmpty(channelId))
        {
            await TouchChannel(channelId!, guildId, tsMs);
        }

        var doc = new BsonDocument
        {
            { "message_id", id },
            { "channel_id", channelId == null ? (BsonValue)BsonNull.Value : new BsonString(channelId) },
            { "guild_id", guildId == null ? (BsonValue)BsonNull.Value : new BsonString(guildId) },
            { "author_id", authorId == null ? (BsonValue)BsonNull.Value : new BsonString(authorId) },
            { "timestamp", timestamp == null ? (BsonValue)BsonNull.Value : new BsonString(timestamp) },
            { "timestamp_ms", tsMs },
            { "source", source },
            { "raw", BsonDocument.Parse(msg.GetRawText()) },
            { "ingested_at", DateTime.UtcNow },
        };

        try
        {
            await _messages.InsertOneAsync(doc);
        }
        catch (MongoWriteException mwx) when (mwx.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // ignore duplicates
        }

        // Maintain a small user lookup table for ID -> latest name (helps profiling by user_id)
        if (_users != null && !string.IsNullOrEmpty(authorId))
        {
            var uf = Builders<BsonDocument>.Filter.Eq("user_id", authorId);
            var uu = Builders<BsonDocument>.Update
                .Set("user_id", authorId)
                .Set("username", authorUsername == null ? (BsonValue)BsonNull.Value : new BsonString(authorUsername))
                .Set("global_name", authorGlobalName == null ? (BsonValue)BsonNull.Value : new BsonString(authorGlobalName))
                .Set("last_seen_ms", tsMs)
                .Set("updated_at", DateTime.UtcNow);

            try
            {
                await _users.UpdateOneAsync(uf, uu, new UpdateOptions { IsUpsert = true });
            }
            catch
            {
                // ignore lookup races/errors
            }
        }
    }


    private static async Task UpsertChannelFromGuildList(JsonElement ch, string guildId)
    {
        if (_channels == null) return;

        var channelId = ch.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrEmpty(channelId)) return;

        var type = ch.TryGetProperty("type", out var tEl) && tEl.TryGetInt32(out var tv) ? tv : (int?)null;
        var name = ch.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
        var parentId = ch.TryGetProperty("parent_id", out var pEl) ? pEl.GetString() : null;
        var nsfw = ch.TryGetProperty("nsfw", out var nsEl) && nsEl.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? nsEl.GetBoolean()
            : (bool?)null;

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var f = Builders<BsonDocument>.Filter.Eq("channel_id", channelId);
        var u = Builders<BsonDocument>.Update
            .Set("channel_id", channelId)
            .Set("guild_id", guildId)
            .Set("type", type == null ? (BsonValue)BsonNull.Value : new BsonInt32(type.Value))
            .Set("name", name == null ? (BsonValue)BsonNull.Value : new BsonString(name))
            .Set("parent_id", parentId == null ? (BsonValue)BsonNull.Value : new BsonString(parentId))
            .Set("nsfw", nsfw == null ? (BsonValue)BsonNull.Value : new BsonBoolean(nsfw.Value))
            .Max("last_seen_ms", nowMs)
            .Set("updated_at", DateTime.UtcNow);

        try
        {
            await _channels.UpdateOneAsync(f, u, new UpdateOptions { IsUpsert = true });
            ChannelCache[channelId] = (guildId, nowMs);
        }
        catch
        {
            // ignore
        }
    }

    private static async Task TouchChannel(string channelId, string? guildId, long tsMs)
    {
        if (_channels == null) return;

        // Use in-memory cache as a cheap guard.
        if (ChannelCache.TryGetValue(channelId, out var existing) && existing.lastSeenMs >= tsMs && (guildId == null || existing.guildId == guildId))
            return;

        var f = Builders<BsonDocument>.Filter.Eq("channel_id", channelId);
        var u = Builders<BsonDocument>.Update
            .Set("channel_id", channelId)
            .Set("guild_id", guildId == null ? (BsonValue)BsonNull.Value : new BsonString(guildId))
            .Max("last_seen_ms", tsMs)
            .Set("updated_at", DateTime.UtcNow);

        try
        {
            await _channels.UpdateOneAsync(f, u, new UpdateOptions { IsUpsert = true });
            ChannelCache[channelId] = (guildId, tsMs);
        }
        catch
        {
            // ignore
        }
    }

    private static async Task<string?> GetGuildIdForChannel(string channelId)
    {
        // Fast path: in-memory cache
        if (ChannelCache.TryGetValue(channelId, out var cached) && !string.IsNullOrEmpty(cached.guildId))
            return cached.guildId;

        // Mongo lookup: channels collection
        if (_channels != null)
        {
            try
            {
                var doc = await _channels.Find(Builders<BsonDocument>.Filter.Eq("channel_id", channelId)).FirstOrDefaultAsync();
                if (doc != null && doc.TryGetValue("guild_id", out var gidVal) && gidVal.IsString)
                {
                    var gid = gidVal.AsString;
                    ChannelCache[channelId] = (gid, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    return gid;
                }
            }
            catch
            {
                // ignore
            }
        }

        // Fallback: backfill state has guild_id for seeded channels
        if (_backfill != null)
        {
            try
            {
                var bf = await _backfill.Find(Builders<BsonDocument>.Filter.Eq("channel_id", channelId)).FirstOrDefaultAsync();
                if (bf != null && bf.TryGetValue("guild_id", out var bg) && bg.IsString)
                {
                    var gid = bg.AsString;
                    ChannelCache[channelId] = (gid, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    return gid;
                }
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private static async Task RunGatewayLoop(string gatewayUrl, string token, int intents)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(gatewayUrl), CancellationToken.None);

        using var helloDoc = await ReceiveJson(ws);
        var interval = helloDoc.RootElement.GetProperty("d").GetProperty("heartbeat_interval").GetInt32();

        int? seq = null;

        using var cts = new CancellationTokenSource();

        var hbTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(interval);
                var payload = new { op = 1, d = seq };
                await SendJson(ws, payload);
            }
        });

        var identify = new
        {
            op = 2,
            d = new
            {
                token,
                intents,
                properties = new { os = "linux", browser = "discord-indexer", device = "discord-indexer" }
            }
        };
        await SendJson(ws, identify);

        while (ws.State == WebSocketState.Open)
        {
            using var msg = await ReceiveJson(ws);
            var root = msg.RootElement;

            if (root.TryGetProperty("s", out var sEl) && sEl.ValueKind != JsonValueKind.Null)
                seq = sEl.GetInt32();

            var op = root.GetProperty("op").GetInt32();
            if (op == 0)
            {
                var t = root.GetProperty("t").GetString();
                var d = root.GetProperty("d");

                if (t == "MESSAGE_CREATE")
                {
                    await InsertMessage(d, source: "live");
                }
            }
            else if (op == 7 || op == 9)
            {
                break;
            }
        }

        cts.Cancel();
        try { await hbTask; } catch { /* ignore */ }

        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
    }

    private static async Task<JsonDocument> ReceiveJson(ClientWebSocket ws)
    {
        var buffer = new byte[1 << 16];
        var sb = new StringBuilder();
        while (true)
        {
            var res = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (res.MessageType == WebSocketMessageType.Close)
                throw new Exception("Gateway closed");

            sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
            if (res.EndOfMessage) break;
        }
        return JsonDocument.Parse(sb.ToString());
    }

    private static Task SendJson(ClientWebSocket ws, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        return ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static string GetEnv(string key, string? def = null)
    {
        var v = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrEmpty(v)) return v;
        if (def != null) return def;
        throw new Exception($"Missing required env var: {key}");
    }
}
