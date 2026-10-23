using System.Net;
using System.Text.Json;
using VamSys.Infrastructure;

namespace VamSys.UnitTests.Transport;

/// <summary>F05 / F15: cursor paging with the safety rules for where a next-page link may point.</summary>
public class PagerTests
{
    static readonly Uri Origin = new("https://api.test/api/v3/operations/");
    static Uri Url(string relative) => new(Origin, relative);

    sealed class Script
    {
        public readonly List<Uri> Requested = [];
        readonly Queue<object> replies = new();
        public Script Page(string json) { replies.Enqueue(json); return this; }
        public Script Fail(HttpStatusCode status) { replies.Enqueue(status); return this; }
        public Task<JsonDocument> Get(Uri uri, CancellationToken ct)
        {
            Requested.Add(uri);
            var reply = replies.Dequeue();
            return reply is HttpStatusCode status ? throw new ApiResponseException(status, "") : Task.FromResult(JsonDocument.Parse((string)reply));
        }
        public Pager Pager => new(Origin, Get);
    }

    static async Task<List<string>> Read(Script script, Uri first, int pageSize = 100)
    {
        var ids = new List<string>();
        await foreach (var row in script.Pager.ReadAsync(first, pageSize, TestContext.Current.CancellationToken))
            ids.Add(row.GetProperty("id").ToString());
        return ids;
    }

    static string Page(string ids, string? next) => "{\"data\":[" + string.Join(",", ids.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(i => "{\"id\":" + i + "}")) + "],\"meta\":{\"next_cursor_url\":" + (next == null ? "null" : "\"" + next + "\"") + "}}";

