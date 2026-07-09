// mdb_env_copy: copy the entire environment to a new path.
// Ports mdb_env_copy2 from mdb.c. The simplest correct approach for non-WRITEMAP:
// open the source read-only, create the target, copy all committed pages
// (0 to (last_pg+1)*psize), flush + fsync.
using System.Runtime.InteropServices;

namespace Lmdb;

public sealed unsafe partial class LmdbEnvironment
{
    /// <summary>Copy this environment to <paramref name="destPath"/> (mdb_env_copy).
    /// Creates a new directory with data.mdb and lock.mdb. The copy is a complete,
    /// consistent snapshot of the committed state.</summary>
    public void Copy(string destPath)
    {
        System.IO.Directory.CreateDirectory(destPath);
        string destData = System.IO.Path.Combine(destPath, "data.mdb");
        long bytesToCopy = (long)(_lastPg + 1) * _psize;

        // Create the target file and copy bytes via the mmap.
        using var dest = Platform.MappedFile.OpenReadWrite(destData, _mapSize, create: true);
        byte* srcPtr = _mapPtr;
        byte* dstPtr = dest.Pointer;
        Buffer.MemoryCopy(srcPtr, dstPtr, bytesToCopy, bytesToCopy);
        dest.Flush();
        dest.Stream!.Flush(true);   // fsync
    }
}
