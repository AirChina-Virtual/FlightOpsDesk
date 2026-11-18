using System.Globalization;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using VamSys.Core;
using VamSys.Infrastructure;

// --write-evidence <report.json> [--dry-run] [--max-requests N] [--only aircraft-internal-remarks]
// Only: null runs the whole G4 / G8 set; WriteEvidence.AircraftInternalRemarks runs that one probe alone.
public sealed record WriteEvidenceOptions(bool DryRun = false, int MaxRequests = WriteEvidence.DefaultMaxRequests, string? Only = null);

// Phase 6 of the improvement plan (6.1 G4, 6.2 G8): real writes against the vAMSYS Operations API, run by hand.
// It creates its own records (named with a run marker), writes values a server might normalise to them and reads
// them back (G4), creates one route of every type other than scheduled and jumpseat, deletes it and reads it again
// (G8), and deletes everything it created. Existing records are only read: the airline parameter, a fleet and the
// two airports of an existing scheduled route. A guard handler refuses, before it leaves the machine, any write
// that is not a POST to a planned collection or a PUT / DELETE of a record this run created, and any request over
// the cap. Nothing is written before the full plan is printed and the confirmation word typed (--dry-run stops
// after the plan). Ctrl+C stops after the request in flight and still cleans up; what cannot be deleted is listed
// in the report and on the console. Credentials are read as by --verify-read-only and never written out.
public static class WriteEvidence
{
    public const string ConfirmWord = "WRITE";
    public const string MarkerPrefix = "FODESK-WE";
    public const int DefaultMaxRequests = 200;
    // --only aircraft-internal-remarks (2026-11-18): the live run read back null for the internal remarks an aircraft
    // was created with. This probe alone finds out whether a PUT keeps them: one fleet, one aircraft in it, three PUTs
    // of internal_remarks only, each read back from the single aircraft and from the fleet's aircraft list.
    public const string AircraftInternalRemarks = "aircraft-internal-remarks";
    public static IReadOnlyList<string> OnlyChoices { get; } = [AircraftInternalRemarks];
    // Exit codes: done and clean; the plan was declined; could not start; stopped early but clean; records left behind.
    public const int Completed = 0, Declined = 1, NotStarted = 2, StoppedClean = 3, LeftRecords = 4;
    // G8: every route type but the two whose soft delete is already proven (IndependentApiEvidence).
    public static IReadOnlyList<string> SoftDeleteTypes { get; } = Schemas.RouteTypeOrder.Where(t => t is not ("scheduled" or "jumpseat")).ToArray();
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static async Task<int> RunAsync(string reportPath, WriteEvidenceOptions options, HttpMessageHandler? inner = null,
        Func<(string Id, string Secret)>? credentials = null, Uri? origin = null, RequestCoordinator? coordinator = null,
        TextReader? input = null, TextWriter? output = null, CancellationToken stop = default, CancellationToken abandonCleanup = default)
    {
        if (options.Only is { } only && !OnlyChoices.Contains(only))
            throw new ArgumentException($"Unknown --only value '{only}'. Known: {string.Join(", ", OnlyChoices)}", nameof(options));
        var (clientId, secret) = (credentials ?? ReadOnlyVerification.Prompt)();
        var run = new Run(reportPath, options, inner, clientId, secret, origin ?? OperationsEndpoints.Origin, coordinator,
            input ?? Console.In, output ?? Console.Out, stop, abandonCleanup);
        try { return await run.ExecuteAsync(); }
        finally { run.Dispose(); }
    }

    sealed class StopRun(string message) : Exception(message);
    sealed class GuardRefused(string message) : InvalidOperationException(message);

    // One record this run plans to create. Path is the record's own address once the POST answered with its ID.
    sealed class Target(string key, string label, string phase)
    {
        public string Key { get; } = key;
        public string Label { get; } = label;
        public string Phase { get; } = phase;
        public string Collection { get; set; } = "";
        public JsonObject Body { get; init; } = [];
        public string? Id { get; set; }
        public string? Path { get; set; }
        // planned, skipped, created, create-failed, create-unknown, deleted, delete-failed, delete-unknown
        public string State { get; set; } = "planned";
        public string? Note { get; set; }
        public JsonObject? Last { get; set; }
        public bool MayExist => State is "created" or "delete-failed" or "delete-unknown" or "create-unknown";
    }

    sealed record Probe(string Record, string Field, string Variant, JsonNode? Value);

    // Answered: the server replied (Status). NoResponse: sent, but no answer (timeout, connection lost): the server
    // may have processed it. NotSent: refused here (cap, guard) or never handed to the client.
    sealed record Exchange(string Kind, int Status, JsonNode? Body, string? Error)
    {
        public bool Ok => Kind == "Answered" && Status is >= 200 and < 300;
    }

    sealed class Run : IDisposable
    {
        readonly string reportPath;
        readonly WriteEvidenceOptions options;
        readonly TextReader input;
        readonly TextWriter log;
        readonly CancellationToken stop, abandonCleanup;
        readonly Uri origin, api, tokenUri;
        readonly HttpClientHandler? owned;
        readonly Guard guard;
        readonly HttpClient client;
        readonly OperationsTransport transport;
        readonly TimeProvider clock;
        readonly JsonObject report = [];
        readonly JsonArray requests = [], createReadback = [], probes = [], softDeletes = [], cleanup = [], remarkSteps = [];
        readonly List<Target> targets = [];
        readonly List<Probe> plannedProbes = [];
        readonly string runId, marker;
        bool Focused => options.Only == AircraftInternalRemarks;

