using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paprika.Data;

namespace Paprika.Store;

/// <summary>
/// Represents a data page storing account data.
/// </summary>
/// <remarks>
/// The page is capable of storing some data inside of it and provides fan out for lower layers.
/// This means that for small amount of data no creation of further layers is required.
///
/// The page preserves locality of the data though. It's either all the children with a given nibble stored
/// in the parent page, or they are flushed underneath.
/// </remarks>
[method: DebuggerStepThrough]
public readonly unsafe struct DataPage(Page page) : IPage<DataPage>
{
    private const int ConsumedNibbles = 1;
    private const int BucketCount = DbAddressList.Of16.Count;

    public static DataPage Wrap(Page page) => Unsafe.As<Page, DataPage>(ref page);
    public static PageType DefaultType => PageType.DataPage;
    public bool IsClean => Map.IsEmpty && Data.Buckets.IsClean;

    private ref PageHeader Header => ref page.Header;

    private ref Payload Data => ref Unsafe.AsRef<Payload>(page.Payload);

    public Page DeleteByPrefix(in NibblePath prefix, IBatchContext batch)
    {
        if (Header.BatchId != batch.BatchId)
        {
            // the page is from another batch, meaning, it's readonly. Copy
            var writable = batch.GetWritableCopy(page);
            return new DataPage(writable).DeleteByPrefix(prefix, batch);
        }

        Map.DeleteByPrefix(prefix);

        ref var buckets = ref Data.Buckets;

        if (prefix.Length >= ConsumedNibbles)
        {
            var index = GetIndex(prefix);
            var childAddr = buckets[index];

            if (childAddr.IsNull == false)
            {
                var sliced = prefix.SliceFrom(ConsumedNibbles);
                var child = batch.GetAt(childAddr);
                child = child.Header.PageType == PageType.DataPage ?
                    new DataPage(child).DeleteByPrefix(sliced, batch) :
                    new BottomPage(child).DeleteByPrefix(sliced, batch);
                buckets[index] = batch.GetAddress(child);
            }
        }

        return page;
    }

    public Page Set(in NibblePath key, in ReadOnlySpan<byte> data, IBatchContext batch)
    {
        if (Header.BatchId != batch.BatchId)
        {
            // The page is from another batch, meaning, it's readonly. Copy on write.
            var writable = batch.GetWritableCopy(page);
            var cowed = batch.GetAddress(writable);
            Set(cowed, key, data, batch);
            return writable;
        }

        Set(batch.GetAddress(page), key, data, batch);
        return page;
    }

    [SkipLocalsInit]
    private static void Set(DbAddress at, in NibblePath key, in ReadOnlySpan<byte> data, IBatchContext batch)
    {
        Debug.Assert(at.IsNull == false, "Should be populated by the caller");
        Debug.Assert(batch.WasWritten(at), "Page should have been cowed before");

        var current = at;
        var k = key;

        while (current.IsNull == false)
        {
            var page = batch.GetAt(current);
            Debug.Assert(batch.WasWritten(current));

            if (page.Header.PageType == PageType.Bottom)
            {
                var result = new BottomPage(page).Set(k, data, batch);

                Debug.Assert(result.Equals(page), "The page should have been copied before");

                return;
            }

            Debug.Assert(page.Header.PageType == PageType.DataPage);

            ref var payload = ref Unsafe.AsRef<Payload>(page.Payload);
            var map = new SlottedArray(payload.DataSpan);
            var oddity = page.Header.Level % 2;

            if (data.IsEmpty)
            {
                // Empty data means deletion.
                // If it's a deletion and a key is empty or there's no child page, delete in page
                if (k.Length < ConsumedNibbles || payload.Buckets[GetIndex(k)].IsNull)
                {
                    // Empty key or a key with no children can be deleted only in-situ
                    map.Delete(k);
                    break;
                }
            }

            // Try to write through, if key may reside on the next level and there's a child that was written in this batch
            DbAddress childAddr;
            if (k.Length >= ConsumedNibbles && ShouldKeepShortKeyLocal(k) == false)
            {
                childAddr = payload.Buckets[GetIndex(k)];
                if (childAddr.IsNull == false && batch.WasWritten(childAddr))
                {
                    // Delete the k in this page just to ensure that the write-through will write the last value.
                    map.Delete(k);

                    k = k.SliceFrom(ConsumedNibbles);
                    current = childAddr;
                    continue;
                }
            }

            // Try to write in the map
            if (map.TrySet(k, data))
            {
                // Update happened, return
                break;
            }

            // First, try to flush the existing
            if (TryFindMostFrequentExistingNibble(oddity, map, payload.Buckets, out var nibble))
            {
                childAddr = EnsureExistingChildWritable(batch, ref payload, nibble);
                FlushDown(map, nibble, childAddr, batch);

                // Spin one more time
                continue;
            }

            // None of the existing was flushable, find the most frequent one
            nibble = FindMostFrequentNibble(oddity, map);

            // Ensure that the child page exists
            childAddr = payload.Buckets[nibble];
            Debug.Assert(childAddr.IsNull,
                "Address should be null. If it wasn't it should be the case that it's found above");

            // Create a child
            var lvl = (byte)(page.Header.Level + ConsumedNibbles);
            batch.GetNewPage<BottomPage>(out childAddr, lvl);

            // Set the mode for the new child to Merkle to make it spread content on the NibblePath length basis
            payload.Buckets[nibble] = childAddr;

            FlushDown(map, nibble, childAddr, batch);
            // Spin again to try to set.
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte GetIndex(in NibblePath k) => k.Nibble0;

    /// <summary>
    /// A single method for stats gathering to make it simple to change the implementation.
    /// </summary>
    private static void GatherStats(int oddity, in SlottedArray map, Span<ushort> stats)
    {
        map.GatherCountStats1Nibble(stats);

        // Provide a minor discount for nibbles that should be kept local
        for (byte nibble = 0; nibble < BucketCount; nibble++)
        {
            ref var s = ref stats[nibble];

            if (s > 0 &&
                ShouldKeepShortKeyLocal(nibble) &&
                map.Contains(NibblePath.Single(nibble, oddity)))
            {
                // The count is bigger than 0 and the nibble should be kept local so test for the key. Only then subtract.
                s -= 1;
            }
        }

        // other proposal to be size based, not count based
        //map.GatherSizeStats1Nibble(stats);
    }

    public void Clear()
    {
        Map.Clear();
        Data.Buckets.Clear();
    }

    private static DbAddress EnsureExistingChildWritable(IBatchContext batch, ref Payload payload, byte nibble)
    {
        var childAddr = payload.Buckets[nibble];

        Debug.Assert(childAddr.IsNull == false, "Should exist");

        batch.GetAt(childAddr);
        batch.EnsureWritableCopy(ref childAddr);
        payload.Buckets[nibble] = childAddr;

        return childAddr;
    }

    private static byte FindMostFrequentNibble(int oddity, in SlottedArray map)
    {
        const int count = SlottedArray.OneNibbleStatsCount;

        Span<ushort> stats = stackalloc ushort[count];

        GatherStats(oddity, map, stats);

        byte biggestIndex = 0;
        for (byte i = 1; i < count; i++)
        {
            if (stats[i] > stats[biggestIndex])
            {
                biggestIndex = i;
            }
        }

        return biggestIndex;
    }

    private static bool TryFindMostFrequentExistingNibble(int oddity, in SlottedArray map,
        in DbAddressList.Of16 children,
        out byte nibble)
    {
        Span<ushort> stats = stackalloc ushort[BucketCount];

        GatherStats(oddity, map, stats);

        byte biggestIndex = 0;
        ushort biggestValue = 0;

        for (byte i = 0; i < BucketCount; i++)
        {
            if (children[i].IsNull == false && stats[i] > biggestValue)
            {
                biggestIndex = i;
                biggestValue = stats[i];
            }
        }

        if (biggestValue > 0)
        {
            nibble = biggestIndex;
            return true;
        }

        nibble = default;
        return false;
    }

    private static void FlushDown(in SlottedArray map, byte nibble, DbAddress child, IBatchContext batch)
    {
        Debug.Assert(batch.WasWritten(child));

        var keepShortKeyLocal = ShouldKeepShortKeyLocal(nibble);

        foreach (var item in map.EnumerateNibble(nibble))
        {
            var sliced = item.Key.SliceFrom(ConsumedNibbles);

            if (keepShortKeyLocal && item.Key.Length == 1)
            {
                Debug.Assert(sliced.IsEmpty, "The local caching is only 1 lvl deep");

                // The key is meant to be kept local, set a deletion underneath and leave it as is.
                Set(child, sliced, ReadOnlySpan<byte>.Empty, batch);
            }
            else
            {
                // Key is not kept local, propagate it down
                Set(child, sliced, item.RawData, batch);

                // Use the fast delete by item.
                map.Delete(item);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ShouldKeepShortKeyLocal(in NibblePath path) =>
        path.Length == 1 && ShouldKeepShortKeyLocal(path.Nibble0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ShouldKeepShortKeyLocal(byte nibble)
    {
        // The criterion whether the given nibble should be kept local or not.

        // How much of the page should be occupied by branches inlined from below, in percents.
        const int maxOccupationPercentage = 50;
        const int fullMerkleBranchEstimation = 512;
        const int inlinedBranchesPerPage = Page.PageSize / fullMerkleBranchEstimation * maxOccupationPercentage / 100;
        const int saveEveryNthBranch = BucketCount / inlinedBranchesPerPage;

        return nibble % saveEveryNthBranch == 0;
    }

    /// <summary>
    /// Represents the data of this data page. This type of payload stores data in 16 nibble-addressable buckets.
    /// These buckets are used to store up to <see cref="DataSize"/> entries before flushing them down as other pages
    /// like page split.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = Size)]
    public struct Payload
    {
        public const int Size = Page.PageSize - PageHeader.Size;
        private const int BucketSize = DbAddressList.Of16.Size;

        /// <summary>
        /// The size of the raw byte data held in this page. Must be long aligned.
        /// </summary>
        private const int DataSize = Size - BucketSize;

        private const int DataOffset = Size - DataSize;

        [FieldOffset(0)] public DbAddressList.Of16 Buckets;

        /// <summary>
        /// The first item of map of frames to allow ref to it.
        /// </summary>
        [FieldOffset(DataOffset)] private byte DataStart;

        /// <summary>
        /// Writable area.
        /// </summary>
        public Span<byte> DataSpan => MemoryMarshal.CreateSpan(ref DataStart, DataSize);
    }

    public bool TryGet(IPageResolver batch, scoped in NibblePath key, out ReadOnlySpan<byte> result)
        => TryGet(batch, key, out result, this.AsPage());

    private static bool TryGet(IPageResolver batch, scoped in NibblePath key, out ReadOnlySpan<byte> result,
        Page page)
    {
        var returnValue = false;
        var sliced = key;

        do
        {
            if (page.Header.PageType == PageType.Bottom)
            {
                return new BottomPage(page).TryGet(batch, sliced, out result);
            }

            var dataPage = new DataPage(page);

            DbAddress bucket = default;
            if (!sliced.IsEmpty)
            {
                // As the CPU does not auto-prefetch across page boundaries
                // Prefetch child page in case we go there next to reduce CPU stalls
                // Do it using soft mode as quite likely the page is loaded to RAM but possibly not in CPU cache.
                bucket = dataPage.Data.Buckets[GetIndex(sliced)];
                if (bucket.IsNull == false)
                    batch.Prefetch(bucket, PrefetchMode.Soft);
            }

            // try regular map
            if (dataPage.Map.TryGet(sliced, out result))
            {
                returnValue = true;
                break;
            }

            if (sliced.IsEmpty) // empty keys are left in page
            {
                break;
            }

            if (bucket.IsNull)
            {
                break;
            }

            // non-null page jump, follow it!
            sliced = sliced.SliceFrom(ConsumedNibbles);
            page = batch.GetAt(bucket);
        } while (true);

        return returnValue;
    }

    public SlottedArray Map => new(Data.DataSpan);

    public void Accept(ref NibblePath.Builder builder, IPageVisitor visitor, IPageResolver resolver, DbAddress addr)
    {
        resolver.Prefetch(Data.Buckets);

        using (visitor.On(ref builder, this, addr))
        {
            for (byte i = 0; i < DbAddressList.Of16.Count; i++)
            {
                var bucket = Data.Buckets[i];
                if (bucket.IsNull)
                {
                    continue;
                }

                var child = resolver.GetAt(bucket);
                var type = child.Header.PageType;

                builder.Push(i);
                {
                    if (type == PageType.DataPage)
                    {
                        new DataPage(child).Accept(ref builder, visitor, resolver, bucket);
                    }
                    else if (type == PageType.Bottom)
                    {
                        new BottomPage(child).Accept(ref builder, visitor, resolver, bucket);
                    }
                    else
                    {
                        throw new InvalidOperationException($"Invalid page type {type}");
                    }
                }
                builder.Pop();
            }
        }
    }
}