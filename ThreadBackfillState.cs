using MongoDB.Bson;

namespace DiscordIndexer;

internal static class ThreadBackfillState
{
    public static bool NeedsCatchup(BsonDocument state, string? head)
        => state.GetValue("done", false).AsBoolean &&
           (state.GetValue("access_blocked", false).AsBoolean ||
            (head != null &&
             (!state.TryGetValue("head_message_id", out var old) || !old.IsString || old.AsString != head)));

    public static bool IsAfter(string id, string stopAt)
        => ulong.Parse(id) > ulong.Parse(stopAt);

    public static BackfillPageResult ApplyPage(string? before, IReadOnlyList<string> ids, string? stopAt)
    {
        var result = BackfillState.ApplyFetchedPage(before, ids);
        if (stopAt == null || ids.Count == 0) return result;
        return result with { Done = ids.Any(id => !IsAfter(id, stopAt)) };
    }
}
