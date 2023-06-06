﻿using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using HdrHistogram;
using Nethermind.Int256;
using Paprika.Chain;
using Paprika.Crypto;
using Paprika.Store;

[assembly: ExcludeFromCodeCoverage]

namespace Paprika.Runner;

public static class Program
{
    private const int BlockCount = PersistentDb ? 100_000 : 10_000;
    private const int RandomSampleSize = 260_000_000;
    private const int AccountsPerBlock = 1000;
    private const int MaxReorgDepth = 64;
    private const int FinalizeEvery = 32;

    private const int RandomSeed = 17;

    private const int NumberOfLogs = PersistentDb ? 100 : 10;

    private const long DbFileSize = PersistentDb ? 128 * Gb : 16 * Gb;
    private const long Gb = 1024 * 1024 * 1024L;

    private const CommitOptions Commit = CommitOptions.FlushDataOnly;

    private const int LogEvery = BlockCount / NumberOfLogs;

    private const bool PersistentDb = false;
    private const bool UseStorage = true;
    private const bool UseBigStorageAccount = false;
    private const int BigStorageAccountSlotCount = 1_000_000;
    private static readonly UInt256[] BigStorageAccountValues = new UInt256[BigStorageAccountSlotCount];

    public static async Task Main(String[] args)
    {
        var dir = Directory.GetCurrentDirectory();
        var dataPath = Path.Combine(dir, "db");

        if (PersistentDb)
        {
            if (Directory.Exists(dataPath))
            {
                Console.WriteLine("Deleting previous db...");
                Directory.Delete(dataPath, true);
            }

            Directory.CreateDirectory(dataPath);
            Console.WriteLine($"Using persistent DB on disk, located: {dataPath}");
        }
        else
        {
            Console.WriteLine("Using in-memory DB for greater speed.");
        }

        Console.WriteLine("Initializing db of size {0}GB", DbFileSize / Gb);
        Console.WriteLine("Starting benchmark with commit level {0}", Commit);

        PagedDb db = PersistentDb
            ? PagedDb.MemoryMappedDb(DbFileSize, MaxReorgDepth, dataPath)
            : PagedDb.NativeMemoryDb(DbFileSize, MaxReorgDepth);

        // consts
        var random = PrepareStableRandomSource();
        var bigStorageAccount = GetAccountKey(random, RandomSampleSize - Keccak.Size);
        var counter = 0;

        await using (var blockchain = new Blockchain(db))
        {
            bool bigStorageAccountCreated = false;

            // writing
            var writing = Stopwatch.StartNew();
            var parentBlockHash = Keccak.Zero;

            var toFinalize = new List<Keccak>();

            for (uint block = 1; block < BlockCount; block++)
            {
                var blockHash = Keccak.Compute(parentBlockHash.Span);
                using var worldState = blockchain.StartNew(parentBlockHash, blockHash, block);

                parentBlockHash = blockHash;

                for (var account = 0; account < AccountsPerBlock; account++)
                {
                    var key = GetAccountKey(random, counter);

                    worldState.SetAccount(key, GetAccountValue(counter));

                    if (UseStorage)
                    {
                        var storageAddress = GetStorageAddress(counter);
                        var storageValue = GetStorageValue(counter);
                        worldState.SetStorage(key, storageAddress, storageValue);
                    }

                    if (UseBigStorageAccount)
                    {
                        if (bigStorageAccountCreated == false)
                        {
                            worldState.SetAccount(bigStorageAccount, new Account(100, 100));
                            bigStorageAccountCreated = true;
                        }

                        var index = counter % BigStorageAccountSlotCount;
                        var storageAddress = GetStorageAddress(index);
                        var storageValue = GetBigAccountStorageValue(counter);
                        BigStorageAccountValues[index] = storageValue;

                        worldState.SetStorage(bigStorageAccount, storageAddress, storageValue);
                    }

                    counter++;
                }

                worldState.Commit();

                // finalize
                if (toFinalize.Count >= FinalizeEvery)
                {
                    // finalize first
                    blockchain.Finalize(toFinalize[0]);
                    toFinalize.Clear();
                }

                toFinalize.Add(blockHash);

                if (block > 0 & block % LogEvery == 0)
                {
                    ReportProgress(block, writing);
                    writing.Restart();
                }
            }

            // flush leftovers by adding one more block for now
            var lastBlock = toFinalize.Last();
            using var placeholder = blockchain.StartNew(lastBlock, Keccak.Compute(lastBlock.Span), BlockCount);
            placeholder.Commit();
            blockchain.Finalize(lastBlock);

            ReportProgress(BlockCount - 1, writing);

            Console.WriteLine("Finalizing the latest block. It may take a while as it will flush everything in the pipeline");
        }

        Console.WriteLine();
        Console.WriteLine("Writing in numbers:");
        Console.WriteLine("- {0} accounts per block", AccountsPerBlock);
        if (UseStorage)
        {
            Console.WriteLine("- each account with 1 storage slot written");
        }

        if (UseBigStorageAccount)
        {
            Console.WriteLine("- each account amends 1 slot in Big Storage account");
        }

        Console.WriteLine("- through {0} blocks ", BlockCount);
        Console.WriteLine("- generated accounts total number: {0} ", counter);
        Console.WriteLine("- space used: {0:F2}GB ", db.Megabytes / 1024);

        // waiting for finalization
        var read = db.BeginReadOnlyBatch();

        // reading
        Console.WriteLine();
        Console.WriteLine("Reading and asserting values...");

        var reading = Stopwatch.StartNew();

        var logReadEvery = counter / NumberOfLogs;
        for (var i = 0; i < counter; i++)
        {
            var key = GetAccountKey(random, i);
            var a = read.GetAccount(key);

            if (a != GetAccountValue(i))
            {
                throw new InvalidOperationException($"Invalid account state for account {i}!");
            }

            if (UseStorage)
            {
                var storageAddress = GetStorageAddress(i);
                var expectedStorageValue = GetStorageValue(i);
                var actualStorage = read.GetStorage(key, storageAddress);

                if (actualStorage != expectedStorageValue)
                {
                    throw new InvalidOperationException($"Invalid storage for account number {i}!");
                }
            }

            if (UseBigStorageAccount)
            {
                var index = i % BigStorageAccountSlotCount;
                var storageAddress = GetStorageAddress(index);
                var expectedStorageValue = BigStorageAccountValues[index];
                var actualStorage = read.GetStorage(bigStorageAccount, storageAddress);

                if (actualStorage != expectedStorageValue)
                {
                    throw new InvalidOperationException($"Invalid storage for big storage account at index {i}!");
                }
            }

            if (i > 0 & i % logReadEvery == 0)
            {
                var secondsPerRead = TimeSpan.FromTicks(reading.ElapsedTicks / logReadEvery).TotalSeconds;
                var readsPerSeconds = 1 / secondsPerRead;

                Console.WriteLine(
                    $"Reading at {i,9} out of {counter} accounts. Current speed: {readsPerSeconds:F1} reads/s");
                reading.Restart();
            }
        }

        Console.WriteLine("Reading state of all of {0} accounts from the last block took {1}",
            counter, reading.Elapsed);

        void ReportProgress(uint block, Stopwatch sw)
        {
            Console.WriteLine($"At block {block,9} / {BlockCount,9}");
        }
    }

