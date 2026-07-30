using System.Text.Json;
using GPTino.AgentHost.Runtime;

namespace GPTino.AgentHost.Tests;

public class SessionUsageStateTests
{
    // Raw notification params captured live from codex app-server (2026-07-30 probe).
    private const string TokenUsageUpdatedParams = """
        {"threadId":"019fb17c-ed4d-7483-a389-fb0abbd77e63","turnId":"019fb17d-0206-7650-a3d1-85cf7129c3f6","tokenUsage":{"total":{"totalTokens":12849,"inputTokens":12844,"cachedInputTokens":11008,"outputTokens":5,"reasoningOutputTokens":0},"last":{"totalTokens":12849,"inputTokens":12844,"cachedInputTokens":11008,"outputTokens":5,"reasoningOutputTokens":0},"modelContextWindow":258400}}
        """;

    private const string RateLimitsUpdatedParams = """
        {"rateLimits":{"limitId":"codex","limitName":null,"primary":{"usedPercent":9,"windowDurationMins":10080,"resetsAt":1785973818},"secondary":null,"credits":{"hasCredits":false,"unlimited":false,"balance":"0"},"individualLimit":null,"planType":"pro","rateLimitReachedType":null}}
        """;

    [Fact]
    public void TryParse_ReadsLiveTokenUsageUpdatedShape()
    {
        using var document = JsonDocument.Parse(TokenUsageUpdatedParams);

        var snapshot = SessionUsageState.TryParse(document.RootElement);

        Assert.NotNull(snapshot);
        Assert.Equal(12_849, snapshot!.TotalTokens);
        Assert.Equal(12_849, snapshot.ContextUsedTokens);
        Assert.Equal(258_400, snapshot.ContextWindow);
        Assert.Empty(snapshot.RateLimits);
    }

    [Fact]
    public void TryParse_ReadsLiveAccountRateLimitsShape()
    {
        using var document = JsonDocument.Parse(RateLimitsUpdatedParams);

        var snapshot = SessionUsageState.TryParse(document.RootElement);

        Assert.NotNull(snapshot);
        var window = Assert.Single(snapshot!.RateLimits);
        // 10080-minute window renders as the weekly label; resetsAt is unix seconds.
        Assert.Equal("weekly", window.Label);
        Assert.Equal(9, window.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_785_973_818), window.ResetsAt);
        // Non-window siblings (limitId, credits, planType) must not become windows.
        Assert.Null(snapshot.TotalTokens);
        Assert.Null(snapshot.ContextWindow);
    }

    [Fact]
    public void TryParse_ReadsLegacyTokenCountInfoShape()
    {
        using var document = JsonDocument.Parse("""
            {"threadId":"t","info":{"totalTokenUsage":{"totalTokens":900},"lastTokenUsage":{"totalTokens":120},"modelContextWindow":272000},"rateLimits":{"primary":{"usedPercent":34,"windowMinutes":300,"resetsAt":"2026-07-30T17:30:00+00:00"}}}
            """);

        var snapshot = SessionUsageState.TryParse(document.RootElement);

        Assert.NotNull(snapshot);
        Assert.Equal(900, snapshot!.TotalTokens);
        Assert.Equal(120, snapshot.ContextUsedTokens);
        Assert.Equal(272_000, snapshot.ContextWindow);
        var window = Assert.Single(snapshot.RateLimits);
        Assert.Equal("5h", window.Label);
        Assert.Equal(34, window.UsedPercent);
    }

    [Fact]
    public void UpdateAccountLimits_KeepsFreshestWindowsForTheAccount()
    {
        var state = new SessionUsageState();
        Assert.Null(state.AccountLimits);

        state.UpdateAccountLimits([new SessionRateLimitWindow("weekly", 9, null)]);

        var limits = state.AccountLimits;
        Assert.NotNull(limits);
        Assert.Equal("weekly", Assert.Single(limits!.Windows).Label);

        // Empty updates never wipe a known snapshot.
        state.UpdateAccountLimits([]);
        Assert.Same(limits, state.AccountLimits);
    }

    [Fact]
    public void Update_LiftsSessionCarriedRateLimitsIntoAccountSnapshot()
    {
        var state = new SessionUsageState();
        var sessionId = Guid.NewGuid();

        var changed = state.Update(
            sessionId,
            new SessionUsageSnapshot(100, 272_000, 50, [new SessionRateLimitWindow("5h", 12, null)]));

        Assert.True(changed);
        Assert.NotNull(state.AccountLimits);
        Assert.Equal("5h", Assert.Single(state.AccountLimits!.Windows).Label);
    }
}
