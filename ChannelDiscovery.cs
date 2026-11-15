using System.Text.Json;

namespace DiscordIndexer;

// Reuse the durable backfill queue; do not fetch history on the gateway receive loop.
internal static class ChannelDiscovery
{
    public static string? GuildNeedingRefresh(string? eventName, JsonElement payload)
    {
        var roleChange = eventName is "GUILD_ROLE_CREATE" or "GUILD_ROLE_UPDATE" or "GUILD_ROLE_DELETE" or "GUILD_MEMBER_UPDATE";
        var categoryChange = eventName == "CHANNEL_UPDATE" &&
            payload.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Number &&
            type.TryGetInt32(out var value) && value == 4;
        if (!(roleChange || categoryChange) ||
            !payload.TryGetProperty("guild_id", out var guild) || guild.ValueKind != JsonValueKind.String)
            return null;
        return string.IsNullOrEmpty(guild.GetString()) ? null : guild.GetString();
    }

    public static async Task HandleGatewayEvent(string? eventName, JsonElement channel,
        Func<JsonElement, string, Task> upsert,
        Func<string, string, Task> seed)
    {
        if (eventName is not ("CHANNEL_CREATE" or "CHANNEL_UPDATE")) return;
        if (!channel.TryGetProperty("guild_id", out var guild) || guild.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(guild.GetString()) ||
            !channel.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(id.GetString()) ||
            !channel.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.Number || !type.TryGetInt32(out var channelType)) return;
        await upsert(channel, guild.GetString()!);
        if (channelType is 0 or 5)
            await seed(id.GetString()!, guild.GetString()!);
    }
}