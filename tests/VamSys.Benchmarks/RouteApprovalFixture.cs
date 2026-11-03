using System.Net;
using System.Text.Json;

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
