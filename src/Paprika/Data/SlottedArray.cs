using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Paprika.Store;
using Paprika.Utils;

namespace Paprika.Data;

/// <summary>
/// Represents an in-page map, responsible for storing items and information related to them.
/// Allows for efficient nibble enumeration so that if a subset of items should be extracted, it's easy to do so.
/// </summary>
/// <remarks>
/// The map is fixed in since as it's page dependent, hence the name.
/// It is a modified version of a slot array, that does not externalize slot indexes.
///
/// It keeps an internal map, now implemented with a not-the-best loop over slots.
/// With the use of key prefix, it should be small enough and fast enough for now.
/// </remarks>
public readonly ref struct SlottedArray
{
    private readonly ref Header _header;
    private readonly Span<byte> _data;

    public SlottedArray(Span<byte> buffer)
    {
        _header = ref Unsafe.As<byte, Header>(ref MemoryMarshal.GetReference(buffer));
        _data = buffer.Slice(Header.Size);
    }

    private readonly ref Slot this[int index]
    {
        get
        {
            var offset = index * Slot.Size;
            if (offset >= _data.Length - Slot.Size)
            {
                ThrowIndexOutOfRangeException();
            }

            return ref Unsafe.As<byte, Slot>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_data), offset));

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowIndexOutOfRangeException()
            {
                throw new IndexOutOfRangeException();
            }
        }
    }

    public bool TrySet(in NibblePath key, ReadOnlySpan<byte> data)
    {
        var hash = Slot.PrepareKey(key, out var preamble, out var trimmed);

        if (TryGetImpl(trimmed, hash, preamble, out var existingData, out var index))
        {
            // same size, copy in place
            if (data.Length == existingData.Length)
            {
                data.CopyTo(existingData);
                return true;
            }

            // cannot reuse, delete existing and add again
            DeleteImpl(index);
        }

        // does not exist yet, calculate total memory needed
        var total = GetTotalSpaceRequired(preamble, trimmed, data);

        if (_header.Taken + total + Slot.Size > _data.Length)
        {
            if (_header.Deleted == 0)
            {
                // nothing to reclaim
                return false;
            }

            // there are some deleted entries, run defragmentation of the buffer and try again
            Deframent();

            // re-evaluate again
            if (_header.Taken + total + Slot.Size > _data.Length)
            {
                // not enough memory
                return false;
            }
        }

        var at = _header.Low;
        ref var slot = ref this[at / Slot.Size];

        // write slot
        slot.Hash = hash;
        slot.KeyPreamble = preamble;
        slot.ItemAddress = (ushort)(_data.Length - _header.High - total);

        // write item: length_key, key, data
        var dest = _data.Slice(slot.ItemAddress, total);

        if (HasKeyBytes(preamble))
        {
            var dest2 = trimmed.WriteToWithLeftover(dest);
            data.CopyTo(dest2);
        }
        else
        {
            data.CopyTo(dest);
        }

        // commit low and high
        _header.Low += Slot.Size;
        _header.High += (ushort)total;

        return true;
    }

    /// <summary>
    /// Gets how many slots are used in the map.
    /// </summary>
    public int Count => _header.Low / Slot.Size;

    public int CapacityLeft => _data.Length - _header.Taken;
    public int CapacityTotal => _data.Length;

    public Enumerator EnumerateAll() =>
        new(this);

    public ref struct Enumerator
    {
        [StructLayout(LayoutKind.Sequential, Pack = sizeof(byte), Size = Size)]
        private ref struct Chunk
        {
            public const int Size = 64;

            private byte _start;

            public Span<byte> Span => MemoryMarshal.CreateSpan(ref _start, Size);
        }

        /// <summary>The map being enumerated.</summary>
        private readonly SlottedArray _map;

        /// <summary>The next index to yield.</summary>
        private int _index;

        private Chunk _bytes;
        private Item _current;

        internal Enumerator(SlottedArray map)
        {
            _map = map;
            _index = -1;
        }

        /// <summary>Advances the enumerator to the next element of the span.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            int index = _index + 1;
            var to = _map.Count;

            ref var slot = ref _map[index];

            while (index < to && slot.IsDeleted) // filter out deleted
            {
                // move by 1
                index += 1;
                slot = ref Unsafe.Add(ref slot, 1);
            }

            if (index < to)
            {
                _index = index;
                _current = Build();
                return true;
            }

            return false;
        }

        public Item Current => _current;

        private Item Build()
        {
            ref var slot = ref _map[_index];
            var span = _map.GetSlotPayload(ref slot);
            var key = Slot.UnPrepareKey(span, slot.Hash, slot.KeyPreamble, _bytes.Span, out var data);

            return new Item(key, data, _index);
        }

        public void Dispose()
        {
        }

        public readonly ref struct Item(NibblePath key, ReadOnlySpan<byte> rawData, int index)
        {
            public int Index { get; } = index;
            public NibblePath Key { get; } = key;
            public ReadOnlySpan<byte> RawData { get; } = rawData;
        }

        // a shortcut to not allocate, just copy the enumerator
        public Enumerator GetEnumerator() => this;
    }

    /// <summary>
    /// Tries to move as many items as possible from this map to the destination map.
    /// </summary>
    /// <remarks>
    /// Returns how many items were moved.
    /// </remarks>
    public int MoveTo(in SlottedArray destination)
    {
        var count = 0;

        foreach (var item in EnumerateAll())
        {
            // try copy all, even if one is not copyable the other might
            if (destination.TrySet(item.Key, item.RawData))
            {
                count++;
                Delete(item);
            }
        }

        return count;
    }

    public const int BucketCount = 16;

    /// <summary>
    /// Gets the aggregated count of entries per nibble.
    /// </summary>
    public void GatherCountStatistics(Span<ushort> buckets)
    {
        Debug.Assert(buckets.Length == BucketCount);

        var to = _header.Low / Slot.Size;
        for (var i = 0; i < to; i++)
        {
            ref var slot = ref this[i];

            // extract only not deleted and these which have at least one nibble
            if (slot.IsDeleted == false && slot.HasAtLeastOneNibble)
            {
                buckets[slot.Nibble0Th] += 1;
            }
        }
    }

    private const int KeyLengthLength = 1;

    private static int GetTotalSpaceRequired(byte preamble, in NibblePath key, ReadOnlySpan<byte> data)
    {
        return (HasKeyBytes(preamble) ? KeyLengthLength + key.RawSpanLength : 0) + data.Length;
    }

    /// <summary>
    /// Checks whether the preamble point that the key might have more data.
    /// </summary>
    private static bool HasKeyBytes(byte preamble) => preamble >= Slot.KeyPreambleWithBytes;

    /// <summary>
    /// Warning! This does not set any tombstone so the reader won't be informed about a delete,
    /// just will miss the value.
    /// </summary>
    public bool Delete(in NibblePath key)
    {
        var hash = Slot.PrepareKey(key, out var preamble, out var trimmed);
        if (TryGetImpl(trimmed, hash, preamble, out _, out var index))
        {
            DeleteImpl(index);
            return true;
        }

        return false;
    }

    public void Delete(in Enumerator.Item item) => DeleteImpl(item.Index);

    private void DeleteImpl(int index)
    {
        // mark as deleted first
        this[index].MarkAsDeleted();
        _header.Deleted++;

        // always try to compact after delete
        CollectTombstones();
    }

    private void Deframent()
    {
        // As data were fitting before, the will fit after so all the checks can be skipped
        var size = Header.Size + _data.Length;
        var array = ArrayPool<byte>.Shared.Rent(size);
        var span = array.AsSpan(0, size);

        span.Clear();
        var copy = new SlottedArray(span);
        var count = _header.Low / Slot.Size;

        for (int i = 0; i < count; i++)
        {
            var copyFrom = this[i];
            if (copyFrom.IsDeleted == false)
            {
                var fromSpan = GetSlotPayload(ref this[i]);

                ref var copyTo = ref copy[copy._header.Low / Slot.Size];

                // copy raw, no decoding
                var high = (ushort)(copy._data.Length - copy._header.High - fromSpan.Length);
                fromSpan.CopyTo(copy._data.Slice(high));

                copyTo.Hash = copyFrom.Hash;
                copyTo.ItemAddress = high;
                copyTo.KeyPreamble = copyFrom.KeyPreamble;

                copy._header.Low += Slot.Size;
                copy._header.High = (ushort)(copy._header.High + fromSpan.Length);
            }
        }

        // finalize by coping over to this
        var raw = MemoryMarshal.CreateSpan(ref Unsafe.As<Header, byte>(ref _header), Header.Size + _data.Length);
        span.CopyTo(raw);

        ArrayPool<byte>.Shared.Return(array);
        Debug.Assert(copy._header.Deleted == 0, "All deleted should be gone");
    }

    /// <summary>
    /// Collects tombstones of entities that used to be. 
    /// </summary>
    private void CollectTombstones()
    {
        // start with the last written and perform checks and cleanup till all the deleted are gone
        var index = Count - 1;

        while (index >= 0 && this[index].IsDeleted)
        {
            // undo writing low
            _header.Low -= Slot.Size;

            // undo writing high
            var slice = GetSlotPayload(ref this[index]);
            var total = slice.Length;
            _header.High = (ushort)(_header.High - total);

            // cleanup
            this[index] = default;
            _header.Deleted--;

            // move back by one to see if it's deleted as well
            index--;
        }
    }

    public bool TryGet(scoped in NibblePath key, out ReadOnlySpan<byte> data)
    {
        var hash = Slot.PrepareKey(key, out byte preamble, out var trimmed);
        if (TryGetImpl(trimmed, hash, preamble, out var span, out _))
        {
            data = span.IsEmpty ? ReadOnlySpan<byte>.Empty : MemoryMarshal.CreateReadOnlySpan(ref span[0], span.Length);
            return true;
        }

        data = default;
        return false;
    }

    [OptimizationOpportunity(OptimizationType.CPU,
        "key encoding is delayed but it might be called twice, here + TrySet")]
    private bool TryGetImpl(in NibblePath key, ushort hash, byte preamble, out Span<byte> data, out int slotIndex)
    {
        var to = _header.Low;

        // uses vectorized search, treating slots as a Span<ushort>
        // if the found index is odd -> found a slot to be queried

        const int notFound = -1;
        var span = MemoryMarshal.Cast<byte, ushort>(_data.Slice(0, to));

        var offset = 0;
        int index = span.IndexOf(hash);

        if (index == notFound)
        {
            data = default;
            slotIndex = default;
            return false;
        }

        while (index != notFound)
        {
            // move offset to the given position
            offset += index;

            if ((offset & Slot.HashShiftForSearch) == Slot.HashShiftForSearch)
            {
                var i = offset / 2;

                ref var slot = ref this[i];
                if (slot.IsDeleted == false && slot.KeyPreamble == preamble)
                {
                    var actual = GetSlotPayload(ref slot);

                    if (slot.HasKeyBytes)
                    {
                        if (NibblePath.TryReadFrom(actual, key, out var leftover))
                        {
                            data = leftover;
                            slotIndex = i;
                            return true;
                        }
                    }
                    else
                    {
                        // The key is contained in the hash, all is equal and good to go!
                        data = actual;
                        slotIndex = i;
                        return true;
                    }
                }
            }

            if (index + 1 >= span.Length)
            {
                // the span is empty and there's not place to move forward
                break;
            }

            // move next: ushorts sliced to the next
            // offset moved by 1 to align
            span = span.Slice(index + 1);
            offset += 1;

            // move to next index
            index = span.IndexOf(hash);
        }

        data = default;
        slotIndex = default;
        return false;
    }

    /// <summary>
    /// Gets the payload pointed to by the given slot without the length prefix.
    /// </summary>
    private Span<byte> GetSlotPayload(ref Slot slot)
    {
        // assert whether the slot has a previous, if not use data.length
        var previousSlotAddress = Unsafe.IsAddressLessThan(ref this[0], ref slot)
            ? Unsafe.Add(ref slot, -1).ItemAddress
            : _data.Length;

        var length = previousSlotAddress - slot.ItemAddress;
        return _data.Slice(slot.ItemAddress, length);
    }

    /// <summary>
    /// Exposes <see cref="Slot.PrepareKey"/> for tests only.
    /// </summary>
    public static (ushort hash, byte preamble) HashForTests(in NibblePath key) =>
        (Slot.PrepareKey(key, out var preamble, out _), preamble);

    /// <summary>
    /// The slot is a size of <see cref="Size"/> bytes.
    ///
    /// It consists of two ushort parts,
    /// 1. <see cref="Raw"/> and
    /// 2. <see cref="Hash"/>.
    ///
    /// <see cref="Hash"/> is a result of <see cref="PrepareKey"/> that returns the value to be memoized in a slot. It only 2 bytes so collision may occur.
    /// <see cref="Raw"/> encodes all the metadata related to the slot.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = sizeof(byte), Size = Size)]
    private struct Slot
    {
        public const int Size = 4;

        /// <summary>
        /// The address mask, currently supports 13 bits which allows to address anything withing 8kb. 
        /// </summary>
        private const ushort AddressMask = unchecked((ushort)~KeyPreambleMask);

        /// <summary>
        /// The address of this item.
        /// </summary>
        public ushort ItemAddress
        {
            get => (ushort)(Raw & AddressMask);
            set => Raw = (ushort)((Raw & ~AddressMask) | value);
        }

        /// <summary>
        /// Whether the given entry is deleted or not
        /// </summary>
        public bool IsDeleted => KeyPreamble == KeyPreambleDelete;

        /// <summary>
        /// Marks the slot as deleted
        /// </summary>
        public void MarkAsDeleted() => KeyPreamble = KeyPreambleDelete;

        // Preamble uses all bits that AddressMask does not
        private const ushort KeyPreambleMask = 0b_1110_0000_0000_0000;
        private const ushort KeyPreambleShift = 13;

        // Lengths encoding
        private const byte KeyPreambleLengthShift = 1;
        private const byte KeyPreambleLengthMask = 0b11; // The length mask of the preamble
        private const byte KeyPreambleLengthLessThan4 = 0b00; // The length < 4 and encoded as the lowest nibble.
        private const byte KeyPreambleLengthIs4 = 0b01; // The length < 4 and encoded as the lowest nibble.
        private const byte KeyPreambleLengthIs5OrMore = 0b10; // The length is 5+, so it has nibbles encoded in the map

        private const byte KeyPreambleEmpty = 0b000; // Empty, no key's nibbles encoded.
        private const byte KeyPreambleOddBit = 0b001; // The bit used for odd-starting paths.
        private const byte KeyPreambleDelete = 0b111; // Empty cannot be odd, odd is used as deleted marker.

        public const byte KeyPreambleWithBytes = KeyPreambleLengthIs5OrMore << KeyPreambleLengthShift;

        private const byte KeyPreambleMaxEncodedLength = 4;
        private const byte KeySlice = 2;

        private const int HashByteShift = 8;

        /// <summary>
        /// Only empty will have both, <see cref="KeyPreamble"/> and <see cref="Hash"/> equal to zero.
        /// </summary>
        public bool HasAtLeastOneNibble => (KeyPreamble | Hash) != 0;

        public byte KeyPreamble
        {
            get => (byte)((Raw & KeyPreambleMask) >> KeyPreambleShift);
            set => Raw = (ushort)((Raw & ~KeyPreambleMask) | (value << KeyPreambleShift));
        }

        public byte Nibble0Th => (byte)(Hash >> (HashByteShift + NibblePath.NibbleShift) & 0xF);

        public bool HasKeyBytes => KeyPreamble >= KeyPreambleWithBytes;

        private ushort Raw;

        /// <summary>
        /// Used for vectorized search
        /// </summary>
        public const int HashShiftForSearch = 1;

        /// <summary>
        /// The memorized result of <see cref="PrepareKey"/> of this item.
        /// </summary>
        public ushort Hash;

        public override string ToString()
        {
            return
                $"{nameof(Hash)}: {Hash}, {nameof(ItemAddress)}: {ItemAddress}";
        }

        /// <summary>
        /// Prepares the key for the search. 
        /// </summary>
        public static ushort PrepareKey(in NibblePath key, out byte preamble, out NibblePath trimmed)
        {
            const int shift = NibblePath.NibbleShift;

            var length = key.Length;
            var oddBit = key.Oddity;

            if (length <= KeyPreambleMaxEncodedLength)
            {
                trimmed = NibblePath.Empty;

                switch (length)
                {
                    // produce hashes aligned with NibblePath ordering
                    case 0:
                        preamble = 0; // no oddity for empty
                        return 0;
                    case 1:
                        preamble = (byte)((KeyPreambleLengthLessThan4 << KeyPreambleLengthShift) | oddBit);
                        return (ushort)(key.GetAt(0) << (shift + HashByteShift) | length);
                    case 2:
                        preamble = (byte)((KeyPreambleLengthLessThan4 << KeyPreambleLengthShift) | oddBit);
                        return (ushort)((((key.GetAt(0) << shift) | key.GetAt(1)) << HashByteShift) | length);
                    case 3:
                        preamble = (byte)((KeyPreambleLengthLessThan4 << KeyPreambleLengthShift) | oddBit);
                        return (ushort)((((key.GetAt(0) << shift) | key.GetAt(1)) << HashByteShift) |
                                        (key.GetAt(2) << shift) | length);
                    case 4:
                        preamble = (byte)((KeyPreambleLengthIs4 << KeyPreambleLengthShift) | oddBit);
                        return (ushort)((((key.GetAt(0) << shift) | key.GetAt(1)) << HashByteShift) |
                                        (key.GetAt(2) << shift) | key.GetAt(3));
                }
            }

            // The path is 5 nibbles or longer
            preamble = (byte)((KeyPreambleLengthIs5OrMore << KeyPreambleLengthShift) | oddBit);
            trimmed = key.SliceFrom(KeySlice).SliceTo(length - KeyPreambleMaxEncodedLength);

            // Extract first 4 nibbles as the hash
            return (ushort)((((key.GetAt(0) << shift) | key.GetAt(1)) << HashByteShift) |
                            (key.GetAt(length - 2) << shift) | key.GetAt(length - 1));
        }

        public static NibblePath UnPrepareKey(ReadOnlySpan<byte> input, ushort hash, byte preamble,
            Span<byte> workingSet, out ReadOnlySpan<byte> data)
        {
            const int shift = NibblePath.NibbleShift;

            var odd = preamble & KeyPreambleOddBit;
            var options = (preamble >> KeyPreambleLengthShift) & KeyPreambleLengthMask;

            if (options == KeyPreambleLengthLessThan4)
            {
                var length = hash & 0x000F;
                data = input;

                switch (length)
                {
                    case 0:
                        return NibblePath.Empty;
                    case 1:
                        return NibblePath.Single((byte)(hash >> (shift + HashByteShift)), odd);
                    case 2:
                        workingSet[0] = (byte)(hash >> HashByteShift);
                        return NibblePath.FromKey(workingSet).SliceTo(2).UnsafeMakeOdd(odd);
                    case 3:
                        workingSet[0] = (byte)(hash >> HashByteShift);
                        workingSet[1] = (byte)(hash & 0xFF);
                        return NibblePath.FromKey(workingSet).SliceTo(3).UnsafeMakeOdd(odd);
                }
            }
            else if (options == KeyPreambleLengthIs4)
            {
                data = input;
                workingSet[0] = (byte)(hash >> HashByteShift);
                workingSet[1] = (byte)(hash & 0xFF);
                return NibblePath.FromKey(workingSet).SliceTo(4).UnsafeMakeOdd(odd);
            }

            const int limit = 3;
            var span = workingSet[..limit]; // use only 3 as its only up to 4 nibbles here + odd

            data = NibblePath.ReadFrom(input, out var trimmed);

            workingSet[0] = (byte)(hash >> HashByteShift);
            var prefix =
                NibblePath.FromKey(span).SliceTo(KeySlice)
                    .UnsafeMakeOdd(odd); // moving odd can make move beyond 0th

            var suffixValue = (byte)(hash & 0xFF);
            var suffix = NibblePath.FromKey(MemoryMarshal.CreateSpan(ref suffixValue, 1));

            return prefix.Append(trimmed, suffix, workingSet[limit..]);
        }
    }

    public override string ToString() => $"{nameof(Count)}: {Count}, {nameof(CapacityLeft)}: {CapacityLeft}";

    [StructLayout(LayoutKind.Sequential, Pack = sizeof(byte), Size = Size)]
    private struct Header
    {
        public const int Size = 8;

        /// <summary>
        /// Represents the distance from the start.
        /// </summary>
        public ushort Low;

        /// <summary>
        /// Represents the distance from the end.
        /// </summary>
        public ushort High;

        /// <summary>
        /// A rough estimates of gaps.
        /// </summary>
        public ushort Deleted;

        public ushort Taken => (ushort)(Low + High);
    }
}