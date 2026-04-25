using System.Diagnostics;
using System.Net;
using System.Text;
using DiscordIndexer;

namespace DiscordIndexer.Tests;

public sealed class DiscordRateLimiterTests
{
    [Fact]
    public async Task GetAsync_SerializesRequestsForSameRouteKey()
    {
        var active = 0;
        var maxActive = 0;
        var calls = 0;

        using var http = new HttpClient(new StubHandler(async _ =>
        {
            var nowActive = Interlocked.Increment(ref active);
            var observedMax = Math.Max(Volatile.Read(ref maxActive), nowActive);
            Interlocked.Exchange(ref maxActive, observedMax);
            Interlocked.Increment(ref calls);

            await Task.Delay(25);

            Interlocked.Decrement(ref active);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var limiter = new DiscordRateLimiter(http);

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            limiter.GetAsync("https://discord.test/channels/123/messages", "GET:/channels/:channelId/messages")));

        Assert.Equal(5, calls);
        Assert.Equal(1, maxActive);
    }

    [Fact]
    public async Task GetAsync_AllowsDifferentRouteKeysToRunIndependently()
    {
        var active = 0;
        var maxActive = 0;
        var enteredTwice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var http = new HttpClient(new StubHandler(async _ =>
        {
            var nowActive = Interlocked.Increment(ref active);
            UpdateMax(ref maxActive, nowActive);

            if (nowActive >= 2)
                enteredTwice.TrySetResult();

            await Task.Delay(50);

            Interlocked.Decrement(ref active);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        var limiter = new DiscordRateLimiter(http);
        var requestA = limiter.GetAsync("https://discord.test/users/@me/guilds", "GET:/users/@me/guilds");
        var requestB = limiter.GetAsync("https://discord.test/guilds/1/channels", "GET:/guilds/:guildId/channels");

        var completed = await Task.WhenAny(enteredTwice.Task, Task.Delay(TimeSpan.FromSeconds(2)));

        await Task.WhenAll(requestA, requestB);

        Assert.Same(enteredTwice.Task, completed);
        Assert.True(maxActive >= 2);
    }

    [Fact]
    public async Task GetAsync_HonorsRetryAfterFromRateLimitBody()
    {
        var calls = 0;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)429)
                {
                    Content = new StringContent("{\"retry_after\":0.25}", Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));

        var limiter = new DiscordRateLimiter(http);

        using var first = await limiter.GetAsync("https://discord.test/channels/123/messages", "GET:/channels/:channelId/messages");
        Assert.Equal(429, (int)first.StatusCode);

        var sw = Stopwatch.StartNew();
        using var second = await limiter.GetAsync("https://discord.test/channels/123/messages", "GET:/channels/:channelId/messages");
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(sw.ElapsedMilliseconds >= 200, $"Expected retry delay; elapsed {sw.ElapsedMilliseconds}ms");
    }

    private static void UpdateMax(ref int target, int candidate)
    {
        int initial;
        do
        {
            initial = Volatile.Read(ref target);
            if (initial >= candidate) return;
        } while (Interlocked.CompareExchange(ref target, candidate, initial) != initial);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request);
    }
}
