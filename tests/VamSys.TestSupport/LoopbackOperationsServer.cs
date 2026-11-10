using System.Net;
using System.Net.Sockets;

namespace VamSys.TestSupport;

/// <summary>
/// Serves a <see cref="FakeOperationsServer"/> over real HTTP on this machine, so a separately started program (the QA
/// package with VAMSYS_QA_OPERATIONS_ORIGIN set to <see cref="Origin"/>) can talk to it. Requests are handled one at a
/// time, in arrival order, like the program sends them. Only the loopback address is used; nothing leaves the machine.
/// </summary>
public sealed class LoopbackOperationsServer : IDisposable
{
    readonly HttpListener listener = new();
    readonly HttpMessageInvoker fake;
    readonly CancellationTokenSource stop = new();
    readonly Task loop;

    public LoopbackOperationsServer(FakeOperationsServer server)
    {
        fake = new HttpMessageInvoker(server, disposeHandler: false);
        // "localhost" (not 127.0.0.1) can be listened on without an administrator's URL reservation.
        Origin = new Uri($"http://localhost:{FreePort()}/");
        listener.Prefixes.Add(Origin.AbsoluteUri);
        listener.Start();
        loop = Task.Run(ServeAsync);
    }

    /// <summary>The value for VAMSYS_QA_OPERATIONS_ORIGIN.</summary>
    public Uri Origin { get; }

    static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    async Task ServeAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            { context = await listener.GetContextAsync().WaitAsync(stop.Token); }
            catch (Exception) when (stop.IsCancellationRequested || !listener.IsListening)
            { return; }
            try
            { await AnswerAsync(context); }
            catch (Exception error)
            {
                // A simulated transport failure (an interceptor that throws) drops the connection, as a lost answer would.
                Failures.Add(error);
                try { context.Response.Abort(); } catch (ObjectDisposedException) { }
            }
        }
    }

    /// <summary>Exceptions the fake server threw; a scripted timeout lands here too.</summary>
    public List<Exception> Failures { get; } = [];

    async Task AnswerAsync(HttpListenerContext context)
    {
        var incoming = context.Request;
        using var request = new HttpRequestMessage(new HttpMethod(incoming.HttpMethod), new Uri(new Uri("https://vamsys.io"), incoming.Url!.PathAndQuery));
        foreach (var name in incoming.Headers.AllKeys.OfType<string>())
            if (!name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) && name is not "Host" and not "Connection")
                request.Headers.TryAddWithoutValidation(name, incoming.Headers.GetValues(name) ?? []);
        if (incoming.HasEntityBody)
        {
            using var body = new StreamReader(incoming.InputStream, incoming.ContentEncoding);
            request.Content = new StringContent(await body.ReadToEndAsync(), System.Text.Encoding.UTF8, incoming.ContentType?.Split(';')[0] ?? "application/json");
        }
        using var answer = await fake.SendAsync(request, stop.Token);
        var response = context.Response;
        response.StatusCode = (int)answer.StatusCode;
        foreach (var (name, values) in answer.Headers)
            if (name is not "Transfer-Encoding" and not "Connection")
                response.Headers[name] = string.Join(",", values);
        var bytes = answer.Content == null ? [] : await answer.Content.ReadAsByteArrayAsync();
        if (answer.Content?.Headers.ContentType is { } type)
            response.ContentType = type.ToString();
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
        listener.Close();
        try { loop.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        fake.Dispose();
        stop.Dispose();
    }
}
