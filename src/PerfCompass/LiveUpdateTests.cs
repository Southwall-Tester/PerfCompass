using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunLiveUpdateTests(Action<string, Action> check, Action<bool, string> assert)
    {
        check("Live updates coalesce a stalled UI without blocking producers", () =>
        {
            var posted = new Queue<Action>(); int calls = 0;
            using var signal = new CoalescedSignal(a => { posted.Enqueue(a); return true; }, () => calls++);
            for (int i = 0; i < 10000; i++) signal.Signal();
            assert(posted.Count == 1 && calls == 0, "Unbounded queue or producer consumed UI work");
            posted.Dequeue()(); assert(calls == 1 && posted.Count == 0, "Burst not consumed once");
            signal.Signal(); assert(posted.Count == 1, "Future frame not scheduled");
        });
        check("Live update signals during consumption schedule one follow-up", () =>
        {
            var posted = new Queue<Action>(); int calls = 0; CoalescedSignal signal = null!;
            using (signal = new(a => { posted.Enqueue(a); return true; }, () =>
            { calls++; if (calls == 1) for (int i = 0; i < 1000; i++) signal.Signal(); }))
            {
                signal.Signal(); posted.Dequeue()();
                assert(calls == 1 && posted.Count == 1, "Concurrent publication lost or queued repeatedly");
                posted.Dequeue()(); assert(calls == 2 && posted.Count == 0, "Follow-up did not settle");
            }
        });
        check("Disposed live updates cannot consume a queued callback", () =>
        {
            var posted = new Queue<Action>(); int calls = 0;
            var signal = new CoalescedSignal(a => { posted.Enqueue(a); return true; }, () => calls++);
            signal.Signal(); signal.Dispose(); signal.Signal(); posted.Dequeue()();
            assert(calls == 0 && posted.Count == 0, "Shutdown callback executed or scheduled again");
        });
        check("Refused live update scheduling is retried by a later signal", () =>
        {
            var posted = new Queue<Action>(); int attempts = 0, calls = 0;
            using var signal = new CoalescedSignal(a => { if (++attempts == 1) return false; posted.Enqueue(a); return true; }, () => calls++);
            signal.Signal(); signal.Signal();
            assert(attempts == 2 && posted.Count == 1, "Refused schedule stuck pending");
            posted.Dequeue()(); assert(calls == 1, "Retry not consumed");
        });
        check("A live UI exception does not prevent future scheduling", () =>
        {
            var posted = new Queue<Action>(); int calls = 0;
            using var signal = new CoalescedSignal(a => { posted.Enqueue(a); return true; }, () => { if (++calls == 1) throw new InvalidOperationException("Injected UI fault"); });
            signal.Signal(); bool caught = false;
            try { posted.Dequeue()(); } catch (InvalidOperationException) { caught = true; }
            signal.Signal(); assert(caught && posted.Count == 1, "Failure swallowed or pending flag stuck");
            posted.Dequeue()(); assert(calls == 2, "Subsequent rendering lost");
        });
        check("Concurrent live producers post a bounded callback", () =>
        {
            var posted = new System.Collections.Concurrent.ConcurrentQueue<Action>(); int calls = 0;
            using var signal = new CoalescedSignal(a => { posted.Enqueue(a); return true; }, () => calls++);
            Parallel.For(0, 10000, _ => signal.Signal());
            assert(posted.Count == 1 && calls == 0, "Concurrent producers created excess callbacks");
            assert(posted.TryDequeue(out var action), "No callback"); action!(); assert(calls == 1, "No consumption");
        });
    }
}
