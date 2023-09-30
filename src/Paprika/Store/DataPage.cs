﻿using System.Diagnostics;
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
public readonly unsafe struct DataPage : IPage
{
    private const int BucketCount = 16;

    private readonly Page _page;

    [DebuggerStepThrough]
    public DataPage(Page page) => _page = page;

    public ref PageHeader Header => ref _page.Header;

    public ref Payload Data => ref Unsafe.AsRef<Payload>(_page.Payload);

    private const int NibbleCount = 1;

    /// <summary>
    /// Sets values for the given <see cref="SetContext.Key"/>
    /// </summary>
    /// <returns>
    /// The actual page which handled the set operation. Due to page being COWed, it may be a different page.
    /// </returns>
    public Page Set(in SetContext ctx)
    {
        if (Header.BatchId != ctx.Batch.BatchId)
        {
            // the page is from another batch, meaning, it's readonly. Copy
            var writable = ctx.Batch.GetWritableCopy(_page);

            return new DataPage(writable).Set(ctx);
        }

        var map = new SlottedArray(Data.DataSpan);
        var key = ctx.Key;

        var path = key.Path;
        var isDelete = ctx.Data.IsEmpty;

        if (isDelete)
        {
            if (path.Length < NibbleCount)
            {
                // path cannot be held on a lower level so delete in here
                return DeleteInMap(key, map);
            }

            // path is not empty, so it might have a child page underneath with data, let's try
            var childPageAddress = Data.Buckets[path.FirstNibble];
            if (childPageAddress.IsNull)
            {
                // there's no lower level, delete in map
                return DeleteInMap(key, map);
            }
        }

        // try write in map
        if (map.TrySet(key, ctx.Data))
        {
            return _page;
        }

        if (TryFlushDownSoftAndWrite(key, ctx.Data, ctx.Batch, map))
            return _page;

        // Flushing down to existing pages didn't make space for this entry, flush the biggest nibble forcefully.
        var biggestNibble = FindMostFrequentNibble(map);

        // try get the child page
        ref var address = ref Data.Buckets[biggestNibble];
        Page child;

        if (address.IsNull)
        {
            // create child as the same type as the parent
            child = ctx.Batch.GetNewPage(out Data.Buckets[biggestNibble], true);
            child.Header.PageType = Header.PageType;
            child.Header.Level = (byte)(Header.Level + 1);
        }
        else
        {
            // the child page is not-null, retrieve it
            child = ctx.Batch.GetAt(address);
        }

        var dataPage = new DataPage(child);
        var batch = ctx.Batch;

        // flush down: force
        dataPage = FlushDown(map, biggestNibble, dataPage, batch, false);
        address = ctx.Batch.GetAddress(dataPage.AsPage());

        // The page has some of the values flushed down, try to add again.
        return Set(ctx);
    }

    private bool TryFlushDownSoftAndWrite(in Key key, in ReadOnlySpan<byte> data, IBatchContext batch, in SlottedArray map)
    {
        // There's no more room in this page. We need to make some, but in a way that will not over-allocate pages.
        // To make it work:
        // 1. find amongst existing children pages that have some capacity left.
        // 2. sort them from the most empty to the least empty
        // 3. loop through them, and flush down the given nibble in a way that will not allocate anything in them
        // 4. after each spin of the loop, try to write map

        // TODO: Change this algorithm into a simple bit-map that is memoized in page.
        // Softly flushed map would memoize whether a page was flushed softly.
        // Here, select only these that were not

        Span<ushort> capacities = stackalloc ushort[BucketCount];
        Span<byte> nibbles = stackalloc byte[BucketCount];

        for (byte i = 0; i < BucketCount; i++)
        {
            nibbles[i] = i;

            var childAddress = Data.Buckets[i];
            if (childAddress.IsNull == false)
            {
                capacities[i] = (ushort)new DataPage(batch.GetAt(childAddress)).Map.CapacityLeft;
            }
        }

        capacities.Sort(nibbles);
        var start = capacities.IndexOfAnyExcept((ushort)0);

        if (start == -1)
        {
            // no child pages with non-zero capacities, default
            return false;
        }

        // contains sorted from the smallest to the biggest capacity
        var nibblesWithSomeCapacity = nibbles.Slice(start);
        for (var i = nibblesWithSomeCapacity.Length - 1; i >= 0; i--)
        {
            var nibble = nibblesWithSomeCapacity[i];
            ref var childAddress = ref Data.Buckets[nibble];
            Debug.Assert(childAddress.IsNull == false, "Only an existing child should be selected for flush down");

            var page = new DataPage(batch.GetAt(childAddress));
            page = FlushDown(map, nibble, page, batch, true);

            // update the child address
            childAddress = batch.GetAddress(page.AsPage());

            // try write again to the map
            if (map.TrySet(key, data))
            {
                return true;
            }
        }

        return false;
    }

    private static DataPage FlushDown(in SlottedArray map, byte nibble, DataPage destination, IBatchContext batch, bool tryAllocFree)
    {
        const int minimumCapacity = 128;

        foreach (var item in map.EnumerateNibble(nibble))
        {
            var key = item.Key.SliceFrom(NibbleCount);

            if (tryAllocFree && destination.Map.CapacityLeft < minimumCapacity)
            {
                // if there's not that much space left in the child, break this loop
                break;
            }

            var set = new SetContext(key, item.RawData, batch);
            destination = new DataPage(destination.Set(set));

            // use the special delete for the item that is much faster than map.Delete(item.Key);
            map.Delete(item);
        }

        return destination;
    }

    private static byte FindMostFrequentNibble(SlottedArray map)
    {
        const int count = SlottedArray.BucketCount;

        Span<ushort> stats = stackalloc ushort[count];
        map.GatherCountStatistics(stats);

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

    private Page DeleteInMap(in Key key, SlottedArray map)
    {
        map.Delete(key);
        if (map.Count == 0 && Data.Buckets.IndexOfAnyExcept(DbAddress.Null) == -1)
        {
            // map is empty, buckets are empty, page is empty
            // TODO: for now, leave as is 
        }

        return _page;
    }

    /// <summary>
    /// Represents the data of this data page. This type of payload stores data in 16 nibble-addressable buckets.
    /// These buckets is used to store up to <see cref="DataSize"/> entries before flushing them down as other pages
    /// like page split. 
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = Size)]
    public struct Payload
    {
        private const int Size = Page.PageSize - PageHeader.Size;
        private const int BucketSize = BucketCount * DbAddress.Size;

        /// <summary>
        /// The size of the raw byte data held in this page. Must be long aligned.
        /// </summary>
        private const int DataSize = Size - BucketSize;

        private const int DataOffset = Size - DataSize;

        /// <summary>
        /// The first field of buckets.
        /// </summary>
        [FieldOffset(0)] private DbAddress Bucket;

        public Span<DbAddress> Buckets => MemoryMarshal.CreateSpan(ref Bucket, BucketCount);

        /// <summary>
        /// The first item of map of frames to allow ref to it.
        /// </summary>
        [FieldOffset(DataOffset)] private byte DataStart;

        /// <summary>
        /// Writable area.
        /// </summary>
        public Span<byte> DataSpan => MemoryMarshal.CreateSpan(ref DataStart, DataSize);
    }

    public bool TryGet(Key key, IReadOnlyBatchContext batch, out ReadOnlySpan<byte> result) =>
        TryGet(key, (IPageResolver)batch, out result);

    private bool TryGet(Key key, IPageResolver batch, out ReadOnlySpan<byte> result)
    {
        // read in-page
        var map = Map;

        // try regular map
        if (map.TryGet(key, out result))
        {
            return true;
        }

        // path longer than 0, try to find in child
        if (key.Path.Length > 0)
        {
            // try to go deeper only if the path is long enough
            var bucket = Data.Buckets[key.Path.FirstNibble];

            // non-null page jump, follow it!
            if (bucket.IsNull == false)
            {
                return new DataPage(batch.GetAt(bucket)).TryGet(key.SliceFrom(NibbleCount), batch, out result);
            }
        }

        result = default;
        return false;
    }

    private SlottedArray Map => new(Data.DataSpan);

    public void Report(IReporter reporter, IPageResolver resolver, int level)
    {
        var emptyBuckets = 0;

        foreach (var bucket in Data.Buckets)
        {
            if (bucket.IsNull)
            {
                emptyBuckets++;
            }
            else
            {
                new DataPage(resolver.GetAt(bucket)).Report(reporter, resolver, level + 1);
            }
        }

        var slotted = new SlottedArray(Data.DataSpan);

        foreach (var item in slotted.EnumerateAll())
        {
            reporter.ReportItem(item.Key, item.RawData);
        }

        reporter.ReportDataUsage(Header.PageType, level, BucketCount - emptyBuckets, slotted.Count, slotted.CapacityLeft);
    }
}