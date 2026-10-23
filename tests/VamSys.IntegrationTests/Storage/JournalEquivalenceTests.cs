using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.IntegrationTests.Storage;

/// <summary>
/// The journal's whole promise in one test: after any sequence of edits, the database written by journal saves is, table by
/// table and column by column, the database a full save of the same workspace writes. Database A is saved through the
/// journal after every step; database B is rewritten completely from a copy of the workspace after every step.
/// Cell-edit row saves, task checkpoints, scoped saves, undo and redo, imports, snapshot merges, conflict changes,
/// job changes and header changes are mixed in; an injected failure now and then must leave the database as it was and the
/// following save must repair everything.
/// </summary>
public class JournalEquivalenceTests
{
    sealed class Run : IDisposable
    {
        readonly TempDataDirectory dirA = TempDataDirectory.Create("vamsys-journal-a-");
        readonly TempDataDirectory dirB = TempDataDirectory.Create("vamsys-journal-b-");
        public readonly WorkspaceStore A, B;
        public readonly Workspace W = new() { Name = "Equivalence", AirlineId = "7", Mode = RunMode.Online };
        readonly Random rng;
        readonly DraftEdits edits = new();
        long revisionB;
        bool scopedPending;
        public int Steps, Failures, RowSaves, Checkpoints, ScopedSaves, EmptySaves, FullFallbacks, Compared;

        public Run(int seed)
        {
            rng = new Random(seed);
            A = new WorkspaceStore(dirA.Path);
            B = new WorkspaceStore(dirB.Path);
            // Start with a populated, saved workspace so the first steps already run on the journal.
            foreach (var kind in Enum.GetValues<ResourceKind>())
                for (var n = rng.Next(0, 25); n > 0; n--)
                    AddRowTo(Data(kind), kind, immediate: true);
            W.Jobs.Add(NewJob());
            A.Save(W);
            SaveB();
            AssertSame("initial");
        }

        public void Dispose() { dirA.Dispose(); dirB.Dispose(); }

        ResourceData Data(ResourceKind kind) => W.Resources[kind];
        ResourceKind AnyKind() => (ResourceKind)rng.Next(Enum.GetValues<ResourceKind>().Length);
        static readonly string[] Fields = ["Name", "Value", "Remarks", "Tags"];

        DataRow NewRow(ResourceKind kind, int n) => new()
        {
            Identity = rng.Next(3) == 0 ? new((1000 + n).ToString(), "7") : null,
            Fields = new() { [Schemas.All[kind].Key] = "k" + Steps + "-" + n + "-" + rng.Next(1000), ["Name"] = "name" + rng.Next(100), ["Value"] = rng.Next(10) == 0 ? "" : "v" + rng.Next(50) },
        };

        void AddRowTo(ResourceData data, ResourceKind kind, bool immediate = false)
        {
            var row = NewRow(kind, data.Draft.Count);
            if (immediate)
            { data.Draft.Add(row); data.Snapshot.Add(row.Copy()); }
            else
                data.Edit([row.LocalId], () => data.Draft.Add(row), structural: true);
            data.Columns = data.Columns.Concat(row.Fields.Keys).Distinct().ToList();
        }

        BatchJob NewJob()
        {
            var job = new BatchJob { Mode = RunMode.Online, Created = DateTimeOffset.UnixEpoch.AddMinutes(rng.Next(1000)) };
            for (var n = rng.Next(1, 6); n > 0; n--)
            {
                var kind = AnyKind();
                var row = NewRow(kind, n);
                job.Items.Add(new ChangeItem { Resource = kind, Kind = ChangeKind.Create, After = row, State = (ItemState)rng.Next(0, 8), Message = "m" + rng.Next(100) });
            }
            return job;
        }

        // ---- steps ------------------------------------------------------------------------------------------------------

        public async Task Step()
        {
            Steps++;
            var cellOnly = false;
            switch (rng.Next(24))
            {
                case 0 or 1 or 2 or 3: CellEdit(); cellOnly = true; break;
                case 4 or 5: AddRows(); break;
                case 6: RemoveRows(); break;
                case 7: Reorder(); break;
                case 8 or 9: BatchEdit(); break;
                case 10: Import(); break;
                case 11: Merge(); break;
                case 12: Conflicts(); break;
                case 13: AddJob(); break;
                case 14 or 15: ChangeJob(); break;
                case 16: RemoveOrSwapJob(); break;
                case 17: Header(); break;
                case 18: UndoRedo(); break;
                case 19: Direct(); break;
                case 20: await Checkpoint(); break;
                case 21: { var data = Data(AnyKind()); data.ClearHistory(); break; }
                case 22: break; // nothing changed
                default: CellEdit(); cellOnly = true; break;
            }
            if (cellOnly && !scopedPending && rng.Next(2) == 0)
            { await RowSave(); return; }
            await Save();
        }

