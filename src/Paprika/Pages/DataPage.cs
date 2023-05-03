﻿using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Paprika.Pages;

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
public readonly unsafe struct DataPage : IAccountPage
{
    private readonly Page _page;

    [DebuggerStepThrough]
    public DataPage(Page page) => _page = page;

    public ref PageHeader Header => ref _page.Header;

    public ref Payload Data => ref Unsafe.AsRef<Payload>(_page.Payload);

    public const int NibbleCount = 1;

    /// <summary>
    /// Represents the data of this data page. This type of payload stores data in 16 nibble-addressable buckets.
    /// These buckets is used to store up to <see cref="FixedMapSize"/> entries before flushing them down as other pages
    /// like page split. 
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = Size)]
    public struct Payload
    {
        private const int Size = Page.PageSize - PageHeader.Size;

        // to align to long
        public const int BucketCount = 16;

        /// <summary>
        /// The size of the <see cref="FixedMap"/> held in this page. Must be long aligned.
        /// </summary>
        private const int FixedMapSize = Size - BucketCount * DbAddress.Size;

        private const int FixedMapOffset = Size - FixedMapSize;

        /// <summary>
        /// The first field of buckets.
        /// </summary>
        [FieldOffset(0)] private DbAddress Bucket;

        public Span<DbAddress> Buckets => MemoryMarshal.CreateSpan(ref Bucket, BucketCount);

        /// <summary>
        /// The first item of map of frames to allow ref to it.
        /// </summary>
        [FieldOffset(FixedMapOffset)] private byte FixedMapStart;

        /// <summary>
        /// Fixed map memory
        /// </summary>
        public Span<byte> FixedMapSpan => MemoryMarshal.CreateSpan(ref FixedMapStart, FixedMapSize);
    }

    /// <summary>
    /// Sets values for the given <see cref="SetContext.Path"/>
    /// </summary>
    /// <param name="ctx"></param>
    /// <param name="level">The nesting level of the call</param>
    /// <returns>
    /// The actual page which handled the set operation. Due to page being COWed, it may be a different page.
    /// 
    /// </returns>
    public Page Set(in SetContext ctx)
    {
        if (Header.BatchId != ctx.Batch.BatchId)
        {
            // the page is from another batch, meaning, it's readonly. Copy
            var writable = ctx.Batch.GetWritableCopy(_page);
            return new DataPage(writable).Set(ctx);
        }

        var path = ctx.Path;
        var nibble = path.FirstNibble;

        var address = Data.Buckets[nibble];

        // the bucket is not null and represents a page jump, follow it
        if (address.IsNull == false && address.IsValidPageAddress)
        {
            var page = ctx.Batch.GetAt(address);
            var updated = new DataPage(page).Set(ctx.TrimPath(NibbleCount));

            // remember the updated
            Data.Buckets[nibble] = ctx.Batch.GetAddress(updated);
            return _page;
        }

        // in-page write
        var data = Serializer.Account.WriteEOATo(stackalloc byte[Serializer.Account.EOAMaxByteCount],
            ctx.Balance, ctx.Nonce);

        var map = new FixedMap(Data.FixedMapSpan);
        if (map.TrySet(FixedMap.Key.Account(path), data))
        {
            return _page;
        }

        // not enough memory in this page, need to push some data one level deeper to a new page
        var child = ctx.Batch.GetNewPage(out _, true);
        var dataPage = new DataPage(child);

        var biggest = map.GetBiggestNibbleBucket();
        foreach (var item in map.EnumerateNibble(biggest))
        {
            // TODO: consider writing data once, so that they don't need to be serialized and deserialized
            Serializer.Account.ReadAccount(item.Data, out var balance, out var nonce);
            var set = new SetContext(item.Path.SliceFrom(1), balance, nonce, ctx.Batch);
            dataPage = new DataPage(dataPage.Set(set));

            // delete the item, it's possible due to the internal construction of the map
            map.Delete(FixedMap.Key.Account(item.Path));
        }

        Data.Buckets[biggest] = ctx.Batch.GetAddress(dataPage.AsPage());

        // The page has some of the values flushed down, try to add again.
        return Set(ctx);
    }

    public void GetAccount(in NibblePath path, IReadOnlyBatchContext batch, out Account result)
    {
        var nibble = path.FirstNibble;
        var bucket = Data.Buckets[nibble];

        // non-null page jump, follow it!
        if (bucket.IsNull == false && bucket.IsValidPageAddress)
        {
            new DataPage(batch.GetAt(bucket)).GetAccount(path.SliceFrom(NibbleCount), batch, out result);
            return;
        }

        // read in-page
        var map = new FixedMap(Data.FixedMapSpan);

        if (map.TryGet(FixedMap.Key.Account(path), out var data))
        {
            Serializer.Account.ReadAccount(data, out var balance, out var nonce);
            result = new Account(balance, nonce);
            return;
        }

        result = default;
    }
}