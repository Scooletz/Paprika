﻿using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paprika.Pages;

/// <summary>
/// Represents an in-page map, responsible for storing items and information related to them.
/// </summary>
/// <remarks>
/// The map is fixed in since as it's page dependent, hence the name.
/// It is a modified version of a slot array, that does not externalize slot indexes.
///
/// It keeps an internal map, now implemented with a not-the-best loop over slots.
/// With the use of key prefix, it should be small enough and fast enough for now.
/// </remarks>
public readonly ref struct FixedMap
{
    public const int MinSize = AllocationGranularity * 3;

    private const int AllocationGranularity = 8;
    private const int ItemLengthLength = 2;

    private readonly ref Header _header;
    private readonly Span<byte> _data;
    private readonly Span<Slot> _slots;
    private readonly Span<byte> _raw;

    public FixedMap(Span<byte> buffer)
    {
        _raw = buffer;
        _header = ref Unsafe.As<byte, Header>(ref _raw[0]);
        _data = buffer.Slice(Header.Size);
        _slots = MemoryMarshal.Cast<byte, Slot>(_data);
    }

    public bool TrySet(NibblePath key, ReadOnlySpan<byte> data)
    {
        if (TryGetImpl(key, out var existingData, out var index))
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

        var hash = Slot.ExtractPrefix(key, out key);
        var encodedKey = key.WriteTo(stackalloc byte[key.MaxByteLength]);

        // does not exist yet, calculate total memory needed
        var total = GetTotalSpaceRequired(encodedKey, data);

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
        ref var id = ref _slots[at / Slot.Size];

        // write slot
        id.Prefix = hash;
        id.ItemAddress = (ushort)(_data.Length - _header.High - total);

        // write item, first length, then key, then data
        var dest = _data.Slice(id.ItemAddress, total);

        WriteEntryLength(dest, (ushort)(encodedKey.Length + data.Length));

        encodedKey.CopyTo(dest.Slice(ItemLengthLength));
        data.CopyTo(dest.Slice(ItemLengthLength + encodedKey.Length));

        // commit low and high
        _header.Low += Slot.Size;
        _header.High += total;
        return true;
    }

    /// <summary>
    /// Counts values according to specified buckets.
    /// </summary>
    public void CountValuesInBuckets(int count)
    {
        Span<ushort> buckets = stackalloc ushort[count];

        var mod = buckets.Length;

        var to = _header.Low / Slot.Size;
        for (var i = 0; i < to; i++)
        {
            ref readonly var slot = ref _slots[i];
            if (slot.IsDeleted == false)
            {
                buckets[slot.Prefix % mod]++;
            }
        }

        var maxI = 0;

        for (int i = 1; i < count; i++)
        {
            if (buckets[i] > buckets[maxI])
            {
                maxI = i;
            }
        }

        // maxI represents the biggest by count bucket now
    }

    private static ushort GetTotalSpaceRequired(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        return (ushort)(key.Length + data.Length + ItemLengthLength);
    }

    public bool Delete(NibblePath key)
    {
        if (TryGetImpl(key, out _, out var index))
        {
            DeleteImpl(index);
            return true;
        }

        return false;
    }

    private void DeleteImpl(int index)
    {
        // mark as deleted first
        _slots[index].IsDeleted = true;
        _header.Deleted++;

        // always try to compact after delete
        CollectTombstones();
    }

    private void Deframent()
    {
        // s as data were fitting before, the will fit after so all the checks can be skipped
        var size = _raw.Length;
        var array = ArrayPool<byte>.Shared.Rent(size);
        var span = array.AsSpan(0, size);

        span.Clear();
        var copy = new FixedMap(span);
        var count = _header.Low / Slot.Size;

        for (int i = 0; i < count; i++)
        {
            var copyFrom = _slots[i];
            if (copyFrom.IsDeleted == false)
            {
                var source = _data.Slice(copyFrom.ItemAddress);
                var length = ReadDataLength(source);
                var fromSpan = source.Slice(0, length + ItemLengthLength);

                ref var copyTo = ref copy._slots[copy._header.Low / Slot.Size];

                // copy raw, no decoding
                var high = (ushort)(copy._data.Length - copy._header.High - fromSpan.Length);
                fromSpan.CopyTo(copy._data.Slice(high));

                copyTo.Prefix = copyFrom.Prefix;
                copyTo.ItemAddress = high;

                copy._header.Low += Slot.Size;
                copy._header.High = (ushort)(copy._header.High + fromSpan.Length);
            }
        }

        // finalize by coping over to this
        span.CopyTo(_raw);

        ArrayPool<byte>.Shared.Return(array);
        Debug.Assert(copy._header.Deleted == 0, "All deleted should be gone");
    }

    /// <summary>
    /// Collects tombstones of entities that used to be. 
    /// </summary>
    private void CollectTombstones()
    {
        // start with the last written and perform checks and cleanup till all the deleted are gone
        var index = _header.Low / Slot.Size - 1;

        while (index >= 0 && _slots[index].IsDeleted)
        {
            // undo writing low
            _header.Low -= Slot.Size;

            // undo writing high
            var slice = _data.Slice(_slots[index].ItemAddress);
            var total = ReadDataLength(slice) + ItemLengthLength;
            _header.High = (ushort)(_header.High - total);

            // cleanup
            _slots[index] = default;
            _header.Deleted--;

            // move back by one to see if it's deleted as well
            index--;
        }
    }

    private static void WriteEntryLength(Span<byte> dest, ushort dataRequiredWithNoLength) =>
        BinaryPrimitives.WriteUInt16LittleEndian(dest, dataRequiredWithNoLength);

    private static ushort ReadDataLength(Span<byte> source) => BinaryPrimitives.ReadUInt16LittleEndian(source);

    public bool TryGet(NibblePath key, out ReadOnlySpan<byte> data)
    {
        if (TryGetImpl(key, out var span, out _))
        {
            data = span;
            return true;
        }

        data = default;
        return false;
    }

    private bool TryGetImpl(NibblePath key, out Span<byte> data, out int slotIndex)
    {
        var hash = Slot.ExtractPrefix(key, out key);
        var encodedKey = key.WriteTo(stackalloc byte[key.MaxByteLength]);

        // TODO: maybe replace with
        // var cast = MemoryMarshal.Cast<Slot, ushort>(_slots.Slice(_header.Low));
        // cast.IndexOf(hash);

        var to = _header.Low / Slot.Size;
        for (int i = 0; i < to; i++)
        {
            ref readonly var slot = ref _slots[i];
            if (slot.IsDeleted == false &&
                slot.Prefix == hash)
            {
                var slice = _data.Slice(slot.ItemAddress);
                var dataLength = ReadDataLength(slice);
                var actual = slice.Slice(2, dataLength);

                // The StartsWith check assumes that all the keys have the same length.
                if (actual.StartsWith(encodedKey))
                {
                    data = actual.Slice(encodedKey.Length);
                    slotIndex = i;
                    return true;
                }
            }
        }

        data = default;
        slotIndex = default;
        return false;
    }

    [StructLayout(LayoutKind.Explicit, Size = Size)]
    private struct Slot
    {
        public const int Size = 4;

        // ItemAddress
        private const ushort AddressMask = Page.PageSize - 1;

        // IsDeleted
        private const int DeletedShift = 15;
        private const ushort DeletedBit = 1 << DeletedShift;

        /// <summary>
        /// The address of this item.
        /// </summary>
        public ushort ItemAddress
        {
            get => (ushort)(Raw & AddressMask);
            set => Raw = (ushort)((Raw & ~AddressMask) | value);
        }

        /// <summary>
        /// Whether it's deleted
        /// </summary>
        public bool IsDeleted
        {
            get => (Raw & DeletedBit) == DeletedBit;
            set => Raw = (ushort)((Raw & ~DeletedBit) | (value ? DeletedBit : 0));
        }

        [FieldOffset(0)] private ushort Raw;

        /// <summary>
        /// The memorized result of <see cref="ExtractPrefix"/> of this item.
        /// </summary>
        [FieldOffset(2)] public ushort Prefix;

        /// <summary>
        /// Builds the hash for the key.
        /// </summary>
        public static ushort ExtractPrefix(NibblePath key, out NibblePath rest)
        {
            rest = key.SliceFrom(4);
            return key.GetFirst4Nibbles();
        }

        public override string ToString()
        {
            return $"{nameof(Prefix)}: {Prefix}, {nameof(ItemAddress)}: {ItemAddress}, {nameof(IsDeleted)}: {IsDeleted}";
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = Size)]
    private struct Header
    {
        public const int Size = 8;

        /// <summary>
        /// Represents the distance from the start.
        /// </summary>
        [FieldOffset(0)] public ushort Low;

        /// <summary>
        /// Represents the distance from the end.
        /// </summary>
        [FieldOffset(2)] public ushort High;

        /// <summary>
        /// A rough estimates of gaps.
        /// </summary>
        [FieldOffset(4)] public ushort Deleted;

        public ushort Taken => (ushort)(Low + High);
    }
}