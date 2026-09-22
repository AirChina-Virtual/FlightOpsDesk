// A deterministic TimeProvider: callers explicitly advance timers; rate limiting remains enabled.
sealed class TestTime : TimeProvider
{
    readonly object gate=new();
    readonly List<Timer> timers=[];
    long ticks;
    public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
    public override long GetTimestamp(){lock(gate)return ticks;}
    public override DateTimeOffset GetUtcNow()=>DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
    public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)
    {var timer=new Timer(this,callback,state);timer.Change(dueTime,period);return timer;}
    public void Advance(TimeSpan amount)
    {
        List<Timer> due;
        lock(gate)
        {
            ticks+=amount.Ticks;due=timers.Where(t=>t.Due<=ticks).ToList();
            foreach(var t in due){timers.Remove(t);if(t.Period>0){t.Due=ticks+t.Period;timers.Add(t);}}
        }
        foreach(var t in due)t.Callback(t.State);
    }
    public async Task Drive(Task task)
    {
        var timeout=global::System.Diagnostics.Stopwatch.StartNew();
        while(!task.IsCompleted)
        {
            long? next;lock(gate)next=timers.Count==0?null:timers.Min(t=>t.Due);
            if(next.HasValue)Advance(TimeSpan.FromTicks(Math.Max(0,next.Value-GetTimestamp())));
            else await Task.Delay(1);
            if(timeout.Elapsed>TimeSpan.FromSeconds(20))throw new TimeoutException("Virtual-time test stalled");
        }
        await task;
    }
    sealed class Timer(TestTime clock,TimerCallback callback,object? state):ITimer
    {
        internal TimerCallback Callback=callback;internal object? State=state;internal long Due;internal long Period;bool disposed;
        public bool Change(TimeSpan dueTime,TimeSpan period)
        {
            lock(clock.gate)
            {
                if(disposed)return false;clock.timers.Remove(this);Period=period.Ticks;
                if(dueTime!=Timeout.InfiniteTimeSpan){Due=clock.ticks+dueTime.Ticks;clock.timers.Add(this);}return true;
            }
        }
        public void Dispose(){lock(clock.gate){disposed=true;clock.timers.Remove(this);}}
        public ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;}
    }
}
