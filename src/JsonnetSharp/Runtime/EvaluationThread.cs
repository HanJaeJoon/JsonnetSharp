// Not in go-jsonnet: goroutine stacks grow to 1 GB on demand, a .NET thread's stack is fixed when it starts.

using System.Globalization;
using System.Runtime.ExceptionServices;

namespace JsonnetSharp;

// Set when the caller of an evaluation stops waiting for it (Thread.Interrupt, or Thread.Abort on .NET Framework).
internal sealed class CancelFlag
{
    internal static readonly CancelFlag Never = new();

    internal volatile bool Requested;
}

// Runs evaluations on big-stack threads that are kept for reuse while busy callers come back within a second,
// so a stream of small evaluations does not pay for a new thread each time.
internal sealed class EvaluationThread
{
    private const int IdleMilliseconds = 1000;

    private static readonly object PoolLock = new();
    private static readonly List<EvaluationThread> Idle = new();

    // A 32-bit process has about 2 GB of address space; once stacks use it up, the next allocation anywhere
    // fails and the process dies, so past this budget evaluations run on the caller's thread. Settable for tests.
    internal static long ReserveLimit = IntPtr.Size == 4 ? 512L << 20 : long.MaxValue;

    // Stack bytes of live evaluation threads, busy or idle (under PoolLock).
    private static long _reserved;

    private readonly int _stackSize;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private Action? _job;
    private bool _exit;

    private EvaluationThread(int stackSize)
    {
        _stackSize = stackSize;
        _thread = new Thread(Loop, stackSize) { IsBackground = true, Name = "JsonnetSharp evaluation" };
    }

    // Runs body on a thread with the given stack size while the caller waits, or on the caller's thread when
    // threads cannot be started (browser WebAssembly, the address space budget is used up).
    internal static T Run<T>(int stackSize, Func<CancelFlag, T> body)
    {
        EvaluationThread? worker = Acquire(stackSize);
        if (worker == null)
        {
            return body(CancelFlag.Never);
        }

        T result = default!;
        Exception? error = null;
        CancelFlag cancel = new();
        CultureInfo culture = CultureInfo.CurrentCulture;
        CultureInfo uiCulture = CultureInfo.CurrentUICulture;
        ExecutionContext? context = ExecutionContext.Capture();
        // Not disposed: the evaluation thread may still be inside Set when the caller wakes up.
        ManualResetEventSlim done = new(false);
        void Job()
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
            try
            {
                result = body(cancel);
            }
            catch (Exception e)
            {
                error = e;
            }
        }
        void Work()
        {
            try
            {
                if (context != null)
                {
                    // AsyncLocal values flow into callbacks as they would on the caller's thread.
                    ExecutionContext.Run(context, _ => Job(), null);
                }
                else
                {
                    Job();
                }
            }
            finally
            {
                SetUninterrupted(done);
            }
        }

        bool started = false;
        bool finished = false;
        try
        {
            worker.Start(Work);
            started = true;
            done.Wait();
            finished = true;
        }
        finally
        {
            if (finished)
            {
                Release(worker);
            }
            else if (!started)
            {
                worker.Retire();
            }
            else
            {
                // The caller was interrupted or aborted (a finally block defers Thread.Abort): stop the
                // evaluation and wait for it, so nothing keeps using the VM after the caller has left.
                cancel.Requested = true;
                worker._thread.Interrupt();
                WaitUninterrupted(done);
                // A pending interrupt must not reach a later evaluation.
                worker.Retire();
            }
        }
        if (error != null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
        return result;
    }

    private static void WaitUninterrupted(ManualResetEventSlim done)
    {
        bool interrupted = false;
        while (true)
        {
            try
            {
                done.Wait();
                break;
            }
            catch (ThreadInterruptedException)
            {
                interrupted = true;
            }
        }
        if (interrupted)
        {
            Thread.CurrentThread.Interrupt();
        }
    }

    // An interrupt meant for the evaluation must not leave the caller waiting forever.
    private static void SetUninterrupted(ManualResetEventSlim done)
    {
        while (true)
        {
            try
            {
                done.Set();
                return;
            }
            catch (ThreadInterruptedException)
            {
            }
        }
    }

    private static EvaluationThread? Acquire(int stackSize)
    {
        lock (PoolLock)
        {
            for (int k = Idle.Count - 1; k >= 0; k--)
            {
                if (Idle[k]._stackSize == stackSize)
                {
                    EvaluationThread idle = Idle[k];
                    Idle.RemoveAt(k);
                    return idle;
                }
            }
            if (_reserved + stackSize > ReserveLimit)
            {
                return null;
            }
            _reserved += stackSize;
        }
        EvaluationThread worker = new(stackSize);
        try
        {
            worker._thread.Start();
        }
        catch (Exception e) when (e is PlatformNotSupportedException || e is OutOfMemoryException)
        {
            lock (PoolLock)
            {
                _reserved -= stackSize;
            }
            return null;
        }
        return worker;
    }

    private static void Release(EvaluationThread worker)
    {
        lock (PoolLock)
        {
            Idle.Add(worker);
        }
    }

    private void Start(Action job)
    {
        lock (PoolLock)
        {
            _job = job;
            // Under the lock: the thread disposes the event under it when it exits.
            _wake.Set();
        }
    }

    private void Retire()
    {
        lock (PoolLock)
        {
            _exit = true;
            // Under the lock: the thread disposes the event under it when it exits.
            _wake.Set();
        }
    }

    private void Loop()
    {
        while (true)
        {
            bool woken;
            try
            {
                woken = _wake.WaitOne(IdleMilliseconds);
            }
            catch (ThreadInterruptedException)
            {
                continue;
            }
            Action? job;
            lock (PoolLock)
            {
                if (_exit || (!woken && _job == null && Idle.Remove(this)))
                {
                    _wake.Dispose();
                    _reserved -= _stackSize;
                    return;
                }
                job = _job;
                _job = null;
            }
            job?.Invoke();
        }
    }
}
