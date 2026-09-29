using VamSys.Infrastructure;

// Virtual time that advances by itself: each one-shot timer jumps the clock to its due time and fires at once.
// Suited to sequential scenarios; rate-limit spacing and backoff are still computed, just not waited for in real time.
// Concurrency and spacing assertions keep using TestTime, whose timers only fire when the test advances them.
sealed class InstantTime : TimeProvider
{
    long ticks=DateTimeOffset.UtcNow.UtcTicks;
    public static RequestCoordinator Requests()=>new(new InstantTime());
    public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
    public override long GetTimestamp()=>Interlocked.Read(ref ticks);
    public override DateTimeOffset GetUtcNow()=>new(GetTimestamp(),TimeSpan.Zero);
    public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan dueTime,TimeSpan period)
    {var timer=new Timer(this,callback,state);timer.Change(dueTime,period);return timer;}
    sealed class Timer(InstantTime clock,TimerCallback callback,object? state):ITimer
    {
        volatile bool disposed;
        public bool Change(TimeSpan dueTime,TimeSpan period)
        {
            if(disposed)return false;
            if(period!=Timeout.InfiniteTimeSpan && period!=TimeSpan.Zero)throw new NotSupportedException("InstantTime only supports one-shot timers");
            if(dueTime==Timeout.InfiniteTimeSpan)return true;
            if(dueTime>TimeSpan.Zero)Interlocked.Add(ref clock.ticks,dueTime.Ticks);
            // Fire asynchronously so the awaiting Task.Delay is fully constructed first.
            ThreadPool.QueueUserWorkItem(_=>{if(!disposed)callback(state);});
            return true;
        }
        public void Dispose()=>disposed=true;
        public ValueTask DisposeAsync(){Dispose();return ValueTask.CompletedTask;}
    }
}
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