        void CellEdit()
        {
            var kind = AnyKind();
            var data = Data(kind);
            if (data.Draft.Count == 0)
            { AddRowTo(data, kind); return; }
            var row = data.Draft[rng.Next(data.Draft.Count)];
            var copy = row.Copy();
            copy.Fields[Fields[rng.Next(Fields.Length)]] = "e" + rng.Next(1000);
            data.Edit([row.LocalId], () => data.ReplaceDraftRows([copy]), newStep: rng.Next(3) != 0);
            edits.Cell(W, kind, row.LocalId);
        }

        void AddRows()
        {
            var kind = AnyKind();
            var data = Data(kind);
            var rows = Enumerable.Range(0, rng.Next(1, 4)).Select(n => NewRow(kind, data.Draft.Count + n)).ToArray();
            data.Edit(rows.Select(r => r.LocalId), () => data.Draft.AddRange(rows), structural: true);
            data.Columns = data.Columns.Concat(rows.SelectMany(r => r.Fields.Keys)).Distinct().ToList();
        }

        void RemoveRows()
        {
            var data = Data(AnyKind());
            if (data.Draft.Count == 0)
                return;
            var gone = Enumerable.Range(0, rng.Next(1, 4)).Select(_ => data.Draft[rng.Next(data.Draft.Count)]).DistinctBy(r => r.LocalId).ToArray();
            data.Edit(gone.Select(r => r.LocalId), () => { foreach (var row in gone) data.Draft.Remove(row); }, structural: true);
        }

        void Reorder()
        {
            var data = Data(AnyKind());
            if (data.Draft.Count < 3)
                return;
            data.Edit([], () =>
            {
                switch (rng.Next(3))
                {
                    case 0: var a = rng.Next(data.Draft.Count); var b = rng.Next(data.Draft.Count); (data.Draft[a], data.Draft[b]) = (data.Draft[b], data.Draft[a]); break;
                    case 1: data.Draft.Reverse(0, data.Draft.Count); break;
                    default: var row = data.Draft[0]; data.Draft.RemoveAt(0); data.Draft.Insert(rng.Next(data.Draft.Count + 1), row); break;
                }
            }, structural: true);
        }

        void BatchEdit()
        {
            var data = Data(AnyKind());
            if (data.Draft.Count == 0)
                return;
            var chosen = data.Draft.OrderBy(_ => rng.Next()).Take(rng.Next(1, Math.Min(40, data.Draft.Count) + 1)).ToArray();
            var field = Fields[rng.Next(Fields.Length)];
            var copies = chosen.Select(r => { var c = r.Copy(); c.Fields[field] = "b" + rng.Next(100); return c; }).ToArray();
            data.Edit(copies.Select(r => r.LocalId), () => data.ReplaceDraftRows(copies));
            if (!data.Columns.Contains(field))
                data.Columns.Add(field);
        }

        void Import()
        {
            var kind = AnyKind();
            var data = Data(kind);
            var rows = Enumerable.Range(0, rng.Next(0, 15)).Select(n => NewRow(kind, n)).ToList();
            foreach (var row in rows)
                row.Fields[Schemas.All[kind].Key] = "imp" + rng.Next(100000);
            rows = [.. rows.DistinctBy(r => r.Fields[Schemas.All[kind].Key])];
            if (rng.Next(2) == 0)
                CsvAdapter.Import(kind, data, rows, baseline: true);
            else
            {
                // A patch import: some existing keys (replaced rows), some new ones (appended rows).
                var existing = data.Draft.Where(r => Schemas.Key(kind, r) != "").Take(rng.Next(0, 4)).Select(r => { var c = new DataRow(); c.Fields[Schemas.All[kind].Key] = r.Get(Schemas.All[kind].Key); c.Fields["Value"] = "patched" + rng.Next(100); return c; });
                var patch = existing.Concat(rows).DistinctBy(r => Schemas.Key(kind, r)).ToList();
                try
                { CsvAdapter.Import(kind, data, patch, baseline: false); }
                catch (Exception e) when (e is FormatException or InvalidOperationException)
                { /* duplicate keys in the random data: the import refuses, nothing changed */ }
            }
        }

