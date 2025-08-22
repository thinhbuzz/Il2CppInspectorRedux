using VersionedSerialization;

namespace Il2CppInspector.Next.Metadata;

public struct GenericContainerIndex(int value) : IReadable, IEquatable<GenericContainerIndex>
{
    public const string TagPrefix = nameof(GenericContainerIndex); 

    private int _value = value;

    public static implicit operator int(GenericContainerIndex idx) => idx._value;
    public static implicit operator GenericContainerIndex(int idx) => new(idx);

    public static int Size(in StructVersion version = default, bool is32Bit = false)
    {
        if (version >= MetadataVersions.V380
            && version.Tag != null
            && version.Tag.Contains(TagPrefix)
            && !version.Tag.Contains($"{TagPrefix}4"))
        {
            if (version.Tag.Contains($"{TagPrefix}2"))
                return sizeof(ushort);

            if (version.Tag.Contains($"{TagPrefix}1"))
                return sizeof(byte);
        }

        return sizeof(int);
    }

    public void Read<TReader>(ref TReader reader, in StructVersion version = default) where TReader : IReader, allows ref struct
    {
        if (version >= MetadataVersions.V380
            && version.Tag != null
            && version.Tag.Contains(TagPrefix)
            && !version.Tag.Contains($"{TagPrefix}4"))
        {
            if (version.Tag.Contains($"{TagPrefix}2"))
            {
                _value = reader.ReadPrimitive<short>();
                _value = _value == ushort.MaxValue ? -1 : _value;
                return;
            }

            if (version.Tag.Contains($"{TagPrefix}1"))
            {
                _value = reader.ReadPrimitive<byte>();
                _value = _value == byte.MaxValue ? -1 : _value;
                return;
            }
        }

        _value = reader.ReadPrimitive<int>();
    }

    #region Equality operators + ToString

    public static bool operator ==(GenericContainerIndex left, GenericContainerIndex right)
        => left._value == right._value;

    public static bool operator !=(GenericContainerIndex left, GenericContainerIndex right)
        => !(left == right);

    public readonly override bool Equals(object? obj)
        => obj is GenericContainerIndex other && Equals(other);

    public readonly bool Equals(GenericContainerIndex other)
        => this == other;

    public readonly override int GetHashCode()
        => HashCode.Combine(_value);

    public readonly override string ToString() => _value.ToString();

    #endregion
}