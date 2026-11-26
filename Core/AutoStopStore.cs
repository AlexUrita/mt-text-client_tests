using System;
using System.Collections.Generic;
using System.Linq;
using MTShared.Network;
namespace MTTextClient.Core;

/// <summary>
/// In-memory store for auto-stop algorithms received from Core via the
/// AUTO_STOP subscription (MTCore 0.7.24554+). Replaces the pre-24554
/// settings-blob model (AutoStopAlgorithm.Balance.Filters), which the vendor
/// removed along with the AutoStopAlgorithmData type.
///
/// The core delivers an <see cref="AutoStopListEvent"/> snapshot on subscribe,
/// then incremental <see cref="AutoStopOnBalanceEvent"/> /
/// <see cref="AutoStopOnReportsEvent"/> events as autostops are added, updated,
/// run, stopped, or removed. Auto-stops are keyed by their core-assigned id.
/// </summary>
public sealed class AutoStopStore
{
    private readonly object _gate = new();
    private readonly Dictionary<long, AutoStopOnBalanceData> _balance = new();
    private readonly Dictionary<long, AutoStopOnReportsData> _reports = new();

    // MTCore 0.7.26721 moved the live per-autostop figures (balance baselines,
    // report totals) off the config structs and onto separate *ValuesEvent
    // pushes keyed by autostop id. Kept apart from the config dictionaries so a
    // fresh AutoStopListEvent snapshot doesn't wipe the last known values.
    private readonly Dictionary<long, AutoStopBalanceDynamicValueData> _balanceValues = new();
    private readonly Dictionary<long, AutoStopReportDynamicValueData> _reportValues = new();

    /// <summary>True once at least one event (snapshot or incremental) landed.</summary>
    public bool HasData { get; private set; }
    public DateTime LastUpdateUtc { get; private set; }

    public IReadOnlyList<AutoStopOnBalanceData> Balance
    {
        get { lock (_gate) { return _balance.Values.OrderBy(a => a.id).ToList(); } }
    }

    public IReadOnlyList<AutoStopOnReportsData> Reports
    {
        get { lock (_gate) { return _reports.Values.OrderBy(a => a.id).ToList(); } }
    }

    public AutoStopOnBalanceData? FindBalanceById(long id)
    {
        lock (_gate) { return _balance.TryGetValue(id, out var v) ? v : null; }
    }

    /// <summary>Latest pushed balance baselines for an autostop id (0.7.26721+
    /// AutoStopOnBalanceValuesEvent), or null if none has arrived yet.</summary>
    public AutoStopBalanceDynamicValueData? FindBalanceValues(long id)
    {
        lock (_gate) { return _balanceValues.TryGetValue(id, out AutoStopBalanceDynamicValueData? v) ? v : null; }
    }

    /// <summary>Latest pushed report figures for an autostop id (0.7.26721+
    /// AutoStopOnReportsValuesEvent), or null if none has arrived yet.</summary>
    public AutoStopReportDynamicValueData? FindReportValues(long id)
    {
        lock (_gate) { return _reportValues.TryGetValue(id, out AutoStopReportDynamicValueData? v) ? v : null; }
    }

    /// <summary>Apply an AUTO_STOP event delivered by the subscription callback.</summary>
    public void ProcessEvent(AutoStopEventData? data)
    {
        if (data == null) { return; }
        lock (_gate)
        {
            switch (data)
            {
                case AutoStopListEvent snapshot:
                    _balance.Clear();
                    _reports.Clear();
                    if (snapshot.AutoStopsOnBalance != null)
                    {
                        foreach (var a in snapshot.AutoStopsOnBalance) { _balance[a.id] = a; }
                    }
                    if (snapshot.AutoStopsOnReports != null)
                    {
                        foreach (var a in snapshot.AutoStopsOnReports) { _reports[a.id] = a; }
                    }
                    break;

                case AutoStopOnBalanceAddedEvent added when added.AutoStops != null:
                    foreach (var a in added.AutoStops) { _balance[a.id] = a; }
                    break;

                case AutoStopOnBalanceUpdatedEvent updated when updated.AutoStops != null:
                    foreach (var a in updated.AutoStops) { _balance[a.id] = a; }
                    break;

                case AutoStopOnBalanceRemovedEvent removed when removed.AutoStopIds != null:
                    foreach (var id in removed.AutoStopIds) { _balance.Remove(id); }
                    break;

                case AutoStopOnBalanceValuesEvent balanceValues when balanceValues.Values != null:
                    foreach (AutoStopBalanceDynamicValueData v in balanceValues.Values)
                    {
                        if (v != null)
                        {
                            _balanceValues[v.id] = v;
                        }
                    }
                    break;

                case AutoStopOnReportsValuesEvent reportValues when reportValues.Values != null:
                    foreach (AutoStopReportDynamicValueData v in reportValues.Values)
                    {
                        if (v != null)
                        {
                            _reportValues[v.id] = v;
                        }
                    }
                    break;

                // OnReports incremental events are intentionally not handled here:
                // every CRUD op force-refreshes the full snapshot (AutoStopListEvent),
                // which carries the current reports list, so a transient event handler
                // would be dead weight. Reports CRUD is a separate, not-yet-surfaced
                // capability (see the AUTO_STOP OnReports family).
            }
            HasData = true;
            LastUpdateUtc = DateTime.UtcNow;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _balance.Clear();
            _reports.Clear();
            _balanceValues.Clear();
            _reportValues.Clear();
            HasData = false;
            LastUpdateUtc = default;
        }
    }
}
