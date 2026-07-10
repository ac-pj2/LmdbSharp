// Key encoding: how to convert .NET key values (long, string, GUID, etc.) to/from
// the byte[] keys stored in LMDB. Each encoding also determines the DatabaseFlags
// for the sub-DB (e.g. INTEGERKEY for numeric types).
using System.Buffers.Binary;
using System.Text;

namespace Lmdb.Objects;

/// <summary>How a collection's primary key is encoded in LMDB.</summary>
public enum KeyType
{
    /// <summary>Auto-incrementing 64-bit integer (default). Stored as 8 bytes LE.</summary>
    AutoLong,
    /// <summary>Long/Int64 key from a [LmdbKey] property.</summary>
    Long,
    /// <summary>String key from a [LmdbKey] property (UTF-8).</summary>
    String,
    /// <summary>Guid key from a [LmdbKey] property (16 bytes).</summary>
    Guid,
}

internal static class KeyEncoding
{
    /// <summary>Encode a key value to bytes.</summary>
    public static byte[] Encode(object key, KeyType keyType) => keyType switch
    {
        KeyType.AutoLong or KeyType.Long => EncodeLong((long)key),
        KeyType.String => Encoding.UTF8.GetBytes((string)key),
        KeyType.Guid => ((Guid)key).ToByteArray(),
        _ => throw new NotSupportedException($"Key type {keyType}"),
    };

    /// <summary>Decode key bytes back to a value.</summary>
    public static object Decode(ReadOnlySpan<byte> data, KeyType keyType) => keyType switch
    {
        KeyType.AutoLong or KeyType.Long => DecodeLong(data),
        KeyType.String => Encoding.UTF8.GetString(data),
        KeyType.Guid => new Guid(data),
        _ => throw new NotSupportedException($"Key type {keyType}"),
    };

    public static byte[] EncodeLong(long v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, v);
        return b;
    }

    public static long DecodeLong(ReadOnlySpan<byte> data)
        => BinaryPrimitives.ReadInt64LittleEndian(data);

    /// <summary>The LMDB DatabaseFlags for a collection sub-DB given its key type.</summary>
    public static DatabaseFlags ToDbFlags(KeyType keyType)
        => keyType == KeyType.AutoLong || keyType == KeyType.Long
            ? DatabaseFlags.IntegerKey : DatabaseFlags.None;
}

/// <summary>Marks the property that holds the primary key for a collection.
/// If absent, the collection auto-generates a long ID and assigns it to an
/// <c>Id</c> (or <c>id</c>) property.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class LmdbKeyAttribute : Attribute
{
}
