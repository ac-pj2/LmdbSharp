// Schema versioning: embeds a version tag in each serialized record so that schema
// evolution is handled gracefully. The default serializer (MemoryPack) includes its
// own versioning via [MemoryPackable(SerializeLayout.Explicit)]. This file adds a
// version-prefix scheme for serializers that don't have built-in versioning (e.g.,
// the JSON serializer).
//
// Wire format: [version:1 byte][payload:N bytes]
//
// Usage:
//   public class UserV1 { ... }
//   public class UserV2 : UserV1 { public string Avatar { get; set; } }
//
//   var serializer = new VersionedSerializer<UserV2>(migrateFromV1);

namespace Lmdb.Objects;

/// <summary>Serializer wrapper that prepends a 1-byte version tag. On deserialize,
/// reads the version and passes it to the migrator if the version differs from current.</summary>
public sealed class VersionedSerializer<T> : IObjectSerializer<T>
{
    private readonly byte _currentVersion;
    private readonly IObjectSerializer<T> _inner;
    private readonly Dictionary<byte, Func<byte[], T>>? _migrators;

    /// <param name="currentVersion">The version tag written for new records.</param>
    /// <param name="inner">The serializer for the current version's type.</param>
    /// <param name="migrators">Optional: per-old-version migration functions that take
    /// the raw payload bytes (after the version tag) and return a T.</param>
    public VersionedSerializer(byte currentVersion, IObjectSerializer<T> inner,
        Dictionary<byte, Func<byte[], T>>? migrators = null)
    {
        _currentVersion = currentVersion;
        _inner = inner;
        _migrators = migrators;
    }

    public void Serialize(T value, System.Buffers.IBufferWriter<byte> writer)
    {
        writer.GetSpan(1)[0] = _currentVersion;
        writer.Advance(1);
        _inner.Serialize(value, writer);
    }

    public T Deserialize(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1)
            throw new InvalidDataException("versioned record too short");

        byte version = data[0];
        ReadOnlySpan<byte> payload = data[1..];

        if (version == _currentVersion)
            return _inner.Deserialize(payload);

        if (_migrators != null && _migrators.TryGetValue(version, out var migrator))
            return migrator(payload.ToArray());

        throw new InvalidOperationException(
            $"no migrator for schema version {version} (current is {_currentVersion})");
    }
}
