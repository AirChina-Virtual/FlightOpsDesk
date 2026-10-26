using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.UnitTests.Api;

/// <summary>
/// An adapter over a fake Operations server and a workspace of the airline "7", plus builders for the rows and change
/// items the API tests need. Uses only the public surface of <see cref="OperationsAdapter"/>, so the tests that use only
/// this kit describe behaviour of the adapter as a whole and hold across refactors of what is behind it.
/// </summary>
internal sealed class ApiKit
{
    public readonly FakeOperationsServer Server = new();
    public readonly Workspace Workspace;
    public readonly OperationsAdapter Api;
    public readonly OperationsTransport Transport;

    public ApiKit(string? airline = "7")
    {
        Workspace = new Workspace { AirlineId = airline, Mode = RunMode.Online };
        // Virtual time: the one-request-per-second spacing and the read retry back-off cost no real time.
        var requests = InstantTime.Requests();
        Transport = new OperationsTransport(Server.CreateClient(), OperationsAdapter.BaseUri, Guid.NewGuid().ToString(),
            new TokenProvider(_ => Task.FromResult(new AccessToken("test", requests.Clock.GetUtcNow().AddHours(1))), requests.Clock), requests);
        Api = new OperationsAdapter(Transport, Workspace);
    }

    public static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    public DataRow Map(ResourceKind kind, string json) => Api.FromJson(kind, Json(json));

    // ---- standard records -------------------------------------------------------------------------------------

    public const string Egll = """{"id":101,"airline_id":7,"icao":"EGLL","iata":"LHR","name":"London","container_ids":[1]}""";
    public const string Egkk = """{"id":102,"airline_id":7,"icao":"EGKK","iata":"LGW","name":"Gatwick","container_ids":[]}""";
    public const string Fleet4 = """{"id":4,"airline_id":7,"name":"Fleet 4","code":"B738","type":"cargo","max_cargo":10000,"parameter_ids":[1],"updated_at":"2026-01-01T00:00:00Z"}""";
    public const string Fleet5 = """{"id":5,"airline_id":7,"name":"Fleet 5","code":"B738","type":"cargo","max_cargo":10000,"parameter_ids":[1]}""";
    public const string Aircraft8 = """{"id":8,"airline_id":7,"fleet_id":4,"registration":"B-TEST","name":"Plane","cargo":1000}""";
    public const string Routing9 = """{"id":9,"airline_id":7,"departure_airport_id":101,"arrival_airport_id":102,"route":"DCT","remarks":"original","tag":["a"]}""";
    public const string Scheduled10 = """{"id":10,"departure_id":101,"arrival_id":102,"type":"scheduled","callsign":"CAO100","flight_number":"CA100","fleet_ids":[4],"hidden":true,"remarks":"original","updated_at":"2026-01-01T00:00:00Z"}""";
    public const string Jumpseat11 = """{"id":11,"departure_id":101,"arrival_id":102,"type":"jumpseat","callsign":null,"flight_number":null,"remarks":"original jumpseat","hidden":false}""";
    public const string Scheduled12 = """{"id":12,"departure_id":101,"arrival_id":102,"type":"scheduled","callsign":"CAO120","flight_number":"CA120","fleet_ids":[4,5],"departure_time":"23:59:37","arrival_time":"01:20:15","start_date":"2026-10-01T00:00:00.1234567+00:00","hidden":false}""";

    /// <summary>Airports 101 and 102 and fleets 4 and 5 known to the adapter (as after a read), with rows in the workspace snapshot.</summary>
    public void LoadStandardAirportsAndFleets()
    {
        foreach (var (kind, json) in new[] { (ResourceKind.Airports, Egll), (ResourceKind.Airports, Egkk), (ResourceKind.Fleets, Fleet4), (ResourceKind.Fleets, Fleet5) })
        {
            var row = Map(kind, json);
            Workspace.Resources[kind].Snapshot.Add(row);
            Workspace.Resources[kind].Draft.Add(row.Dup());
        }
    }

    // ---- change items -----------------------------------------------------------------------------------------

    public static ChangeItem Update(ResourceKind kind, DataRow before, params (string Field, string Value)[] edits)
        => new()
        {
            Resource = kind,
            Kind = ChangeKind.Update,
            Before = before,
            After = before.With(edits.Select(e => KeyValuePair.Create(e.Field, e.Value))),
            Fields = edits.ToDictionary(e => e.Field, e => new FieldChange(FieldIntent.Set, e.Value))
        };

    public static ChangeItem Create(ResourceKind kind, params (string Field, string Value)[] fields)
        => new()
        {
            Resource = kind,
            Kind = ChangeKind.Create,
            After = new DataRow { Fields = fields.ToDictionary(p => p.Field, p => p.Value) },
            Fields = fields.ToDictionary(p => p.Field, p => new FieldChange(FieldIntent.Set, p.Value))
        };

    public static ChangeItem Delete(ResourceKind kind, DataRow before)
        => new() { Resource = kind, Kind = ChangeKind.Delete, Before = before, After = before.With("_delete", "TRUE"), Fields = [] };

    // ---- assertions -------------------------------------------------------------------------------------------

    /// <summary>The message code of the block the action throws.</summary>
    public static string BlockedWith(Action action)
    {
        var error = Assert.ThrowsAny<Exception>(action);
        return MessageErrors.Describe(error).Code ?? error.Message;
    }

    public static string BlockedField(Action action)
    {
        var error = Assert.ThrowsAny<Exception>(action);
        return MessageErrors.Describe(error).Arguments.TryGetValue("field", out var field) ? field.Text ?? "" : "";
    }

    /// <summary>A single-record answer: the record wrapped in <c>data</c>.</summary>
    public static FakeResponse Data(string json) => FakeResponse.Ok("{\"data\":" + json + "}");

    public static string Body(ApiStep step) => JsonSerializer.Serialize(step.Body);
}
