using MongoDB.Bson;

namespace DiscordIndexer;

internal static class BackfillState
{
    public static readonly TimeSpan DefaultClaimTimeout = TimeSpan.FromMinutes(10);

    public static bool IsClaimable(BsonDocument doc, DateTime utcNow, TimeSpan claimTimeout)
    {
        if (doc.TryGetValue("done", out var doneVal) && doneVal.IsBoolean && doneVal.AsBoolean)
            return false;

        var claimed = doc.TryGetValue("claimed", out var claimedVal) && claimedVal.IsBoolean && claimedVal.AsBoolean;
        if (!claimed)
            return true;

        if (!doc.TryGetValue("updated_at", out var updatedAtVal) || !updatedAtVal.IsValidDateTime)
            return true;

        return updatedAtVal.ToUniversalTime() <= utcNow.Subtract(claimTimeout);
    }

    public static string? ReadCursor(BsonDocument doc)
        => doc.TryGetValue("cursor_before", out var cursor) && cursor.IsString ? cursor.AsString : null;

    public static BackfillPageResult ApplyFetchedPage(string? previousCursor, IReadOnlyList<string> messageIds)
    {
        if (messageIds.Count == 0)
            return new BackfillPageResult(previousCursor, Done: true, InsertedCount: 0);

        return new BackfillPageResult(messageIds[^1], Done: false, InsertedCount: messageIds.Count);
    }
}

internal sealed record BackfillPageResult(string? NewCursor, bool Done, int InsertedCount);
