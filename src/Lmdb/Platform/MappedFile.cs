// Memory-mapped file access, layered on the .NET BCL MemoryMappedFile API.
//
// For the read path we map the data file's actual length read-only and expose a
// raw byte* into the region (LMDB is fundamentally pointer-arithmetic over mmap).
// The write path will add a read/write mapping sized to mm_mapsize plus msync.
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace Lmdb.Platform;

internal sealed unsafe class MappedFile : IDisposable
{
    private FileStream? _fs;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;
    private byte* _ptr;
    private bool _ownsPointer;

    public byte* Pointer => _ptr;
    public long Size { get; private set; }
    public bool Writable { get; private set; }

    /// <summary>Open an existing file read-only and map its full length.</summary>
    public static MappedFile OpenReadOnly(string path)
    {
        var f = new MappedFile();
        f._fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        f.Size = f._fs.Length;
        f.Writable = false;
        f._mmf = MemoryMappedFile.CreateFromFile(
            f._fs, null, 0L, MemoryMappedFileAccess.Read,
            HandleInheritability.None, leaveOpen: false);
        f._view = f._mmf.CreateViewAccessor(0L, f.Size, MemoryMappedFileAccess.Read);
        f.Acquire();
        return f;
    }

    /// <summary>Open (or create) a file read/write and map mapSize bytes.</summary>
    public static MappedFile OpenReadWrite(string path, long mapSize, bool create)
    {
        var f = new MappedFile();
        f._fs = new FileStream(
            path, create ? FileMode.OpenOrCreate : FileMode.Open,
            FileAccess.ReadWrite, FileShare.ReadWrite);
        f.Writable = true;
        if (create && f._fs.Length < mapSize) f._fs.SetLength(mapSize);
        f.Size = f._fs.Length < mapSize ? mapSize : f._fs.Length;
        f._mmf = MemoryMappedFile.CreateFromFile(
            f._fs, null, f.Size, MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None, leaveOpen: false);
        f._view = f._mmf.CreateViewAccessor(0L, f.Size, MemoryMappedFileAccess.ReadWrite);
        f.Acquire();
        return f;
    }

    private void Acquire()
    {
        var handle = _view!.SafeMemoryMappedViewHandle;
        handle.AcquirePointer(ref _ptr);
        _ownsPointer = true;
    }

    /// <summary>Flush the view to disk (msync/FlushViewOfFile).</summary>
    public void Flush() => _view?.Flush();

    public void Dispose()
    {
        if (_ownsPointer && _view != null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _ownsPointer = false;
        }
        _view?.Dispose();
        _mmf?.Dispose();
        _fs?.Dispose();
        _ptr = null;
    }
}
