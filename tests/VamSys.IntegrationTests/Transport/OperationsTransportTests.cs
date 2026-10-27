using System.Net;
using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;
using static VamSys.IntegrationTests.Transport.TransportFixture;

namespace VamSys.IntegrationTests.Transport;

/// <summary>
/// I01 / I03 / F15 through the real transport: what goes on the wire for reads and writes, which failures are
/// retried (GET only), and which are reported as "not sent".
/// </summary>
public class OperationsTransportTests
{
    static readonly HttpMethod[] WriteMethods = [HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete];
    public static TheoryData<string> Writes() => new() { "POST", "PUT", "PATCH", "DELETE" };

    // ---- the request itself -------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_carries_the_bearer_token_and_accepts_json()
    {
        var f = new TransportFixture();
        f.Server.On(HttpMethod.Get, "fleet", FakeResponse.Ok("""{"data":[]}"""));
        using var doc = await f.Transport.GetAsync(Url("fleet"), TestContext.Current.CancellationToken);
        var request = Assert.Single(f.Server.Requests);
        Assert.Equal("Bearer token-1", request.Header("Authorization"));
        Assert.Equal("application/json", request.Header("Accept"));
        Assert.Null(request.Body);
    }

    [Fact]
    public async Task Write_body_is_serialized_json_with_the_same_headers()
    {
        var f = new TransportFixture();
        f.Server.On(HttpMethod.Post, "fleet", FakeResponse.WithStatus(201, """{"id":5}"""));
        using var doc = await f.Transport.SendAsync(HttpMethod.Post, Url("fleet"), new { name = "A", ids = new[] { 1, 2 } }, TestContext.Current.CancellationToken);
        var request = Assert.Single(f.Server.Requests);
        Assert.Equal("""{"name":"A","ids":[1,2]}""", request.Body);
        Assert.Equal("Bearer token-1", request.Header("Authorization"));
        Assert.StartsWith("application/json", request.Header("Content-Type"), StringComparison.Ordinal);
        Assert.Equal(5, doc.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task No_content_answers_as_json_null()
    {
        var f = new TransportFixture();
        f.Server.On(HttpMethod.Delete, "fleet/4", new FakeResponse(HttpStatusCode.NoContent, null));
        using var doc = await f.Transport.SendAsync(HttpMethod.Delete, Url("fleet/4"), null, TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.ValueKind);
    }

    [Fact]
    public async Task Last_response_describes_the_latest_exchange_without_secrets()
    {
        var f = new TransportFixture();
        f.Server.On(HttpMethod.Post, "fleet", FakeResponse.WithStatus(201, """{"id":5,"access_token":"abc"}"""));
        using var doc = await f.Transport.SendAsync(HttpMethod.Post, Url("fleet"), new { name = "A" }, TestContext.Current.CancellationToken);
        var last = f.Transport.LastResponse!;
        Assert.Equal("POST", last.Method);
        Assert.Equal(201, last.Status);
        Assert.Equal("/api/v3/operations/fleet", last.Path);
        Assert.DoesNotContain("abc", last.ResponseBody, StringComparison.Ordinal);
        Assert.DoesNotContain("token-1", last.ResponseBody ?? "", StringComparison.Ordinal);
    }

    // ---- allowed destinations -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("http://vamsys.io/api/v3/operations/fleet")]
    [InlineData("https://elsewhere.test/api/v3/operations/fleet")]
    [InlineData("https://vamsys.io:8443/api/v3/operations/fleet")]
    [InlineData("https://user:pw@vamsys.io/api/v3/operations/fleet")]
    [InlineData("https://vamsys.io/oauth/token")]
    [InlineData("https://vamsys.io/api/v3/other")]
    public async Task Reads_and_writes_outside_the_operations_origin_are_refused_before_any_request(string url)
    {
        var f = new TransportFixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Transport.GetAsync(new Uri(url), TestContext.Current.CancellationToken));
        foreach (var method in WriteMethods)
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Transport.SendAsync(method, new Uri(url), null, TestContext.Current.CancellationToken));
        Assert.Empty(f.Server.Requests);
        Assert.Equal(0, f.TokenCalls);
    }

    // ---- reads are retried --------------------------------------------------------------------------------------

