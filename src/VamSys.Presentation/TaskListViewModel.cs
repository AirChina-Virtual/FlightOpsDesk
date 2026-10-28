using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VamSys.Core;

namespace VamSys.Presentation;

// The task page: one card per job of the current workspace, newest first. Progress is committed to the job models at
// once and only presented later: changes are collected and shown together when the presentation timer fires
// (50 ms in the window), or at once for an immediate change. Saving never waits for this.
public sealed class TaskListViewModel : ObservableObject
{
    readonly ILocalizationService language;
    readonly IAutosaveTimer presentation;
    readonly Func<BatchJob, TaskJobViewModel> create;
    readonly Dictionary<Guid, TaskJobViewModel> index = [];
    readonly Dictionary<Guid, HashSet<Guid>> pending = [];
    bool canCancel;

    public TaskListViewModel(ILocalizationService language, IAutosaveTimer presentation, Func<BatchJob, TaskJobViewModel> create)
    {
        this.language = language;
        this.presentation = presentation;
        this.create = create;
    }

    public ObservableCollection<TaskJobViewModel> Jobs { get; } = [];
    // The workspace instance the cards were built for; a reloaded or switched workspace starts over.
    public Workspace? Owner { get; private set; }
    public string Header => language.Get("Text_59164F39E2");
    public string CancelLabel => language.Get("Text_14F7E13979");
    public bool CanCancel { get => canCancel; set => SetProperty(ref canCancel, value); }

    public bool TryGet(Guid jobId, out TaskJobViewModel job) => index.TryGetValue(jobId, out job!);

    // Adds cards for jobs the list has not seen, keeping newest first. Returns true when the owner changed.
    public bool Sync(Workspace workspace)
    {
        var reset = !ReferenceEquals(Owner, workspace);
        if (reset)
        {
            Clear();
            Owner = workspace;
        }
        if (index.Count == workspace.Jobs.Count)
            return reset;
        foreach (var job in workspace.Jobs.OrderByDescending(j => j.Created))
        {
            if (index.ContainsKey(job.Id))
                continue;
            var model = create(job);
            index.Add(job.Id, model);
            var at = 0;
            while (at < Jobs.Count && Jobs[at].Job.Created > job.Created)
                at++;
            Jobs.Insert(at, model);
        }
        return reset;
    }

    // A committed change of a job or one of its items. The counters update now; the texts follow on the next flush.
    public void Notify(Workspace workspace, Guid jobId, Guid? itemId, bool immediate)
    {
        Sync(workspace);
        if (!index.TryGetValue(jobId, out var job))
            return;
        job.Update(itemId, false);
        if (!pending.TryGetValue(jobId, out var items))
            pending[jobId] = items = [];
        if (itemId is Guid id)
            items.Add(id);
        if (immediate)
        { Flush(); return; }
        if (!presentation.IsEnabled)
            presentation.Start();
    }

    public void Flush()
    {
        presentation.Stop();
        foreach (var (id, items) in pending)
            if (index.TryGetValue(id, out var job))
            {
                foreach (var item in items)
                    job.Present(item);
                job.RefreshHeader();
            }
        pending.Clear();
    }

    // Command availability follows the window state.
    public void RefreshAvailability()
    {
        foreach (var job in Jobs)
            job.RefreshHeader();
    }

    public void RefreshLanguage()
    {
        Flush();
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(CancelLabel));
        foreach (var job in Jobs)
            job.RefreshLanguage();
    }

    public void Clear()
    {
        Owner = null;
        Jobs.Clear();
        index.Clear();
        pending.Clear();
    }
}
