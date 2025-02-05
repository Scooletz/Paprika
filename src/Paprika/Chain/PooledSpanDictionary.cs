using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paprika.Crypto;
using Paprika.Data;
using Paprika.Store;
using Paprika.Utils;

namespace Paprika.Chain;

/// <summary>
/// The pooled span dictionary uses memory from <see cref="BufferPool"/> to store large chunks of memory with minimal or no allocations.
/// </summary>
public class PooledSpanDictionary : IDisposable
{
    /// <summary>
    /// Gets the size of the address to the next item.
    /// </summary>
    /// <remarks>
    /// Pointer size. Assumes 64 bits.     
    /// </remarks>
    private const int PointerSize = 8;

    private const int BufferSize = BufferPool.BufferSize;

    private readonly BufferPool _pool;
    private readonly bool _preserveOldValues;
    private readonly List<Page> _pages = new();

    private Page _current;
    private int _position;

    private readonly Root _root;

    /// <summary>
    /// Initializes a new pooled dictionary instance.
    /// </summary>
    /// <param name="pool">The pool to take data from and return to.</param>
    /// <param name="preserveOldValues">Whether dictionary should preserve previous values or aggressively reuse memory when possible.</param>
    /// <remarks>
    /// Set <paramref name="preserveOldValues"/> to true, if the data written once should not be overwritten.
    /// This allows to hold values returned by the dictionary through multiple operations.
    /// </remarks>
    public PooledSpanDictionary(BufferPool pool, bool preserveOldValues = false)
    {
        _pool = pool;
        _preserveOldValues = preserveOldValues;

        var pages = new Page[Root.PageCount];
        for (var i = 0; i < Root.PageCount; i++)
        {
            pages[i] = RentNewPage(false);
        }

        ParallelUnbalancedWork.For(0, Root.PageCount, pages, (i, p) =>
        {
            p[i].Clear();
            return p;
        });

        _root = new Root(pages);

        AllocateNewPage();
    }

