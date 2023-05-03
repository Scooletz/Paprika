﻿using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using HdrHistogram;
using Nethermind.Int256;
using Paprika.Crypto;
using Paprika.Db;

[assembly: ExcludeFromCodeCoverage]

namespace Paprika.Runner;

public static class Program
{
    private const int BlockCount = 2_000;
    private const int RandomSampleSize = 260_000_000;
    private const int AccountsPerBlock = 1000;

    private const int RandomSeed = 17;

    private const int NumberOfLogs = 10;

    private const long DbFileSize = 8 * Gb;
    private const long Gb = 1024 * 1024 * 1024L;
    private const CommitOptions Commit = CommitOptions.FlushDataOnly;
    private const int LogEvery = BlockCount / NumberOfLogs;

    public static void Main(String[] args)
    {
        var dir = Directory.GetCurrentDirectory();
        var dataPath = Path.Combine(dir, "db");

        if (Directory.Exists(dataPath))
        {
            Console.WriteLine("Deleting previous db...");
            Directory.Delete(dataPath, true);
        }

        Directory.CreateDirectory(dataPath);

        Console.WriteLine("Initializing db of size {0}GB", DbFileSize / Gb);
        Console.WriteLine("Starting benchmark with commit level {0}", Commit);

        var histograms = new
        {
            allocated = new IntHistogram(short.MaxValue, 3),
            reused = new IntHistogram(short.MaxValue, 3),
            total = new IntHistogram(short.MaxValue, 3),
        };

        var db = new MemoryMappedPagedDb(DbFileSize, 64, dataPath, metrics =>
        {
            histograms.allocated.RecordValue(metrics.PagesAllocated);
            histograms.reused.RecordValue(metrics.PagesReused);
            histograms.total.RecordValue(metrics.TotalPagesWritten);
        });

        var random = PrepareStableRandomSource();

        var counter = 0;

        // writing
        var writing = Stopwatch.StartNew();

        for (uint block = 0; block < BlockCount; block++)
        {
            using var batch = db.BeginNextBlock();

            for (var account = 0; account < AccountsPerBlock; account++)
            {
                var key = GetAccountKey(random, counter);

                batch.Set(key, new Account(block, block));
                batch.SetStorage(key, GetStorageAddress(random, counter), (UInt256)counter);
                counter++;
            }

            if (block > 0 & block % LogEvery == 0)
            {
                // log
                Console.WriteLine("At block: {0,4}", block);
                Console.WriteLine("- total avg. speed {0}/block", TimeSpan.FromTicks(writing.ElapsedTicks / LogEvery));
                Console.WriteLine("- disk space used {0:F2}GB", db.ActualMegabytesOnDisk / 1024);

                Console.WriteLine("- 90th percentiles:");
                Write90Th(histograms.allocated, "new pages allocated");
                Write90Th(histograms.reused, "pages reused allocated");
                Write90Th(histograms.total, "total pages written");

                writing.Restart();

                Console.WriteLine();
            }

            batch.Commit(Commit);
        }

        Console.WriteLine("Writing state of {0} accounts per block, each with 1 storage, through {1} blocks, generated {2} accounts, used {3:F2}GB",
            AccountsPerBlock, BlockCount, counter, db.ActualMegabytesOnDisk / 1024);

        // reading
        var reading = Stopwatch.StartNew();
        using var read = db.BeginReadOnlyBatch();

        for (var account = 0; account < counter; account++)
        {
            var key = GetAccountKey(random, counter);
            var storage = GetStorageAddress(random, counter);
            read.GetAccount(key);
            read.GetStorage(key, storage);
        }

        Console.WriteLine("Reading state of all of {0} accounts from the last block took {1}",
            counter, reading.Elapsed);

        Console.WriteLine("90th percentiles:");
        Write90Th(histograms.allocated, "new pages allocated");
        Write90Th(histograms.reused, "pages reused allocated");
        Write90Th(histograms.total, "total pages written");
    }

    private static void Write90Th(HistogramBase histogram, string name)
    {
        Console.WriteLine($"   - {name} per block: {histogram.GetValueAtPercentile(0.9)}");
        histogram.Reset();
    }

    private static Keccak GetAccountKey(Span<byte> accountsBytes, int counter)
    {
        // do the rolling over account bytes, so each is different but they don't occupy that much memory
        // it's not de Bruijn, but it's as best as possible.

        Keccak key = default;
        accountsBytes.Slice(counter, Keccak.Size).CopyTo(key.BytesAsSpan);
        return key;
    }

    private static Keccak GetStorageAddress(Span<byte> random, int counter)
    {
        // do the rolling over account bytes, so each is different but they don't occupy that much memory
        // it's not de Bruijn, but it's as best as possible.
        Keccak key = default;

        // go from the end
        var address = RandomSampleSize - counter - Keccak.Size;
        random.Slice(address, Keccak.Size).CopyTo(key.BytesAsSpan);
        return key;
    }

    private static unsafe Span<byte> PrepareStableRandomSource()
    {
        Console.WriteLine("Preparing random accounts addresses...");
        var accounts = new Span<byte>(NativeMemory.Alloc((UIntPtr)RandomSampleSize), RandomSampleSize);
        new Random(RandomSeed).NextBytes(accounts);
        Console.WriteLine("Accounts prepared");
        return accounts;
    }
}