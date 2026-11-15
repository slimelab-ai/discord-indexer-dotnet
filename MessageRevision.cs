using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;

namespace DiscordIndexer;

internal static class MessageRevision
{
    // Replayed/catch-up REST pages can race settled gateway edits. Never let an
    // older payload overwrite a message with a later Discord edited_timestamp.
    public static FilterDefinition<BsonDocument> RefreshFilter(string id, JsonElement message)
    {
        var f = Builders<BsonDocument>.Filter;
        var revision = message.TryGetProperty("edited_timestamp", out var edited) && edited.ValueKind == JsonValueKind.String
            ? edited.GetString() : null;
        var allowed = revision == null
            ? f.Eq("raw.edited_timestamp", BsonNull.Value)
            : f.Or(f.Eq("raw.edited_timestamp", BsonNull.Value), f.Lte("raw.edited_timestamp", revision));
        return f.Eq("message_id", id) & allowed;
    }
}