    public bool TryGet(scoped ReadOnlySpan<byte> key, ulong hash, out ReadOnlySpan<byte> result)
    {
        var (leftover, bucket) = GetBucketAndLeftover(hash);
        var search = TryGetImpl(key, leftover, bucket);
        if (search.IsFound)
        {
            result = search.Data;
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>
    /// The total overhead to write one item.
    /// </summary>
    public const int ItemOverhead = PreambleLength + AddressLength + KeyLengthLength + ValueLengthLength;

    // Preamble
    private const byte PreambleBits = 0b1110_0000;
    private const byte DestroyedBit = 0b1000_0000;
    private const byte MetadataBits = 0b0110_0000;
    private const byte MetadataShift = 5;

    public const int MaxMetadata = MetadataBits >> MetadataShift;

    private const byte Byte0Mask = 0b0001_1111;

    // How many bytes are used for preamble + hash leftover
    private const int PreambleLength = 3;

    private const int AddressLength = PointerSize;

    private const int KeyLengthLength = 1;
    private const int ValueLengthLength = 2;

    private SearchResult TryGetImpl(scoped ReadOnlySpan<byte> key, uint leftover, uint bucket)
    {
        Debug.Assert(BitOperations.LeadingZeroCount(leftover) >= 11, "First 10 bits should be left unused");

        ref var location = ref _root[(int)bucket];

        var address = Volatile.Read(ref location);

        if (address == UIntPtr.Zero) goto NotFound;

        do
        {
            ref var at = ref ReadAtAddress(address);

            var header = at & PreambleBits;
            if ((header & DestroyedBit) == 0)
            {
                // not destroyed, ready to be searched, decode leftover, big endian
                var leftoverStored = GetLeftover(ref at);

                if (leftoverStored == leftover)
                {
                    ref var payload = ref Unsafe.Add(ref at, PreambleLength + AddressLength);

                    var storedKeyLength = (int)payload;
                    if (storedKeyLength == key.Length)
                    {
                        var storedKey = MemoryMarshal.CreateSpan(ref Unsafe.Add(ref payload, KeyLengthLength),
                            storedKeyLength);
                        if (storedKey.SequenceEqual(key))
                        {
                            return new SearchResult(ref at);
                        }
                    }
                }
            }

            // Decode next entry address
            address = Unsafe.ReadUnaligned<UIntPtr>(ref Unsafe.Add(ref at, PreambleLength));
        } while (address != 0);
    NotFound:
        return default;
    }

    private static unsafe ref byte ReadAtAddress(UIntPtr address) => ref Unsafe.AsRef<byte>(address.ToPointer());

    private static (uint leftover, uint bucket) GetBucketAndLeftover(ulong hash)
        => Math.DivRem(Mix(hash), Root.BucketCount);

    private static (uint leftover, uint bucket) GetBucketAndLeftover(uint mixed)
        => Math.DivRem(mixed, Root.BucketCount);

    public void CopyTo(PooledSpanDictionary destination, Predicate<byte> metadataWhere, bool append = false)
    {
        foreach (var kvp in this)
        {
            if (metadataWhere(kvp.Metadata))
            {
                destination.SetImpl(kvp.Key, kvp.Hash, kvp.Value, ReadOnlySpan<byte>.Empty, kvp.Metadata, append);
            }
        }
    }

    public void CopyTo<TAccessor>(PooledSpanDictionary destination, Predicate<byte> metadataWhere, in BitMapFilter<TAccessor> filter, bool append = false)
        where TAccessor : struct, BitMapFilter.IAccessor<TAccessor>
    {
        foreach (var kvp in this)
        {
            if (metadataWhere(kvp.Metadata))
            {
                Key.ReadFrom(kvp.Key, out var key);
                filter.Add(Blockchain.GetHash(key));
                destination.SetImpl(kvp.Key, kvp.Hash, kvp.Value, ReadOnlySpan<byte>.Empty, kvp.Metadata, append);
            }
        }
    }

    private static int GetLeftover(ref byte sliced) =>
        ((sliced & Byte0Mask) << 16) +
        (Unsafe.Add(ref sliced, 1) << 8) +
        Unsafe.Add(ref sliced, 2);

    private readonly ref struct SearchResult
    {
        public bool IsFound => !Unsafe.IsNullRef(ref _header);

        private readonly ref byte _header;
        private readonly ref byte GetData
        {
            get
            {
                ref var payload = ref Unsafe.Add(ref _header, PreambleLength + AddressLength);
                return ref Unsafe.Add(ref payload, KeyLengthLength + payload);
            }
        }

        public SearchResult(ref byte header)
        {
            _header = ref header;
        }

        /// <summary>
        /// The length is encoded as the first byte.
        /// </summary>
        public readonly ReadOnlySpan<byte> Data
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                ref var data = ref GetData;
                return MemoryMarshal.CreateSpan(ref Unsafe.Add(ref data, ValueLengthLength),
                Unsafe.ReadUnaligned<ushort>(ref data));
            }
        }

        public readonly byte Metadata => (byte)((_header & MetadataBits) >> MetadataShift);

        public bool TryUpdateInSitu(ReadOnlySpan<byte> data0, ReadOnlySpan<byte> data1, byte metadata)
        {
            if (IsFound == false)
                return false;

            var length = data0.Length + data1.Length;
            ref var data = ref GetData;
            if (Unsafe.ReadUnaligned<ushort>(ref data) >= length)
            {
                // update metadata bit
                _header = (byte)((_header & ~MetadataBits) | (metadata << MetadataShift));

                // There's the place to have it update in place
                Unsafe.WriteUnaligned(ref data, (ushort)length);

                var destination = MemoryMarshal.CreateSpan(ref Unsafe.Add(ref data, ValueLengthLength), length);
                data0.CopyTo(destination);
                if (data1.IsEmpty == false)
                {
                    data1.CopyTo(destination[data0.Length..]);
                }

                return true;
            }

            return false;
        }

        public void Destroy()
        {
            Debug.Assert(IsFound, "Only found can be destroyed");
            _header |= DestroyedBit;
        }
    }

    public void Set(scoped ReadOnlySpan<byte> key, ulong hash, ReadOnlySpan<byte> data, byte metadata) =>
        SetImpl(key, Mix(hash), data, default, metadata);

    public void Set(scoped ReadOnlySpan<byte> key, ulong hash, ReadOnlySpan<byte> data0, ReadOnlySpan<byte> data1,
        byte metadata) => SetImpl(key, Mix(hash), data0, data1, metadata);

