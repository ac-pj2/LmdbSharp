// Memory-mapped file access, layered on the .NET BCL MemoryMappedFile API.
//
// LMDB is fundamentally pointer-arithmetic over an mmap. For the read path we map
// the data file read-only. For the write path we map a read/write region sized to
// the environment's mapsize (the file is extended to mapsize — sparse on Linux —
// so new pages can be allocated by writing beyond the current data length).
//
// Non-WRITEMAP LMDB writes dirty pages via pwrite() and lets the shared mmap
// reflect them. We instead mirror WRITEMAP semantics: mutate the mmap directly
// (dirty copies live in native buffers until commit, then are copied in) and
// flush the view + fsync the file for durability.
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
    public FileStream? Stream => _fs;

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

    /// <summary>Open (or create) a file read/write and map mapSize bytes. The file is
    /// extended to mapSize so pages up to mapSize/psize are allocatable.</summary>
    public static MappedFile OpenReadWrite(string path, long mapSize, bool create)
    {
        var f = new MappedFile();
        var mode = create ? FileMode.OpenOrCreate : FileMode.Open;
        f._fs = new FileStream(path, mode, FileAccess.ReadWrite, FileShare.ReadWrite);
        f.Writable = true;
        // Always ensure the file covers mapSize (sparse on Linux). This lets the
        // write path allocate pages up to mapsize by writing into the mmap.
        if (f._fs.Length < mapSize) f._fs.SetLength(mapSize);
        f.Size = mapSize;
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