    [Theory]
    [InlineData(500)] [InlineData(502)] [InlineData(503)] [InlineData(429)]
    public async Task Get_is_retried_after_throttling_or_a_server_error(int status)
    {
        var f = new TransportFixture();
        using var metrics = PerformanceRun.Begin(Guid.NewGuid(), Guid.NewGuid());
        f.Server.Enqueue(HttpMethod.Get, "fleet", FakeResponse.WithStatus(status), FakeResponse.WithStatus(status), FakeResponse.Ok("""{"data":[]}"""));
        using var doc = await f.Transport.GetAsync(Url("fleet"), TestContext.Current.CancellationToken);
        Assert.Equal(3, f.Server.Requests.Count);
        Assert.Equal(2, metrics.Retries);
    }

    [Theory]
    [InlineData(500)] [InlineData(429)]
    public async Task Get_gives_up_after_three_retries_and_reports_the_last_answer(int status)
    {
        var f = new TransportFixture();
        f.Server.On(HttpMethod.Get, "fleet", FakeResponse.WithStatus(status, """{"message":"busy"}"""));
        var error = await Assert.ThrowsAsync<ApiResponseException>(() => f.Transport.GetAsync(Url("fleet"), TestContext.Current.CancellationToken));
        Assert.Equal(4, f.Server.Requests.Count);
        Assert.Equal((HttpStatusCode)status, error.Status);
        Assert.Equal("busy", error.Details!.ServerMessage);
    }

    [Fact]
    public async Task Get_retries_connection_failures_and_client_timeouts()
    {
        var f = new TransportFixture();
        var calls = 0;
        f.Server.On(HttpMethod.Get, "fleet", _ => ++calls switch
        {
            1 => throw new HttpRequestException("reset"),
            2 => throw new TaskCanceledException("timeout", new TimeoutException()),
            _ => FakeResponse.Ok("""{"data":[]}""")
        });
        using var doc = await f.Transport.GetAsync(Url("fleet"), TestContext.Current.CancellationToken);
        Assert.Equal(3, f.Server.Requests.Count);
    }