    private void SetImpl(scoped ReadOnlySpan<byte> key, uint mixed, ReadOnlySpan<byte> data0, ReadOnlySpan<byte> data1,
        byte metadata, bool append = false)
    {
        Debug.Assert(metadata <= MaxMetadata, "Metadata size breached");

        var (leftover, bucket) = GetBucketAndLeftover(mixed);

        if (append == false)
        {
            var search = TryGetImpl(key, leftover, bucket);

            if (search.IsFound)
            {
                if (_preserveOldValues == false)
                {
                    if (search.TryUpdateInSitu(data0, data1, metadata))
                        return;
                }

                // Destroy the search as it should not be visible later and move on with inserting as usual
                search.Destroy();
            }
        }

        Debug.Assert(BitOperations.LeadingZeroCount(leftover) >= 10, "First 10 bits should be left unused");

        var dataLength = data1.Length + data0.Length;

        var size = PreambleLength + AddressLength + KeyLengthLength + key.Length + ValueLengthLength + dataLength;
        Span<byte> destination = Write(size, out var address);


        // Write preamble, big endian
        destination[0] = (byte)((leftover >> 16) | (uint)(metadata << MetadataShift));
        destination[1] = (byte)(leftover >> 8);
        destination[2] = (byte)(leftover & 0xFF);

        // Key length
        const int keyStart = PreambleLength + AddressLength;
        destination[keyStart] = (byte)key.Length;

        // Key
        key.CopyTo(destination.Slice(keyStart + KeyLengthLength));

        // Value length
        var valueStart = keyStart + KeyLengthLength + key.Length;

        Unsafe.WriteUnaligned(ref destination[valueStart], (ushort)dataLength);

        data0.CopyTo(destination[(valueStart + ValueLengthLength)..]);
        data1.CopyTo(destination[(valueStart + ValueLengthLength + data0.Length)..]);

        // Write root back to the bucket at the root.
        // The location is a subject of CAS operation and should always be aligned. This is ensured by the root.
        // All the other writes require no alignment as they are not updated with CAS.
        ref var location = ref _root[(int)bucket];

        UIntPtr root;
        var current = Volatile.Read(ref location);

        do
        {
            // copy to the root first
            root = current;

            // write next
            Unsafe.WriteUnaligned(ref destination[PreambleLength], root);

            // try swap remembering current
            current = Interlocked.CompareExchange(ref location, address, root);
        } while (current != root);
    }

    public void Destroy(scoped ReadOnlySpan<byte> key, ulong hash)
    {
        var (leftover, bucket) = GetBucketAndLeftover(hash);
        var found = TryGetImpl(key, leftover, bucket);

        if (found.IsFound)
            found.Destroy();
    }

    public Enumerator GetEnumerator() => new(this);

    /// <summary>
    /// Enumerator walks through all the values beside the ones that were destroyed in this dictionary
    /// with <see cref="PooledSpanDictionary.Destroy"/>.
    /// </summary>
    public unsafe ref struct Enumerator(PooledSpanDictionary dictionary)
    {
        private int _bucket = -1;
        private UIntPtr _address = 0;
        private ref byte _at;

        public bool MoveNext()
        {
            while (_bucket < Root.BucketCount)
            {
                // On empty, scan to the next bucket that is not empty
                while (_address == UIntPtr.Zero)
                {
                    _bucket++;
                    if (_bucket == Root.BucketCount)
                    {
                        return false;
                    }

                    _address = Volatile.Read(ref dictionary._root[_bucket]);
                }

                // Scan the bucket till it's not destroyed
                while (_address != UIntPtr.Zero)
                {
                    // Capture the current, move address to next immediately
                    ref var at = ref ReadAtAddress(_address);

                    // The position is captured in ref at above, move to next
                    _address = Unsafe.ReadUnaligned<UIntPtr>(ref Unsafe.Add(ref at, PreambleLength));

                    if ((at & DestroyedBit) == 0)
                    {
                        // Set at the at as it represents an active item
                        _at = ref at;
                        return true;
                    }
                }
            }

            return false;
        }

        public KeyValue Current => new(ref _at, (uint)_bucket);

        public void Dispose()
        {
            // throw new ex
        }

        public readonly ref struct KeyValue
        {
            private readonly uint _bucket;
            private readonly ref byte _b;

            public ReadOnlySpan<byte> Key
            {
                get
                {
                    // Key length
                    const int keyStart = PreambleLength + AddressLength;
                    ref var start = ref Unsafe.Add(ref _b, keyStart);
                    return MemoryMarshal.CreateSpan(ref Unsafe.Add(ref start, 1), start);
                }
            }

            public ReadOnlySpan<byte> Value
            {
                get
                {
                    // Key length
                    const int keyStart = PreambleLength + AddressLength;
                    var keyLength = Unsafe.Add(ref _b, keyStart);

                    // Get the start to data pointer
                    ref var data = ref Unsafe.Add(ref _b, PreambleLength + AddressLength + KeyLengthLength + keyLength);

                    return MemoryMarshal.CreateSpan(ref Unsafe.Add(ref data, ValueLengthLength),
                        Unsafe.ReadUnaligned<ushort>(ref data));
                }
            }

            public uint Hash => ((uint)GetLeftover(ref _b) << Root.BucketCountLog2) | _bucket;

            public byte Metadata => (byte)((_b & MetadataBits) >> MetadataShift);

            public KeyValue(ref byte b, uint bucket)
            {
                _bucket = bucket;
                _b = ref b;
            }

            public void Destroy()
            {
                _b |= DestroyedBit;
            }
        }
    }

