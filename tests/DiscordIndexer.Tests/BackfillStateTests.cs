using DiscordIndexer;
using MongoDB.Bson;

namespace DiscordIndexer.Tests;

public sealed class BackfillStateTests
{
    [Fact]
    public void IsClaimable_ReclaimsStaleClaimAfterCrash()
    {
        var now = new DateTime(2026, 4, 26, 6, 0, 0, DateTimeKind.Utc);
        var doc = new BsonDocument
        {
            { "channel_id", "channel-1" },
            { "done", false },
            { "claimed", true },
            { "updated_at", now.AddMinutes(-11) },
        };

        Assert.True(BackfillState.IsClaimable(doc, now, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void IsClaimable_DoesNotDoubleClaimFreshWorker()
    {
        var now = new DateTime(2026, 4, 26, 6, 0, 0, DateTimeKind.Utc);
        var doc = new BsonDocument
        {
            { "channel_id", "channel-1" },
            { "done", false },
            { "claimed", true },
            { "updated_at", now.AddMinutes(-2) },
        };

        Assert.False(BackfillState.IsClaimable(doc, now, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void IsClaimable_NeverClaimsCompletedChannel()
    {
        var now = new DateTime(2026, 4, 26, 6, 0, 0, DateTimeKind.Utc);
        var doc = new BsonDocument
        {
            { "channel_id", "channel-1" },
            { "done", true },
            { "claimed", false },
            { "updated_at", now.AddHours(-1) },
        };

        Assert.False(BackfillState.IsClaimable(doc, now, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void ApplyFetchedPage_AdvancesCursorToOldestMessageOnlyAfterPageIsAvailable()
    {
        var result = BackfillState.ApplyFetchedPage(previousCursor: null, new[] { "105", "104", "103" });

        Assert.Equal("103", result.NewCursor);
        Assert.False(result.Done);
        Assert.Equal(3, result.InsertedCount);
    }

    [Fact]
    public void ApplyFetchedPage_ReprocessingSamePageAfterCrashKeepsSameCursorForHoleFilling()
    {
        var firstAttempt = BackfillState.ApplyFetchedPage(previousCursor: null, new[] { "105", "104", "103" });

        // Simulates a crash after some/all inserts but before channel state update.
        // On restart the stale claim is reclaimed and Discord returns the same page again;
        // duplicate message inserts are ignored by message_id, then the cursor advances
        // to the same oldest id. No page is skipped, so the backlog hole gets filled.
        var retryAttempt = BackfillState.ApplyFetchedPage(previousCursor: null, new[] { "105", "104", "103" });

        Assert.Equal(firstAttempt, retryAttempt);
        Assert.Equal("103", retryAttempt.NewCursor);
    }

    [Fact]
    public void ApplyFetchedPage_EmptyPageMarksBackfillDoneWithoutMovingCursor()
    {
        var result = BackfillState.ApplyFetchedPage(previousCursor: "103", Array.Empty<string>());

        Assert.Equal("103", result.NewCursor);
        Assert.True(result.Done);
        Assert.Equal(0, result.InsertedCount);
    }
}
