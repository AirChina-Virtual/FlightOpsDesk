using System.Collections.Immutable;
using VamSys.App;
using VamSys.Core;

namespace VamSys.UnitTests.Presentation;

public class DataTableViewModelTests
{
    [Fact]
    public void Data_paging_keeps_bounded_pages_anchors_page_size_and_clamps_a_shortened_last_page()
    {
        var model = new DataTableViewModel();
        var ids = Enumerable.Range(0, 1001).Select(_ => Guid.NewGuid()).ToArray();
        model.Apply(new(ids, 0), "", 0, null);
        Assert.Equal(200, model.PageSize);
        Assert.Equal(6, model.PageCount);
        Assert.Equal(200, model.PageIds().Length);

        model.SetPage(6);
        Assert.Equal(ids[1000], model.PageIds().Single());

        model.SetPage(3);
        model.SetPageSize(100);
        Assert.Equal(5, model.Page);
        Assert.Equal(ids[400], model.PageIds()[0]);

        model.SetPageSize(500);
        Assert.Equal(1, model.Page);
        Assert.Equal(500, model.PageIds().Length);

        model.SetPage(3);
        model.Apply(new(ids.Take(400).ToArray(), 0), "", 0, null);
        Assert.Equal(1, model.Page);

        model.Apply(new([], 0), "", 0, null);
        Assert.Equal(1, model.Page);
        Assert.Equal(1, model.PageCount);
        Assert.Empty(model.PageIds());
        Assert.Equal(0, model.First);
    }

    [Fact]
    public void Cross_page_selection_persists_on_paging_and_sorting_and_clears_when_search_or_filter_changes()
    {
        var model = new DataTableViewModel();
        var ids = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToArray();
        model.Apply(new(ids, 0), "", 0, null);
        model.SelectPage([ids[0]]);
        model.SetPage(2);
        model.SelectPage([ids[200]]);
        Assert.True(model.Selected.SetEquals([ids[0], ids[200]]));

        model.SelectPage([]);
        Assert.True(model.Selected.SetEquals([ids[0]]));

        model.Apply(new(ids.Reverse().ToArray(), 0), "", 0, "ID");
        Assert.Contains(ids[0], model.Selected);

        model.SelectAll();
        Assert.Equal(501, model.Selected.Count);
        Assert.True(model.PageIds().Length <= model.PageSize);

        model.Apply(new(ids, 0), "changed", 0, "ID");
        Assert.Empty(model.Selected);
        Assert.Equal(1, model.Page);

        model.SelectPage([ids[0]]);
        model.Apply(new(ids, 0), "changed", 1, "ID");
        Assert.Empty(model.Selected);
    }

    [Fact]
    public void Full_data_queries_preserve_search_deleted_flags_stable_sort_and_cancellation()
    {
        var rows = Enumerable.Range(0, 50000).Select(n => new DataQueryRow(Guid.NewGuid(), ImmutableArray.Create("row " + n, n == 49999 ? "Needle" : "value"), n % 2 == 0 ? "same" : "SAME", n % 10 == 0, n % 17 == 0)).ToArray();
        var all = DataTableViewModel.Query(rows, "", 0, true, CancellationToken.None);
        Assert.True(all.RowIds.SequenceEqual(rows.Select(r => r.LocalId)));

        var found = DataTableViewModel.Query(rows, "needle", 0, false, CancellationToken.None);
        Assert.Equal(rows[^1].LocalId, found.RowIds.Single());

        Assert.Equal(5000, DataTableViewModel.Query(rows, "", 1, false, CancellationToken.None).RowIds.Length);
        Assert.Equal(rows.Count(r => r.Deleted), DataTableViewModel.Query(rows, "", 2, false, CancellationToken.None).RowIds.Length);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => DataTableViewModel.Query(rows, "", 0, true, canceled.Token));
    }

    [Fact]
    public void Cross_page_selection_feeds_only_selected_row_identities_into_batch_changes()
    {
        var rows = Enumerable.Range(0, 501).Select(n => new DataRow { Fields = new Dictionary<string, string> { ["ID"] = n.ToString(), ["Tags"] = "" } }).ToArray();
        var model = new DataTableViewModel();
        model.Apply(DataTableViewModel.Query(rows.Select(r => new DataQueryRow(r.LocalId, [], "", false, false)).ToArray(), "", 0, false, CancellationToken.None), "", 0, null);
        model.Selected.Add(rows[0].LocalId);
        model.SetPage(2);
        model.Selected.Add(rows[200].LocalId);

        var preview = BatchEditPreview.Rule(BatchSelection.Capture(rows.Where(r => model.Selected.Contains(r.LocalId))), new(RuleKind.Set, "Tags", "selected"));

        Assert.Equal(2, preview.Changes.Count);
        Assert.True(preview.Changes.Select(r => r.LocalId).ToHashSet().SetEquals(model.Selected));
    }
}