    [Fact]
    public async Task Get_failing_every_time_throws_the_connection_error_after_four_requests()
    {
        var f = new TransportFixture();
        f.Server.On(HttpMethod.Get, "fleet", _ => throw new HttpRequestException("reset"));
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Transport.GetAsync(Url("fleet"), TestContext.Current.CancellationToken));
        Assert.Equal(4, f.Server.Requests.Count);
    }

    [Fact]
    public async Task Get_cancelled_by_the_caller_is_not_retried()
    {
        var f = new TransportFixture();
        using var cancel = new CancellationTokenSource();
        f.Server.On(HttpMethod.Get, "fleet", _ => { cancel.Cancel(); throw new TaskCanceledException("canceled", null, cancel.Token); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Transport.GetAsync(Url("fleet"), cancel.Token));
        Assert.Single(f.Server.Requests);
    }

    [Theory]
    [InlineData(400)] [InlineData(401)] [InlineData(403)] [InlineData(404)] [InlineData(422)] [InlineData(409)]
    public async Task Get_is_not_retried_after_a_client_error(int status)
    {
        var f = new TransportFixture();
        f.Server.On(HttpMethod.Get, "fleet", FakeResponse.WithStatus(status));
        await Assert.ThrowsAnyAsync<Exception>(() => f.Transport.GetAsync(Url("fleet"), TestContext.Current.CancellationToken));
        Assert.Single(f.Server.Requests);
    }

    [Fact]
    public async Task Get_retries_wait_the_backoff_and_the_retry_after()
    {
        var time = new InstantTime();
        var f = new TransportFixture(time);
        var stamps = new List<DateTimeOffset>();
        f.Server.On(HttpMethod.Get, "fleet", _ =>
        {
            stamps.Add(time.GetUtcNow());
            return stamps.Count switch { 1 => FakeResponse.WithStatus(500), 2 => FakeResponse.WithStatus(500), 3 => Retry(429, 30), _ => FakeResponse.Ok("""{"data":[]}""") };
        });
        using var doc = await f.Transport.GetAsync(Url("fleet"), TestContext.Current.CancellationToken);
        Assert.Equal(4, stamps.Count);
        Assert.True(stamps[1] - stamps[0] >= TimeSpan.FromSeconds(1));
        Assert.True(stamps[2] - stamps[1] >= TimeSpan.FromSeconds(2));
        Assert.True(stamps[3] - stamps[2] >= TimeSpan.FromSeconds(30));
    }

    // ---- writes are sent once -----------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task A_write_answered_with_a_server_error_or_throttle_is_never_resent(string name)
    {
        var method = new HttpMethod(name);
        foreach (var status in new[] { 500, 502, 503, 504, 429 })
        {
            var f = new TransportFixture();
            f.Server.On(method, "fleet/4", FakeResponse.WithStatus(status));
            var error = await Assert.ThrowsAsync<ApiResponseException>(() => f.Transport.SendAsync(method, Url("fleet/4"), method == HttpMethod.Delete ? null : new { a = 1 }, TestContext.Current.CancellationToken));
            Assert.Equal((HttpStatusCode)status, error.Status);
            Assert.Single(f.Server.Requests);
        }
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task A_write_lost_after_sending_is_reported_as_it_happened_and_never_resent(string name)
    {
        var method = new HttpMethod(name);
        var f = new TransportFixture();
        f.Server.On(method, "fleet/4", _ => throw new HttpRequestException("reset"));
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Transport.SendAsync(method, Url("fleet/4"), null, TestContext.Current.CancellationToken));
        Assert.Single(f.Server.Requests);

        var g = new TransportFixture();
        g.Server.On(method, "fleet/4", _ => throw new TaskCanceledException("timeout", new TimeoutException()));
        await Assert.ThrowsAsync<TaskCanceledException>(() => g.Transport.SendAsync(method, Url("fleet/4"), null, TestContext.Current.CancellationToken));
        Assert.Single(g.Server.Requests);
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task A_write_cancelled_after_sending_is_not_a_not_sent_error(string name)
    {
        var method = new HttpMethod(name);
        var f = new TransportFixture();
        using var cancel = new CancellationTokenSource();
        f.Server.On(method, "fleet/4", _ => { cancel.Cancel(); throw new TaskCanceledException("canceled", null, cancel.Token); });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Transport.SendAsync(method, Url("fleet/4"), null, cancel.Token));
        Assert.IsNotType<RequestNotSentException>(error);
        Assert.Single(f.Server.Requests);
    }

    [Theory]
    [InlineData(401)] [InlineData(403)]
    public async Task Authentication_refusal_is_reported_once_and_drops_the_token(int status)
    {
        var f = new TransportFixture();
        f.Server.On(HttpMethod.Post, "fleet", FakeResponse.WithStatus(status, """{"message":"no"}"""));
        var error = await Assert.ThrowsAsync<ApiAccessException>(() => f.Transport.SendAsync(HttpMethod.Post, Url("fleet"), new { a = 1 }, TestContext.Current.CancellationToken));
        Assert.Equal(status, error.Status);
        Assert.Equal(status, error.Details!.Status);
        Assert.Single(f.Server.Requests);
        f.Server.On(HttpMethod.Post, "fleet", FakeResponse.WithStatus(201));
        using var _ = await f.Transport.SendAsync(HttpMethod.Post, Url("fleet"), new { a = 1 }, TestContext.Current.CancellationToken);
        Assert.Equal(2, f.TokenCalls);
        Assert.Equal("Bearer token-2", f.Server.Requests[^1].Header("Authorization"));
    }

    [Fact]
    public async Task Validation_failure_exposes_the_field_errors()
    {
        var f = new TransportFixture();
        f.Server.On(HttpMethod.Put, "fleet/4", FakeResponse.WithStatus(422, """{"message":"bad","errors":{"name":["required"]}}"""));
        var error = await Assert.ThrowsAsync<ApiResponseException>(() => f.Transport.SendAsync(HttpMethod.Put, Url("fleet/4"), new { }, TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, error.Status);
        Assert.Equal(["required"], error.Details!.FieldErrors["name"]);
        Assert.Equal(TransportOutcome.FieldError, ResponseClassifier.Classify(error, true));
    }

    [Fact]
    public async Task Throttled_write_makes_the_next_request_wait_for_retry_after()
    {
        var time = new InstantTime();
        var f = new TransportFixture(time);
        f.Server.Enqueue(HttpMethod.Post, "fleet", Retry(429, 30), FakeResponse.WithStatus(201));
        await Assert.ThrowsAsync<ApiResponseException>(() => f.Transport.SendAsync(HttpMethod.Post, Url("fleet"), new { }, TestContext.Current.CancellationToken));
        var after = time.GetUtcNow();
        using var _ = await f.Transport.SendAsync(HttpMethod.Post, Url("fleet"), new { }, TestContext.Current.CancellationToken);
        Assert.True(time.GetUtcNow() - after >= TimeSpan.FromSeconds(29.9));
        Assert.Equal(2, f.Server.Requests.Count);
    }

    // ---- not sent -----------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task A_write_that_fails_before_it_is_sent_is_wrapped_as_not_sent(string name)
    {
        var method = new HttpMethod(name);
        var f = new TransportFixture();
        f.TokenFailure = () => throw new HttpRequestException("token endpoint down");
        var error = await Assert.ThrowsAsync<RequestNotSentException>(() => f.Transport.SendAsync(method, Url("fleet/4"), null, TestContext.Current.CancellationToken));
        Assert.IsType<HttpRequestException>(error.InnerException);
        Assert.Empty(f.Server.Requests);
        Assert.Equal(TransportOutcome.NotSent, ResponseClassifier.Classify(error, false));
    }

    [Fact]
    public async Task Token_refusal_before_a_write_keeps_the_access_error_inside()
    {
        var f = new TransportFixture();
        f.TokenFailure = () => throw new ApiAccessException("denied");
        var error = await Assert.ThrowsAsync<RequestNotSentException>(() => f.Transport.SendAsync(HttpMethod.Post, Url("fleet"), new { }, TestContext.Current.CancellationToken));
        Assert.IsType<ApiAccessException>(error.InnerException);
        Assert.Empty(f.Server.Requests);
    }

    [Fact]
    public async Task A_write_cancelled_before_it_starts_is_not_sent()
    {
        var f = new TransportFixture();
        var error = await Assert.ThrowsAsync<RequestNotSentException>(() => f.Transport.SendAsync(HttpMethod.Post, Url("fleet"), new { }, new CancellationToken(true)));
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        Assert.Empty(f.Server.Requests);
        Assert.Equal(0, f.TokenCalls);
    }

    [Fact]
    public async Task A_read_that_fails_before_it_is_sent_is_not_wrapped()
    {
        var f = new TransportFixture();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Transport.GetAsync(Url("fleet"), new CancellationToken(true)));
        f.TokenFailure = () => throw new HttpRequestException("token endpoint down");
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Transport.GetAsync(Url("fleet"), TestContext.Current.CancellationToken));
        Assert.Empty(f.Server.Requests);
    }

    [Fact]
    public async Task A_write_cancelled_while_queued_behind_the_rate_limit_is_not_sent()
    {
        var time = new TestTime();
        var f = new TransportFixture(time);
        f.Server.On(HttpMethod.Get, "fleet", FakeResponse.Ok("""{"data":[]}"""));
        f.Server.On(HttpMethod.Post, "fleet", FakeResponse.WithStatus(201));
        using (await f.Transport.GetAsync(Url("fleet"), TestContext.Current.CancellationToken)) { }
        using var cancel = new CancellationTokenSource();
        var queued = f.Transport.SendAsync(HttpMethod.Post, Url("fleet"), new { }, cancel.Token);
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.False(queued.IsCompleted);
        cancel.Cancel();
        var error = await Assert.ThrowsAsync<RequestNotSentException>(() => queued);
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        Assert.Single(f.Server.Requests);
    }

    [Fact]
    public async Task The_before_send_hook_runs_just_before_the_request_and_a_failure_in_it_means_not_sent()
    {
        var f = new TransportFixture();
        f.Server.On(HttpMethod.Post, "fleet", FakeResponse.WithStatus(201));
        DateTimeOffset? hookTime = null;
        var seenAtHook = -1;
        using (await f.Transport.SendAsync(HttpMethod.Post, Url("fleet"), new { }, TestContext.Current.CancellationToken, when => { hookTime = when; seenAtHook = f.Server.Requests.Count; return Task.CompletedTask; })) { }
        Assert.Equal(f.Time.GetUtcNow(), hookTime!.Value);
        Assert.Equal(0, seenAtHook);

        var g = new TransportFixture();
        await Assert.ThrowsAsync<RequestNotSentException>(() => g.Transport.SendAsync(HttpMethod.Post, Url("fleet"), new { }, TestContext.Current.CancellationToken, _ => throw new InvalidOperationException()));
        Assert.Empty(g.Server.Requests);
    }

    [Fact]
    public async Task A_persistence_failure_in_the_before_send_hook_is_not_disguised_as_not_sent()
    {
        var f = new TransportFixture();
        await Assert.ThrowsAsync<PersistenceException>(() => f.Transport.SendAsync(HttpMethod.Post, Url("fleet"), new { }, TestContext.Current.CancellationToken, _ => throw new PersistenceException(new IOException("disk"))));
        Assert.Empty(f.Server.Requests);
    }

    // ---- rate limit across transports ---------------------------------------------------------------------------

    [Fact]
    public async Task Transports_of_one_va_share_the_budget_and_stay_one_second_apart()
    {
        var time = new TestTime();
        var coordinator = new RequestCoordinator(time);
        var a = new TransportFixture(time, coordinator);
        var b = new TransportFixture(time, coordinator);
        var stamps = new List<DateTimeOffset>();
        foreach (var f in new[] { a, b })
            f.Server.On(HttpMethod.Get, "fleet", _ => { lock (stamps) stamps.Add(time.GetUtcNow()); return FakeResponse.Ok("{}"); });
        async Task Run() { for (var i = 0; i < 61; i++) using (await (i % 2 == 0 ? a : b).Transport.GetAsync(Url("fleet"), TestContext.Current.CancellationToken)) { } }
        await time.Drive(Run());
        Assert.Equal(61, stamps.Count);
        Assert.True(stamps[^1] - stamps[0] >= TimeSpan.FromSeconds(60));
        Assert.All(stamps.Zip(stamps.Skip(1)), pair => Assert.True(pair.Second - pair.First >= TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Transports_for_different_vas_do_not_wait_for_each_other()
    {
        var time = new TestTime();
        var coordinator = new RequestCoordinator(time);
        var a = new TransportFixture(time, coordinator, "one");
        var b = new TransportFixture(time, coordinator, "two");
        foreach (var f in new[] { a, b })
            f.Server.On(HttpMethod.Get, "fleet", FakeResponse.Ok("{}"));
        using (await a.Transport.GetAsync(Url("fleet"), TestContext.Current.CancellationToken)) { }
        using (await b.Transport.GetAsync(Url("fleet"), TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)) { }
        Assert.Equal(DateTimeOffset.UnixEpoch, time.GetUtcNow());
    }

    [Fact]
    public async Task A_queued_read_can_be_cancelled_without_sending()
    {
        var time = new TestTime();
        var f = new TransportFixture(time);
        f.Server.On(HttpMethod.Get, "fleet", FakeResponse.Ok("{}"));
        using (await f.Transport.GetAsync(Url("fleet"), TestContext.Current.CancellationToken)) { }
        using var cancel = new CancellationTokenSource();
        var pending = f.Transport.GetAsync(Url("fleet"), cancel.Token);
        await Task.Delay(20, TestContext.Current.CancellationToken);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Single(f.Server.Requests);
    }

    public static TheoryData<string> BudgetModes() => new() { "delta", "date", "budget", "network", "500" };

    [Theory]
    [MemberData(nameof(BudgetModes))]
    public async Task Retry_after_server_budget_and_read_backoff_keep_the_production_waits(string mode)
    {
        var time = new TestTime();
        var f = new TransportFixture(time);
        using var metrics = PerformanceRun.Begin(Guid.NewGuid(), Guid.NewGuid());
        var calls = 0;
        f.Server.On(HttpMethod.Get, "fleet", _ =>
        {
            if (++calls > 1)
                return FakeResponse.Ok("{}");
            switch (mode)
            {
                case "network": throw new HttpRequestException();
                case "500": return FakeResponse.WithStatus(500);
                case "date": return new(HttpStatusCode.TooManyRequests, "{}", new Dictionary<string, string> { ["Retry-After"] = time.GetUtcNow().AddSeconds(5).ToString("R") });
                case "budget": return new(HttpStatusCode.TooManyRequests, "{}", new Dictionary<string, string> { ["Retry-After"] = "5", ["X-RateLimit-Remaining"] = "0" });
                default: return Retry(429, 5);
            }
        });
        var task = f.Transport.GetAsync(Url("fleet"), TestContext.Current.CancellationToken);
        await time.Drive(task);
        (await task).Dispose();
        Assert.Equal(2, calls);
        Assert.Equal(1, metrics.Retries);
        var expected = mode == "budget" ? 60 : mode is "network" or "500" ? 1 : 5;
        Assert.True((time.GetUtcNow() - DateTimeOffset.UnixEpoch).TotalSeconds >= expected);
        Assert.True(metrics.RateWaitMs + metrics.BackoffMs >= 1000);
    }

    // ---- paging through the facade ------------------------------------------------------------------------------

    static async Task<List<int>> Ids(OperationsTransport transport, string path)
    {
        var ids = new List<int>();
        await foreach (var row in transport.ReadPagesAsync(Url(path), TestContext.Current.CancellationToken))
            ids.Add(row.GetProperty("id").GetInt32());
        return ids;
    }

    [Fact]
    public async Task Pages_are_requested_with_one_hundred_rows_and_followed_by_cursor()
    {
        var f = new TransportFixture();
        f.Server.Enqueue(HttpMethod.Get, "fleet",
            FakeResponse.Ok("""{"data":[{"id":1},{"id":2}],"meta":{"next_cursor_url":"https://vamsys.io/api/v3/operations/fleet?cursor=b"}}"""),
            FakeResponse.Ok("""{"data":[{"id":3}],"meta":{"next_cursor_url":null}}"""));
        Assert.Equal([1, 2, 3], await Ids(f.Transport, "fleet"));
        Assert.Equal(["/api/v3/operations/fleet?page%5Bsize%5D=100", "/api/v3/operations/fleet?cursor=b&page%5Bsize%5D=100"], f.Server.Requests.Select(r => r.PathAndQuery));
    }

    [Fact]
    public async Task Page_size_zero_keeps_the_server_default()
    {
        var f = new TransportFixture();
        f.Server.On(HttpMethod.Get, "fleet", FakeResponse.Ok("""{"data":[]}"""));
        var transport = new OperationsTransport(f.Client, OperationsAdapter.BaseUri, "va", f.Tokens, f.Coordinator) { PageSize = 0 };
        Assert.Empty(await Ids(transport, "fleet"));
        Assert.Equal("/api/v3/operations/fleet", f.Server.Requests[0].PathAndQuery);
    }

    [Theory]
    [InlineData(400)] [InlineData(422)]
    public async Task A_rejected_page_size_falls_back_to_the_default_page(int status)
    {
        var f = new TransportFixture();
        f.Server.Enqueue(HttpMethod.Get, "fleet", FakeResponse.WithStatus(status), FakeResponse.Ok("""{"data":[{"id":1}]}"""));
        Assert.Equal([1], await Ids(f.Transport, "fleet"));
        Assert.Equal(["/api/v3/operations/fleet?page%5Bsize%5D=100", "/api/v3/operations/fleet"], f.Server.Requests.Select(r => r.PathAndQuery));
    }

    [Fact]
    public async Task A_next_link_to_another_origin_or_path_stops_the_enumeration()
    {
        foreach (var cursor in new[] { "https://elsewhere.test/api/v3/operations/fleet?c=1", "http://vamsys.io/api/v3/operations/fleet?c=1", "https://vamsys.io/oauth/token" })
        {
            var f = new TransportFixture();
            f.Server.Enqueue(HttpMethod.Get, "fleet", FakeResponse.Ok("{\"data\":[{\"id\":1}],\"meta\":{\"next_cursor_url\":\"" + cursor + "\"}}"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Ids(f.Transport, "fleet"));
            Assert.Single(f.Server.Requests);
        }
    }

    [Fact]
    public async Task A_cursor_loop_is_detected()
    {
        var f = new TransportFixture();
        f.Server.On(HttpMethod.Get, "fleet", FakeResponse.Ok("""{"data":[{"id":1}],"meta":{"next_cursor_url":"https://vamsys.io/api/v3/operations/fleet?cursor=b"}}"""));
        await Assert.ThrowsAsync<FormatException>(() => Ids(f.Transport, "fleet"));
        Assert.Equal(2, f.Server.Requests.Count);
    }

    [Fact]
    public async Task A_page_that_fails_with_an_auth_error_surfaces_it()
    {
        var f = new TransportFixture();
        f.Server.On(HttpMethod.Get, "fleet", FakeResponse.WithStatus(401));
        await Assert.ThrowsAsync<ApiAccessException>(() => Ids(f.Transport, "fleet"));
        Assert.Single(f.Server.Requests);
    }

    // ---- C11: assertions of the C3 scenarios that were only matched by test name ----------------------------------

    [Fact]
    public async Task The_diagnostic_of_a_failed_write_does_not_carry_secrets_from_the_answer()
    {
        var f = new TransportFixture();
        f.Server.On(HttpMethod.Post, "fleet", FakeResponse.WithStatus(500, """{"access_token":"secret-value","error":"failed"}"""));
        var error = await Assert.ThrowsAsync<ApiResponseException>(() => f.Transport.SendAsync(HttpMethod.Post, Url("fleet"), new { name = "x" }, TestContext.Current.CancellationToken));
        Assert.Single(f.Server.Requests);
        Assert.DoesNotContain("secret-value", error.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", error.Details?.ResponseBody ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", f.Transport.LastResponse?.ResponseBody ?? "", StringComparison.Ordinal);
    }
}
