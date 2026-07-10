// Serializer abstraction for the object database.
//
// The default uses MemoryPack (fastest C# serializer, streams directly to IBufferWriter).
// Users can plug in any serializer by implementing IObjectSerializer<T>.
using System.Buffers;
using System.Text;

namespace Lmdb.Objects;

/// <summary>Serializes/deserializes objects to/from bytes. Implement this to plug in a
/// custom serializer (JSON, MessagePack, protobuf, etc.).</summary>
public interface IObjectSerializer<T>
{
    /// <summary>Serialize <paramref name="value"/> to <paramref name="writer"/>.
    /// Implementations should write directly to the buffer (no intermediate byte[]).</summary>
    void Serialize(T value, IBufferWriter<byte> writer);

    /// <summary>Deserialize from a span that points into the LMDB memory map.</summary>
    T Deserialize(ReadOnlySpan<byte> data);
}

/// <summary>Default serializer using MemoryPack. Requires T to be annotated with
/// [MemoryPackable]. For types that can't be annotated, register a custom
/// IObjectSerializer<T>.</summary>
public sealed class MemoryPackObjectSerializer<T> : IObjectSerializer<T>
{
    public static readonly MemoryPackObjectSerializer<T> Instance = new();

    public void Serialize(T value, IBufferWriter<byte> writer)
        => MemoryPack.MemoryPackSerializer.Serialize(writer, value);

    public T Deserialize(ReadOnlySpan<byte> data)
        => MemoryPack.MemoryPackSerializer.Deserialize<T>(data)!;
}

/// <summary>Serializer for System.Text.Json (schemaless, no attributes needed).</summary>
public sealed class JsonObjectSerializer<T> : IObjectSerializer<T>
{
    public static readonly JsonObjectSerializer<T> Instance = new();

    public void Serialize(T value, IBufferWriter<byte> writer)
    {
        using var jsonWriter = new System.Text.Json.Utf8JsonWriter(writer);
        System.Text.Json.JsonSerializer.Serialize(jsonWriter, value);
        jsonWriter.Flush();
    }

    public T Deserialize(ReadOnlySpan<byte> data)
        => System.Text.Json.JsonSerializer.Deserialize<T>(data)!;
}
