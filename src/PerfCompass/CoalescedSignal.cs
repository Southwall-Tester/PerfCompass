namespace PerfCompass;

// Producers never wait for the consumer. One posted callback coalesces signals;
// a signal during consumption schedules one follow-up.
internal sealed class CoalescedSignal : System.IDisposable
{
    private readonly object gate = new();
    private readonly System.Func<System.Action, bool> post;
    private readonly System.Action consume;
    private bool pending, dirty, disposed;
    internal CoalescedSignal(System.Func<System.Action, bool> post, System.Action consume)
    { this.post = post; this.consume = consume; }
    internal void Signal()
    {
        lock (gate)
        {
            if (disposed) return;
            dirty = true;
            if (pending) return;
            pending = true;
        }
        Post();
    }
    private void Post()
    {
        if (post(Drain)) return;
        lock (gate) pending = false; // A later signal retries a refused post.
    }
    private void Drain()
    {
        lock (gate)
        {
            if (disposed) { pending = false; return; }
            dirty = false;
        }
        try { consume(); }
        finally
        {
            bool again;
            lock (gate)
            { again = !disposed && dirty; if (!again) pending = false; }
            if (again) Post();
        }
    }
    public void Dispose() { lock (gate) { disposed = true; dirty = false; } }
}
