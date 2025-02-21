using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paprika.Data;

namespace Paprika.Store;

/// <summary>
/// A fan out page that fans out to 256 nibbles.
/// </summary>
/// <param name="page"></param>
[method: DebuggerStepThrough]
public readonly unsafe struct FanOutPage(Page page) : IPage<FanOutPage>
{
    /// <summary>
    /// The maximum length of the key that will be offloaded to the sidecar.
    /// </summary>
    private const int MerkleSideCarMaxKeyLength = 1;
    private const int ConsumedNibbles = 2;
    private const int BucketCount = DbAddressList.Of256.Count;

    public static FanOutPage Wrap(Page page) => Unsafe.As<Page, FanOutPage>(ref page);
    public static PageType DefaultType => PageType.FanOut256;
    public bool IsClean => Data.IsClean;

    private ref PageHeader Header => ref page.Header;

    private ref Payload Data => ref Unsafe.AsRef<Payload>(page.Payload);

    public Page DeleteByPrefix(in NibblePath prefix, IBatchContext batch)
    {
        if (Header.BatchId != batch.BatchId)
        {
            // the page is from another batch, meaning, it's readonly. Copy
            var writable = batch.GetWritableCopy(page);
            return new FanOutPage(writable).DeleteByPrefix(prefix, batch);
        }

        Map.DeleteByPrefix(prefix);

        if (ShouldBeInSideCar(prefix) && Data.SideCar.IsNull == false)
        {
            new BottomPage(batch.EnsureWritableCopy(ref Data.SideCar)).DeleteByPrefix(prefix, batch);
        }

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
            return new FanOutPage(writable).Set(key, data, batch);
        }

        if (ShouldBeInSideCar(key))
        {
            if (data.IsEmpty && Data.SideCar.IsNull)
            {
                // No side-car, nothing to remove
                return page;
            }

            // Ensure side-car exists
            var sideCar = Data.SideCar.IsNull
                ? batch.GetNewPage<BottomPage>(out Data.SideCar, page.Header.Level)
                : new BottomPage(batch.EnsureWritableCopy(ref Data.SideCar));

            sideCar.Set(key, data, batch);
            return page;
        }

        Debug.Assert(ShouldBeInSideCar(key) == false);

        if (TryWriteInAlreadyWrittenChild(key, data, batch))
            return page;

        // Try to write in the map
        if (Map.TrySet(key, data))
        {
            return page;
        }

        FlushDown(batch);

        if (TryWriteInAlreadyWrittenChild(key, data, batch))
            return page;

        // Write back in the map
        if (Map.TrySet(key, data) == false)
        {
            ThrowNoSpace();
        }

        return page;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNoSpace() => throw new Exception("Should have place in map");

    private bool TryWriteInAlreadyWrittenChild(in NibblePath key, ReadOnlySpan<byte> data, IBatchContext batch)
    {
        var childAddr = Data.Buckets[GetIndex(key)];
        if (childAddr.IsNull || !batch.WasWritten(childAddr))
            return false;

        // Delete the key in this page just to ensure that the write-through will write the last value.
        Map.Delete(key);

        var sliced = key.SliceFrom(ConsumedNibbles);
        var child = batch.GetAt(childAddr);

        SetInChild(child, sliced, data, batch);

        return true;
    }

    private static void SetInChild(Page child, in NibblePath key, ReadOnlySpan<byte> data, IBatchContext batch)
    {
        Debug.Assert(batch.WasWritten(batch.GetAddress(child)));

        if (child.Header.PageType == PageType.Bottom)
        {
            new BottomPage(child).Set(key, data, batch);
        }
        else
        {
            new DataPage(child).Set(key, data, batch);
        }
    }

    private static bool ShouldBeInSideCar(in NibblePath k) => k.Length <= MerkleSideCarMaxKeyLength;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte GetIndex(in NibblePath k) => (byte)(k.Nibble0 << NibblePath.NibbleShift | k.GetAt(1));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (byte nibble0, byte nibble1) ParseIndex(int index) => ((byte)(index >> NibblePath.NibbleShift),
        (byte)(index & NibblePath.NibbleMask));

    public void Clear() => Data.Clear();

    private void FlushDown(IBatchContext batch)
    {
        var map = Map;

        foreach (var item in map.EnumerateAll())
        {
            var index = GetIndex(item.Key);

            var childAddr = Data.Buckets[index];
            Page child = default;

            if (childAddr.IsNull)
            {
                child = batch.GetNewPage<BottomPage>(out childAddr, (byte)(page.Header.Level + ConsumedNibbles)).AsPage();
                Data.Buckets[index] = childAddr;
            }
            else if (batch.WasWritten(childAddr) == false)
            {
                child = batch.EnsureWritableCopy(ref childAddr);
                Data.Buckets[index] = childAddr;
            }
            else
            {
                child = batch.GetAt(childAddr);
            }

            Debug.Assert(batch.WasWritten(childAddr));
            map.Delete(item);
            SetInChild(child, item.Key.SliceFrom(ConsumedNibbles), item.RawData, batch);
        }
    }

    /// <summary>
    /// Represents the data of this data page. This type of payload stores data in 16 nibble-addressable buckets.
    /// These buckets are used to store up to <see cref="DataSize"/> entries before flushing them down as other pages
    /// like page split.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = Size)]
    public struct Payload
    {
        private const int Size = Page.PageSize - PageHeader.Size;
        private const int BucketSize = DbAddressList.Of256.Size;

        /// <summary>
        /// The size of the raw byte data held in this page. Must be long aligned.
        /// </summary>
        private const int DataSize = Size - BucketSize - DbAddress.Size;

        private const int DataOffset = Size - DataSize;

        [FieldOffset(0)] public DbAddressList.Of256 Buckets;

        [FieldOffset(DbAddressList.Of256.Size)] public DbAddress SideCar;

        /// <summary>
        /// The first item of map of frames to allow ref to it.
        /// </summary>
        [FieldOffset(DataOffset)] private byte DataStart;

        /// <summary>
        /// Writable area.
        /// </summary>
        public Span<byte> DataSpan => MemoryMarshal.CreateSpan(ref DataStart, DataSize);

        public bool IsClean => new SlottedArray(DataSpan).IsEmpty && Buckets.IsClean && SideCar.IsNull;

        public void Clear()
        {
            new SlottedArray(DataSpan).Clear();
            Buckets.Clear();
            SideCar = default;
        }
    }

    public bool TryGet(IPageResolver batch, scoped in NibblePath key, out ReadOnlySpan<byte> result)
    {
        if (ShouldBeInSideCar(key))
        {
            if (Data.SideCar.IsNull)
            {
                result = default;
                return false;
            }

            return new BottomPage(batch.GetAt(Data.SideCar)).TryGet(batch, key, out result);
        }

        if (Map.TryGet(key, out result))
        {
            return result.IsEmpty == false;
        }

        // non-null page jump, follow it!
        var childAddr = Data.Buckets[GetIndex(key)];
        if (childAddr.IsNull)
        {
            return false;
        }

        var sliced = key.SliceFrom(ConsumedNibbles);
        var child = batch.GetAt(childAddr);

        return child.Header.PageType == PageType.Bottom
            ? new BottomPage(child).TryGet(batch, sliced, out result)
            : new DataPage(child).TryGet(batch, sliced, out result);
    }

    private SlottedArray Map => new(Data.DataSpan);

    public void Accept(ref NibblePath.Builder builder, IPageVisitor visitor, IPageResolver resolver, DbAddress addr)
    {
        resolver.Prefetch(Data.Buckets);

        using (visitor.On(ref builder, this, addr))
        {
            for (var i = 0; i < BucketCount; i++)
            {
                var bucket = Data.Buckets[i];
                if (bucket.IsNull)
                {
                    continue;
                }

                var child = resolver.GetAt(bucket);
                var type = child.Header.PageType;

                var (nibble0, nibble1) = ParseIndex(i);

                builder.Push(nibble0, nibble1);
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
                builder.Pop(2);
            }

            if (Data.SideCar.IsNull == false)
            {
                new BottomPage(resolver.GetAt(Data.SideCar)).Accept(ref builder, visitor, resolver, Data.SideCar);
            }
        }
    }
}