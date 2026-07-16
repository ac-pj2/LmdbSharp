// Native-memory shim: every allocation and free in the engine routes through
// here so tests can (a) inject deterministic allocation failures at any point
// in a transaction's life and (b) assert that no path leaks native memory.
// Zero overhead in production beyond a predictable branch and an interlocked
// counter.
using System.Runtime.InteropServices;
using System.Threading;

namespace Lmdb;

internal static unsafe class Mem
{
    /// <summary>Test hook: countdown to an injected failure. When it reaches 0
    /// the allocation throws <see cref="OutOfMemoryException"/> and the hook
    /// disarms. -1 (default) = disabled. Thread-static so fault tests cannot
    /// poison allocations on other test threads.</summary>
    [ThreadStatic] internal static int FailAfterCore;   // 0 = disabled; k+1 = fail after k
    internal static int FailAfter
    {
        get => FailAfterCore - 1;
        set => FailAfterCore = value + 1;
    }

    /// <summary>Live allocation count on this thread (allocs minus frees).
    /// After every transaction and environment allocated on a thread is
    /// disposed there, this returns to its prior value — the leak assertion of
    /// the fault-injection tests.</summary>
    [ThreadStatic] internal static long Outstanding;

    internal static void* Alloc(nuint bytes)
    {
        Gate();
        void* p = NativeMemory.Alloc(bytes);
        Outstanding++;
        return p;
    }

    internal static void* AllocZeroed(nuint bytes)
    {
        Gate();
        void* p = NativeMemory.AllocZeroed(bytes);
        Outstanding++;
        return p;
    }

    internal static void Free(void* p)
    {
        if (p == null) return;
        NativeMemory.Free(p);
        Outstanding--;
    }

    private static void Gate()
    {
        int f = FailAfterCore;
        if (f == 0) return;   // disabled ([ThreadStatic] default)
        if (f == 1)
        {
            FailAfterCore = 0;   // disarm: recovery paths may allocate
            throw new OutOfMemoryException("injected native allocation failure");
        }
        FailAfterCore = f - 1;
    }
}
