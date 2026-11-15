using System.Text.Json;
using DiscordIndexer;
using MongoDB.Bson;

namespace DiscordIndexer.Tests;

public sealed class ChannelDiscoveryTests
{
    [Theory]
    [InlineData("CHANNEL_CREATE", 0)]
    [InlineData("CHANNEL_UPDATE", 0)]
    [InlineData("CHANNEL_CREATE", 5)]
    [InlineData("CHANNEL_UPDATE", 5)]
    public async Task ChannelEventsSeedWithoutWaitingForPeriodicDiscovery(string eventName, int type)
    {
        var calls = new List<string>();
        var channel = JsonSerializer.SerializeToElement(new { id = "123", guild_id = "456", type });
        await ChannelDiscovery.HandleGatewayEvent(eventName, channel,
            (_, guild) => { calls.Add("upsert:" + guild); return Task.CompletedTask; },
            (id, guild) => { calls.Add($"seed:{id}:{guild}"); return Task.CompletedTask; });
        Assert.Equal(new[] { "upsert:456", "seed:123:456" }, calls);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(15)]
    [InlineData(16)]
    public async Task NonMessageChannelsOnlyUpdateMetadata(int type)
    {
        var upserts = 0;
        await ChannelDiscovery.HandleGatewayEvent("CHANNEL_UPDATE",
            JsonSerializer.SerializeToElement(new { id = "123", guild_id = "456", type }),
            (_, _) => { upserts++; return Task.CompletedTask; },
            (_, _) => throw new Exception("Must not backfill a non-message channel"));
        Assert.Equal(1, upserts);
    }

    [Theory]
    [InlineData("CHANNEL_DELETE", "{\"id\":\"123\",\"guild_id\":\"456\",\"type\":0}")]
    [InlineData("CHANNEL_CREATE", "{\"id\":\"123\",\"type\":1}")]
    [InlineData("CHANNEL_UPDATE", "{\"guild_id\":\"456\",\"type\":0}")]
    public async Task UnrelatedAndIncompleteEventsAreIgnored(string eventName, string json)
    {
        using var doc = JsonDocument.Parse(json);
        await ChannelDiscovery.HandleGatewayEvent(eventName, doc.RootElement,
            (_, _) => throw new Exception("Unexpected metadata write"),
            (_, _) => throw new Exception("Unexpected backfill"));
    }

    [Theory]
    [InlineData("GUILD_ROLE_UPDATE", 0, "456")]
    [InlineData("GUILD_ROLE_CREATE", 0, "456")]
    [InlineData("GUILD_ROLE_DELETE", 0, "456")]
    [InlineData("GUILD_MEMBER_UPDATE", 0, "456")]
    [InlineData("CHANNEL_UPDATE", 4, "456")]
    [InlineData("CHANNEL_UPDATE", 0, null)]
    [InlineData("MESSAGE_CREATE", 0, null)]
    public void RoleAndCategoryChangesRefreshGuild(string eventName, int type, string? expected)
    {
        var payload = JsonSerializer.SerializeToElement(new { guild_id = "456", type });
        Assert.Equal(expected, ChannelDiscovery.GuildNeedingRefresh(eventName, payload));
        Assert.Null(ChannelDiscovery.GuildNeedingRefresh(eventName, JsonSerializer.SerializeToElement(new { type })));
    }

    [Fact]
    public async Task PermissionEventReopensBlockedHistoryButDuplicateDoesNotResetProgress()
    {
        var state = new BsonDocument { { "done", true }, { "access_blocked", true } };
        var channel = JsonSerializer.SerializeToElement(new { id = "123", guild_id = "456", type = 0 });
        var resets = 0;
        Task Seed(string id, string guild)
        {
            if (ThreadBackfillState.NeedsCatchup(state, null))
            {
                resets++;
                state["done"] = false;
                state["access_blocked"] = false;
                state["cursor_before"] = "100";
            }
            return Task.CompletedTask;
        }
        await ChannelDiscovery.HandleGatewayEvent("CHANNEL_UPDATE", channel, (_, _) => Task.CompletedTask, Seed);
        await ChannelDiscovery.HandleGatewayEvent("CHANNEL_UPDATE", channel, (_, _) => Task.CompletedTask, Seed);
        Assert.Equal(1, resets);
        Assert.Equal("100", BackfillState.ReadCursor(state));
    }
}