﻿using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Nethermind.Int256;
using Paprika.Crypto;
using Paprika.Data;
using Paprika.Store;
using Paprika.Utils;

namespace Paprika.Chain;

/// <summary>
/// The blockchain is the main component of Paprika, that can deal with latest, safe and finalized blocks.
///
/// For latest and safe, it uses a notion of block, that allows switching heads, querying from different heads etc.
/// For the finalized blocks, they are queued to a <see cref="Channel"/> that is consumed by a flushing mechanism
/// using the <see cref="PagedDb"/>.
/// </summary>
/// <remarks>
/// The current implementation assumes a single threaded access. For multi-threaded, some adjustments will be required.
/// The following should be covered:
/// 1. reading a state at a given time based on the root. Should never fail.
/// 2. TBD
/// </remarks>
public class Blockchain : IAsyncDisposable
{
    // allocate 1024 pages (4MB) at once
    private readonly PagePool _pool = new(1024);

    // It's unlikely that there will be many blocks per number as it would require the network to be heavily fragmented. 
    private readonly ConcurrentDictionary<uint, Block[]> _blocksByNumber = new();
    private readonly ConcurrentDictionary<Keccak, Block> _blocksByHash = new();
    private readonly Channel<Block> _finalizedChannel;
    private readonly ConcurrentQueue<(IReadOnlyBatch reader, IEnumerable<uint> blockNumbers)> _alreadyFlushedTo;

    private readonly PagedDb _db;
    private uint _lastFinalized;
    private IReadOnlyBatch _dbReader;
    private readonly Task _flusher;

    public Blockchain(PagedDb db)
    {
        _db = db;
        _finalizedChannel = Channel.CreateUnbounded<Block>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        _alreadyFlushedTo = new();
        _dbReader = db.BeginReadOnlyBatch();

        _flusher = FinalizedFlusher();
    }

