﻿using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Paprika.Crypto;
using Paprika.Pages.Frames;

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
public readonly unsafe struct DataPage : IPage
{
    private readonly Page _page;

    [DebuggerStepThrough]
    public DataPage(Page page) => _page = page;

    public ref PageHeader Header => ref _page.Header;

    public ref Payload Data => ref Unsafe.AsRef<Payload>(_page.Payload);

    /// <summary>
    /// Represents the data of this data page. This type of payload stores data in 16 nibble-addressable buckets.
    /// These buckets is used to store up to <see cref="FrameCount"/> entries before flushing them down as other pages
    /// like page split. 
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = Size)]
    public struct Payload
    {
        private const int Size = Page.PageSize - PageHeader.Size;

        public const int BucketCount = 16;

        /// <summary>
        /// The offset to the first frame.
        /// </summary>
        private const int FramesDataOffset = sizeof(int) + BitPool32.Size + BucketCount * DbAddress.Size;

        /// <summary>
        /// How many frames fit in this page.
        /// </summary>
        public const int FrameCount = (Size - FramesDataOffset) / ContractFrame.Size;

        /// <summary>
        /// The bit map of frames used at this page.
        /// </summary>
        [FieldOffset(0)] public BitPool32 FrameUsed;

        /// <summary>
        /// The nibble addressable buckets.
        /// </summary>
        [FieldOffset(BitPool32.Size)] private fixed int BucketsData[BucketCount];

        /// <summary>
        /// Map of <see cref="BucketsData"/>.
        /// </summary>
        [FieldOffset(BitPool32.Size)] private DbAddress Bucket;

        public Span<DbAddress> Buckets => MemoryMarshal.CreateSpan(ref Bucket, BucketCount);

        /// <summary>
        /// Data for storing frames.
        /// </summary>
        [FieldOffset(FramesDataOffset)] private fixed byte FramesData[FrameCount * ContractFrame.Size];

        /// <summary>
        /// Map of <see cref="FramesData"/> as a type to allow ref to it.
        /// </summary>
        [FieldOffset(FramesDataOffset)] private ContractFrame Frame;

        /// <summary>
        /// Access all the frames.
        /// </summary>
        public Span<ContractFrame> Frames => MemoryMarshal.CreateSpan(ref Frame, FrameCount);
    }

    /// <summary>
    /// Sets values for the given <see cref="SetContext.Key"/>
    /// </summary>
    /// <param name="ctx"></param>
    /// <param name="batch"></param>
    /// <param name="level">The nesting level of the call</param>
    /// <returns>
    /// The actual page which handled the set operation. Due to page being COWed, it may be a different page.
    /// 
    /// </returns>
    public Page Set(in SetContext ctx, IBatchContext batch, int level)
    {
        if (Header.BatchId != batch.BatchId)
        {
            // the page is from another batch, meaning, it's readonly. Copy
            var writable = batch.GetWritableCopy(_page);
            return new DataPage(writable).Set(ctx, batch, level);
        }

        var frames = Data.Frames;
        var nibble = NibblePath.FromKey(ctx.Key.BytesAsSpan, level).FirstNibble;

        var address = Data.Buckets[nibble];

        // the bucket is not null and represents a page jump, follow it
        if (address.IsNull == false && address.IsValidPageAddress)
        {
            var page = batch.GetAt(address);
            var updated = new DataPage(page).Set(ctx, batch, level + 1);

            // remember the updated
            Data.Buckets[nibble] = batch.GetAddress(updated);
            return _page;
        }

        // try update existing
        if (address.TryGetFrameIndex(out var frameIndex))
        {
            // there is at least one frame with this nibble
            while (frameIndex.IsNull == false)
            {
                ref var frame = ref frames[frameIndex.Value];

                if (frame.Key.Equals(ctx.Key))
                {
                    // update
                    frame.Balance = ctx.Balance;
                    frame.Nonce = ctx.Nonce;

                    return _page;
                }

                // jump to the next
                frameIndex = frame.Header.NextFrame;
            }
        }

        // fail to update, insert
        address.TryGetFrameIndex(out var previousFrameIndex);
        if (Data.FrameUsed.TrySetLowestBit(Payload.FrameCount, out var reserved))
        {
            ref var frame = ref Data.Frames[reserved];

            frame.Key = ctx.Key;
            frame.Balance = ctx.Balance;
            frame.Nonce = ctx.Nonce;

            // set the next to create the linked list
            frame.Header = FrameHeader.BuildContract(previousFrameIndex);

            // overwrite the bucket with the recent one
            Data.Buckets[nibble] = DbAddress.JumpToFrame(FrameIndex.FromIndex(reserved), Data.Buckets[nibble]);
            return _page;
        }

        // failed to find an empty frame,
        // select a bucket to empty and proceed with creating a child page
        // there must be at least one as otherwise it would be propagated down to the page
        var biggestBucket = DbAddress.Null;
        var index = -1;

        for (var i = 0; i < Payload.BucketCount; i++)
        {
            if (Data.Buckets[i].IsSamePage && Data.Buckets[i].SamePageJumpCount > biggestBucket.SamePageJumpCount)
            {
                biggestBucket = Data.Buckets[i];
                index = i;
            }
        }

        // address is set to the most counted
        var child = batch.GetNewPage(out Data.Buckets[index], true);
        var dataPage = new DataPage(child);

        // copy the data pointed by address to the new dataPage, clean up its bits from reserved frames
        biggestBucket.TryGetFrameIndex(out var biggestFrameChain);

        while (biggestFrameChain.IsNull == false)
        {
            ref var frame = ref frames[biggestFrameChain.Value];

            var set = new SetContext(frame.Key, frame.Balance, frame.Nonce);
            dataPage.Set(set, batch, (byte)(level + 1));

            // the frame is no longer used, clear it
            Data.FrameUsed.ClearBit(biggestFrameChain.Value);

            // jump to the next
            biggestFrameChain = frame.Header.NextFrame;
        }

        // there's a place on this page now, add it again
        Set(ctx, batch, level);

        return _page;
    }

    public void GetAccount(in Keccak key, IReadOnlyBatchContext batch, out Account result, int level)
    {
        var frames = Data.Frames;
        var nibble = NibblePath.FromKey(key.BytesAsSpan, level).FirstNibble;
        var bucket = Data.Buckets[nibble];

        // non-null page jump, follow it!
        if (bucket.IsNull == false && bucket.IsValidPageAddress)
        {
            new DataPage(batch.GetAt(bucket)).GetAccount(key, batch, out result, level + 1);
            return;
        }

        if (bucket.TryGetFrameIndex(out var frameIndex))
        {
            while (frameIndex.IsNull == false)
            {
                ref var frame = ref frames[frameIndex.Value];

                if (frame.Key.Equals(key))
                {
                    result = new Account(frame.Balance, frame.Nonce);
                    return;
                }

                frameIndex = frame.Header.NextFrame;
            }
        }

        result = default;
    }
}