    private void AllocateNewPage()
    {
        var page = RentNewPage(false);
        _current = page;
        _position = 0;
    }

    private Page RentNewPage(bool clear)
    {
        var page = _pool.Rent(clear);
        _pages.Add(page);
        return page;
    }

    private static uint Mix(ulong hash) => unchecked((uint)((hash >> 32) ^ hash));

    private readonly object _lock = new();

    private static int Align(int value, int alignment) => (value + (alignment - 1)) & -alignment;

    private Span<byte> Write(int size, out UIntPtr addr)
    {
        // Memoize to make contention as small as possible
        Page current;
        int position;

        // Align so that we don't step on each other too much.
        size = Align(size, PointerSize);

        lock (_lock)
        {
            if (BufferSize - _position < size)
            {
                // not enough memory
                AllocateNewPage();
            }

            position = _position;
            current = _current;

            // Amend _position so that it can be used by other threads.
            _position += size;
        }

        // allocated before the position is changed
        addr = current.Raw + (UIntPtr)position;
        return current.Span.Slice(position, size);
    }

    public void Dispose()
    {
        foreach (var page in _pages)
        {
            _pool.Return(page);
        }

        _pages.Clear();
    }

    public override string ToString() => $"Memory: {_pages.Count * BufferSize / 1024}kb";

    public void Describe(TextWriter text, Key.Predicate? predicate = null)
    {
        predicate ??= (in Key _) => true;

        foreach (var kvp in this)
        {
            Key.ReadFrom(kvp.Key, out var key);

            if (predicate(key) == false)
                continue;

            switch (key.Type)
            {
                case DataType.Account:
                    Account.ReadFrom(kvp.Value, out Account account);
                    text.WriteLine($"Account [{S(key.Path)}] -> {account.ToString()}");
                    break;
                case DataType.StorageCell:
                    text.WriteLine(
                        $"Storage [{S(key.Path)}, {S(key.StoragePath)}] -> {kvp.Value.ToHexString(true)}");
                    break;
                case DataType.Merkle:
                    if (key.StoragePath.Length <= 0)
                    {
                        text.WriteLine($"Merkle, State [{(key.Path.ToString())}] (updated)");
                    }
                    else
                        text.WriteLine($"Merkle, Storage [{S(key.Path)}, {key.StoragePath.ToString()}] (updated)");

                    break;
                default:
                    throw new ArgumentOutOfRangeException($"The type {key.Type} is not handled");
            }
        }

        return;

        static string S(in NibblePath full) => full.UnsafeAsKeccak.ToString();
    }

    [StructLayout(LayoutKind.Explicit, Size = SizeOf)]
    private readonly struct Root
    {
        [FieldOffset(0)]
        private readonly Page _pages;

        public Root(Page[] pages)
        {
            Debug.Assert(pages.Length == PageCount);
            pages.CopyTo(MemoryMarshal.CreateSpan(ref _pages, PageCount));
        }

        /// <summary>
        /// The size of this structure.
        /// </summary>
        private const int SizeOf = PageCount * PointerSize;

        /// <summary>
        /// The total number of pages used by the root construct. The bigger, the bigger fanout it is.
        /// </summary>
        public const int PageCount = 32;

        public static readonly int BucketCountLog2 = BitOperations.Log2(BucketCount);

        public const int BucketCount = PageCount * BucketsPerPage;
        private const int BucketsPerPage = Page.PageSize / PointerSize;
        private const int InPageMask = BucketsPerPage - 1;
        private static readonly int PageShift = BitOperations.Log2(BucketsPerPage);

        public unsafe ref UIntPtr this[int bucket]
        {
            get
            {
                var shift = bucket >> PageShift;
                var raw = Unsafe.Add(ref Unsafe.AsRef(in _pages), shift).Raw;
                return ref Unsafe.Add(ref Unsafe.AsRef<UIntPtr>(raw.ToPointer()), bucket & InPageMask);
            }
        }
    }
}
