using System.Buffers;
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
public readonly unsafe struct DataPage(Page page) : IPageWithData<DataPage>, IClearable
{
    private const int ConsumedNibbles = 1;
    private const int BucketCount = DbAddressList.Of16.Count;

    private static class Modes
    {
        /// <summary>
        /// <see cref="Payload.Buckets"/> are used as regular fan out navigation.
        /// </summary>
        public const byte Fanout = 0;

        /// <summary>
        /// This is a leaf page and it will use <see cref="Payload.Buckets"/> to keep additional overflow pages.
        /// </summary>
        public const byte Leaf = 1;
    }

    private static class LeafMode
    {
        public const int Bucket = 0;
    }

    public static DataPage Wrap(Page page) => Unsafe.As<Page, DataPage>(ref page);

    private ref PageHeader Header => ref page.Header;

    private ref Payload Data => ref Unsafe.AsRef<Payload>(page.Payload);

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

            ref var payload = ref Unsafe.AsRef<Payload>(page.Payload);
            var map = new SlottedArray(payload.DataSpan);

            if (page.Header.Metadata == Modes.Fanout)
            {
                if (data.IsEmpty)
                {
                    // Empty data means deletion.
                    // If it's a deletion and a key is empty or there's no child page, delete in page
                    if (k.IsEmpty || payload.Buckets[k.FirstNibble].IsNull)
                    {
                        // Empty key or a key with no children can be deleted only in-situ
                        map.Delete(k);
                        break;
                    }
                }

                // Try to write through, if key is not empty and there's a child that was written in this batch
                DbAddress childAddr;
                if (k.IsEmpty == false)
                {
                    childAddr = payload.Buckets[k.FirstNibble];
                    if (childAddr.IsNull == false && batch.WasWritten(childAddr))
                    {
                        // Child was written, advance k and update current
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
                if (TryFindMostFrequentExistingNibble(map, payload.Buckets, out var nibble))
                {
                    childAddr = EnsureExistingChildWritable(batch, ref payload, nibble);
                    FlushDown(map, nibble, childAddr, batch);

                    // Spin one more time
                    continue;
                }

                // None of the existing was flushable, find the most frequent one
                nibble = FindMostFrequentNibble(map);

                // Ensure that the child page exists
                childAddr = payload.Buckets[nibble];
                Debug.Assert(childAddr.IsNull,
                    "Address should be null. If it wasn't it should be the case that it's found above");

                // Create a child

                // Get new page without clearing. Clearing is done manually.
                var child = batch.GetNewPage(out childAddr, false);
                new DataPage(child).Clear();

                child.Header.PageType = PageType.DataPage;
                child.Header.Level = (byte)(page.Header.Level + ConsumedNibbles);

                // Set the mode for the new child to Merkle to make it spread content on the NibblePath length basis
                child.Header.Metadata = Modes.Leaf;
                payload.Buckets[nibble] = childAddr;

                FlushDown(map, nibble, childAddr, batch);
                // Spin again to try to set.
            }
            else
            {
                Debug.Assert(page.Header.Metadata == Modes.Leaf);

                // If it's a delete and no child, just delete
                if (data.IsEmpty && payload.Buckets[LeafMode.Bucket].IsNull)
                {
                    map.Delete(k);
                    break;
                }

                // Try set in the map
                if (map.TrySet(k, data))
                {
                    // Update happened, return
                    break;
                }

                // Failed to set, use overflow.
                // 1. check if delete, then delete in both.
                // 2. flush some down
                // 3. retry set
                var overflow = GetWritableOverflow(batch, ref payload);

                Debug.Assert(payload.Buckets[LeafMode.Bucket].IsNull == false);

                if (data.IsEmpty)
                {
                    map.Delete(k);
                    overflow.Delete(k);
                    break;
                }

                // Move down
                map.MoveNonEmptyKeysTo(new MapSource(overflow), treatEmptyAsTombstone: true);

                // Try set again
                if (map.TrySet(k, data))
                {
                    // Update happened, return
                    break;
                }

                // Failed to set, requires transforming the leaf to a full-blown fan-out data page.
                TurnToFanOut(current, overflow, batch);

                // Set the actual key
                Set(current, k, data, batch);
                break;
            }
        }
    }

    private static void TurnToFanOut(DbAddress current, in SlottedArray overflow, IBatchContext batch)
    {
        // The plan:
        // 1. Remove from overflow all the keys that exist in the current.
        // 2. The overflow contains no keys from the current.
        // 3. Change the mode
        // 4. Find the biggest nibble from both, overflow and the current map
        // 5. Create a child page for this nibble.
        // 6. Set values from overflow to this page.

        var page = batch.GetAt(current);

        ref var payload = ref Unsafe.AsRef<Payload>(page.Payload);
        var map = new SlottedArray(payload.DataSpan);

        // Register for reuse and clear immediately. The overflow is kept as a map in memory
        batch.RegisterForFutureReuse(batch.GetAt(payload.Buckets[LeafMode.Bucket]));
        payload.Buckets[LeafMode.Bucket] = DbAddress.Null;

        // 1. & 2.
        overflow.RemoveKeysFrom(map);

        // 3. 
        page.Header.Metadata = Modes.Fanout;

        // 4.
        Span<ushort> stats = stackalloc ushort[BucketCount];
        overflow.GatherCountStats1Nibble(stats);
        map.GatherCountStats1Nibble(stats);

        byte nibbleWithMostData = 0;
        for (byte i = 1; i < BucketCount; i++)
        {
            if (stats[i] > stats[nibbleWithMostData])
            {
                nibbleWithMostData = i;
            }
        }

        // 5
        // Get new page without clearing. Clearing is done manually.
        var child = batch.GetNewPage(out var childAddr, false);
        new DataPage(child).Clear();

        child.Header.PageType = PageType.DataPage;
        child.Header.Level = (byte)(page.Header.Level + ConsumedNibbles);

        // Set the mode for the new child to Merkle to make it spread content on the NibblePath length basis
        child.Header.Metadata = Modes.Leaf;
        payload.Buckets[nibbleWithMostData] = childAddr;

        // 6
        foreach (var item in overflow.EnumerateAll())
        {
            Set(current, item.Key, item.RawData, batch);
        }
    }

    private static SlottedArray GetWritableOverflow(IBatchContext batch, ref Payload payload)
    {
        var addr = payload.Buckets[LeafMode.Bucket];
        LeafOverflowPage overflow;
        Page overflowPage;

        if (addr.IsNull)
        {
            // Manual clear below
            overflowPage = batch.GetNewPage(out addr, clear: false);

            overflowPage.Header.PageType = PageType.LeafOverflow;
            overflow = new LeafOverflowPage(overflowPage);
            overflow.Map.Clear();
        }
        else
        {
            overflowPage = batch.EnsureWritableCopy(ref addr);
            overflow = new LeafOverflowPage(overflowPage);

            Debug.Assert(overflowPage.Header.PageType == PageType.LeafOverflow);
        }

        payload.Buckets[LeafMode.Bucket] = addr;

        Debug.Assert(overflow.AsPage().Header.BatchId == batch.BatchId);

        return overflow.Map;
    }

    public void Clear()
    {
        new SlottedArray(Data.DataSpan).Clear();
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

    private static byte FindMostFrequentNibble(in SlottedArray map)
    {
        const int count = SlottedArray.BucketCount;

        Span<ushort> stats = stackalloc ushort[count];

        map.GatherCountStats1Nibble(stats);

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

    private static bool TryFindMostFrequentExistingNibble(in SlottedArray map, in DbAddressList.Of16 children,
        out byte nibble)
    {
        Span<ushort> stats = stackalloc ushort[BucketCount];
        map.GatherCountStats1Nibble(stats);

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

        foreach (var item in map.EnumerateNibble(nibble))
        {
            var sliced = item.Key.SliceFrom(ConsumedNibbles);

            Set(child, sliced, item.RawData, batch);

            // Use the special delete for the item that is much faster than map.Delete(item.Key);
            map.Delete(item);
        }
    }

    /// <summary>
    /// Represents the data of this data page. This type of payload stores data in 16 nibble-addressable buckets.
    /// These buckets is used to store up to <see cref="DataSize"/> entries before flushing them down as other pages
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

    public bool TryGet(IReadOnlyBatchContext batch, scoped in NibblePath key, out ReadOnlySpan<byte> result)
        => TryGet(batch, key, out result, this);

    private static bool TryGet(IReadOnlyBatchContext batch, scoped in NibblePath key, out ReadOnlySpan<byte> result,
        DataPage page)
    {
        var returnValue = false;
        var sliced = key;

        do
        {
            batch.AssertRead(page.Header);

            if (page.Header.Metadata == Modes.Leaf)
            {
                if (page.Map.TryGet(sliced, out result))
                    return true;

                var overflowAddr = page.Data.Buckets[LeafMode.Bucket];
                return !overflowAddr.IsNull &&
                       new LeafOverflowPage(batch.GetAt(overflowAddr)).Map.TryGet(sliced, out result);
            }

            DbAddress bucket = default;
            if (!sliced.IsEmpty)
            {
                // As the CPU does not auto-prefetch across page boundaries
                // Prefetch child page in case we go there next to reduce CPU stalls
                bucket = page.Data.Buckets[sliced.FirstNibble];
                batch.Prefetch(bucket);
            }

            // try regular map
            if (page.Map.TryGet(sliced, out result))
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
            var child = batch.GetAt(bucket);
            page = Unsafe.As<Page, DataPage>(ref child);
        } while (true);

        return returnValue;
    }

    public SlottedArray Map => new(Data.DataSpan);

    public int CapacityLeft => Map.CapacityLeft;

    public void Report(IReporter reporter, IPageResolver resolver, int pageLevel, int trimmedNibbles)
    {
        resolver.Prefetch(Data.Buckets);

        var slotted = new SlottedArray(Data.DataSpan);
        reporter.ReportDataUsage(Header.PageType, pageLevel, trimmedNibbles, slotted);

        foreach (var bucket in Data.Buckets)
        {
            if (bucket.IsNull)
                continue;

            var child = resolver.GetAt(bucket);

            if (IsFanOut)
            {
                new DataPage(child).Report(reporter, resolver, pageLevel + 1, trimmedNibbles + 1);
            }
            else
            {
                new LeafOverflowPage(child).Report(reporter, resolver, pageLevel + 1, trimmedNibbles + 1);
            }
        }
    }

    public void Accept(ref NibblePath.Builder builder, IPageVisitor visitor, IPageResolver resolver, DbAddress addr)
    {
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

                if (IsFanOut)
                {
                    builder.Push(i);
                    {
                        new DataPage(child).Accept(ref builder, visitor, resolver, bucket);
                    }
                    builder.Pop();
                }
                else
                {
                    new LeafOverflowPage(child).Accept(ref builder, visitor, resolver, bucket);
                }
            }
        }
    }

    private bool IsFanOut => Header.Metadata == Modes.Fanout;
}