    [Fact]
    public async Task Follows_the_cursor_to_the_last_page()
    {
        var script = new Script().Page(Page("1,2", Url("fleet?cursor=b").AbsoluteUri)).Page(Page("3", null));
        Assert.Equal(["1", "2", "3"], await Read(script, Url("fleet")));
        Assert.Equal(2, script.Requested.Count);
        Assert.Contains("cursor=b", script.Requested[1].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_page_ends_the_enumeration()
    {
        var script = new Script().Page(Page("", null));
        Assert.Empty(await Read(script, Url("fleet")));
        Assert.Single(script.Requested);
    }

    [Fact]
    public async Task Relative_cursor_urls_resolve_against_the_origin()
    {
        var script = new Script().Page(Page("1", "/api/v3/operations/fleet?cursor=z")).Page(Page("2", null));
        Assert.Equal(["1", "2"], await Read(script, Url("fleet")));
        Assert.Equal("https://api.test/api/v3/operations/fleet?cursor=z&page%5Bsize%5D=100", script.Requested[1].AbsoluteUri);
    }

    [Fact]
    public async Task Next_page_url_is_used_when_there_is_no_cursor_url()
    {
        var script = new Script().Page("{\"data\":[{\"id\":1}],\"meta\":{\"next_page_url\":\"" + Url("fleet?page=2").AbsoluteUri + "\"}}").Page(Page("2", null));
        Assert.Equal(["1", "2"], await Read(script, Url("fleet")));
    }

    [Fact]
    public async Task Missing_meta_or_empty_cursor_means_last_page()
    {
        Assert.Equal(["1"], await Read(new Script().Page("""{"data":[{"id":1}]}"""), Url("fleet")));
        Assert.Equal(["1"], await Read(new Script().Page("""{"data":[{"id":1}],"meta":{"next_cursor_url":""}}"""), Url("fleet")));
    }

    [Fact]
    public async Task Requests_one_hundred_rows_per_page_by_default()
    {
        var script = new Script().Page(Page("1", null));
        await Read(script, Url("fleet?filter%5Bname%5D=x"));
        Assert.Equal("https://api.test/api/v3/operations/fleet?filter%5Bname%5D=x&page%5Bsize%5D=100", script.Requested[0].AbsoluteUri);
    }

    [Fact]
    public async Task A_page_size_already_in_the_query_is_kept_and_zero_keeps_the_server_default()
    {
        var kept = new Script().Page(Page("1", null));
        await Read(kept, Url("fleet?page[size]=5"));
        Assert.Equal(Url("fleet?page[size]=5"), kept.Requested[0]);
        var none = new Script().Page(Page("1", null));
        await Read(none, Url("fleet"), pageSize: 0);
        Assert.Equal(Url("fleet"), none.Requested[0]);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task First_page_rejecting_the_size_falls_back_to_the_server_default_for_all_pages(HttpStatusCode status)
    {
        var script = new Script().Fail(status).Page(Page("1", Url("fleet?cursor=b").AbsoluteUri)).Page(Page("2", null));
        Assert.Equal(["1", "2"], await Read(script, Url("fleet")));
        Assert.Equal(3, script.Requested.Count);
        Assert.Contains("page%5Bsize%5D=100", script.Requested[0].Query, StringComparison.Ordinal);
        Assert.Equal(Url("fleet"), script.Requested[1]);
        Assert.DoesNotContain("page", script.Requested[2].Query.Replace("cursor", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Other_first_page_failures_are_not_retried_without_the_size(HttpStatusCode status)
    {
        var script = new Script().Fail(status);
        var error = await Assert.ThrowsAsync<ApiResponseException>(() => Read(script, Url("fleet")));
        Assert.Equal(status, error.Status);
        Assert.Single(script.Requested);
    }

    [Fact]
    public async Task A_later_page_rejection_is_not_swallowed()
    {
        var script = new Script().Page(Page("1", Url("fleet?cursor=b").AbsoluteUri)).Fail(HttpStatusCode.UnprocessableEntity);
        await Assert.ThrowsAsync<ApiResponseException>(() => Read(script, Url("fleet")));
        Assert.Equal(2, script.Requested.Count);
    }

    [Fact]
    public async Task Fallback_is_skipped_when_the_request_carried_no_page_size()
    {
        var script = new Script().Fail(HttpStatusCode.BadRequest);
        await Assert.ThrowsAsync<ApiResponseException>(() => Read(script, Url("fleet"), pageSize: 0));
        Assert.Single(script.Requested);
    }

    [Fact]
    public async Task A_cursor_pointing_back_at_a_visited_page_is_a_loop()
    {
        var script = new Script().Page(Page("1", Url("fleet?cursor=b").AbsoluteUri)).Page(Page("2", Url("fleet?cursor=b").AbsoluteUri));
        var error = await Assert.ThrowsAsync<FormatException>(() => Read(script, Url("fleet")));
        Assert.Equal(2, script.Requested.Count);
        Assert.False(string.IsNullOrEmpty(error.Message));
    }

    [Fact]
    public async Task A_cursor_pointing_at_the_first_page_is_a_loop()
    {
        var script = new Script().Page(Page("1", Url("fleet").AbsoluteUri));
        await Assert.ThrowsAsync<FormatException>(() => Read(script, Url("fleet")));
        Assert.Single(script.Requested);
    }

    [Theory]
    [InlineData("https://elsewhere.test/api/v3/operations/fleet?cursor=b")]
    [InlineData("https://api.test:8443/api/v3/operations/fleet?cursor=b")]
    [InlineData("http://api.test/api/v3/operations/fleet?cursor=b")]
    [InlineData("https://user:pw@api.test/api/v3/operations/fleet?cursor=b")]
    [InlineData("https://api.test/other/fleet?cursor=b")]
    [InlineData("https://api.test/api/v3/oauth/token")]
    [InlineData("/api/v3/other")]
    public async Task A_next_link_outside_the_operations_origin_is_rejected_without_a_request(string cursor)
    {
        var script = new Script().Page(Page("1", cursor));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(script, Url("fleet")));
        Assert.Single(script.Requested);
    }

    [Theory]
    [InlineData("https://elsewhere.test/api/v3/operations/fleet")]
    [InlineData("http://api.test/api/v3/operations/fleet")]
    [InlineData("https://api.test/api/v3/other")]
    public async Task A_first_url_outside_the_operations_origin_is_rejected_without_a_request(string first)
    {
        var script = new Script();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(script, new Uri(first)));
        Assert.Empty(script.Requested);
    }

    [Theory]
    [InlineData("""{"meta":{}}""")]
    [InlineData("""{"data":{"id":1}}""")]
    [InlineData("""{"data":null}""")]
    public async Task A_page_without_a_data_array_is_a_format_error(string json)
        => await Assert.ThrowsAsync<FormatException>(() => Read(new Script().Page(json), Url("fleet")));

    [Fact]
    public async Task Cancellation_is_passed_to_the_page_request()
    {
        using var cancel = new CancellationTokenSource();
        var seen = default(CancellationToken);
        var pager = new Pager(Origin, (uri, ct) => { seen = ct; return Task.FromResult(JsonDocument.Parse(Page("1", null))); });
        await foreach (var _ in pager.ReadAsync(Url("fleet"), 100, cancel.Token)) { }
        Assert.Equal(cancel.Token, seen);
    }

    [Fact]
    public async Task Rows_stay_valid_after_their_page_is_disposed()
    {
        var script = new Script().Page(Page("7", null));
        var rows = new List<JsonElement>();
        await foreach (var row in script.Pager.ReadAsync(Url("fleet"), 100, TestContext.Current.CancellationToken))
            rows.Add(row);
        Assert.Equal(7, rows[0].GetProperty("id").GetInt32());
    }
}
