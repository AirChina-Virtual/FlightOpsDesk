// Verbatim port of the legacy runner scenarios: analyzer rules the old runner never applied are silenced here.
#pragma warning disable CA2000, CA1307, CA1310
using System.Net;
using System.Text.Json;
using VamSys.Core;
using VamSys.Infrastructure;
using VamSys.TestSupport;

namespace VamSys.IntegrationTests.Api.Legacy;

// Helpers carried over from the legacy runner for the migrated API scenarios.
sealed class ScriptedHttp(IEnumerable<Func<HttpRequestMessage,Task<HttpResponseMessage>>> scripts):HttpMessageHandler
{
    readonly Queue<Func<HttpRequestMessage,Task<HttpResponseMessage>>> queue=new(scripts);
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Calls++;return queue.Dequeue()(request);}
    public static HttpResponseMessage Json(string value,HttpStatusCode status=HttpStatusCode.OK)=>new(status){Content=new StringContent(value,System.Text.Encoding.UTF8,"application/json")};
}
sealed class AuditHttp:HttpMessageHandler
{
    public List<HttpMethod> Methods=[];
    public Func<HttpRequestMessage,CancellationToken,HttpResponseMessage> Next=(r,ct)=>throw new Exception("Unexpected request");
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    { Methods.Add(request.Method);return Task.FromResult(Next(request,ct)); }
}
// Explicit metadata for legacy synthetic scheduled-route fixtures; never intercept writes or route reads.
static class RouteApprovalFixture
{
    public static HttpResponseMessage? Respond(HttpRequestMessage request)
    {
        if(request.Method!=HttpMethod.Get)return null;
        var path=request.RequestUri!.AbsolutePath;
        string body;
        if(path.EndsWith("/parameters"))body=JsonSerializer.Serialize(new{data=new[]{"AC","N0","E0","OL","MI","FI","F0"}.Select((prefix,i)=>new
            {id=i+1,icao_code="ACA",iata_code=prefix,approved=true,restricted=false,fleet_ids=new[]{4}}),meta=new{next_cursor_url=(string?)null}});
        else if(path.EndsWith("/fleet/4"))body="""{"data":{"id":4,"airline_id":7,"name":"Approved fixture fleet","code":"B738","type":"pax"}}""";
        else return null;
        return new(HttpStatusCode.OK){Content=new StringContent(body)};
    }
}

static class LegacyFixtures
{
    public static DataRow Dup(this DataRow row) => row.ToBuilder().ToRow();

    internal static void LegacyFleetSteps(ChangeItem item)
    {
        var post=new ApiStep(HttpMethod.Post,"fleet",new(){["name"]=item.After.Get("Name"),["code"]=item.After.Get("Type Code"),["type"]=item.After.Get("Type (pax/cargo/...)")});
        var put=new ApiStep(HttpMethod.Put,"{created}",new(){["max_pax"]=long.Parse(item.After.Get("Max Passengers"),System.Globalization.CultureInfo.InvariantCulture)});
        OperationsAdapter.PrepareSteps(item,[post,put]);item.ApiExpectedValues=OperationsAdapter.ExpectedValues([post,put]);
    }
}

