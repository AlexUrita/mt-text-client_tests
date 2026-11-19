using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MTTextClient.Core;
using Xunit;

namespace MTTextClient.Tests.Unit;

/// <summary>
/// Unit coverage for <see cref="LiveMarketStore.WaitForKey"/> — the event-signalled
/// wait that replaced the funding-rate busy-poll (30x Thread.Sleep(200)).
/// Verifies the fast path, the signalled-unblock path, the timeout path, and that
/// an Update racing waiter-registration is never missed (the race the re-check and
/// the under-lock signal are there to close).
/// </summary>
public sealed class LiveMarketStoreWaitUnitTests
{
    private static LiveMarketEntry Entry(string symbol) =>
        new LiveMarketEntry(symbol, "FUTURES", "{}");

    [Fact]
    [Trait("Category", "Unit")]
    public void WaitForKey_returns_true_immediately_when_already_present()
    {
        var store = new LiveMarketStore();
        store.Update("btcusdt:FUTURES", Entry("btcusdt"));

        var sw = Stopwatch.StartNew();
        bool got = store.WaitForKey("btcusdt:FUTURES", 5000);
        sw.Stop();

        got.Should().BeTrue();
        sw.ElapsedMilliseconds.Should().BeLessThan(200); // fast path, no wait
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void WaitForKey_times_out_and_returns_false_when_no_push_arrives()
    {
        var store = new LiveMarketStore();

        var sw = Stopwatch.StartNew();
        bool got = store.WaitForKey("never:FUTURES", 200);
        sw.Stop();

        got.Should().BeFalse();
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(150); // actually waited the budget
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WaitForKey_unblocks_promptly_when_an_update_arrives()
    {
        var store = new LiveMarketStore();
        const string key = "ethusdt:FUTURES";

        var sw = Stopwatch.StartNew();
        Task<bool> waiter = Task.Run(() => store.WaitForKey(key, 5000));
        Thread.Sleep(100); // let the waiter register before the push
        store.Update(key, Entry("ethusdt"));
        bool got = await waiter;
        sw.Stop();

        got.Should().BeTrue();
        sw.ElapsedMilliseconds.Should().BeLessThan(2000); // signalled — nowhere near the 5s timeout
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WaitForKey_never_misses_an_update_that_races_registration()
    {
        // Fire Update concurrently with WaitForKey so the push can land in the
        // narrow window between the fast-path check and waiter registration. Every
        // iteration must observe it (true) AND return promptly — a missed wakeup
        // would block the full 2s timeout, so the total-time budget catches it.
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 200; i++)
        {
            var store = new LiveMarketStore();
            string key = $"sym{i}:FUTURES";
            Task pusher = Task.Run(() => store.Update(key, Entry($"sym{i}")));
            bool got = store.WaitForKey(key, 2000);
            await pusher;
            got.Should().BeTrue($"iteration {i} must observe the racing update");
        }
        sw.Stop();

        // 200 iterations with no missed wakeup finish in well under a second; if any
        // single wait had timed out (missed signal) this would blow past the budget.
        sw.ElapsedMilliseconds.Should().BeLessThan(5000);
    }
}