        void Merge()
        {
            var kind = AnyKind();
            var data = Data(kind);
            var incoming = new List<DataRow>();
            foreach (var row in data.Snapshot.Where(_ => rng.Next(4) != 0))
            {
                var copy = row.Copy();
                copy.Identity ??= new((5000 + incoming.Count).ToString(), "7");
                if (rng.Next(3) == 0)
                    copy.Fields["Value"] = "remote" + rng.Next(100);
                incoming.Add(copy);
            }
            for (var n = rng.Next(0, 4); n > 0; n--)
            {
                var row = NewRow(kind, n);
                row.Identity = new((9000 + n + rng.Next(500)).ToString(), "7");
                incoming.Add(row);
            }
            incoming = [.. incoming.DistinctBy(r => r.Identity!.RemoteId)];
            SnapshotMerger.Merge(kind, data, incoming);
        }

        void Conflicts()
        {
            var data = Data(AnyKind());
            if (data.Conflicts.Count > 0 && rng.Next(2) == 0)
                data.Conflicts.RemoveAt(rng.Next(data.Conflicts.Count));
            else if (data.Draft.Count > 0)
                data.Conflicts.Add(new(data.Draft[rng.Next(data.Draft.Count)].LocalId, Fields[rng.Next(Fields.Length)], "o", "l" + rng.Next(10), "r" + rng.Next(10)));
            if (rng.Next(4) == 0)
                data.SnapshotAt = DateTimeOffset.UnixEpoch.AddSeconds(rng.Next(100000));
        }

        void AddJob() => W.Jobs.Add(NewJob());

        // Jobs and their items are changed in place; that is only visible to the journal when the job is marked.
        void ChangeJob()
        {
            if (W.Jobs.Count == 0)
                return;
            var job = W.Jobs[rng.Next(W.Jobs.Count)];
            if (job.Items.Count > 0)
                job.Items[rng.Next(job.Items.Count)].State = (ItemState)rng.Next(0, 8);
            job.SetStatus((JobStatus)rng.Next(0, Enum.GetValues<JobStatus>().Length), Messages.Define("Text_5026A63B58"));
            W.Journal().TouchJob(job);
        }

        void RemoveOrSwapJob()
        {
            if (W.Jobs.Count == 0)
                return;
            if (rng.Next(2) == 0 || W.Jobs.Count < 2)
                W.Jobs.RemoveAt(rng.Next(W.Jobs.Count));
            else
                (W.Jobs[0], W.Jobs[^1]) = (W.Jobs[^1], W.Jobs[0]);
        }

        void Header()
        {
            switch (rng.Next(5))
            {
                case 0: W.Name = "Name " + rng.Next(1000); break;
                case 1: W.Mode = (RunMode)rng.Next(0, 3); break;
                case 2: W.ClientId = "client" + rng.Next(10); break;
                case 3: W.ConnectedAt = DateTimeOffset.UnixEpoch.AddSeconds(rng.Next(100000)); break;
                default: W.VerifiedOperations.Add("Op" + rng.Next(1000)); break;
            }
        }

        void UndoRedo()
        {
            var data = Data(AnyKind());
            if (rng.Next(2) == 0 && data.Undo.Count > 0)
                data.UndoEdit();
            else if (data.Redo.Count > 0)
                data.RedoEdit();
        }

        // List manipulation that bypasses every ResourceData helper.
        void Direct()
        {
            var data = Data(AnyKind());
            switch (rng.Next(5))
            {
                case 0: data.Snapshot.Add(NewRow(ResourceKind.Fleets, 1)); break;
                case 1 when data.Snapshot.Count > 0: data.Snapshot.RemoveAt(rng.Next(data.Snapshot.Count)); break;
                case 2 when data.Draft.Count > 0: data.Draft.RemoveAll(r => rng.Next(8) == 0); data.InvalidateDraftIndex(); data.ClearHistory(); break;
                case 3: data.Columns.Add("c" + rng.Next(100)); break;
                default: data.Snapshot = [.. data.Snapshot.Select(r => r.Copy())]; break;
            }
        }

        // A task checkpoint writes a job on its own; the journal must stay correct around it.
        async Task Checkpoint()
        {
            // A checkpoint updates rows that must already be stored.
            if (scopedPending || W.Jobs.Count == 0 || W.Jobs.All(j => j.Items.Count == 0))
                return;
            var job = W.Jobs.First(j => j.Items.Count > 0);
            var item = job.Items[rng.Next(job.Items.Count)];
            item.State = (ItemState)rng.Next(0, 8);
            item.Message = "checkpoint " + rng.Next(1000);
            await A.CheckpointWriter(W).WriteAsync(new(CheckpointKind.Task, job, item), default);
            Checkpoints++;
        }

