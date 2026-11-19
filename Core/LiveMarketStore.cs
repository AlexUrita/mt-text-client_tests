#nullable disable
namespace MTTextClient.Core;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

public sealed class LiveMarketEntry
{
    public string Symbol { get; }
    public string MarketType { get; }
    public string MetricsJson { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public LiveMarketEntry(string symbol, string marketType, string metricsJson)
    {
        Symbol = symbol;
        MarketType = marketType;
        MetricsJson = metricsJson;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}

public sealed class LiveMarketStore
{
    private readonly ConcurrentDictionary<string, LiveMarketEntry> _entries =
        new ConcurrentDictionary<string, LiveMarketEntry>(StringComparer.OrdinalIgnoreCase);

    // Per-key one-shot waiters, so a caller can block on the first push for a
    // symbol instead of busy-polling. Guarded by _waiterLock; Update() signals
    // under the same lock WaitForKey() registers under (see WaitForKey).
    private readonly object _waiterLock = new object();
    private readonly Dictionary<string, List<ManualResetEventSlim>> _waiters =
        new Dictionary<string, List<ManualResetEventSlim>>(StringComparer.OrdinalIgnoreCase);

    public void Update(string key, LiveMarketEntry entry)
    {
        _entries.AddOrUpdate(key, entry, (_, existing) =>
        {
            existing.MetricsJson = entry.MetricsJson;
            existing.UpdatedAtUtc = DateTime.UtcNow;
            return existing;
        });
        SignalWaiters(key);
    }

    /// <summary>
    /// Block until an entry for <paramref name="key"/> is present (or already is),
    /// up to <paramref name="timeoutMs"/>. Returns true if the entry exists by the
    /// time we return. Event-signalled — no busy-poll: a waiter is registered under
    /// <c>_waiterLock</c> (the same lock <see cref="Update"/> signals under) with a
    /// re-check after registration, so an <see cref="Update"/> that lands between
    /// the fast-path check and registration can never be missed.
    /// </summary>
    public bool WaitForKey(string key, Int32 timeoutMs)
    {
        // Fast path: already present — no waiter, no wait.
        if (_entries.ContainsKey(key))
        {
            return true;
        }

        ManualResetEventSlim ev = new ManualResetEventSlim(false);
        lock (_waiterLock)
        {
            // Re-check under the lock to close the register-vs-signal race:
            // Update() adds to _entries, then signals under _waiterLock.
            if (_entries.ContainsKey(key))
            {
                ev.Dispose();
                return true;
            }
            if (!_waiters.TryGetValue(key, out List<ManualResetEventSlim> list))
            {
                list = new List<ManualResetEventSlim>();
                _waiters[key] = list;
            }
            list.Add(ev);
        }
        try
        {
            return ev.Wait(timeoutMs) || _entries.ContainsKey(key);
        }
        finally
        {
            lock (_waiterLock)
            {
                if (_waiters.TryGetValue(key, out List<ManualResetEventSlim> list))
                {
                    list.Remove(ev);
                    if (list.Count == 0)
                    {
                        _waiters.Remove(key);
                    }
                }
            }
            ev.Dispose();
        }
    }

    private void SignalWaiters(string key)
    {
        ManualResetEventSlim[] toSignal = null;
        lock (_waiterLock)
        {
            if (_waiters.TryGetValue(key, out List<ManualResetEventSlim> list) && list.Count > 0)
            {
                toSignal = list.ToArray();
            }
        }
        if (toSignal == null)
        {
            return;
        }
        foreach (ManualResetEventSlim ev in toSignal)
        {
            // A waiter that already timed out and disposed its event is harmless
            // to skip (it re-checks _entries after its Wait returns).
            try { ev.Set(); }
            catch (ObjectDisposedException) { }
        }
    }

    public bool TryGet(string key, out LiveMarketEntry entry)
    {
        return _entries.TryGetValue(key, out entry);
    }

    public IReadOnlyList<LiveMarketEntry> GetAll()
    {
        return _entries.Values.ToArray();
    }

    public void Clear()
    {
        _entries.Clear();
    }

    public int Count => _entries.Count;
}
