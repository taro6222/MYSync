using MYSync.Desktop;
using MYSync.Sync.Core;
using MYSync.Sync.Infrastructure;

internal static class TransferHistoryChecks
{
    public static void Run()
    {
        static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
        var history = new TransferHistory();
        var pair = Guid.NewGuid(); var run = Guid.NewGuid();
        var first = new SyncProgress(SyncPhase.Scanning, "start", "same.txt", SyncAction.Upload,
            RunId: run, ActivityId: Guid.NewGuid(), Kind: EntryKind.File, Event: TransferEvent.Started);
        history.Apply(pair, "pair", first);
        history.Apply(pair, "pair", first with { Phase = SyncPhase.Transferring, Bytes = 100, Event = TransferEvent.None });
        history.Apply(pair, "pair", first with { Phase = SyncPhase.Attention, Event = TransferEvent.Retrying });
        history.Apply(pair, "pair", first with { Phase = SyncPhase.Transferring, Bytes = 60, Event = TransferEvent.None });
        history.Apply(pair, "pair", first with { Phase = SyncPhase.Verifying, Event = TransferEvent.Applied });
        Check(history.SessionBytes == 160 && history.Rows.Single().Bytes == 160, "retry bytes counted incorrectly");
        Check(!history.Rows.Single().Done && history.CompletedFiles == 0, "unverified operation shown as completed");
        var second = first with { ActivityId = Guid.NewGuid(), Path = "second.txt" };
        history.Apply(pair, "pair", second);
        history.Apply(pair, "pair", second with { Phase = SyncPhase.Verifying, Event = TransferEvent.Applied });
        Check(history.Rows[0].Path == "second.txt" && history.Rows[1].Path == "same.txt", "history not newest first");
        var done = new SyncProgress(SyncPhase.Idle, "verified", RunId: run);
        history.Apply(pair, "pair", done); history.Apply(pair, "pair", done);
        Check(history.CompletedFiles == 2 && history.Rows.All(x => x.Done), "verified files counted more than once");
        var cancelled = first with { RunId = Guid.NewGuid(), ActivityId = Guid.NewGuid() };
        history.Apply(pair, "pair", cancelled);
        history.Apply(pair, "pair", cancelled with { Phase = SyncPhase.Idle, Event = TransferEvent.Cancelled });
        Check(history.Rows.Count == 3 && history.Rows[0].State == "중단", "repeat path overwrote past record or cancellation missing");
        Check(history.CompletedFiles == 2, "cancelled file counted as successful");
        for (var i = 0; i < TransferHistory.Limit + 2; i++)
        {
            var item = first with { RunId = Guid.NewGuid(), ActivityId = Guid.NewGuid() };
            history.Apply(pair, "pair", item);
            history.Apply(pair, "pair", item with { Phase = SyncPhase.Attention, Event = TransferEvent.Failed });
        }
        Check(history.Rows.Count == TransferHistory.Limit && history.SessionBytes == 160, "retention discarded session totals");
        Console.WriteLine("PASS: newest-first file history, retry byte totals, verified completion, repeated paths, cancellation and bounded retention");
    }
}
