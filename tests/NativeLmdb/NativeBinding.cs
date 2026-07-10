// Minimal P/Invoke binding to the system liblmdb.so.0 (the real C LMDB).
// Used ONLY for benchmark comparison against our pure C# port.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NativeLmdb;

public static unsafe class Native
{
    private const string Lib = "liblmdb.so.0";

    [StructLayout(LayoutKind.Sequential)]
    public struct MDB_val { public nuint mv_size; public void* mv_data; }

    [DllImport(Lib)] public static extern int mdb_env_create(out void* env);
    [DllImport(Lib)] public static extern void mdb_env_close(void* env);
    [DllImport(Lib)] public static extern int mdb_env_set_mapsize(void* env, ulong size);
    [DllImport(Lib)] public static extern int mdb_env_open(void* env, string path, uint flags, int mode);
    [DllImport(Lib)] public static extern int mdb_txn_begin(void* env, void* parent, uint flags, out void* txn);
    [DllImport(Lib)] public static extern int mdb_txn_commit(void* txn);
    [DllImport(Lib)] public static extern void mdb_txn_abort(void* txn);
    [DllImport(Lib)] public static extern int mdb_dbi_open(void* txn, string? name, uint flags, out uint dbi);
    [DllImport(Lib)] public static extern int mdb_put(void* txn, uint dbi, MDB_val* key, MDB_val* data, uint flags);
    [DllImport(Lib)] public static extern int mdb_get(void* txn, uint dbi, MDB_val* key, MDB_val* data);
    [DllImport(Lib)] public static extern int mdb_cursor_open(void* txn, uint dbi, out void* cur);
    [DllImport(Lib)] public static extern void mdb_cursor_close(void* cur);
    [DllImport(Lib)] public static extern int mdb_cursor_get(void* cur, MDB_val* key, MDB_val* data, int op);
    [DllImport(Lib)] public static extern IntPtr mdb_strerror(int err);

    public const uint MDB_NOTLS = 0x200000;
    public const int MDB_FIRST = 0;
    public const int MDB_NEXT = 8;
    public const int MDB_NOTFOUND = -30798;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MDB_val Val(ReadOnlySpan<byte> s)
    {
        fixed (byte* p = s)
            return new MDB_val { mv_size = (nuint)s.Length, mv_data = p };
    }

    public static void Check(this int rc)
    {
        if (rc != 0 && rc != MDB_NOTFOUND)
            throw new Exception($"mdb error {rc}: {Marshal.PtrToStringAnsi(mdb_strerror(rc))}");
    }
}

public sealed unsafe class NativeEnv : IDisposable
{
    public void* Ptr;

    public NativeEnv(string path, long mapSize, bool create)
    {
        if (create)
        {
            if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true);
            System.IO.Directory.CreateDirectory(path);
        }

        Native.mdb_env_create(out Ptr).Check();
        Native.mdb_env_set_mapsize(Ptr, (ulong)mapSize).Check();
        Native.mdb_env_open(Ptr, path, Native.MDB_NOTLS, 420).Check();
    }

    public void Dispose()
    {
        if (Ptr != null) { Native.mdb_env_close(Ptr); Ptr = null; }
    }
}