    private static Account GetAccountValue(int counter)
    {
        return new Account((UInt256)counter, (UInt256)counter);
    }

    private static UInt256 GetStorageValue(int counter) => (UInt256)counter + 100000;

    private static UInt256 GetBigAccountStorageValue(int counter) => (UInt256)counter + 123456;

    private static Keccak GetAccountKey(Span<byte> accountsBytes, int counter)
    {
        // do the rolling over account bytes, so each is different but they don't occupy that much memory
        // it's not de Bruijn, but it's as best as possible.

        Keccak key = default;
        accountsBytes.Slice(counter, Keccak.Size).CopyTo(key.BytesAsSpan);
        return key;
    }

    private static Keccak GetStorageAddress(int counter)
    {
        // do the rolling over account bytes, so each is different but they don't occupy that much memory
        // it's not de Bruijn, but it's as best as possible.
        Keccak key = default;

        BinaryPrimitives.WriteInt32LittleEndian(key.BytesAsSpan, counter);
        return key;
    }

    private static byte[] PrepareStableRandomSource()
    {
        Console.WriteLine("Preparing random accounts addresses...");
        var accounts = GC.AllocateArray<byte>(RandomSampleSize);
        new Random(RandomSeed).NextBytes(accounts);
        Console.WriteLine("Accounts prepared");
        return accounts;
    }
}