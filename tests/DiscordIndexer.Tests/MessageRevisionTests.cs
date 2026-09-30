using System.Text.Json;
using DiscordIndexer;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace DiscordIndexer.Tests;

public sealed class MessageRevisionTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"edited_timestamp\":null}")]
    public void UneditedReplayCannotOverwriteEditedMessage(string payload)
    {
        using var json = JsonDocument.Parse(payload);
        var rendered = MessageRevision.RefreshFilter("123", json.RootElement).Render(
            BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>(), BsonSerializer.SerializerRegistry);
        Assert.Equal("123", rendered["message_id"].AsString);
        Assert.Equal(BsonNull.Value, rendered["raw.edited_timestamp"]);
    }

    [Fact]
    public void EditedReplayIsAtomicallyRestrictedToSameOrEarlierStoredRevision()
    {
        using var json = JsonDocument.Parse("{\"edited_timestamp\":\"2026-09-01T01:00:00+00:00\"}");
        var rendered = MessageRevision.RefreshFilter("123", json.RootElement).Render(
            BsonSerializer.SerializerRegistry.GetSerializer<BsonDocument>(), BsonSerializer.SerializerRegistry);
        Assert.Equal("123", rendered["message_id"].AsString);
        var revisions = rendered["$or"].AsBsonArray;
        Assert.Equal(BsonNull.Value, revisions[0]["raw.edited_timestamp"]);
        Assert.Equal("2026-09-01T01:00:00+00:00", revisions[1]["raw.edited_timestamp"]["$lte"].AsString);
    }
}