        public Run(string reportPath, WriteEvidenceOptions options, HttpMessageHandler? inner, string clientId, string secret, Uri origin,
            RequestCoordinator? coordinator, TextReader input, TextWriter log, CancellationToken stop, CancellationToken abandonCleanup)
        {
            (this.reportPath, this.options, this.input, this.log, this.stop, this.abandonCleanup, this.origin) = (reportPath, options, input, log, stop, abandonCleanup, origin);
            api = new Uri(origin, "api/v3/operations/");
            tokenUri = new Uri(origin, "oauth/token");
            owned = inner is null ? new HttpClientHandler { AllowAutoRedirect = false } : null;
            guard = new Guard(inner ?? owned!, tokenUri, api) { Limit = options.MaxRequests };
            client = new HttpClient(guard, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(90) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FlightOpsDesk-write-evidence");
            transport = new OperationsTransport(client, api, "operations-shared", ReadOnlyVerification.Tokens(client, tokenUri, clientId, secret, "*"), coordinator) { PageSize = 500 };
            clock = coordinator?.Clock ?? TimeProvider.System;
            runId = clock.GetUtcNow().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            marker = $"{MarkerPrefix} {runId}";
        }

        public void Dispose()
        {
            client.Dispose();
            guard.Dispose();
            owned?.Dispose();
        }

        public async Task<int> ExecuteAsync()
        {
            report["tool"] = "VamSys.QaSeed --write-evidence";
            report["startedUtc"] = clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
            report["origin"] = origin.AbsoluteUri;
            report["runId"] = runId;
            report["marker"] = marker;
            report["dryRun"] = options.DryRun;
            report["maxRequests"] = options.MaxRequests;
            report["only"] = options.Only;

            Discovery found;
            try { found = Focused ? await DiscoverFleetAsync() : await DiscoverAsync(); }
            catch (Exception e) when (e is StopRun or ApiResponseException or ApiAccessException or HttpRequestException or TaskCanceledException
                or InvalidOperationException or FormatException or KeyNotFoundException or JsonException or RequestNotSentException)
            {
                await log.WriteLineAsync("只读发现失败，没有发出任何写入：" + e.Message);
                return await FinishAsync("discovery failed: " + e.Message, NotStarted);
            }
            if (Focused)
                PlanAircraftRemarks(found);
            else
                Plan(found);
            var expected = Focused ? await PrintAircraftRemarksPlanAsync(found) : await PrintPlanAsync(found);
            if (expected > options.MaxRequests)
            {
                await log.WriteLineAsync($"预计请求 {expected} 次，超过上限 {options.MaxRequests}：未写入。用 --max-requests 调高上限后重试。");
                return await FinishAsync("over the request cap", NotStarted);
            }
            if (options.DryRun)
            {
                await log.WriteLineAsync("--dry-run：只打印计划，没有发出任何写入。");
                return await FinishAsync("dry-run", Completed);
            }
            await log.WriteAsync($"输入 {ConfirmWord} 并回车开始写入，其他任何输入取消：");
            var typed = stop.IsCancellationRequested ? null : await input.ReadLineAsync();
            if (stop.IsCancellationRequested || typed?.Trim() != ConfirmWord)
            {
                await log.WriteLineAsync("已取消，没有发出任何写入。");
                return await FinishAsync("declined", Declined);
            }

            // Writes from here on. The cap keeps room for deleting everything that may have been created.
            guard.WritesAllowed = true;
            guard.Limit = options.MaxRequests - 2 * targets.Count;
            string outcome;
            try
            {
                await (Focused ? WriteAircraftRemarksAsync() : WriteAsync());
                outcome = "completed";
            }
            catch (StopRun e) { outcome = "stopped: " + e.Message; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { outcome = "stopped: Ctrl+C"; }
            // Whatever went wrong, what was created must still be cleaned up and reported.
            catch (Exception e) { outcome = $"stopped: {e.GetType().Name}: {e.Message}"; }
            if (outcome != "completed")
                await log.WriteLineAsync("中途停止（" + outcome + "），开始清理本轮新建的记录。");
            guard.Limit = options.MaxRequests;
            try { await CleanupAsync(); }
            catch (Exception e) { outcome += $"; cleanup stopped: {e.GetType().Name}: {e.Message}"; }
            var left = targets.Count(t => t.MayExist);
            return await FinishAsync(outcome, left > 0 ? LeftRecords : outcome == "completed" ? Completed : StoppedClean);
        }

        // ---- read-only discovery -------------------------------------------------------------------------------

        sealed record Discovery(string ParameterId, string Icao, string Iata, long FleetId, string FleetCode, long TemplateRouteId,
            long DepartureId, long ArrivalId, int RoutesScanned, List<string> Numbers, List<string> Earlier);

        async Task<Discovery> DiscoverAsync()
        {
            await log.WriteLineAsync("只读发现：航司参数、机队、全部航线（只发 GET）……");
            var parameters = await ReadAllAsync("parameters");
            var eligible = parameters.Where(p => p.TryGetProperty("approved", out var a) && a.ValueKind == JsonValueKind.True
                && p.TryGetProperty("restricted", out var r) && r.ValueKind == JsonValueKind.False
                && Text(p, "icao_code").Length > 0 && Text(p, "iata_code").Length > 0).ToList();
            var fleets = await ReadAllAsync("fleet");
            var earlier = fleets.Where(f => Text(f, "name").StartsWith(MarkerPrefix, StringComparison.OrdinalIgnoreCase))
                .Select(f => "fleet/" + f.GetProperty("id")).ToList();

            // The template: an existing scheduled route whose fleet, callsign and flight number fit an approved,
            // unrestricted parameter. Its fleet and airports are used read-only by every route this run creates.
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            JsonElement? template = null;
            (string Id, string Icao, string Iata, long Fleet) chosen = ("", "", "", 0);
            var scanned = 0;
            await foreach (var route in transport.ReadPagesAsync(new Uri(api, "routes"), CancellationToken.None))
            {
                scanned++;
                used.Add(Text(route, "callsign"));
                used.Add(Text(route, "flight_number"));
                if (Text(route, "remarks").StartsWith(MarkerPrefix, StringComparison.OrdinalIgnoreCase) || Text(route, "internal_remarks").StartsWith(MarkerPrefix, StringComparison.OrdinalIgnoreCase))
                    earlier.Add("routes/" + route.GetProperty("id"));
                if (template is not null || Text(route, "type") != "scheduled" || Id(route, "departure_id") is null || Id(route, "arrival_id") is null
                    || Id(route, "departure_id") == Id(route, "arrival_id"))
                    continue;
                foreach (var fleet in Ids(route, "fleet_ids"))
                    foreach (var p in eligible)
                        if (template is null && Ids(p, "fleet_ids").Contains(fleet) && fleets.Any(f => Id(f, "id") == fleet)
                            && Text(route, "callsign").StartsWith(Text(p, "icao_code"), StringComparison.OrdinalIgnoreCase)
                            && Text(route, "flight_number").StartsWith(Text(p, "iata_code"), StringComparison.OrdinalIgnoreCase))
                        {
                            template = route.Clone();
                            chosen = (p.GetProperty("id").ToString(), Text(p, "icao_code").ToUpperInvariant(), Text(p, "iata_code").ToUpperInvariant(), fleet);
                        }
            }
            if (template is not { } t)
                throw new StopRun("没有找到可作模板的 scheduled 航线（机队与呼号／班号前缀须属于同一个已批准、未受限的航司参数）");
            var code = Text(fleets.First(f => Id(f, "id") == chosen.Fleet), "code");
            if (code.Length != 4)
                throw new StopRun($"模板机队 {chosen.Fleet} 没有 4 位机型代码");

            // Callsign up to 7 and flight number up to 6 characters (RoutePlanner); numbers no route uses yet, from the top.
            var digits = Math.Min(7 - chosen.Icao.Length, 6 - chosen.Iata.Length);
            if (digits < 2)
                throw new StopRun("航司参数的前缀太长，放不下班号数字");
            var numbers = new List<string>();
            for (var n = (int)Math.Pow(10, digits) - 1; n >= (int)Math.Pow(10, digits - 1) && numbers.Count < 2 + SoftDeleteTypes.Count; n--)
            {
                var text = n.ToString(CultureInfo.InvariantCulture);
                if (!used.Contains(chosen.Icao + text) && !used.Contains(chosen.Iata + text))
                    numbers.Add(text);
            }
            if (numbers.Count < 2 + SoftDeleteTypes.Count)
                throw new StopRun("找不到足够的未使用班号");
            var found = new Discovery(chosen.Id, chosen.Icao, chosen.Iata, chosen.Fleet, code, Id(t, "id")!.Value,
                Id(t, "departure_id")!.Value, Id(t, "arrival_id")!.Value, scanned, numbers, earlier);
            report["discovery"] = new JsonObject
            {
                ["parameterId"] = found.ParameterId,
                ["callsignPrefix"] = found.Icao,
                ["flightNumberPrefix"] = found.Iata,
                ["fleetId"] = found.FleetId,
                ["fleetCode"] = found.FleetCode,
                ["templateRouteId"] = found.TemplateRouteId,
                ["departureAirportId"] = found.DepartureId,
                ["arrivalAirportId"] = found.ArrivalId,
                ["routesScanned"] = scanned,
                ["unusedNumbers"] = new JsonArray(numbers.Select(n => (JsonNode?)n).ToArray()),
                ["earlierRunRecords"] = new JsonArray(earlier.Select(e => (JsonNode?)e).ToArray()),
                ["requests"] = guard.Sent
            };
            return found;
        }

        // --only aircraft-internal-remarks: the parameters and the fleet list only (no routes). The new fleet links an
        // approved, unrestricted parameter and copies the type code of one of that parameter's fleets.
        async Task<Discovery> DiscoverFleetAsync()
        {
            await log.WriteLineAsync("只读发现：航司参数、机队（只发 GET）……");
            var parameters = await ReadAllAsync("parameters");
            var fleets = await ReadAllAsync("fleet");
            var earlier = fleets.Where(f => Text(f, "name").StartsWith(MarkerPrefix, StringComparison.OrdinalIgnoreCase))
                .Select(f => "fleet/" + f.GetProperty("id")).ToList();
            foreach (var p in parameters.Where(p => p.TryGetProperty("approved", out var a) && a.ValueKind == JsonValueKind.True
                && p.TryGetProperty("restricted", out var r) && r.ValueKind == JsonValueKind.False))
                foreach (var fleet in Ids(p, "fleet_ids"))
                    if (fleets.FirstOrDefault(f => Id(f, "id") == fleet) is { ValueKind: JsonValueKind.Object } row && Text(row, "code").Length == 4)
                    {
                        var found = new Discovery(p.GetProperty("id").ToString(), Text(p, "icao_code"), Text(p, "iata_code"), fleet, Text(row, "code"), 0, 0, 0, 0, [], earlier);
                        report["discovery"] = new JsonObject
                        {
                            ["parameterId"] = found.ParameterId,
                            ["fleetId"] = found.FleetId,
                            ["fleetCode"] = found.FleetCode,
                            ["earlierRunRecords"] = new JsonArray(earlier.Select(e => (JsonNode?)e).ToArray()),
                            ["requests"] = guard.Sent
                        };
                        return found;
                    }
            throw new StopRun("没有找到已批准、未受限且关联了带 4 位机型代码机队的航司参数");
        }

        async Task<List<JsonElement>> ReadAllAsync(string resource)
        {
            var rows = new List<JsonElement>();
            await foreach (var row in transport.ReadPagesAsync(new Uri(api, resource), CancellationToken.None))
                rows.Add(row.Clone());
            return rows;
        }

        // ---- the plan ------------------------------------------------------------------------------------------

        // The fleet (hidden in Phoenix, linked to the parameter found) and an aircraft in it, as G4 creates them.
        void AddFleetAndAircraft(Discovery d)
        {
            var stamp = runId[9..15];
            targets.Add(new("g4-fleet", "机队", "G4")
            {
                Collection = "fleet",
                Body = new()
                {
                    ["name"] = $"{marker} fleet",
                    ["code"] = d.FleetCode,
                    ["type"] = "pax-cargo",
                    ["max_pax"] = 100,
                    ["max_cargo"] = 10000,
                    ["hide_in_phoenix"] = true,
                    ["parameter_ids"] = new JsonArray(d.ParameterId)
                }
            });
            targets.Add(new("g4-aircraft", "飞机", "G4")
            {
                Collection = "fleet/{g4-fleet}/aircraft",
                Body = new()
                {
                    ["name"] = $"{marker} aircraft",
                    ["registration"] = "FDW" + stamp,
                    ["passengers"] = 100,
                    ["cargo"] = 1000,
                    ["internal_remarks"] = marker
                }
            });
        }

        void Plan(Discovery d)
        {
            var today = clock.GetUtcNow().UtcDateTime.Date;
            // Hidden routes that start in 60 days and run for 30: no pilot can book them while they exist.
            var start = today.AddDays(60);
            string Iso(DateTime day, string time = "00:00:00", string offset = "+00:00") => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T" + time + offset;
            JsonObject RouteBody(string type, string number) => new()
            {
                ["type"] = type,
                ["departure_id"] = d.DepartureId,
                ["arrival_id"] = d.ArrivalId,
                ["callsign"] = d.Icao + number,
                ["flight_number"] = d.Iata + number,
                ["fleet_ids"] = new JsonArray(d.FleetId),
                ["departure_time"] = "08:00:00",
                ["arrival_time"] = "10:00:00",
                ["start_date"] = Iso(start),
                ["end_date"] = Iso(start.AddDays(30)),
                ["service_days"] = new JsonArray("monday"),
                ["hidden"] = true,
                ["remarks"] = $"{marker} {type}",
                ["internal_remarks"] = $"{marker} {type}"
            };
            AddFleetAndAircraft(d);
            targets.Add(new("g4-routing", "航路", "G4")
            {
                Collection = "routings",
                Body = new()
                {
                    ["departure_airport_id"] = d.DepartureId,
                    ["arrival_airport_id"] = d.ArrivalId,
                    ["route"] = "DCT",
                    ["remarks"] = marker,
                    ["internal_remarks"] = marker,
                    ["tag"] = new JsonArray("fodesk-we"),
                    ["days_of_operation"] = new JsonArray("monday")
                }
            });
            targets.Add(new("g4-route", "scheduled 航线", "G4") { Collection = "routes", Body = RouteBody("scheduled", d.Numbers[0]) });
            for (var i = 0; i < SoftDeleteTypes.Count; i++)
                targets.Add(new("g8-" + SoftDeleteTypes[i], SoftDeleteTypes[i] + " 航线", "G8") { Collection = "routes", Body = RouteBody(SoftDeleteTypes[i], d.Numbers[2 + i]) });

            // G4 probes: one value per PUT, each different from what the record holds, so that "not applied" and
            // "normalised" can be told apart. The record's marker stays in a field no probe touches (internal remarks
            // for routings, routes and aircraft) or inside every probed value (fleet name).
            static JsonNode Raw(string json) => JsonNode.Parse(json)!;
            void P(string record, string field, string variant, JsonNode? value) => plannedProbes.Add(new(record, field, variant, value));
            var reg = "FDW" + runId[9..15];
            P("g4-fleet", "name", "trim-padding", $"  {marker} fleet t1  ");
            P("g4-fleet", "name", "lowercase", $"{marker} fleet c2".ToLowerInvariant());
            P("g4-fleet", "name", "inner-whitespace", $"{marker}  fleet   w3");
            P("g4-fleet", "code", "lowercase", d.FleetCode.ToLowerInvariant());
            P("g4-fleet", "max_pax", "number-as-string", "120");
            P("g4-fleet", "max_pax", "decimal-number", Raw("130.0"));
            P("g4-fleet", "max_cargo", "number-as-string", "12000");
            P("g4-aircraft", "name", "trim-padding", $"  {marker} aircraft t1  ");
            P("g4-aircraft", "registration", "lowercase", (reg + "B").ToLowerInvariant());
            P("g4-aircraft", "registration", "trim-padding", $"  {reg}C  ");
            P("g4-aircraft", "selcal", "lowercase", "ab-cd");
            P("g4-aircraft", "hexcode", "lowercase", "abc123");
            P("g4-aircraft", "passengers", "number-as-string", "110");
            P("g4-aircraft", "cargo", "number-as-string", "1100");
            P("g4-aircraft", "fin_number", "empty-string", "");
            P("g4-routing", "route", "trim-padding", "  DCT ABCDE DCT  ");
            P("g4-routing", "route", "lowercase", "dct fghij dct");
            P("g4-routing", "remarks", "trim-padding", $"  {marker} r1  ");
            P("g4-routing", "remarks", "empty-string", "");
            P("g4-routing", "tag", "array-order", new JsonArray("b-tag", "a-tag"));
            P("g4-routing", "tag", "array-duplicates", new JsonArray("d-tag", "d-tag"));
            P("g4-routing", "days_of_operation", "array-order", new JsonArray("tuesday", "monday"));
            P("g4-routing", "days_of_operation", "case", new JsonArray("Wednesday"));
            P("g4-route", "callsign", "lowercase", (d.Icao + d.Numbers[1]).ToLowerInvariant());
            P("g4-route", "flight_number", "lowercase", (d.Iata + d.Numbers[1]).ToLowerInvariant());
            P("g4-route", "departure_time", "time-without-seconds", "08:05");
            P("g4-route", "arrival_time", "time-without-leading-zero", "9:30:00");
            P("g4-route", "start_date", "date-only", start.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            P("g4-route", "start_date", "date-utc-z", Iso(start.AddDays(2), offset: "Z"));
            P("g4-route", "start_date", "date-other-offset", Iso(start.AddDays(3), "08:00:00", "+08:00"));
            P("g4-route", "end_date", "date-fraction", Iso(start.AddDays(29), "00:00:00.500000"));
            P("g4-route", "remarks", "trim-padding", $"  {marker} r1  ");
            P("g4-route", "remarks", "empty-string", "");
            P("g4-route", "cost_index", "lowercase", "auto");
            P("g4-route", "cost_index", "leading-zero", "050");
            P("g4-route", "cost_index", "number-for-string", 75);
            P("g4-route", "altitude", "number-as-string", "35000");
            P("g4-route", "service_days", "array-order", new JsonArray("tuesday", "monday"));
            P("g4-route", "service_days", "case", new JsonArray("Wednesday"));
            P("g4-route", "fleet_ids", "string-element", new JsonArray(d.FleetId.ToString(CultureInfo.InvariantCulture)));
            P("g4-route", "tag", "array-order", new JsonArray("b-tag", "a-tag"));
        }

        async Task<int> PrintPlanAsync(Discovery d)
        {
            var g4 = targets.Count(t => t.Phase == "G4");
            var g8 = targets.Count(t => t.Phase == "G8");
            var reads = guard.Sent;
            var writes = new JsonObject
            {
                ["POST"] = targets.Count,
                ["PUT"] = plannedProbes.Count,
                ["DELETE"] = targets.Count,
                ["GET"] = targets.Count + plannedProbes.Count + g8 + g4
            };
            var total = reads + targets.Count * 2 + plannedProbes.Count * 2 + g8 * 2 + g4 * 2;
            var after = total - reads;
            var plan = new JsonObject
            {
                ["records"] = new JsonArray(targets.Select(t => (JsonNode?)new JsonObject
                    { ["key"] = t.Key, ["label"] = t.Label, ["phase"] = t.Phase, ["post"] = t.Collection, ["body"] = t.Body.DeepClone() }).ToArray()),
                ["probes"] = new JsonArray(plannedProbes.Select(p => (JsonNode?)new JsonObject
                    { ["record"] = p.Record, ["field"] = p.Field, ["variant"] = p.Variant, ["value"] = p.Value?.DeepClone() }).ToArray()),
                ["softDeleteTypes"] = new JsonArray(SoftDeleteTypes.Select(t => (JsonNode?)t).ToArray()),
                ["requestsAfterConfirmation"] = writes,
                ["expectedRequests"] = total
            };
            report["plan"] = plan;

            var w = new System.Text.StringBuilder();
            w.AppendLine();
            w.AppendLine($"===== 写入取证计划（运行标记：{marker}）=====");
            w.AppendLine($"服务器：{origin.AbsoluteUri}");
            w.AppendLine($"只读引用（不修改）：航司参数 #{d.ParameterId}（前缀 {d.Icao}／{d.Iata}）、机队 #{d.FleetId}（{d.FleetCode}）、机场 #{d.DepartureId} → #{d.ArrivalId}（取自已有航线 #{d.TemplateRouteId}）");
            w.AppendLine($"已读航线 {d.RoutesScanned} 条，用于挑选未使用的班号。");
            if (d.Earlier.Count > 0)
                w.AppendLine($"注意：服务器上已有带 {MarkerPrefix} 标记的记录（以前的运行留下，本次不会处理）：{string.Join(", ", d.Earlier)}");
            w.AppendLine("将新建以下记录（全部带运行标记，结束时删除；航线隐藏且 60 天后才生效，机队对 Phoenix 隐藏）：");
            var i = 1;
            foreach (var t in targets)
                w.AppendLine($"  {i++,2}. [{t.Phase}] {t.Label,-16} POST {t.Collection}  {t.Body.ToJsonString(Compact)}");
            w.AppendLine($"G4 规范化探测：{plannedProbes.Count} 次 PUT，每次后 GET 回读（"
                + string.Join("，", plannedProbes.GroupBy(p => p.Record).Select(g => $"{targets.First(t => t.Key == g.Key).Label} {g.Count()}")) + "）；每条新建后也 GET 回读一次");
            foreach (var p in plannedProbes)
                w.AppendLine($"      {p.Record,-12} {p.Field,-18} {p.Variant,-26} {p.Value?.ToJsonString(Compact)}");
            w.AppendLine($"G8 软删除：{string.Join("、", SoftDeleteTypes)} 各 POST 1、GET 1、DELETE 1、GET 1");
            w.AppendLine($"清理：G4 的 {g4} 条记录各 DELETE 1、GET 1（G8 的删除未成功时也在这里重试）");
            w.AppendLine($"预计请求：只读发现已用 {reads} 次；确认后 POST {targets.Count}、PUT {plannedProbes.Count}、DELETE {targets.Count}、GET {targets.Count + plannedProbes.Count + g8 + g4}，"
                + $"共约 {after} 次；全程约 {total} 次，按每秒最多 1 次约 {Math.Ceiling(after * 1.2 / 60)} 分钟。上限 {options.MaxRequests} 次（其中 {2 * targets.Count} 次留给清理）。");
            w.AppendLine("Ctrl+C：当前请求完成后停止并清理；再按一次放弃清理。");
            await log.WriteLineAsync(w.ToString());
            return total;
        }

        // ---- --only aircraft-internal-remarks ------------------------------------------------------------------

        const string Remarks = "internal_remarks";

        void PlanAircraftRemarks(Discovery d)
        {
            AddFleetAndAircraft(d);
            plannedProbes.Add(new("g4-aircraft", Remarks, "value-1", $"{marker} ir1"));
            plannedProbes.Add(new("g4-aircraft", Remarks, "value-2", $"{marker} ir2"));
            plannedProbes.Add(new("g4-aircraft", Remarks, "empty-string", ""));
        }

        // Each step reads the aircraft twice: alone (as after any write) and in its fleet's list.
        int RemarkReads => 1 + plannedProbes.Count;

        async Task<int> PrintAircraftRemarksPlanAsync(Discovery d)
        {
            var reads = guard.Sent;
            var after = targets.Count * 2 + plannedProbes.Count * 2 + RemarkReads + targets.Count * 2;
            var total = reads + after;
            report["plan"] = new JsonObject
            {
                ["records"] = new JsonArray(targets.Select(t => (JsonNode?)new JsonObject
                    { ["key"] = t.Key, ["label"] = t.Label, ["post"] = t.Collection, ["body"] = t.Body.DeepClone() }).ToArray()),
                ["probes"] = new JsonArray(plannedProbes.Select(p => (JsonNode?)new JsonObject
                    { ["record"] = p.Record, ["field"] = p.Field, ["variant"] = p.Variant, ["value"] = p.Value?.DeepClone() }).ToArray()),
                ["requestsAfterConfirmation"] = new JsonObject
                {
                    ["POST"] = targets.Count,
                    ["PUT"] = plannedProbes.Count,
                    ["DELETE"] = targets.Count,
                    ["GET"] = targets.Count + plannedProbes.Count + RemarkReads + targets.Count
                },
                ["expectedRequests"] = total
            };
            var w = new System.Text.StringBuilder();
            w.AppendLine();
            w.AppendLine($"===== 写入取证计划：只测飞机 internal_remarks（运行标记：{marker}）=====");
            w.AppendLine($"服务器：{origin.AbsoluteUri}");
            w.AppendLine($"只读引用（不修改）：航司参数 #{d.ParameterId}、机队 #{d.FleetId} 的机型代码 {d.FleetCode}。不读、不写航线和航路。");
            if (d.Earlier.Count > 0)
                w.AppendLine($"注意：服务器上已有带 {MarkerPrefix} 标记的机队（以前的运行留下，本次不会处理）：{string.Join(", ", d.Earlier)}");
            w.AppendLine("将新建以下记录（带运行标记，结束时先删飞机、后删机队；机队对 Phoenix 隐藏）：");
            var i = 1;
            foreach (var t in targets)
                w.AppendLine($"  {i++,2}. {t.Label,-6} POST {t.Collection}  {t.Body.ToJsonString(Compact)}");
            w.AppendLine($"飞机新建后 GET 回读；再依次 PUT 只含 {Remarks} 的请求体，每次后 GET 回读：");
            foreach (var p in plannedProbes)
                w.AppendLine($"      {p.Variant,-14} {p.Value?.ToJsonString(Compact)}");
            w.AppendLine($"新建后和每次 PUT 后另读一次机队下的飞机列表（共 {RemarkReads} 次），区分字段不存在与 null。");
            w.AppendLine($"预计请求：只读发现已用 {reads} 次；确认后 POST {targets.Count}、PUT {plannedProbes.Count}、DELETE {targets.Count}、GET {targets.Count + plannedProbes.Count + RemarkReads + targets.Count}，"
                + $"共约 {after} 次；全程约 {total} 次，约 {Math.Ceiling(after * 1.2 / 60)} 分钟。上限 {options.MaxRequests} 次。");
            w.AppendLine("Ctrl+C：当前请求完成后停止并清理；再按一次放弃清理。");
            await log.WriteLineAsync(w.ToString());
            return total;
        }

        async Task WriteAircraftRemarksAsync()
        {
            var fleet = targets.First(t => t.Key == "g4-fleet");
            var aircraft = targets.First(t => t.Key == "g4-aircraft");
            if (!await CreateAsync(fleet))
            { aircraft.State = "skipped"; return; }
            aircraft.Collection = $"fleet/{fleet.Id}/aircraft";
            if (!await CreateAsync(aircraft))
                return;
            await RemarkStepAsync(aircraft, "create (POST)", aircraft.Body[Remarks], null, aircraft.Last);
            foreach (var probe in plannedProbes)
            {
                var result = await ProbeAsync(aircraft, probe);
                var read = result.ContainsKey("readback");
                await RemarkStepAsync(aircraft, "PUT " + probe.Variant, probe.Value, (int?)result["putStatus"], read ? aircraft.Last : null);
            }
        }

        // One step of the probe: what was sent, what the single GET held, and what the fleet's aircraft list holds.
        async Task RemarkStepAsync(Target aircraft, string step, JsonNode? sent, int? putStatus, JsonObject? single)
        {
            var entry = new JsonObject { ["step"] = step, ["sent"] = sent?.DeepClone(), ["writeStatus"] = putStatus };
            remarkSteps.Add(entry);
            entry["single"] = FieldState(single, read: single != null);
            var list = await SendAsync(HttpMethod.Get, aircraft.Collection + "?page%5Bsize%5D=100", null, "aircraft list " + aircraft.Key);
            Require(list);
            var row = list.Ok && list.Body?["data"] is JsonArray rows
                ? rows.OfType<JsonObject>().FirstOrDefault(r => r["id"]?.ToString() == aircraft.Id) : null;
            entry["list"] = FieldState(row, read: list.Ok);
            entry["list"]!["status"] = list.Status;
            entry["list"]!["found"] = row != null;
            entry["saved"] = single != null && single.TryGetPropertyValue(Remarks, out var value) && JsonNode.DeepEquals(value, sent);
        }

        static JsonObject FieldState(JsonObject? row, bool read)
        {
            if (!read || row is null)
                return new() { ["read"] = read, ["present"] = false };
            return row.TryGetPropertyValue(Remarks, out var value)
                ? new() { ["read"] = true, ["present"] = true, ["value"] = value?.DeepClone() }
                : new() { ["read"] = true, ["present"] = false };
        }

        static string Shown(JsonNode? state) => state is null || !(bool)state["read"]! ? "未读到"
            : !(bool)state["present"]! ? "字段不存在" : state["value"] is null ? "null" : state["value"]!.ToJsonString(Compact);

        // The verdict: a PUT of a non-empty value read back unchanged means PUT keeps the field.
        JsonObject RemarksConclusion()
        {
            var puts = remarkSteps.OfType<JsonObject>().Where(s => ((string)s["step"]!).StartsWith("PUT", StringComparison.Ordinal)
                && s["sent"] is JsonValue v && v.GetValue<string>().Length > 0).ToList();
            var answered = puts.Where(s => s["writeStatus"] is JsonValue st && (int)st is >= 200 and < 300 && (bool)s["single"]!["read"]!).ToList();
            var create = remarkSteps.OfType<JsonObject>().FirstOrDefault(s => (string?)s["step"] == "create (POST)");
            var putSaved = answered.Count == 0 ? (bool?)null : answered.All(s => (bool)s["saved"]!) ? true : answered.Any(s => (bool)s["saved"]!) ? null : false;
            return new()
            {
                ["postSaved"] = create is null ? null : (bool)create["saved"]!,
                ["putSaved"] = putSaved,
                ["conclusion"] = putSaved switch
                {
                    true => "PUT keeps internal_remarks",
                    false => "PUT does not keep internal_remarks",
                    _ => answered.Count == 0 ? "undetermined: no accepted PUT was read back" : "undetermined: PUTs disagree"
                }
            };
        }

        // ---- writes --------------------------------------------------------------------------------------------

        async Task WriteAsync()
        {
            Target T(string key) => targets.First(t => t.Key == key);
            var fleet = T("g4-fleet");
            var aircraft = T("g4-aircraft");
            if (await CreateAsync(fleet))
            {
                aircraft.Collection = $"fleet/{fleet.Id}/aircraft";
                await CreateAsync(aircraft);
            }
            else
                aircraft.State = "skipped";
            await CreateAsync(T("g4-routing"));
            await CreateAsync(T("g4-route"));
            foreach (var probe in plannedProbes)
                await ProbeAsync(T(probe.Record), probe);
            foreach (var target in targets.Where(t => t.Phase == "G8"))
                if (await CreateAsync(target))
                    await SoftDeleteAsync(target);
        }

        async Task<bool> CreateAsync(Target t)
        {
            guard.Collections.Add(t.Collection);
            await log.WriteLineAsync($"[{t.Phase}] 新建{t.Label}……");
            var post = await SendAsync(HttpMethod.Post, t.Collection, t.Body, t.Phase + " create " + t.Key);
            if (post.Kind != "Answered" || post.Status >= 500)
            {
                // Sent without a usable answer: the record may exist and its ID is unknown.
                t.State = post.Kind == "NotSent" ? "skipped" : "create-unknown";
                t.Note = post.Error;
                throw new StopRun($"新建{t.Label}没有得到可用的应答（{Describe(post)}）");
            }
            if (post.Status is 401 or 403)
            { t.State = "skipped"; throw new StopRun($"HTTP {post.Status}：凭据或权限不足"); }
            if (!post.Ok)
            {
                t.State = "create-failed";
                t.Note = $"HTTP {post.Status}: {post.Error}";
                await log.WriteLineAsync($"    新建{t.Label}被拒绝：HTTP {post.Status}");
                return false;
            }
            var id = Row(post.Body)?["id"]?.ToString();
            if (string.IsNullOrEmpty(id) || !long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            { t.State = "create-unknown"; t.Note = "the POST answer has no id"; throw new StopRun($"新建{t.Label}的应答里没有 ID"); }
            t.Id = id;
            t.Path = $"{t.Collection}/{id}";
            t.State = "created";
            guard.Owned.Add(t.Path);
            await log.WriteLineAsync($"    已新建 {t.Path}");

            var get = await SendAsync(HttpMethod.Get, t.Path, null, t.Phase + " readback " + t.Key);
            Require(get);
            if (!get.Ok)
            { createReadback.Add(new JsonObject { ["record"] = t.Key, ["id"] = id, ["readStatus"] = get.Status }); return true; }
            t.Last = Row(get.Body);
            foreach (var (field, sent) in t.Body)
            {
                var present = t.Last!.TryGetPropertyValue(field, out var actual);
                createReadback.Add(new JsonObject
                {
                    ["record"] = t.Key,
                    ["id"] = id,
                    ["field"] = field,
                    ["sent"] = sent?.DeepClone(),
                    ["readback"] = present ? actual?.DeepClone() : (JsonNode?)"(missing)",
                    ["equal"] = present && JsonNode.DeepEquals(sent, actual),
                    ["kind"] = Normalisation.Classify(sent, actual, present)
                });
            }
            return true;
        }

        async Task<JsonObject> ProbeAsync(Target t, Probe p)
        {
            var result = new JsonObject { ["record"] = t.Key, ["id"] = t.Id, ["field"] = p.Field, ["variant"] = p.Variant, ["sent"] = p.Value?.DeepClone() };
            probes.Add(result);
            if (t.State != "created")
            { result["kind"] = "skipped"; result["note"] = "record not created"; return result; }
            var put = await SendAsync(HttpMethod.Put, t.Path!, new JsonObject { [p.Field] = p.Value?.DeepClone() }, "G4 probe " + t.Key);
            Require(put);
            result["putStatus"] = put.Status;
            if (!put.Ok)
                result["putError"] = put.Error;
            if (!put.Ok && put.Status < 500)
            { result["kind"] = "rejected"; return result; }
            var get = await SendAsync(HttpMethod.Get, t.Path!, null, "G4 readback " + t.Key);
            Require(get);
            result["readStatus"] = get.Status;
            if (!get.Ok)
            { result["kind"] = "read-failed"; return result; }
            var row = Row(get.Body)!;
            var present = row.TryGetPropertyValue(p.Field, out var actual);
            // Without a read-back after the create (it was not 2xx), nothing is known of the value before.
            JsonNode? before = null;
            var hadBefore = t.Last is { } last && last.TryGetPropertyValue(p.Field, out before);
            var same = present && hadBefore && JsonNode.DeepEquals(before, actual);
            var kind = Normalisation.Classify(p.Value, actual, present);
            result["readback"] = present ? actual?.DeepClone() : (JsonNode?)"(missing)";
            result["before"] = hadBefore ? before?.DeepClone() : (JsonNode?)"(missing)";
            result["equal"] = present && JsonNode.DeepEquals(p.Value, actual);
            result["sameAsBefore"] = same;
            result["kind"] = kind == "other" && same ? "not-applied" : kind;
            t.Last = row;
            return result;
        }

        async Task SoftDeleteAsync(Target t)
        {
            var result = new JsonObject { ["type"] = (string?)t.Body["type"], ["id"] = t.Id };
            softDeletes.Add(result);
            await log.WriteLineAsync($"[G8] 删除 {t.Path} 并回读……");
            var delete = await SendAsync(HttpMethod.Delete, t.Path!, null, "G8 delete " + t.Key);
            result["deleteStatus"] = delete.Kind == "Answered" ? (JsonNode?)delete.Status : null;
            if (delete.Kind != "Answered")
            { t.State = "delete-unknown"; result["deleteOutcome"] = delete.Kind; }
            Require(delete);
            if (delete.Ok)
                t.State = "deleted";
            else
                result["deleteError"] = delete.Error;
            var get = await SendAsync(HttpMethod.Get, t.Path!, null, "G8 read after delete " + t.Key);
            Require(get);
            result["getAfterDeleteStatus"] = get.Status;
            result["readableAfterDelete"] = get.Ok;
            if (get.Ok)
                result["getAfterDeleteBody"] = Row(get.Body)?.DeepClone();
            await log.WriteLineAsync($"    DELETE {Status(delete)}，随后 GET {get.Status}");
        }

        // ---- cleanup -------------------------------------------------------------------------------------------

        async Task CleanupAsync()
        {
            var pending = targets.Where(t => t.MayExist && t.Path != null).Reverse().ToList();
            if (pending.Count > 0)
                await log.WriteLineAsync($"清理：删除本轮新建的 {pending.Count} 条记录……");
            foreach (var t in pending)
            {
                var entry = new JsonObject { ["record"] = t.Key, ["id"] = t.Id, ["path"] = t.Path };
                cleanup.Add(entry);
                if (abandonCleanup.IsCancellationRequested)
                { entry["result"] = "abandoned (Ctrl+C)"; t.Note = "cleanup abandoned"; continue; }
                Exchange delete;
                try { delete = await SendAsync(HttpMethod.Delete, t.Path!, null, "cleanup " + t.Key, cleanup: true); }
                catch (Exception e) { delete = new("NotSent", 0, null, $"{e.GetType().Name}: {e.Message}"); }
                entry["deleteStatus"] = delete.Kind == "Answered" ? (JsonNode?)delete.Status : null;
                if (delete.Kind == "Answered" && (delete.Ok || delete.Status == 404))
                {
                    t.State = "deleted";
                    entry["result"] = delete.Ok ? "deleted" : "already gone (404)";
                    try
                    {
                        var get = await SendAsync(HttpMethod.Get, t.Path!, null, "cleanup read " + t.Key, cleanup: true);
                        entry["getAfterDeleteStatus"] = get.Kind == "Answered" ? (JsonNode?)get.Status : null;
                    }
                    catch (Exception e) { entry["getAfterDeleteError"] = e.GetType().Name; }
                }
                else
                {
                    t.State = delete.Kind == "Answered" ? "delete-failed" : "delete-unknown";
                    t.Note = Describe(delete);
                    entry["result"] = "failed: " + Describe(delete);
                }
            }
        }

        // ---- requests ------------------------------------------------------------------------------------------

        async Task<Exchange> SendAsync(HttpMethod method, string path, JsonObject? body, string phase, bool cleanup = false)
        {
            // Ctrl+C takes effect between requests: a write already on the wire is never cut off.
            if (!cleanup)
                stop.ThrowIfCancellationRequested();
            for (var attempt = 0; ; attempt++)
            {
                var entry = new JsonObject
                {
                    ["n"] = requests.Count + 1,
                    ["phase"] = phase,
                    ["method"] = method.Method,
                    ["path"] = path,
                    ["at"] = clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)
                };
                if (body != null)
                    entry["sent"] = body.DeepClone();
                requests.Add(entry);
                Exchange result;
                try
                {
                    using var json = await transport.SendAsync(method, new Uri(api, path), body, CancellationToken.None);
                    var node = JsonNode.Parse(json.RootElement.GetRawText());
                    result = new("Answered", transport.LastResponse?.Status ?? 200, node, null);
                    entry["response"] = node?.DeepClone();
                }
                catch (ApiResponseException e)
                {
                    result = new("Answered", (int)e.Status, null, Shorten(e.Diagnostic));
                    entry["error"] = result.Error;
                }
                catch (ApiAccessException e)
                {
                    result = new("Answered", e.Status ?? 401, null, e.Message);
                    entry["error"] = e.Message;
                }
                catch (Exception e) when (Refusal(e) is { } refused)
                {
                    result = new("NotSent", 0, null, refused);
                    entry["outcome"] = "not sent: " + refused;
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or TimeoutException or JsonException)
                {
                    result = new("NoResponse", 0, null, e.GetType().Name + ": " + e.Message);
                    entry["outcome"] = "no response: " + result.Error;
                }
                if (result.Kind == "Answered")
                    entry["status"] = result.Status;
                // 429: the server did not process the request; the transport has already pushed the next turn past
                // Retry-After, so sending it again is safe. Reads are retried inside the transport.
                if (result.Kind == "Answered" && result.Status == 429 && method != HttpMethod.Get && attempt < 2)
                {
                    await log.WriteLineAsync("    HTTP 429：按 Retry-After 等待后重发");
                    continue;
                }
                return result;
            }
        }

        static string? Refusal(Exception e)
        {
            for (Exception? x = e; x != null; x = x.InnerException)
                if (x is GuardRefused g)
                    return g.Message;
            return e is RequestNotSentException ? "request not sent: " + e.Message : null;
        }

        // Anything but an answer stops the run (the server's state is not known); so do 401 and 403.
        static void Require(Exchange e)
        {
            if (e.Kind != "Answered")
                throw new StopRun(Describe(e));
            if (e.Status is 401 or 403)
                throw new StopRun($"HTTP {e.Status}：凭据或权限不足");
        }

        static string Describe(Exchange e) => e.Kind == "Answered" ? $"HTTP {e.Status}" + (e.Error is null ? "" : ": " + e.Error) : $"{e.Kind}: {e.Error}";
        static string Status(Exchange e) => e.Kind == "Answered" ? e.Status.ToString(CultureInfo.InvariantCulture) : e.Kind;
        static string? Shorten(string? text) => text is { Length: > 2000 } ? text[..2000] + "…" : text;

        // ---- report --------------------------------------------------------------------------------------------

        async Task<int> FinishAsync(string outcome, int exitCode)
        {
            var left = targets.Where(t => t.MayExist).ToList();
            report["outcome"] = outcome;
            report["exitCode"] = exitCode;
            report["records"] = new JsonArray(targets.Select(t => (JsonNode?)new JsonObject
                { ["key"] = t.Key, ["label"] = t.Label, ["id"] = t.Id, ["path"] = t.Path, ["state"] = t.State, ["note"] = t.Note }).ToArray());
            report["G4_createReadback"] = createReadback;
            report["G4_probes"] = probes;
            report["G4_summary"] = new JsonArray(probes.Concat(createReadback).GroupBy(p => (string?)p!["kind"] ?? "(none)")
                .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => (JsonNode?)new JsonObject { ["kind"] = g.Key, ["count"] = g.Count() }).ToArray());
            report["G8_softDelete"] = softDeletes;
            if (Focused)
            {
                var verdict = RemarksConclusion();
                verdict["steps"] = remarkSteps;
                report["aircraftInternalRemarks"] = verdict;
            }
            report["cleanup"] = cleanup;
            report["leftovers"] = new JsonArray(left.Select(t => (JsonNode?)new JsonObject
            {
                ["record"] = t.Key,
                ["label"] = t.Label,
                ["id"] = t.Id,
                ["path"] = t.Path,
                ["marker"] = marker,
                ["reason"] = t.State == "create-unknown" ? "POST sent without a usable answer: the record may exist; search vAMSYS for the marker" : t.Note
            }).ToArray());
            report["finishedUtc"] = clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
            report["requestCount"] = guard.Sent;
            report["requests"] = requests;
            report["wire"] = guard.Wire;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, report.ToJsonString(Indented));

            var w = new System.Text.StringBuilder();
            w.AppendLine();
            w.AppendLine($"结果：{outcome}；共发出 {guard.Sent} 次请求；报告：{Path.GetFullPath(reportPath)}");
            if (probes.Count + createReadback.Count > 0)
                w.AppendLine("G4 回读差异：" + string.Join("，", report["G4_summary"]!.AsArray().Select(k => $"{k!["kind"]} {k["count"]}")));
            if (Focused && report["aircraftInternalRemarks"] is JsonObject remarks)
            {
                w.AppendLine("飞机 internal_remarks：");
                foreach (var step in remarkSteps)
                    w.AppendLine($"  {step!["step"],-22} 发送 {step["sent"]?.ToJsonString(Compact) ?? "null"}{(step["writeStatus"] is { } ws ? $"（HTTP {ws}）" : "")} → 单条 GET {Shown(step["single"])}；列表 {Shown(step["list"])}");
                w.AppendLine("结论：" + ((bool?)remarks["putSaved"] switch
                {
                    true => "PUT 能保存 internal_remarks（PUT 后回读与发送值一致）",
                    false => "PUT 不能保存 internal_remarks（PUT 被接受，但回读与发送值不一致）",
                    _ => "无法判断（" + remarks["conclusion"] + "）"
                }) + ((bool?)remarks["postSaved"] switch { true => "；POST 新建时保存了该字段", false => "；POST 新建时没有保存该字段", _ => "" }));
            }
            foreach (var s in softDeletes)
                w.AppendLine($"G8 {s!["type"],-14} 航线 {s["id"]}：DELETE {s["deleteStatus"]?.ToString() ?? "无应答"}，随后 GET {s["getAfterDeleteStatus"]?.ToString() ?? "-"}");
            if (left.Count > 0)
            {
                var bar = new string('!', 72);
                w.AppendLine(bar);
                w.AppendLine($"!! 以下 {left.Count} 条本轮新建的记录没有清理掉，请到 vAMSYS 手动删除（标记：{marker}）：");
                foreach (var t in left)
                    w.AppendLine($"!!   {t.Label,-16} ID {t.Id ?? "未知"}  {t.Path ?? t.Collection}  {(t.State == "create-unknown" ? "POST 已发出但没有取得 ID，记录可能已存在，请按标记搜索" : t.Note)}");
                w.AppendLine(bar);
            }
            await log.WriteLineAsync(w.ToString());
            return exitCode;
        }
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    // Single GET and create answers wrap the record in "data", except routings, which answer the bare object.
    static JsonObject? Row(JsonNode? body) => body is JsonObject o ? o["data"] as JsonObject ?? o : null;
    static string Text(JsonElement row, string field) => row.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    static long? Id(JsonElement row, string field) => row.TryGetProperty(field, out var v) && v.ValueKind is JsonValueKind.Number or JsonValueKind.String
        && long.TryParse(v.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    static List<long> Ids(JsonElement row, string field) => row.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Array
        ? v.EnumerateArray().Select(e => long.TryParse(e.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0).Where(id => id > 0).ToList() : [];

    // Lets GET anywhere below the API through, POST only to a collection this run planned, PUT and DELETE only to a
    // record this run created, and the token request; writes only after the confirmation; nothing over the cap.
    sealed class Guard(HttpMessageHandler inner, Uri tokenUri, Uri api) : DelegatingHandler(inner)
    {
        int sent;
        public int Sent => Volatile.Read(ref sent);
        public int Limit { get; set; }
        public bool WritesAllowed { get; set; }
        public HashSet<string> Collections { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Owned { get; } = new(StringComparer.Ordinal);
        // Every request handed on, as sent on the wire (retries and token requests included): method, path, status.
        // No bodies, headers or query values (cursors are opaque; the token request carries the credentials).
        public JsonArray Wire { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var token = request.Method == HttpMethod.Post && uri == tokenUri;
            var below = uri.Host == api.Host && uri.AbsolutePath.StartsWith(api.AbsolutePath, StringComparison.Ordinal);
            var path = below ? uri.AbsolutePath[api.AbsolutePath.Length..] : null;
            var allowed = token
                || below && request.Method == HttpMethod.Get
                || below && WritesAllowed && request.Method == HttpMethod.Post && Collections.Contains(path!) && uri.Query.Length == 0
                || below && WritesAllowed && (request.Method == HttpMethod.Put || request.Method == HttpMethod.Delete) && Owned.Contains(path!) && uri.Query.Length == 0;
            if (!allowed)
                throw new GuardRefused($"refused {request.Method} {uri.AbsolutePath}");
            if (Sent >= Limit)
                throw new GuardRefused($"request cap {Limit} reached");
            Interlocked.Increment(ref sent);
            var entry = new JsonObject { ["method"] = request.Method.Method, ["path"] = uri.AbsolutePath + (uri.Query.Length > 0 ? "?…" : "") };
            lock (Wire)
                Wire.Add(entry);
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                entry["status"] = (int)response.StatusCode;
                return response;
            }
            catch (Exception e)
            {
                entry["outcome"] = "no response: " + e.GetType().Name;
                throw;
            }
        }
    }
}
