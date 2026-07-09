// LMDB error codes (lmdb.h) + managed exception type.
namespace Lmdb;

// Values are negative for failures, mirroring the C enum exactly.
public enum LmdbErr : int
{
    Success            = 0,
    KeyExist            = -30799,
    NotFound            = -30798,
    PageNotFound        = -30797,
    Corrupted           = -30796,
    Panic               = -30795,
    VersionMismatch     = -30794,
    Invalid             = -30793,
    MapFull             = -30792,
    DbsFull             = -30791,
    ReadersFull        = -30790,
    TlsFull             = -30789,
    TxnFull             = -30788,
    CursorFull          = -30787,
    PageFull            = -30786,
    MapResized          = -30785,
    Incompatible        = -30784,
    BadRslot            = -30783,
    BadTxn              = -30782,
    BadValsize          = -30781,
    BadDbi              = -30780,
    Problem             = -30779, // MDB_LAST_ERRCODE
}

public static class Err
{
    public static string Message(LmdbErr err)
    {
        // Trimmed from mdb.c:1743 strerror table.
        return err switch
        {
            LmdbErr.Success         => "Successful return",
            LmdbErr.KeyExist        => "key/data pair already exists",
            LmdbErr.NotFound        => "key/data pair not found (EOF)",
            LmdbErr.PageNotFound    => "Request page not found in DB",
            LmdbErr.Corrupted       => "Located page was wrong type",
            LmdbErr.Panic           => "Update of meta page failed or environment had fatal error",
            LmdbErr.VersionMismatch => "Database environment version mismatch",
            LmdbErr.Invalid         => "File is not an LMDB file",
            LmdbErr.MapFull         => "Environment mapsize limit reached",
            LmdbErr.DbsFull         => "Environment maxdbs limit reached",
            LmdbErr.ReadersFull     => "Environment maxreaders limit reached",
            LmdbErr.TlsFull         => "Too many TLS keys (reader table full)",
            LmdbErr.TxnFull         => "Nested txns reach max depth",
            LmdbErr.CursorFull      => "Stack depth limit reached (too many cursor parents)",
            LmdbErr.PageFull        => "Page has not enough space",
            LmdbErr.MapResized      => "Database was grown by another process, mapsize too small",
            LmdbErr.Incompatible    => "Operation and DB incompatible, or DB flags changed",
            LmdbErr.BadRslot        => "Invalid reuse of reader locktable slot",
            LmdbErr.BadTxn          => "Cannot handle a nested txn in a read txn",
            LmdbErr.BadValsize      => "Too big key/data, or wrong DUPFIXED size",
            LmdbErr.BadDbi          => "The specified DBI was changed unexpectedly",
            LmdbErr.Problem         => "Unexpected problem",
            _                       => $"Unknown LMDB error ({(int)err})",
        };
    }
}

public class LmdbException : Exception
{
    public LmdbErr ErrorCode { get; }

    public LmdbException(LmdbErr err)
        : base($"{Err.Message(err)} [rc={(int)err}]") => ErrorCode = err;

    public LmdbException(LmdbErr err, string detail)
        : base($"{Err.Message(err)} [rc={(int)err}]: {detail}") => ErrorCode = err;
}