        // ---- saves and comparison ---------------------------------------------------------------------------------------

        void SaveB()
        {
            var copy = Workspace.Deserialize(W.Serialize());
            copy.StorageRevision = revisionB;
            B.Save(copy);
            revisionB = copy.StorageRevision;
        }

        async Task RowSave()
        {
            var taken = edits.Take();
            if (!taken.CanSaveRows(W))
            { await A.SaveChangesAsync(W); }
            else
            {
                Assert.True(await A.TrySaveDraftRowsAsync(W, taken.Changes));
                RowSaves++;
            }
            Verify();
        }

        async Task Save()
        {
            edits.Take();
            var roll = rng.Next(20);
            if (roll == 0)
            { await FailingSave(); return; }
            if (roll <= 2)
            {
                // Scoped: the rest stays pending; the next full-scope save must still produce the exact database.
                await A.SaveChangesAsync(W, roll == 1 ? JournalScope.Header : JournalScope.Jobs);
                ScopedSaves++;
                scopedPending = true;
                return;
            }
            await A.SaveChangesAsync(W);
            if (A.LastSave!.WroteNothing)
                EmptySaves++;
            if (A.LastSave.Kind == "Full")
                FullFallbacks++;
            scopedPending = false;
            Verify();
        }

        // An injected failure in the middle of a journal save: the database stays as it was, memory is unchanged, and the
        // save after it writes everything.
        async Task FailingSave()
        {
            var before = StoredState.Dump(dirA.Path, withRevision: true);
            var memory = W.Serialize();
            var revision = W.StorageRevision;
            A.Barrier = point => { if (point == "Journal:BeforeCommit") throw new IOException("injected"); };
            var threw = false;
            try
            { await A.SaveChangesAsync(W); }
            catch (IOException)
            { threw = true; }
            finally
            { A.Barrier = null; }
            if (threw)
            {
                Failures++;
                Assert.Equal(before, StoredState.Dump(dirA.Path, withRevision: true));
                Assert.Equal(memory, W.Serialize());
                Assert.Equal(revision, W.StorageRevision);
            }
            await A.SaveChangesAsync(W);
            if (threw)
                Assert.Equal("Full", A.LastSave!.Kind);
            scopedPending = false;
            Verify();
        }

        void Verify()
        {
            SaveB();
            AssertSame("step " + Steps);
            Compared++;
        }

        void AssertSame(string context) => Assert.True(StoredState.Dump(dirB.Path) == StoredState.Dump(dirA.Path), "journal database differs from the full-save database at " + context + Diff());

        string Diff()
        {
            var a = StoredState.Dump(dirA.Path).Split('\n');
            var b = StoredState.Dump(dirB.Path).Split('\n');
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
                if (a[i] != b[i])
                    return $"\n  line {i}\n  journal: {Clip(a[i])}\n  full:    {Clip(b[i])}";
            return $"\n  lengths {a.Length} / {b.Length}";
        }
        static string Clip(string s) => s.Length > 300 ? s[..300] + "..." : s;
    }

    [Theory]
    [InlineData(101, 700)]
    [InlineData(202, 700)]
    [InlineData(303, 700)]
    [InlineData(404, 700)]
    [InlineData(505, 700)]
    [InlineData(606, 700)]
    [InlineData(20261022, 700)]
    [InlineData(31415926, 700)]
    public async Task A_journal_database_equals_a_full_save_database_after_every_step(int seed, int steps)
    {
        using var run = new Run(seed);
        for (var n = 0; n < steps; n++)
            await run.Step();
        TestContext.Current.SendDiagnosticMessage($"seed {seed}: {run.Steps} steps, {run.Compared} compared, {run.RowSaves} row saves, {run.Checkpoints} checkpoints, {run.ScopedSaves} scoped saves, {run.EmptySaves} empty saves, {run.Failures} injected failures, {run.FullFallbacks} full saves");
        // The mix must really have exercised every route, or the test proves less than it claims.
        Assert.True(run.Compared > steps / 3, $"only {run.Compared} comparisons");
        Assert.True(run.RowSaves > 0 && run.Checkpoints > 0 && run.ScopedSaves > 0 && run.EmptySaves >= 0 && run.Failures > 0, $"row saves {run.RowSaves}, checkpoints {run.Checkpoints}, scoped {run.ScopedSaves}, failures {run.Failures}");
        Assert.True(run.FullFallbacks < run.Compared / 2, "most saves should have been incremental");
    }
}
