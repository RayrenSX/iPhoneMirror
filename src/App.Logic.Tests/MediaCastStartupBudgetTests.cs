using IPhoneMirror.App.Services;

internal static class MediaCastStartupBudgetTests
{
    internal static void Run()
    {
        var now = TimeSpan.Zero;
        var budget = new MediaCastStartupBudget(() => now);
        now = TimeSpan.FromSeconds(14);
        budget.MarkInputReady(0);
        now = TimeSpan.FromSeconds(28);
        budget.MarkInputReady(1);
        now = TimeSpan.FromSeconds(42);
        budget.MarkBytesReceived(188);
        now = TimeSpan.FromSeconds(56);
        budget.MarkBytesReceived(376);
        Assert(budget.Remaining == TimeSpan.FromSeconds(4), "Real stages may extend idle time but not the absolute limit");
        now = TimeSpan.FromSeconds(59);
        budget.MarkResponseReady();
        Assert(budget.Stage == "player_open" && budget.Remaining == TimeSpan.FromSeconds(1),
            "Response readiness must leave the original hard deadline intact");
        now = TimeSpan.FromSeconds(60);
        budget.MarkBytesReceived(564);
        Assert(budget.HasTimedOut, "Progress cannot extend startup beyond sixty seconds");

        now = TimeSpan.Zero;
        budget = new MediaCastStartupBudget(() => now);
        now = TimeSpan.FromSeconds(14);
        budget.MarkInputReady(0);
        now = TimeSpan.FromSeconds(28);
        budget.MarkInputReady(0);
        Assert(budget.Remaining == TimeSpan.FromSeconds(1), "Repeated input logs cannot renew idle time");
        now = TimeSpan.FromSeconds(29);
        budget.MarkInputReady(1);
        budget.MarkBytesReceived(188);
        budget.MarkResponseReady();
        Assert(budget.HasTimedOut && budget.Stage == "input_0_ready", "Late events cannot revive expired startup");

        now = TimeSpan.Zero;
        budget = new MediaCastStartupBudget(1, () => now);
        now = TimeSpan.FromSeconds(14);
        budget.MarkInputReady(1);
        budget.MarkInputReady(-1);
        Assert(budget.Remaining == TimeSpan.FromSeconds(1), "Only configured inputs count as milestones");
        now = TimeSpan.FromSeconds(15);
        Assert(budget.HasTimedOut, "A source with no progress must time out after fifteen seconds");

        now = TimeSpan.Zero;
        budget = new MediaCastStartupBudget(() => now);
        now = TimeSpan.FromSeconds(14);
        budget.MarkBytesReceived(MediaCastStartupBudget.MaximumFiniteBytes);
        now = TimeSpan.FromSeconds(28);
        budget.MarkBytesReceived(MediaCastStartupBudget.MaximumFiniteBytes);
        budget.MarkBytesReceived(MediaCastStartupBudget.MaximumFiniteBytes + 1);
        budget.MarkBytesReceived(1);
        budget.MarkInputReady(1);
        Assert(budget.Remaining == TimeSpan.FromSeconds(1), "Prefix progress must increase within its memory bound");

        now = TimeSpan.Zero;
        budget = new MediaCastStartupBudget(() => now);
        now = TimeSpan.FromSeconds(14);
        budget.MarkBytesReceived(MediaCastStartupBudget.MaximumPrefixBytes);
        now = TimeSpan.FromSeconds(28);
        budget.MarkBytesReceived(MediaCastStartupBudget.MaximumPrefixBytes + 188);
        Assert(budget.Remaining == TimeSpan.FromSeconds(15), "Persisted finite bytes beyond the RAM prefix are real startup progress");

        now = TimeSpan.Zero;
        budget = new MediaCastStartupBudget(() => now);
        now = TimeSpan.FromSeconds(14);
        budget.MarkBytesReceived(188);
        now = TimeSpan.FromSeconds(28);
        budget.MarkResponseReady();
        now = TimeSpan.FromSeconds(42);
        budget.MarkBytesReceived(376);
        budget.MarkInputReady(1);
        budget.MarkResponseReady();
        Assert(budget.Remaining == TimeSpan.FromSeconds(1) && budget.Stage == "player_open",
            "An offered response gets one bounded WMF opening window");
        now = TimeSpan.FromSeconds(43);
        Assert(budget.HasTimedOut, "Streaming output cannot keep an unopened player alive");
        Console.WriteLine("PASS: HLS startup uses unique milestones, fifteen-second idle limits and an immutable sixty-second maximum.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