    /// <summary>
    /// The flusher method run as a reader of the <see cref="_finalizedChannel"/>.
    /// </summary>
    private async Task FinalizedFlusher()
    {
        var reader = _finalizedChannel.Reader;

        try
        {
            while (await reader.WaitToReadAsync())
            {
                // bulk all the finalized blocks in one batch
                List<uint> flushedBlockNumbers = new();

                var watch = Stopwatch.StartNew();

                using var batch = _db.BeginNextBatch();
                while (watch.Elapsed < FlushEvery && reader.TryRead(out var block))
                {
                    flushedBlockNumbers.Add(block.BlockNumber);

                    batch.SetMetadata(block.BlockNumber, block.Hash);
                    block.Apply(batch);
                }

                await batch.Commit(CommitOptions.FlushDataAndRoot);

                _alreadyFlushedTo.Enqueue((_db.BeginReadOnlyBatch(), flushedBlockNumbers));
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
    }

    private static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(2);

    public IWorldState StartNew(Keccak parentKeccak, Keccak blockKeccak, uint blockNumber)
    {
        ReuseAlreadyFlushed();

        var parent = _blocksByHash.TryGetValue(parentKeccak, out var p) ? p : null;

        // not added to dictionaries until Commit
        return new Block(parentKeccak, parent, blockKeccak, blockNumber, this);
    }

    public void Finalize(Keccak keccak)
    {
        ReuseAlreadyFlushed();

        // find the block to finalize
        if (_blocksByHash.TryGetValue(keccak, out var block) == false)
        {
            throw new Exception("Block that is marked as finalized is not present");
        }

        Debug.Assert(block.BlockNumber > _lastFinalized,
            "Block that is finalized should have a higher number than the last finalized");

        // gather all the blocks between last finalized and this.
        var count = block.BlockNumber - _lastFinalized;
        Stack<Block> finalized = new((int)count);
        for (var blockNumber = block.BlockNumber; blockNumber > _lastFinalized; blockNumber--)
        {
            // to finalize
            finalized.Push(block);

            if (block.TryGetParent(out block) == false)
            {
                // no next block, break
                break;
            }
        }

        while (finalized.TryPop(out block))
        {
            // publish for the PagedDb
            _finalizedChannel.Writer.TryWrite(block);
        }

        _lastFinalized += count;
    }

    /// <summary>
    /// Finds the given key using the db reader representing the finalized blocks.
    /// </summary>
    private bool TryReadFromFinalized(in Key key, out ReadOnlySpan<byte> result)
    {
        return _dbReader.TryGet(key, out result);
    }

    private void ReuseAlreadyFlushed()
    {
        while (_alreadyFlushedTo.TryDequeue(out var flushed))
        {
            // TODO: this is wrong, non volatile access, no visibility checks. For now should do.

            // set the last reader
            var previous = _dbReader;

            _dbReader = flushed.reader;

            previous.Dispose();

            foreach (var blockNumber in flushed.blockNumbers)
            {
                _lastFinalized = Math.Max(blockNumber, _lastFinalized);

                // clean blocks with a given number
                if (_blocksByNumber.Remove(blockNumber, out var blocks))
                {
                    foreach (var block in blocks)
                    {
                        // remove by hash as well
                        _blocksByHash.TryRemove(block.Hash, out _);
                        block.Dispose();
                    }
                }
            }
        }
    }

    /// <summary>
    /// Represents a block that is a result of ExecutionPayload, storing it in a in-memory trie
    /// </summary>
    private class Block : RefCountingDisposable, IWorldState
    {
        public Keccak Hash { get; }
        public Keccak ParentHash { get; }
        public uint BlockNumber { get; }

        // a weak-ref to allow collecting blocks once they are finalized
        private readonly WeakReference<Block>? _parent;
        private readonly BloomFilter _bloom;

        private readonly Blockchain _blockchain;

        private readonly List<Page> _pages = new();
        private readonly List<RawFixedMap> _maps = new();

        public Block(Keccak parentHash, Block? parent, Keccak hash, uint blockNumber, Blockchain blockchain)
        {
            _parent = parent != null ? new WeakReference<Block>(parent) : null;
            _blockchain = blockchain;

            Hash = hash;
            BlockNumber = blockNumber;
            ParentHash = parentHash;

            // rent pages for the bloom
            _bloom = new BloomFilter(Rent());
        }

        private Page Rent()
        {
            var page = Pool.Rent(true);
            _pages.Add(page);
            return page;
        }

        /// <summary>
        /// Commits the block to the block chain.
        /// </summary>
        public void Commit()
        {
            // set to blocks in number and in blocks by hash
            _blockchain._blocksByNumber.AddOrUpdate(BlockNumber,
                static (_, block) => new[] { block },
                static (_, existing, block) =>
                {
                    var array = existing;
                    Array.Resize(ref array, array.Length + 1);
                    array[^1] = block;
                    return array;
                }, this);

            _blockchain._blocksByHash.TryAdd(Hash, this);
        }

        private PagePool Pool => _blockchain._pool;

        public UInt256 GetStorage(in Keccak account, in Keccak address)
        {
            var bloom = BloomForStorageOperation(account, address);
            var key = Key.StorageCell(NibblePath.FromKey(account), address);

            using var owner = TryGet(bloom, key);
            if (owner.IsEmpty == false)
            {
                Serializer.ReadStorageValue(owner.Span, out var value);
                return value;
            }

            // TODO: memory ownership of the span
            if (_blockchain.TryReadFromFinalized(in key, out var span))
            {
                Serializer.ReadStorageValue(span, out var value);
                return value;
            }

            return default;
        }

        public Account GetAccount(in Keccak account)
        {
            var bloom = BloomForAccountOperation(account);
            var key = Key.Account(NibblePath.FromKey(account));

            using var owner = TryGet(bloom, key);
            if (owner.IsEmpty == false)
            {
                Serializer.ReadAccount(owner.Span, out var result);
                return result;
            }

            // TODO: memory ownership of the span
            if (_blockchain.TryReadFromFinalized(in key, out var span))
            {
                Serializer.ReadAccount(span, out var result);
                return result;
            }

            return default;
        }

        private static int BloomForStorageOperation(in Keccak key, in Keccak address) =>
            key.GetHashCode() ^ address.GetHashCode();

        private static int BloomForAccountOperation(in Keccak key) => key.GetHashCode();

        public void SetAccount(in Keccak key, in Account account)
        {
            _bloom.Set(BloomForAccountOperation(key));

            var path = NibblePath.FromKey(key);

            Span<byte> payload = stackalloc byte[Serializer.BalanceNonceMaxByteCount];
            payload = Serializer.WriteAccount(payload, account);

            Set(Key.Account(path), payload);
        }

        public void SetStorage(in Keccak key, in Keccak address, UInt256 value)
        {
            _bloom.Set(BloomForStorageOperation(key, address));

            var path = NibblePath.FromKey(key);

            Span<byte> payload = stackalloc byte[Serializer.StorageValueMaxByteCount];
            payload = Serializer.WriteStorageValue(payload, value);

            Set(Key.StorageCell(path, address), payload);
        }

        private void Set(in Key key, in ReadOnlySpan<byte> payload)
        {
            RawFixedMap map;

            if (_maps.Count == 0)
            {
                map = new RawFixedMap(Rent());
                _maps.Add(map);
            }
            else
            {
                map = _maps[^1];
            }

            if (map.TrySet(key, payload))
            {
                return;
            }

            // not enough space, allocate one more
            map = new RawFixedMap(Rent());
            _maps.Add(map);

            map.TrySet(key, payload);
        }

        /// <summary>
        /// A recursive search through the block and its parent until null is found at the end of the weekly referenced
        /// chain.
        /// </summary>
        private ReadOnlySpanOwner<byte> TryGet(int bloom, in Key key)
        {
            var acquired = TryAcquireLease();
            if (acquired == false)
            {
                return default;
            }

            // lease: acquired
            if (_bloom.IsSet(bloom))
            {
                // go from last to youngest to find the recent value
                for (int i = _maps.Count - 1; i >= 0; i--)
                {
                    if (_maps[i].TryGet(key, out var span))
                    {
                        // return with owned lease
                        return new ReadOnlySpanOwner<byte>(span, this);
                    }
                }
            }

            // lease no longer needed
            ReleaseLeaseOnce();

            // search the parent
            if (TryGetParent(out var parent))
                return parent.TryGet(bloom, key);

            return default;
        }

        public bool TryGetParent([MaybeNullWhen(false)] out Block parent)
        {
            parent = default;
            return _parent != null && _parent.TryGetTarget(out parent);
        }

        protected override void CleanUp()
        {
            // return all the pages
            foreach (var page in _pages)
            {
                Pool.Return(page);
            }
        }

        public void Apply(IBatch batch)
        {
            foreach (var map in _maps)
            {
                map.Apply(batch);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // mark writer as complete
        _finalizedChannel.Writer.Complete();

        // await the flushing task
        await _flusher;

        // once the flushing is done, dispose the pool
        _pool.Dispose();
    }
}