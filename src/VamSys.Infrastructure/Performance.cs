using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace VamSys.Infrastructure;

public sealed class RequestCoordinator(TimeProvider? timeProvider=null)
{
    public static RequestCoordinator Shared { get; }=new();
    public TimeProvider Clock { get; }=timeProvider ?? TimeProvider.System;
    internal sealed class Budget { internal readonly SemaphoreSlim Gate=new(1); internal DateTimeOffset Next; internal bool Backoff; }
    internal readonly ConcurrentDictionary<string,Budget> Budgets=new();
}
public sealed class PerformanceRun : IDisposable
{
    static readonly AsyncLocal<PerformanceRun?> ambient=new();
    readonly PerformanceRun? previous;
    readonly object gate=new();
    readonly long started=Stopwatch.GetTimestamp();
    long? canceled;
    public static PerformanceRun? Current=>ambient.Value;
    public string Phase { get; set; }="Other";
    public Guid WorkspaceId { get; }
    public Guid JobId { get; }
    public Guid RunId { get; }=Guid.NewGuid();
    public Dictionary<string,long> Requests { get; }=[];
    public long Retries { get; private set; }
    public long Pages { get; private set; }
    public long Candidates { get; private set; }
    public long IndexBuilds { get; private set; }
    public double RateWaitMs { get; private set; }
    public double BackoffMs { get; private set; }
    public Dictionary<string,long> Checkpoints { get; }=[];
    public long SaveAttempts { get; private set; }
    public long SaveSuccesses { get; private set; }
    public long SaveFailures { get; private set; }
    public long JsonBytes { get; private set; }
    public double SerializationMs { get; private set; }
    public double SqliteMs { get; private set; }
    public double? CancellationMs { get; private set; }
    public double DurationMs { get; private set; }
    public string Outcome { get; private set; }="Running";
    PerformanceRun(Guid workspaceId,Guid jobId){WorkspaceId=workspaceId;JobId=jobId;previous=ambient.Value;ambient.Value=this;}
    public static PerformanceRun Begin(Guid workspaceId,Guid jobId)=>new(workspaceId,jobId);
    public void Request(string resource,string method){lock(gate){var key=$"{resource}/{Phase}/{method}";Requests[key]=Requests.GetValueOrDefault(key)+1;}}
    public void Retry(){lock(gate)Retries++;}
    public void Page(){lock(gate)Pages++;}
    public void Candidate(){lock(gate)Candidates++;}
    public void IndexBuilt(){lock(gate)IndexBuilds++;}
    public void Wait(TimeSpan time,bool backoff){lock(gate){if(backoff)BackoffMs+=time.TotalMilliseconds;else RateWaitMs+=time.TotalMilliseconds;}}
    public void Saving(){lock(gate)SaveAttempts++;}
    public void Checkpoint(string kind){lock(gate)Checkpoints[kind]=Checkpoints.GetValueOrDefault(kind)+1;}
    public void Serialized(long bytes,TimeSpan time){lock(gate){JsonBytes+=bytes;SerializationMs+=time.TotalMilliseconds;}}
    public void Saved(bool success,TimeSpan time){lock(gate){if(success)SaveSuccesses++;else SaveFailures++;SqliteMs+=time.TotalMilliseconds;}}
    public void MarkCancellation(){lock(gate)canceled ??= Stopwatch.GetTimestamp();}
    public void Finish(string outcome){lock(gate){Outcome=outcome;DurationMs=Stopwatch.GetElapsedTime(started).TotalMilliseconds;if(canceled is long t)CancellationMs=Stopwatch.GetElapsedTime(t).TotalMilliseconds;}}
    public bool TryWrite(string directory)
    {
        try
        {
            var folder=Path.Combine(directory,WorkspaceId.ToString(),JobId.ToString());Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder,RunId+".json"),JsonSerializer.Serialize(this,new JsonSerializerOptions{WriteIndented=true}));return true;
        }
        catch { return false; } // Diagnostics never alter a business outcome.
    }
    public void Dispose()=>ambient.Value=previous;
}
