using System.Net;
using System.Text.Json;
using DiscordIndexer;
using MongoDB.Bson;

namespace DiscordIndexer.Tests;

public sealed class ThreadDiscoveryTests
{
    private static JsonElement Parent(int type) => JsonSerializer.SerializeToElement(new { id = "100", type });
    private static string Thread(string id, int type = 11, string timestamp = "2026-09-01T00:00:00Z")
        => JsonSerializer.Serialize(new { id, type, parent_id = "100", last_message_id = "500", thread_metadata = new { archive_timestamp = timestamp } });
    private static string Page(params string[] threads) => "{\"threads\":[" + string.Join(',', threads) + "],\"has_more\":false}";
    private static HttpResponseMessage Response(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json) };

    [Fact]
    public async Task PublicArchivesPaginateByTimestampAndDeduplicateActiveThreads()
    {
        var urls = new List<string>();
        var seeded = new List<JsonElement>();
        var discovery = new ThreadDiscovery((url, _) =>
        {
            urls.Add(url);
            if (url.Contains("/threads/active")) return Task.FromResult(Response(Page(Thread("200"))));
            if (url.Contains("archived/public") && !url.Contains("before="))
                return Task.FromResult(Response("{\"threads\":[" + Thread("200") + "],\"has_more\":true}"));
            if (url.Contains("archived/public")) return Task.FromResult(Response(Page(Thread("199"))));
            return Task.FromResult(Response("{}", HttpStatusCode.Forbidden));
        }, (thread, guild) => { Assert.Equal("guild", guild); seeded.Add(thread.Clone()); return Task.CompletedTask; }, _ => { });
        await discovery.DiscoverGuild("https://example.invalid", "guild", [Parent(0)]);
        Assert.Equal(new[] { "200", "199" }, seeded.Select(t => t.GetProperty("id").GetString()));
        Assert.All(seeded, t => Assert.Equal("100", t.GetProperty("parent_id").GetString()));
        Assert.Contains(urls, u => u.Contains("before=2026-09-01T00%3A00%3A00Z"));
    }

    [Fact]
    public async Task JoinedPrivateArchivesUseSnowflakeDespiteForbiddenWiderPrivateEndpoint()
    {
        var urls = new List<string>();
        var ids = new List<string>();
        var discovery = new ThreadDiscovery((url, _) =>
        {
            urls.Add(url);
            if (url.Contains("users/@me") && !url.Contains("before="))
                return Task.FromResult(Response("{\"threads\":[" + Thread("300", 12) + "],\"has_more\":true}"));
            if (url.Contains("users/@me")) return Task.FromResult(Response(Page(Thread("299", 12))));
            if (url.Contains("archived/private")) return Task.FromResult(Response("{}", HttpStatusCode.Forbidden));
            return Task.FromResult(Response(Page()));
        }, (t, _) => { ids.Add(t.GetProperty("id").GetString()!); return Task.CompletedTask; }, _ => { });
        await discovery.DiscoverGuild("https://example.invalid", "guild", [Parent(0)]);
        Assert.Equal(new[] { "300", "299" }, ids);
        Assert.Contains(urls, u => u.Contains("users/@me/threads/archived/private?limit=100&before=300"));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(16)]
    public async Task AnnouncementForumAndMediaParentsDiscoverPublicArchivesOnly(int type)
    {
        var urls = new List<string>();
        var discovery = new ThreadDiscovery((url, _) => { urls.Add(url); return Task.FromResult(Response(Page(Thread("200")))); },
            (_, _) => Task.CompletedTask, _ => { });
        await discovery.DiscoverGuild("https://example.invalid", "guild", [Parent(type)]);
        Assert.Equal(2, urls.Count);
        Assert.Contains(urls, u => u.Contains("archived/public"));
        Assert.DoesNotContain(urls, u => u.Contains("private"));
    }

    [Fact]
    public async Task RateLimitedPageRetriesWithoutSkippingAndMissingPermissionDoesNotAbortGuild()
    {
        var attempts = 0;
        var waits = new List<TimeSpan>();
        var ids = new List<string>();
        var discovery = new ThreadDiscovery((url, _) =>
        {
            if (url.Contains("/threads/active")) return Task.FromResult(Response("{}", HttpStatusCode.Forbidden));
            if (attempts++ == 0) return Task.FromResult(Response("{\"retry_after\":0.5}", HttpStatusCode.TooManyRequests));
            return Task.FromResult(Response(Page(Thread("200"))));
        }, (t, _) => { ids.Add(t.GetProperty("id").GetString()!); return Task.CompletedTask; }, _ => { },
            wait => { waits.Add(wait); return Task.CompletedTask; });
        await discovery.DiscoverGuild("https://example.invalid", "guild", [Parent(15)]);
        Assert.Single(ids);
        Assert.Equal(TimeSpan.FromMilliseconds(500), Assert.Single(waits));
    }

    [Fact]
    public async Task RepeatedCursorFailsInsteadOfLoopingForever()
    {
        var discovery = new ThreadDiscovery((url, _) => Task.FromResult(Response(url.Contains("active") ? Page() :
            "{\"threads\":[" + Thread("200") + "],\"has_more\":true}")), (_, _) => Task.CompletedTask, _ => { });
        await Assert.ThrowsAsync<InvalidDataException>(() => discovery.DiscoverGuild("https://example.invalid", "guild", [Parent(15)]));
    }

    [Fact]
    public async Task RediscoveryAfterFailureReplaysPagesForPersistedDeduplication()
    {
        var persisted = new HashSet<string>();
        var fail = true;
        var discovery = new ThreadDiscovery((url, _) =>
        {
            if (url.Contains("active")) return Task.FromResult(Response(Page()));
            if (!url.Contains("before=")) return Task.FromResult(Response("{\"threads\":[" + Thread("200") + "],\"has_more\":true}"));
            if (fail) throw new IOException("simulated interrupted scan");
            return Task.FromResult(Response(Page(Thread("199"))));
        }, (t, _) => { persisted.Add(t.GetProperty("id").GetString()!); return Task.CompletedTask; }, _ => { });
        await Assert.ThrowsAsync<IOException>(() => discovery.DiscoverGuild("https://example.invalid", "guild", [Parent(15)]));
        fail = false;
        await discovery.DiscoverGuild("https://example.invalid", "guild", [Parent(15)]);
        Assert.Equal(2, persisted.Count);
    }

    [Fact]
    public void RediscoveryPreservesUnfinishedCursorAndCatchupStopsAtPreviousHead()
    {
        var state = new BsonDocument { { "done", false }, { "head_message_id", "500" }, { "cursor_before", "100" } };
        Assert.False(ThreadBackfillState.NeedsCatchup(state, "600"));
        Assert.Equal("100", BackfillState.ReadCursor(state));
        state["done"] = true;
        Assert.True(ThreadBackfillState.NeedsCatchup(state, "600"));
        Assert.False(ThreadBackfillState.NeedsCatchup(state, "500"));
        var first = ThreadBackfillState.ApplyPage(null, ["600", "599"], "500");
        Assert.False(first.Done);
        var last = ThreadBackfillState.ApplyPage(first.NewCursor, ["501", "499"], "500");
        Assert.True(last.Done); // also stops if the prior head has since been deleted
        Assert.False(ThreadBackfillState.IsAfter("499", "500"));
    }
}
