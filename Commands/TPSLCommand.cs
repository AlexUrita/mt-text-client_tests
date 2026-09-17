using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using MTShared.Network;
using MTShared.Structs;
using MTShared.Types;
using MTTextClient.Core;
namespace MTTextClient.Commands;

/// <summary>
/// TPSL commands — view and manage Take Profit / Stop Loss positions.
///
/// Subcommands:
///   tpsl list                  — list all TPSL positions (from subscription)
///   tpsl cancel <id>           — cancel a TPSL position
///   tpsl subscribe             — subscribe to TPSL updates
///   tpsl unsubscribe           — unsubscribe from TPSL updates
///
/// Supports @profile targeting.
/// </summary>
public sealed class TPSLCommand : ICommand
{
    private readonly ConnectionManager _manager;

    public string Name => "tpsl";
    public string Description => "View and manage Take Profit / Stop Loss positions";
    public string Usage => "tpsl <list|cancel <id>|subscribe|unsubscribe> [@profile]";

    public TPSLCommand(ConnectionManager manager)
    {
        _manager = manager;
    }

    public CommandResult Execute(string[] args)
    {
        if (args.Length == 0)
        {
            return CommandResult.Fail(
                "Usage: tpsl <subcommand>\n" +
                "  list         — list all TPSL positions\n" +
                "  cancel <id>  — cancel a TPSL position\n" +
                "  subscribe    — subscribe to TPSL updates\n" +
                "  unsubscribe  — unsubscribe from TPSL updates");
        }

        string? targetProfile = null;
        bool confirmFlag = false;
        var cleanArgs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith('@'))
            {
                targetProfile = args[i][1..];
            }
            else if (args[i].Equals("--confirm", StringComparison.OrdinalIgnoreCase) ||
                     args[i].Equals("-y", StringComparison.OrdinalIgnoreCase))
            {
                confirmFlag = true;
            }
            else
            {
                cleanArgs.Add(args[i]);
            }
        }

        if (cleanArgs.Count == 0)
        {
            return CommandResult.Fail("Missing subcommand.");
        }

        string subcommand = cleanArgs[0].ToLowerInvariant();

        return subcommand switch
        {
            "list" => HandleList(targetProfile),
            "cancel" => HandleCancel(cleanArgs, targetProfile, confirmFlag),
            "subscribe" => HandleSubscribe(targetProfile),
            "unsubscribe" => HandleUnsubscribe(targetProfile),
            "join" => HandleJoin(cleanArgs, targetProfile, confirmFlag),
            "split" => HandleSplit(cleanArgs, targetProfile, confirmFlag),
            // TPSL bulk operations (loop wrappers around the existing single-item wire methods).
            "cancel-many" => HandleCancelMany(cleanArgs, targetProfile, confirmFlag),
            "split-many"  => HandleSplitMany(cleanArgs, targetProfile, confirmFlag),
            // Panic operations (immediate MARKET close via TPSL mechanism).
            "panic"       => HandlePanic(cleanArgs, targetProfile, confirmFlag),
            "panic-many"  => HandlePanicMany(cleanArgs, targetProfile, confirmFlag),
            _ => CommandResult.Fail($"Unknown subcommand: {subcommand}. Use: list, cancel, subscribe, unsubscribe, join, split, cancel-many, split-many, panic, panic-many")
        };
    }

    private CoreConnection? ResolveConnection(string? targetProfile, out CommandResult? error)
    {
        error = null;
        CoreConnection? conn = _manager.Resolve(targetProfile);
        if (conn == null)
        {
            error = targetProfile != null
                ? CommandResult.Fail($"No connection '{targetProfile}'. Use 'status' to see connections.")
                : CommandResult.Fail("Not connected. Use 'connect <profile>' first.");
            return null;
        }
        if (!conn.IsConnected)
        {
            error = CommandResult.Fail($"[{conn.Name}] Not connected.");
            return null;
        }
        return conn;
    }

    private CommandResult HandleList(string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        // Open a transient SendAlgorithmTPSLsSubscribe on every read
        // Mirrors the vendor read pattern. Replaces the prior "subscribe first then list"
        // pattern that left cold connections returning an empty stub. Lazily
        // creates TPSLStore inside ForceRefreshTPSL.
        conn.ForceRefreshTPSL();

        TPSLStore? store = conn.TPSLStore;
        if (store == null || !store.HasData)
        {
            return CommandResult.Ok($"[{conn.Name}] TPSL Positions (0 entries).");
        }

        IReadOnlyList<TPSLPositionSnapshot> positions = store.GetAll();
        var sb = new StringBuilder();
        sb.AppendLine($"[{conn.Name}] TPSL Positions ({positions.Count}):");
        sb.AppendLine();

        for (int i = 0; i < positions.Count; i++)
        {
            TPSLPositionSnapshot pos = positions[i];
            sb.AppendLine($"  [{i}] ID: {pos.Id}");
            sb.AppendLine($"      Symbol: {pos.Symbol} ({pos.MarketType}) {pos.Side}");
            sb.AppendLine($"      Qty: {pos.Qty:F6} @ Entry: {pos.EntryPrice:F4}");
            sb.AppendLine($"      TP: {(pos.TakeProfitEnabled ? $"{pos.TakeProfitPercent:F2}% ({pos.TakeProfitStatus})" : "OFF")}");
            sb.AppendLine($"      SL: {(pos.StopLossEnabled ? $"{pos.StopLossPercent:F2}% ({pos.StopLossStatus})" : "OFF")}");
            if (pos.TrailingEnabled)
            {
                sb.AppendLine($"      Trailing: {pos.TrailingSpread:F2}%");
            }
            sb.AppendLine($"      Running: {pos.IsRunning} | Split: {pos.SplitCount}x{pos.SplitPercentage:F1}%");
            sb.AppendLine();
        }

        return CommandResult.Ok(sb.ToString());
    }

    private CommandResult HandleCancel(List<string> cleanArgs, string? targetProfile, bool confirm)
    {
        if (cleanArgs.Count < 2)
        {
            return CommandResult.Fail("Usage: tpsl cancel <id> --confirm [@profile]");
        }
        if (!confirm)
        {
            return CommandResult.Fail($"Cancel TPSL ID {cleanArgs[1]}? Use --confirm to proceed.");
        }
        if (!long.TryParse(cleanArgs[1], out long tpslId))
        {
            return CommandResult.Fail($"Invalid TPSL ID: {cleanArgs[1]}");
        }

        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        // Prime the cache so CancelTPSL echoes the full identity tuple (an
        // id-only stub is silently dropped) and so the book check below can
        // confirm the cancel.
        conn.ForceRefreshTPSL();

        // Short ack timeout: the TPSLCancelNotificationData ack is version-fragile,
        // so the TPSL book — not the ack — is the source of truth.
        NotificationMessageData? result = conn.CancelTPSL(tpslId, timeoutMs: 4_000);
        bool gone = WaitForTpslGone(conn, tpslId, 6_000, 750);
        if (gone)
        {
            return CommandResult.Ok(
                $"[{conn.Name}] Cancel TPSL {tpslId}: cancelled" +
                (result != null && result.IsOk
                    ? $" — {result.notificationCode}"
                    : " (verified via TPSL book; wire ack not received)"));
        }
        if (result == null)
        {
            return CommandResult.Fail(
                $"[{conn.Name}] Cancel TPSL {tpslId} UNCONFIRMED: no wire ack, and the TPSL is still " +
                "present after 6s. Re-check with `tpsl list`.");
        }
        return result.IsOk
            ? CommandResult.Ok(
                $"[{conn.Name}] Cancel TPSL {tpslId}: {result.notificationCode} — {result.msgString} " +
                "(ack OK; still listed — may settle shortly, re-check `tpsl list`)")
            : CommandResult.Fail(
                $"[{conn.Name}] Cancel TPSL {tpslId} failed: {result.notificationCode} — {result.msgString}");
    }

    private CommandResult HandleSubscribe(string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        bool subscribed = conn.SubscribeTPSL();
        if (!subscribed)
        {
            return CommandResult.Fail($"[{conn.Name}] Failed to subscribe to TPSL updates.");
        }

        return CommandResult.Ok($"[{conn.Name}] Subscribed to TPSL updates. Use 'tpsl list' to view data.");
    }

    private CommandResult HandleUnsubscribe(string? targetProfile)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        conn.UnsubscribeTPSL();
        return CommandResult.Ok($"[{conn.Name}] Unsubscribed from TPSL updates.");
    }

    private CommandResult HandleJoin(List<string> args, string? targetProfile, bool confirmed)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (!confirmed)
        {
            return CommandResult.Fail("tpsl join requires --confirm. Provide TPSL IDs to join.");
        }

        if (args.Count < 2)
        {
            return CommandResult.Fail("Usage: tpsl join <id1> <id2> [<id3>...] --confirm");
        }

        List<long> ids = new List<long>();
        for (int i = 1; i < args.Count; i++)
        {
            if (long.TryParse(args[i], out long id))
            {
                ids.Add(id);
            }
        }

        if (ids.Count < 2)
        {
            return CommandResult.Fail("Need at least 2 TPSL IDs to join.");
        }

        TPSLInfoListData tpslData = new TPSLInfoListData();
        tpslData.infoData = ids.Select(id => new TPSLInfoData { id = id }).ToList();

        // Prime the cache (JoinTPSL echoes the full identity tuple per id) and
        // snapshot the book so we can confirm the merge without the ack.
        conn.ForceRefreshTPSL();
        string beforeSig = TpslBookSignature(conn);

        NotificationMessageData? result = conn.JoinTPSL(tpslData, timeoutMs: 4_000);
        bool changed = WaitForTpslBookChange(conn, beforeSig, 6_000, 750);
        if (changed)
        {
            return CommandResult.Ok(
                $"[{conn.Name}] TPSL join [{string.Join(", ", ids)}]: applied" +
                (result != null && result.IsOk
                    ? $" — {result.notificationCode}"
                    : " (verified via TPSL book change; wire ack not received)"));
        }
        if (result == null)
        {
            return CommandResult.Fail(
                $"[{conn.Name}] TPSL join UNCONFIRMED: no wire ack and the TPSL book is unchanged after 6s. " +
                "Re-check with `tpsl list`.");
        }
        return result.IsOk
            ? CommandResult.Ok($"[{conn.Name}] TPSL join: {result.notificationCode} (ack OK; book not yet changed — re-check `tpsl list`)")
            : CommandResult.Fail($"[{conn.Name}] TPSL join failed: {result.notificationCode} — {result.jsonData}");
    }

    private CommandResult HandleSplit(List<string> args, string? targetProfile, bool confirmed)
    {
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null)
        {
            return error!;
        }

        if (!confirmed)
        {
            return CommandResult.Fail("tpsl split requires --confirm. Provide TPSL ID to split.");
        }

        if (args.Count < 2)
        {
            return CommandResult.Fail("Usage: tpsl split <tpsl_id> --confirm");
        }

        if (!long.TryParse(args[1], out long tpslId))
        {
            return CommandResult.Fail($"Invalid TPSL ID: {args[1]}");
        }

        TPSLInfoData tpslData = new TPSLInfoData();
        tpslData.id = tpslId;

        // Prime the cache (SplitTPSL echoes the full identity tuple) and snapshot
        // the book so the split can be confirmed without the ack.
        conn.ForceRefreshTPSL();
        string beforeSig = TpslBookSignature(conn);

        NotificationMessageData? result = conn.SplitTPSL(tpslData, timeoutMs: 4_000);
        bool changed = WaitForTpslBookChange(conn, beforeSig, 6_000, 750);
        if (changed)
        {
            return CommandResult.Ok(
                $"[{conn.Name}] TPSL split {tpslId}: applied" +
                (result != null && result.IsOk
                    ? $" — {result.notificationCode}"
                    : " (verified via TPSL book change; wire ack not received)"));
        }
        if (result == null)
        {
            return CommandResult.Fail(
                $"[{conn.Name}] TPSL split {tpslId} UNCONFIRMED: no wire ack and the TPSL book is unchanged " +
                "after 6s. Re-check with `tpsl list`.");
        }
        return result.IsOk
            ? CommandResult.Ok($"[{conn.Name}] TPSL split {tpslId}: {result.notificationCode} (ack OK; book not yet changed — re-check `tpsl list`)")
            : CommandResult.Fail($"[{conn.Name}] TPSL split failed: {result.notificationCode} — {result.jsonData}");
    }

    // ── TPSL bulk operations ─────────────────────────────────────
    //
    // The MTShared wire protocol exposes only single-item Cancel / Split
    // requests. These "many" tools are loop wrappers that emit one wire call
    // per ID and aggregate the results. A single bad ID does not abort the
    // remaining ones — every ID is attempted and reported individually.

    private CommandResult HandleCancelMany(List<string> args, string? targetProfile, bool confirmed)
    {
        if (!confirmed)
        {
            return CommandResult.Fail("tpsl cancel-many requires --confirm. Provide TPSL IDs to cancel.");
        }
        if (args.Count < 2)
        {
            return CommandResult.Fail("Usage: tpsl cancel-many <id1> <id2> [...] --confirm");
        }
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null) return error!;

        // Prime the cache via a transient subscribe so the per-id
        // CancelTPSL path finds the full TPSLInfoData (cancel binds by full
        // identity tuple).
        conn.ForceRefreshTPSL();

        // Fire all cancels with a short, version-fragile-ack timeout; keep each
        // per-id wire result for fallback reporting.
        var sent = new List<(long id, string? parseError, NotificationMessageData? result)>();
        for (int i = 1; i < args.Count; i++)
        {
            if (!long.TryParse(args[i], out long id))
            {
                sent.Add((0L, args[i], null));
                continue;
            }
            sent.Add((id, null, conn.CancelTPSL(id, timeoutMs: 4_000)));
        }

        // One post-loop book read is the source of truth: any id no longer in the
        // book is cancelled regardless of whether its (version-fragile) ack arrived.
        conn.ForceRefreshTPSL();

        var rows = new List<object>();
        int ok = 0, fail = 0;
        foreach ((long id, string? parseError, NotificationMessageData? result) in sent)
        {
            if (parseError != null)
            {
                rows.Add(new { id = parseError, success = false, message = "invalid id" });
                fail++;
                continue;
            }
            bool gone = conn.TPSLStore?.GetById(id) == null;
            bool ackOk = result != null && result.IsOk;
            bool success = gone || ackOk;
            if (success) ok++; else fail++;
            rows.Add(new
            {
                id,
                success,
                notificationCode = ackOk ? result!.notificationCode.ToString()
                                 : gone ? "VERIFIED"
                                 : result?.notificationCode.ToString() ?? "TIMEOUT",
                message = ackOk ? (result!.msgString ?? "")
                        : gone ? "cancelled (verified via TPSL book)"
                        : result?.msgString ?? "no response — still present, re-check `tpsl list`"
            });
        }
        return CommandResult.Ok(
            $"[{conn.Name}] TPSL cancel-many: {ok} succeeded, {fail} failed (of {args.Count - 1}).",
            new { Server = conn.Name, Ok = ok, Failed = fail, Results = rows });
    }

    private CommandResult HandleSplitMany(List<string> args, string? targetProfile, bool confirmed)
    {
        if (!confirmed)
        {
            return CommandResult.Fail("tpsl split-many requires --confirm. Provide TPSL IDs to split.");
        }
        if (args.Count < 2)
        {
            return CommandResult.Fail("Usage: tpsl split-many <id1> <id2> [...] --confirm");
        }
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null) return error!;

        // Prime the cache so SplitTPSL can resolve the full cached TPSLInfoData
        // by id instead of falling back to an id-only stub.
        conn.ForceRefreshTPSL();

        var rows = new List<object>();
        int ok = 0, fail = 0;
        for (int i = 1; i < args.Count; i++)
        {
            if (!long.TryParse(args[i], out long id))
            {
                rows.Add(new { id = args[i], success = false, message = "invalid id" });
                fail++;
                continue;
            }
            NotificationMessageData? result = conn.SplitTPSL(new TPSLInfoData { id = id }, timeoutMs: 4_000);
            bool success = result != null && result.IsOk;
            if (success) ok++; else fail++;
            rows.Add(new
            {
                id,
                success,
                notificationCode = result?.notificationCode.ToString() ?? "TIMEOUT",
                message = result?.msgString ?? "no response"
            });
        }
        return CommandResult.Ok(
            $"[{conn.Name}] TPSL split-many: {ok} succeeded, {fail} failed (of {args.Count - 1}).",
            new { Server = conn.Name, Ok = ok, Failed = fail, Results = rows });
    }

    // ── TPSL panic close ─────────────────────────────────────────
    //
    // "Panic" close = immediate MARKET-order exit of the position underlying
    // the named TPSL. Implementation: look up the TPSL in the store (must be
    // subscribed first), extract its symbol/market/side, build PositionData,
    // and call ClosePositionByTPSL with OrderType.MARKET.

    private CommandResult HandlePanic(List<string> args, string? targetProfile, bool confirmed)
    {
        if (!confirmed)
        {
            return CommandResult.Fail("tpsl panic requires --confirm. This will MARKET-close the position via TPSL.");
        }
        if (args.Count < 2)
        {
            return CommandResult.Fail("Usage: tpsl panic <tpsl_id> --confirm");
        }
        if (!long.TryParse(args[1], out long tpslId))
        {
            return CommandResult.Fail($"Invalid TPSL ID: {args[1]}");
        }
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null) return error!;

        var panicResult = PanicSingle(conn, tpslId);
        return panicResult.Success
            ? CommandResult.Ok(
                $"[{conn.Name}] TPSL panic {tpslId}: {panicResult.NotificationCode}",
                new { Server = conn.Name, Id = tpslId, panicResult.NotificationCode, panicResult.Message })
            : CommandResult.Fail(
                $"[{conn.Name}] TPSL panic {tpslId} failed: {panicResult.NotificationCode} — {panicResult.Message}");
    }

    private CommandResult HandlePanicMany(List<string> args, string? targetProfile, bool confirmed)
    {
        if (!confirmed)
        {
            return CommandResult.Fail("tpsl panic-many requires --confirm. Provide TPSL IDs to MARKET-close.");
        }
        if (args.Count < 2)
        {
            return CommandResult.Fail("Usage: tpsl panic-many <id1> <id2> [...] --confirm");
        }
        CoreConnection? conn = ResolveConnection(targetProfile, out CommandResult? error);
        if (conn == null) return error!;

        // Prime the cache; PanicSingle requires a cached TPSLInfoData lookup
        // (otherwise short-circuits with NOT_FOUND + "subscribe first" hint).
        conn.ForceRefreshTPSL();

        var rows = new List<object>();
        int ok = 0, fail = 0;
        for (int i = 1; i < args.Count; i++)
        {
            if (!long.TryParse(args[i], out long id))
            {
                rows.Add(new { id = args[i], success = false, message = "invalid id" });
                fail++;
                continue;
            }
            var r = PanicSingle(conn, id, verifyTimeoutMs: 3_000);
            if (r.Success) ok++; else fail++;
            rows.Add(new { id, success = r.Success, r.NotificationCode, r.Message });
        }
        return CommandResult.Ok(
            $"[{conn.Name}] TPSL panic-many: {ok} succeeded, {fail} failed (of {args.Count - 1}).",
            new { Server = conn.Name, Ok = ok, Failed = fail, Results = rows });
    }

    /// <summary>
    /// Single-ID panic helper used by HandlePanic and HandlePanicMany.
    /// Looks up the TPSL snapshot for the position metadata; without an
    /// active TPSL subscription (`tpsl subscribe`) the store is empty and
    /// the lookup fails with a "subscribe first" diagnostic.
    /// </summary>
    private static (bool Success, string NotificationCode, string Message) PanicSingle(
        CoreConnection conn, long tpslId, int verifyTimeoutMs = 6_000)
    {
        if (conn.TPSLStore?.GetById(tpslId) == null)
        {
            return (false, "NOT_FOUND",
                "TPSL not found in store. Run 'tpsl subscribe' first, then 'tpsl list' to confirm IDs.");
        }
        // The TPSL-overload of panic-sell is the canonical wire path here —
        // echoes the full cached TPSLInfoData (looked up inside PanicSellTpsl)
        // so the server can bind by the full identity tuple. Closing through
        // ClosePositionByTPSL with a sparse PositionData stub was the prior
        // approach and was silently rejected.
        // Short ack timeout: the PanicSellNotificationData ack is version-fragile.
        // Panic MARKET-closes the underlying position, which removes the TPSL
        // entry from the book — that disappearance is the version-robust proof.
        NotificationMessageData? result = conn.PanicSellTpsl(tpslId, timeoutMs: 4_000);
        bool gone = WaitForTpslGone(conn, tpslId, verifyTimeoutMs, 750);
        if (gone)
        {
            return result != null && result.IsOk
                ? (true, result.notificationCode.ToString(), result.msgString ?? "")
                : (true, "VERIFIED", "closed (verified via TPSL book; wire ack not received)");
        }
        if (result == null)
        {
            return (false, "UNCONFIRMED",
                "no wire ack and TPSL still present after verify window — re-check `tpsl list`");
        }
        return (result.IsOk, result.notificationCode.ToString(), result.msgString ?? "");
    }

    // ── Book-verified confirmation ───────────────────────────────────────
    //
    // The TPSL mutator acks (TPSLCancelNotificationData, PanicSellNotificationData,
    // OrderSplitNotificationData, OrderJoinNotificationData) all arrive on the
    // shared notification channel and have been renamed / re-typed / dropped
    // across CORE builds, so a missing ack is NOT proof of failure. These helpers
    // confirm the effect against the observable TPSL book instead, which keeps the
    // mutators correct on 25810 and future versions regardless of ack behaviour.

    /// <summary>Poll the TPSL book until <paramref name="id"/> is gone or the
    /// timeout elapses. Used by cancel and panic — both remove the entry (cancel
    /// drops the bracket, panic MARKET-closes the position). Each poll forces a
    /// fresh transient subscribe so the store reflects the current server state.</summary>
    private static bool WaitForTpslGone(CoreConnection conn, long id, int timeoutMs, int pollMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            conn.ForceRefreshTPSL(2_000);
            if (conn.TPSLStore?.GetById(id) == null) { return true; }
            if (sw.ElapsedMilliseconds >= timeoutMs) { return false; }
            System.Threading.Thread.Sleep(pollMs);
        }
    }

    /// <summary>Signature of the whole TPSL book (sorted ids + per-entry split /
    /// join / running state). A change after a split or join is the version-robust
    /// proof the mutation took effect — those ops rewrite entries in place / merge
    /// ids rather than removing a single known id, so "gone" doesn't apply.</summary>
    private static string TpslBookSignature(CoreConnection conn)
    {
        TPSLStore? store = conn.TPSLStore;
        if (store == null) { return ""; }
        var sb = new StringBuilder();
        foreach (TPSLPositionSnapshot p in store.GetAll())
        {
            sb.Append(p.Id).Append(':')
              .Append(p.SplitCount).Append('x').Append(p.SplitPercentage.ToString("F1")).Append(':')
              .Append(p.UseJoinKey).Append(':').Append(p.JoinKey).Append(':')
              .Append(p.IsRunning).Append('|');
        }
        return sb.ToString();
    }

    /// <summary>Poll until the TPSL book signature differs from
    /// <paramref name="beforeSig"/> or the timeout elapses. Used by split and join.</summary>
    private static bool WaitForTpslBookChange(CoreConnection conn, string beforeSig, int timeoutMs, int pollMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            conn.ForceRefreshTPSL(2_000);
            if (TpslBookSignature(conn) != beforeSig) { return true; }
            if (sw.ElapsedMilliseconds >= timeoutMs) { return false; }
            System.Threading.Thread.Sleep(pollMs);
        }
    }

}
