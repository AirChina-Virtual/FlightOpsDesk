namespace VamSys.Core;

public sealed class ApiAccessException(string message) : Exception(message);
public sealed class PersistenceException(Exception inner) : Exception(Messages.Define("Text_8BEFA80716"), inner);
public sealed class BatchExecutor(IResourceReader reader, IResourceWriter writer) : IBatchExecutor
{
    static bool EqualFields(DataRow current, DataRow expected) => expected.Fields.Where(p => p.Key != "_delete").All(p => current.Get(p.Key) == p.Value);
    public Task ExecuteAsync(BatchJob job,Func<Task> persist,CancellationToken ct)=>ExecuteAsync(job,persist,ct,null);
    public async Task ExecuteAsync(BatchJob job, Func<Task> persist, CancellationToken ct,Action<ChangeItem?>? progress)
    {
        ChangeItem? active=null;var save = persist;
        persist = async () => { try { await save();progress?.Invoke(active); } catch (Exception e) { throw new PersistenceException(e); } };
        job.SetStatus(JobStatus.Running, Messages.Define("Text_5026A63B58"));
        foreach (var item in job.Items.OrderBy(i => i.Kind == ChangeKind.Delete ? 10 - (int)i.Resource : (int)i.Resource))
        {
            active=item;
            if (ct.IsCancellationRequested) { job.SetStatus(JobStatus.Canceled, Messages.Define("Text_6F312AE253")); break; }
            if (item.State is ItemState.Succeeded or ItemState.Conflict or ItemState.Failed) continue;
            if (item.State == ItemState.Skipped && item.Dependencies.Count == 0) continue;
            if (item.Dependencies.Any(id => job.Items.FirstOrDefault(i => i.Id == id)?.State != ItemState.Succeeded))
            { item.State = ItemState.Skipped; item.SetMessage(Messages.Define("Text_3E85DDB213")); await persist(); continue; }
            if (item.State == ItemState.Skipped) item.State = ItemState.Pending;
            var key = item.RemoteId ?? Schemas.Key(item.Resource, item.After);
            try
            {
                if (item.State is ItemState.Running or ItemState.Unknown)
                {
                    if (string.IsNullOrEmpty(key)) { item.State = ItemState.Unknown; item.SetMessage(Messages.Define("Text_3BFE500D9C")); await persist(); continue; }
                    var found = await reader.FindAsync(item.Resource, key, ct);
                    var recoveryExpected = item.After.Copy();
                    if (item.RemoteId != null) recoveryExpected.Fields[Schemas.All[item.Resource].Key] = item.RemoteId;
                    if (item.Kind == ChangeKind.Delete ? found is null : found != null && EqualFields(found, recoveryExpected))
                    { item.State = ItemState.Succeeded; item.SetMessage(Messages.Define("Text_A65DECDE8F")); }
                    else { item.State = ItemState.Unknown; item.SetMessage(Messages.Define("Text_E0B2732184")); }
                    await persist(); continue;
                }
                if (item.Before != null)
                {
                    var current = await reader.FindAsync(item.Resource, key, ct);
                    if (current is null || !EqualFields(current, item.Before))
                    { item.State = ItemState.Conflict; item.SetMessage(Messages.Define("Text_D876D1E7AF")); await persist(); continue; }
                }
                foreach (var f in Schemas.ReferenceFields(item.Resource))
                {
                    var original = item.After.Get(f);
                    if (!original.Contains("local:", StringComparison.OrdinalIgnoreCase)) continue;
                    var resolved = original.Split(',', StringSplitOptions.TrimEntries).Select(token =>
                    {
                        if (!token.StartsWith("local:", StringComparison.OrdinalIgnoreCase)) return token;
                        var dependency = job.Items.FirstOrDefault(i => ("local:" + i.After.LocalId).Equals(token, StringComparison.OrdinalIgnoreCase));
                        return dependency?.State == ItemState.Succeeded && dependency.RemoteId != null ? dependency.RemoteId : throw MessageErrors.Attach(new ArgumentException(Messages.Define("Text_4B53041E43")), Messages.Define("Text_4B53041E43"));
                    });
                    item.After.Fields[f] = string.Join(',', resolved);
                    item.Fields[f] = new(FieldIntent.Set, item.After.Fields[f]);
                }
                item.State = ItemState.Running; item.SetMessage(Messages.Define("Text_DCC1A22D9E")); await persist();
                item.RemoteId = await writer.WriteAsync(item, ct);
                await persist(); // Persist returned identity before read-back.
                key = item.RemoteId ?? key;
                var verified = await reader.FindAsync(item.Resource, key, ct);
                var expected = item.After.Copy();
                if (item.RemoteId != null) expected.Fields[Schemas.All[item.Resource].Key] = item.RemoteId;
                if (item.Kind == ChangeKind.Delete ? verified is null : verified != null && EqualFields(verified, expected))
                { item.State = ItemState.Succeeded; item.SetMessage(Messages.Define("Text_631F4DBEC2")); }
                else { item.State = ItemState.Unknown; item.SetMessage(Messages.Define("Text_477A5F591D")); }
            }
            catch (ApiAccessException) { item.State = item.State == ItemState.Running ? ItemState.Unknown : ItemState.Pending; item.SetMessage(Messages.Define("Text_CB72CB3DAC")); job.SetStatus(JobStatus.Paused, Messages.Define("Text_5B0F395E13")); await persist(); break; }
            catch (Exception e) when (e is OperationCanceledException or HttpRequestException or TimeoutException)
            {
                bool sent = item.State == ItemState.Running;
                item.State = sent ? ItemState.Unknown : ItemState.Pending;
                item.SetMessage(sent ? Messages.Define("Text_395B73E720") : Messages.Define("Text_08E3371510"));
                active=item;
            if (ct.IsCancellationRequested) { job.SetStatus(JobStatus.Canceled, Messages.Define("Text_6F312AE253")); await persist(); break; }
            }
            catch (Exception e) when (e is not PersistenceException) { item.State = ItemState.Failed; item.SetMessage(e is FormatException or ArgumentException ? Messages.Define("Text_EFDA7294FF") : Messages.Define("Text_A60B4675CF")); }
            await persist();
        }
        active=null;
        if (job.StatusCode == JobStatus.Running) { var complete = job.Items.All(i => i.State == ItemState.Succeeded); job.SetStatus(complete ? JobStatus.Completed : JobStatus.NeedsAttention, Messages.Define(complete ? "Text_140197D868" : "Text_E9008C53CF")); }
        await persist();
    }
}
