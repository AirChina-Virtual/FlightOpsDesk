using System.Net;
using VamSys.Core;
using VamSys.Infrastructure;

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

