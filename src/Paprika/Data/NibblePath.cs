using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paprika.Crypto;

namespace Paprika.Data;

/// <summary>
/// Represents a nibble path in a way that makes it efficient for comparisons.
/// </summary>
/// <remarks>
/// The implementation diverges from the Ethereum encoding for extensions or leafs.
/// The divergence is to never perform bit shift of the whole path and always align to byte boundary.
/// If the path starts in on odd nibble, it will include one byte and use only its higher nibble.
/// </remarks>
public readonly ref struct NibblePath
{
    /// <summary>
    /// An array of singles, that can be used to create a path of length 1, both odd and even.
    /// Used by <see cref="Single"/>.
    /// </summary>
    private static ReadOnlySpan<byte> Singles => new byte[]
    {
        0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77,
        0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF
    };

    private static ReadOnlySpan<byte> Duals => new byte[]
    {
        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f,
        0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x1b, 0x1c, 0x1d, 0x1e, 0x1f,
        0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2a, 0x2b, 0x2c, 0x2d, 0x2e, 0x2f,
        0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x3b, 0x3c, 0x3d, 0x3e, 0x3f,
        0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x4b, 0x4c, 0x4d, 0x4e, 0x4f,
        0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5a, 0x5b, 0x5c, 0x5d, 0x5e, 0x5f,
        0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x6b, 0x6c, 0x6d, 0x6e, 0x6f,
        0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x7b, 0x7c, 0x7d, 0x7e, 0x7f,
        0x80, 0x81, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8a, 0x8b, 0x8c, 0x8d, 0x8e, 0x8f,
        0x90, 0x91, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0x9b, 0x9c, 0x9d, 0x9e, 0x9f,
        0xa0, 0xa1, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xab, 0xac, 0xad, 0xae, 0xaf,
        0xb0, 0xb1, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xbb, 0xbc, 0xbd, 0xbe, 0xbf,
        0xc0, 0xc1, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xcb, 0xcc, 0xcd, 0xce, 0xcf,
        0xd0, 0xd1, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xdb, 0xdc, 0xdd, 0xde, 0xdf,
        0xe0, 0xe1, 0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xeb, 0xec, 0xed, 0xee, 0xef,
        0xf0, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa, 0xfb, 0xfc, 0xfd, 0xfe, 0xff
    };

    /// <summary>
    /// Creates a <see cref="NibblePath"/> with length of 1.
    /// </summary>
    /// <param name="nibble">The nibble that should be in the path.</param>
    /// <param name="odd">The oddity.</param>
    /// <returns>The path</returns>
    /// <remarks>
    /// Highly optimized, branchless, just a few moves and adds.
    /// </remarks>
    public static NibblePath Single(byte nibble, int odd)
    {
        Debug.Assert(nibble <= NibbleMask, "Nibble breached the value");
        Debug.Assert(odd <= 1, "Odd should be 1 or 0");

        ref var singles = ref Unsafe.AsRef(ref MemoryMarshal.GetReference(Singles));
        return new NibblePath(ref Unsafe.Add(ref singles, nibble), (byte)odd, 1);
    }

    public static NibblePath Dual(byte @byte)
    {
        ref var duals = ref Unsafe.AsRef(ref MemoryMarshal.GetReference(Duals));
        return new NibblePath(ref Unsafe.Add(ref duals, @byte), 0, 2);
    }

    public const int MaxLengthValue = byte.MaxValue / 2 + 2;
    public const int NibblePerByte = 2;
    public const int NibbleShift = 8 / NibblePerByte;
    public const int NibbleMask = 15;

    private const int LengthShift = 1;
    private const int PreambleLength = 1;
    private const int OddBit = 1;

    public readonly byte Length;
    private readonly ref byte _span;
    private readonly byte _odd;

    public bool IsOdd => _odd == OddBit;
    public int Oddity => _odd;

    public static NibblePath Empty => default;

    public bool IsEmpty => Length == 0;

    public static NibblePath Parse(string hex)
    {
        var nibbles = new byte[(hex.Length + 1) / 2];
        var path = FromKey(nibbles).SliceTo(hex.Length);

        for (var i = 0; i < hex.Length; i++)
        {
            path.UnsafeSetAt(i, 0, byte.Parse(hex.AsSpan(i, 1), NumberStyles.HexNumber));
        }

        return path;
    }

    public static NibblePath FromKey(ReadOnlySpan<byte> key, int nibbleFrom = 0)
    {
        var count = key.Length * NibblePerByte;
        return new NibblePath(key, nibbleFrom, count - nibbleFrom);
    }

    public static NibblePath FromKey(ReadOnlySpan<byte> key, int nibbleFrom, int length)
    {
        return new NibblePath(key, nibbleFrom, length);
    }

    /// <summary>
    /// Creates a nibble path from raw nibbles (a byte per nibble), using the <paramref name="workingSet"/> as the memory to use.
    /// </summary>
    public static NibblePath FromRawNibbles(ReadOnlySpan<byte> nibbles, Span<byte> workingSet)
    {
        var span = workingSet.Slice(0, (nibbles.Length + 1) / 2);
        var copy = new NibblePath(span, 0, nibbles.Length);

        for (int i = 0; i < nibbles.Length; i++)
        {
            copy.UnsafeSetAt(i, 0, nibbles[i]);
        }

        return copy;
    }

    /// <summary>
    /// Reuses the memory of this nibble path moving it to odd position.
    /// </summary>
    public void UnsafeMakeOdd()
    {
        Debug.Assert(_odd == 0, "Should not be applied to odd");

        var i = (int)Length;
        if (i == 1)
        {
            _span = (byte)(_span >> NibbleShift);
        } 
        else if (i <= 4)
        {
            ref ushort u = ref Unsafe.As<byte,ushort>(ref _span);
            var s = BinaryPrimitives.ReverseEndianness(u);
            if (i == 4)
            {
                var overflow = (byte)((s & 0xf) << 4);
                Unsafe.Add(ref _span, 2) = overflow;
            }
            s >>= NibbleShift;
            u = BinaryPrimitives.ReverseEndianness(s);
        }
        else
        {
            LargeUnsafeMakeOdd();
        }
        Unsafe.AsRef(in _odd) = OddBit;

    }

    private void LargeUnsafeMakeOdd()
    {
        for (var i = (int)Length; i > 0; i--)
        {
            UnsafeSetAt(i, 0, GetAt(i - 1));
        }
    }

    /// <summary>
    /// Creates the nibble path from preamble and raw slice
    /// </summary>
    public static NibblePath FromRaw(byte preamble, ReadOnlySpan<byte> slice)
    {
        return new NibblePath(slice, preamble & OddBit, preamble >> LengthShift);
    }

    /// <summary>
    /// </summary>
    /// <param name="key"></param>
    /// <param name="nibbleFrom"></param>
    /// <returns>
    /// The Keccak needs to be "in" here, as otherwise a copy would be create and the ref
    /// would point to a garbage memory.
    /// </returns>
    public static NibblePath FromKey(in Keccak key, int nibbleFrom = 0)
    {
        var count = Keccak.Size * NibblePerByte;
        return new NibblePath(key.BytesAsSpan, nibbleFrom, count - nibbleFrom);
    }

    /// <summary>
    /// Returns the underlying payload as <see cref="Keccak"/>.
    /// It does it in an unsafe way and requires an external check whether it's possible.
    /// </summary>
    public ref Keccak UnsafeAsKeccak => ref Unsafe.As<byte, Keccak>(ref _span);

    private NibblePath(ReadOnlySpan<byte> key, int nibbleFrom, int length)
    {
        _span = ref Unsafe.Add(ref MemoryMarshal.GetReference(key), nibbleFrom / 2);
        _odd = (byte)(nibbleFrom & OddBit);
        Length = (byte)length;
    }

    private NibblePath(ref byte span, byte odd, byte length)
    {
        _span = ref span;
        _odd = odd;
        Length = length;
    }

    /// <summary>
    /// The estimate of the max length, used for stackalloc estimations.
    /// </summary>
    public int MaxByteLength => Length / 2 + 2;

    public const int KeccakNibbleCount = Keccak.Size * NibblePerByte;

    public const int FullKeccakByteLength = Keccak.Size + 2;

    /// <summary>
    /// Writes the nibble path into the destination.
    /// </summary>
    /// <param name="destination">The destination to write to.</param>
    /// <returns>The leftover that other writers can write to.</returns>
    public Span<byte> WriteToWithLeftover(Span<byte> destination)
    {
        var length = WriteImpl(destination);
        return destination.Slice(length);
    }

    /// <summary>
    /// Writes the nibbles to the destination.  
    /// </summary>
    /// <param name="destination"></param>
    /// <returns>The actual bytes written.</returns>
    public Span<byte> WriteTo(Span<byte> destination)
    {
        var length = WriteImpl(destination);
        return destination.Slice(0, length);
    }

    public byte RawPreamble => (byte)((_odd & OddBit) | (Length << LengthShift));

    private int WriteImpl(Span<byte> destination)
    {
        var odd = _odd & OddBit;
        var length = GetSpanLength(Length, _odd);

        destination[0] = (byte)(odd | (Length << LengthShift));

        if (!Unsafe.AreSame(ref _span, ref Unsafe.Add(ref MemoryMarshal.GetReference(destination), PreambleLength)))
        {
            MemoryMarshal.CreateSpan(ref _span, length).CopyTo(destination.Slice(PreambleLength));
        }

        // clearing the oldest nibble, if needed
        // yes, it can be branch free
        if (((odd + Length) & 1) == 1)
        {
            ref var oldest = ref destination[length];
            oldest = (byte)(oldest & 0b1111_0000);
        }

        return length + PreambleLength;
    }

    /// <summary>
    /// Slices the beginning of the nibble path as <see cref="Span{T}.Slice(int)"/> does.
    /// </summary>
    public NibblePath SliceFrom(int start)
    {
        Debug.Assert(Length - start >= 0, "Path out of boundary");

        if (Length - start == 0)
            return Empty;

        return new(ref Unsafe.Add(ref _span, (_odd + start) / 2),
            (byte)((start & 1) ^ _odd), (byte)(Length - start));
    }

    /// <summary>
    /// Trims the end of the nibble path so that it gets to the specified length.
    /// </summary>
    public NibblePath SliceTo(int length)
    {
        Debug.Assert(length <= Length, "Cannot slice the NibblePath beyond its Length");
        return new NibblePath(ref _span, _odd, (byte)length);
    }

    public byte this[int nibble] => GetAt(nibble);

    public byte GetAt(int nibble) => (byte)((GetRefAt(nibble) >> GetShift(nibble)) & NibbleMask);

    private int GetShift(int nibble) => (1 - ((nibble + _odd) & OddBit)) * NibbleShift;

    /// <summary>
    /// Sets a <paramref name="value"/> of the nibble at the given <paramref name="nibble"/> location.
    /// This is unsafe. Use only for owned memory. 
    /// </summary>
    private void UnsafeSetAt(int nibble, byte countOdd, byte value)
    {
        ref var b = ref GetRefAt(nibble);
        var shift = GetShift(nibble + countOdd);
        var mask = NibbleMask << shift;

        b = (byte)((b & ~mask) | (value << shift));
    }

    private ref byte GetRefAt(int nibble) => ref Unsafe.Add(ref _span, (nibble + _odd) / 2);

    /// <summary>
    /// Appends a <paramref name="nibble"/> to the end of the path,
    /// using the <paramref name="workingSet"/> as the underlying memory for the new new <see cref="NibblePath"/>.
    /// </summary>
    /// <remarks>
    /// The copy is required as the original path can be based on the readonly memory.
    /// </remarks>
    /// <returns>The newly copied nibble path.</returns>
    public NibblePath AppendNibble(byte nibble, Span<byte> workingSet)
    {
        if (workingSet.Length < MaxByteLength)
        {
            ThrowNotEnoughMemory();
        }

        // TODO: do a ref comparison with Unsafe, if the same, no need to copy!
        WriteTo(workingSet);

        var appended = new NibblePath(ref workingSet[PreambleLength], _odd, (byte)(Length + 1));
        appended.UnsafeSetAt(Length, 0, nibble);
        return appended;
    }

    /// <summary>
    /// Appends the <see cref="other"/> path using the <paramref name="workingSet"/> as the working memory.
    /// </summary>
    public NibblePath Append(scoped in NibblePath other, Span<byte> workingSet)
    {
        if (workingSet.Length <= MaxByteLength)
        {
            ThrowNotEnoughMemory();
        }

        // TODO: do a ref comparison with Unsafe, if the same, no need to copy!
        WriteTo(workingSet);

        var appended = new NibblePath(ref workingSet[PreambleLength], _odd, (byte)(Length + other.Length));
        for (int i = 0; i < other.Length; i++)
        {
            appended.UnsafeSetAt(Length + i, 0, other[i]);
        }

        return appended;
    }

    /// <summary>
    /// Appends the <see cref="other1"/> and then <see cref="other2"/> path using the <paramref name="workingSet"/> as the working memory.
    /// </summary>
    public NibblePath Append(scoped in NibblePath other1, scoped in NibblePath other2, Span<byte> workingSet)
    {
        if (workingSet.Length <= MaxByteLength)
        {
            ThrowNotEnoughMemory();
        }

        // TODO: do a ref comparison with Unsafe, if the same, no need to copy!
        WriteTo(workingSet);

        var appended = new NibblePath(ref workingSet[PreambleLength], _odd, (byte)(Length + other1.Length + other2.Length));

        for (var i = 0; i < other1.Length; i++)
        {
            appended.UnsafeSetAt(Length + i, 0, other1[i]);
        }

        for (var i = 0; i < other2.Length; i++)
        {
            appended.UnsafeSetAt(Length + other1.Length + i, 0, other2[i]);
        }

        return appended;
    }

    public byte FirstNibble => (byte)((_span >> ((1 - _odd) * NibbleShift)) & NibbleMask);

    private static int GetSpanLength(byte length, int odd) => (length + 1 + odd) / 2;

    /// <summary>
    /// Gets the raw underlying span behind the path, removing the odd encoding.
    /// </summary>
    public ReadOnlySpan<byte> RawSpan => MemoryMarshal.CreateSpan(ref _span, RawSpanLength);

    public int RawSpanLength => GetSpanLength(Length, _odd);

    public static ReadOnlySpan<byte> ReadFrom(ReadOnlySpan<byte> source, out NibblePath nibblePath)
    {
        var b = source[0];

        var odd = OddBit & b;
        var length = (byte)(b >> LengthShift);

        nibblePath = new NibblePath(source.Slice(PreambleLength), odd, length);

        return source.Slice(PreambleLength + GetSpanLength(length, odd));
    }

    public static Span<byte> ReadFrom(Span<byte> source, out NibblePath nibblePath)
    {
        var b = source[0];

        var odd = OddBit & b;
        var length = (byte)(b >> LengthShift);

        nibblePath = new NibblePath(source.Slice(PreambleLength), odd, length);

        return source.Slice(PreambleLength + GetSpanLength(length, odd));
    }

    public static bool TryReadFrom(Span<byte> source, in NibblePath expected, out Span<byte> leftover)
    {
        if (source[0] != expected.RawPreamble)
        {
            leftover = default;
            return false;
        }

        leftover = ReadFrom(source, out var actualKey);
        return actualKey.Equals(expected);
    }

    public int FindFirstDifferentNibble(in NibblePath other)
    {
        var length = Math.Min(other.Length, Length);
        if (length == 0)
        {
            // special case, empty is different at zero
            return 0;
        }

        if (_odd == other._odd)
        {
            // The most common case in Trie.
            // As paths will start on the same level, the odd will be encoded same way for them.
            // This means that an unrolled version can be used.

            ref var left = ref _span;
            ref var right = ref other._span;

            var position = 0;
            var isOdd = (_odd & OddBit) != 0;
            if (isOdd)
            {
                // This means first byte is not a whole byte
                if ((left & NibbleMask) != (right & NibbleMask))
                {
                    // First nibble differs
                    return 0;
                }

                // Equal so start comparing at next byte
                position = 1;
            }

            // Byte length is half of the nibble length
            var byteLength = length / 2;
            if (!isOdd && ((length & 1) > 0))
            {
                // If not isOdd, but the length is odd, then we need to add one more byte
                byteLength += 1;
            }

            var leftSpan = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref left, position), byteLength);
            var rightSpan = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref right, position), byteLength);
            var divergence = leftSpan.CommonPrefixLength(rightSpan);

            position += divergence * 2;
            if (divergence == leftSpan.Length)
            {
                // Remove the extra nibble that made it up to a full byte, if added.
                return Math.Min(length, position);
            }

            // Check which nibble is different
            if ((leftSpan[divergence] & 0xf0) == (rightSpan[divergence] & 0xf0))
            {
                // Are equal, so the next nibble is the one that differs
                return position + 1;
            }

            return position;
        }

        return Fallback(in this, in other, length);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int Fallback(in NibblePath @this, in NibblePath other, int length)
        {
            // fallback, the slow path version to make the method work in any case
            int i = 0;
            for (; i < length; i++)
            {
                if (@this.GetAt(i) != other.GetAt(i))
                {
                    return i;
                }
            }

            return length;
        }
    }

    private const int HexPreambleLength = 1;

    public int HexEncodedLength => Length / NibblePerByte + HexPreambleLength;

    private const byte OddFlag = 0x10;
    private const byte LeafFlag = 0x20;

    public void HexEncode(Span<byte> destination, bool isLeaf)
    {
        destination[0] = (byte)(isLeaf ? LeafFlag : 0x000);

        // This is the usual fast path for leaves, as they are aligned with oddity and length.
        // length: odd, odd: 1
        // length: even, odd: 0
        if ((Length & OddBit) == _odd)
        {
            if (_odd == OddBit)
            {
                // store odd
                destination[0] += (byte)(OddFlag + (_span & NibbleMask));
            }

            // copy the rest as is
            MemoryMarshal.CreateSpan(ref Unsafe.Add(ref _span, _odd), Length / 2)
                .CopyTo(destination.Slice(HexPreambleLength));

            return;
        }

        // this cases should happen only on extensions, as leafs are aligned to the end of the key.
        ref var b = ref _span;

        // length: even, odd: 1
        if (_odd == OddBit)
        {
            // the length is even, no need to amend destination[0]
            for (var i = 0; i < Length / 2; i++)
            {
                destination[i + 1] = (byte)(((b & NibbleMask) << NibbleShift) |
                                            ((Unsafe.Add(ref b, 1) >> NibbleShift) & NibbleMask));
                b = ref Unsafe.Add(ref b, 1);
            }

            return;
        }

        // length: odd, odd: 0
        if ((Length & OddBit) == OddBit)
        {
            destination[0] += (byte)(OddFlag + ((b >> NibbleShift) & NibbleMask));

            // the length is even, no need to amend destination[0]
            for (var i = 0; i < Length / 2; i++)
            {
                destination[i + 1] = (byte)(((b & NibbleMask) << NibbleShift) |
                                            ((Unsafe.Add(ref b, 1) >> NibbleShift) & NibbleMask));
                b = ref Unsafe.Add(ref b, 1);
            }

            return;
        }


        throw new Exception("WRONG!");
    }

    public override string ToString()
    {
        if (Length == 0)
            return "";

        Span<char> path = stackalloc char[Length];
        ref var ch = ref path[0];

        for (int i = _odd; i < Length + _odd; i++)
        {
            var b = Unsafe.Add(ref _span, i / 2);
            var nibble = (b >> ((1 - (i & OddBit)) * NibbleShift)) & NibbleMask;

            ch = Hex[nibble];
            ch = ref Unsafe.Add(ref ch, 1);
        }

        return new string(path);
    }

    private static readonly char[] Hex = "0123456789ABCDEF".ToArray();

    public bool Equals(in NibblePath other)
    {
        if (other.Length != Length || (other._odd & OddBit) != (_odd & OddBit))
            return false;

        return FindFirstDifferentNibble(other) == Length;
    }

    public override int GetHashCode()
    {
        if (Length <= 1)
        {
            // for a single nibble path, make it different from empty.
            return Length == 0 ? 0 : 1 << GetAt(0);
        }

        unchecked
        {
            ref var span = ref _span;

            uint hash = (uint)Length << 24;
            nuint length = Length;

            if (_odd == OddBit)
            {
                // mix in first half
                hash |= (uint)(_span & 0x0F) << 20;
                span = ref Unsafe.Add(ref span, 1);
                length -= 1;
            }

            if (length % 2 == 1)
            {
                // mix in
                hash |= (uint)GetAt((int)length - 1) << 16;
                length -= 1;
            }

            Debug.Assert(length % 2 == 0, "Length should be even here");

            length /= 2; // make it byte

            // 8 bytes
            if (length >= sizeof(long))
            {
                nuint offset = 0;
                nuint longLoop = length - sizeof(long);
                if (longLoop != 0)
                {
                    do
                    {
                        hash = BitOperations.Crc32C(hash,
                            Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref span, offset)));
                        offset += sizeof(long);
                    } while (longLoop > offset);
                }

                // Do final hash as sizeof(long) from end rather than start
                hash = BitOperations.Crc32C(hash, Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref span, longLoop)));

                return (int)hash;
            }

            // 4 bytes
            if (length >= sizeof(int))
            {
                hash = BitOperations.Crc32C(hash, Unsafe.ReadUnaligned<uint>(ref span));
                length -= sizeof(int);
                if (length > 0)
                {
                    // Do final hash as sizeof(long) from end rather than start
                    hash = BitOperations.Crc32C(hash, Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref span, length)));
                }

                return (int)hash;
            }

            // 2 bytes
            if (length >= sizeof(short))
            {
                hash = BitOperations.Crc32C(hash, Unsafe.ReadUnaligned<ushort>(ref span));
                length -= sizeof(short);
                if (length > 0)
                {
                    // Do final hash as sizeof(long) from end rather than start
                    hash = BitOperations.Crc32C(hash, Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref span, length)));
                }

                return (int)hash;
            }

            // 1 byte
            return (int)BitOperations.Crc32C(hash, span);
        }
    }

    public bool HasOnlyZeroes()
    {
        // TODO: optimize
        for (var i = 0; i < Length; i++)
        {
            if (GetAt(i) != 0)
            {
                return false;
            }
        }

        return true;
    }

    [DoesNotReturn]
    [StackTraceHidden]
    static void ThrowNotEnoughMemory()
    {
        throw new ArgumentException("Not enough memory to append");
    }
}
