using System.Buffers.Binary;
using FluentAssertions;
using NUnit.Framework;
using Paprika.Crypto;
using Paprika.Data;
using Paprika.Store;
using static Paprika.Tests.Values;

namespace Paprika.Tests.Store;

public class DbTests
{
    private const int SmallDb = 256 * Page.PageSize;
    private const int MB = 1024 * 1024;
    private const int MB16 = 16 * MB;
    private const int MB64 = 64 * MB;
    private const int MB128 = 128 * MB;
    private const int MB256 = 256 * MB;

    [Test]
    public async Task Simple()
    {
        const int max = 1024;

        using var db = PagedDb.NativeMemoryDb(MB256);

        for (int i = 0; i < max; i++)
        {
            var key = GetKey(i);

            using var batch = db.BeginNextBatch();

            var value = GetValue(i);

            batch.SetAccount(key, value);
            batch.SetStorage(key, key, value);
            batch.SetRaw(Key.Merkle(NibblePath.FromKey(key)), value);

            await batch.Commit(CommitOptions.FlushDataAndRoot);
        }

        using var read = db.BeginNextBatch();

        Assert(read);
        return;

        static void Assert(IReadOnlyBatch read)
        {
            for (int i = 0; i < max; i++)
            {
                var key = GetKey(i);
                var expected = GetValue(i);

                read.ShouldHaveAccount(key, expected);
                read.AssertStorageValue(key, key, expected);
                read.TryGet(Key.Merkle(NibblePath.FromKey(key)), out var value).Should().BeTrue();
                if (value.SequenceEqual(expected) == false)
                {
                    NUnit.Framework.Assert.Fail(
                        $"Failed at {i} where {ParseValue(value)} should have been equal to {ParseValue(expected)}");
                }
            }
        }
    }

    [TestCase(100_000, 1, TestName = "Long history, single account")]
    [TestCase(500, 2_000, TestName = "Short history, many accounts")]
    public async Task Page_reuse(int blockCount, int accountsCount)
    {
        const int size = MB256;

        using var db = PagedDb.NativeMemoryDb(size);

        for (var i = 0; i < blockCount; i++)
        {
            // ReSharper disable once ConvertToUsingDeclaration
            using (var batch = db.BeginNextBatch())
            {
                for (var account = 0; account < accountsCount; account++)
                {
                    batch.SetAccount(GetKey(account), GetValue(account));
                }

                await batch.Commit(CommitOptions.FlushDataOnly);
            }
        }

        Console.WriteLine($"Uses {db.Megabytes:P}MB out of pre-allocated {size / MB}MB od disk.");

        AssertPageMetadataAssigned(db);
    }

    [Test]
    public async Task Readonly_transaction_block_till_they_are_released()
    {
        const int size = MB16;
        const int blocksDuringReadAcquired = 500; // the number should be smaller than the number of buckets in the root
        const int blocksPostRead = 10_000;
        const int start = -1;

        using var db = PagedDb.NativeMemoryDb(size);

        // write first value
        using (var block = db.BeginNextBatch())
        {
            block.SetAccount(Key0, GetValue(start));
            await block.Commit(CommitOptions.FlushDataOnly);
        }

        var actualSize = await SetAndSnapshotSize(db);

        // write again
        for (var i = 0; i < blocksPostRead; i++)
        {
            // ReSharper disable once ConvertToUsingDeclaration
            using (var block = db.BeginNextBatch())
            {
                var value = GetValue(i + start);

                block.SetAccount(Key0, value);
                await block.Commit(CommitOptions.FlushDataOnly);
            }
        }

        db.Megabytes.Should().Be(actualSize, "Database should not grow without read transaction active.");

        Console.WriteLine($"Uses {db.Megabytes:P}MB out of pre-allocated {actualSize / MB}MB od disk.");

        AssertPageMetadataAssigned(db);
        return;

        static async Task<double> SetAndSnapshotSize(PagedDb db)
        {
            using var read = db.BeginReadOnlyBatch();

            for (var i = 0; i < blocksDuringReadAcquired; i++)
            {
                // ReSharper disable once ConvertToUsingDeclaration
                using (var block = db.BeginNextBatch())
                {
                    // assert previous
                    block.ShouldHaveAccount(Key0, GetValue(i + start));

                    // write current
                    block.SetAccount(Key0, GetValue(i));

                    block.VerifyDbPagesOnCommit();
                    await block.Commit(CommitOptions.FlushDataOnly);

                    read.ShouldHaveAccount(Key0, GetValue(start));
                }
            }

            return db.Megabytes;
        }
    }

    [Test]
    public async Task State_and_storage()
    {
        const int size = MB64;
        using var db = PagedDb.NativeMemoryDb(size);

        const int count = 10_000;

        using (var batch = db.BeginNextBatch())
        {
            for (int i = 0; i < count; i++)
            {
                var address = GetStorageAddress(i);

                batch.SetAccount(Key0, GetValue(i));
                batch.SetStorage(Key0, address, GetValue(i));
            }

            await batch.Commit(CommitOptions.FlushDataOnly);
        }

        using (var read = db.BeginReadOnlyBatch())
        {
            for (int i = 0; i < count; i++)
            {
                var address = GetStorageAddress(i);
                read.AssertStorageValue(Key0, address, GetValue(i));
            }
        }

        AssertPageMetadataAssigned(db);
        return;

        static Keccak GetStorageAddress(int i)
        {
            var address = Key1A;
            BinaryPrimitives.WriteInt32LittleEndian(address.BytesAsSpan, i);
            return address;
        }
    }

    [Test]
    public async Task Cross_block_entries()
    {
        const int size = MB64;
        using var db = PagedDb.NativeMemoryDb(size);

        const int count = 1600;

        for (var i = 0; i < count; i++)
        {
            using (var batch = db.BeginNextBatch())
            {
                var address = GetStorageAddress(i);

                batch.SetAccount(Key0, GetValue(i));
                batch.SetStorage(Key0, address, GetValue(i));

                await batch.Commit(CommitOptions.FlushDataAndRoot);
            }
        }

        using (var read = db.BeginReadOnlyBatch())
        {
            for (int i = 0; i < count; i++)
            {
                var address = GetStorageAddress(i);
                read.AssertStorageValue(Key0, address, GetValue(i));
            }
        }

        AssertPageMetadataAssigned(db);
        return;

        static Keccak GetStorageAddress(int i)
        {
            var address = Key1A;
            BinaryPrimitives.WriteInt32LittleEndian(address.BytesAsSpan, i);
            return address;
        }
    }

    [Test]
    public async Task Spin_large()
    {
        var account = Keccak.EmptyTreeHash;

        const int size = MB256;
        using var db = PagedDb.NativeMemoryDb(size);

        const int batches = 25;
        const int storageSlots = 20_000;
        const int storageKeyLength = 32;

        var value = new byte[32];
        var storageKeys = new byte[storageSlots + storageKeyLength];

        var random = new Random(13);
        random.NextBytes(storageKeys);
        random.NextBytes(value);

        using var reads = new CompositeDisposable<IVisitableReadOnlyBatch>();

        for (var i = 0; i < batches; i++)
        {
            using var batch = db.BeginNextBatch();

            for (var slot = 0; slot < storageSlots; slot++)
            {
                batch.SetStorage(account, GetStorageAddress(slot), value);
            }

            await batch.Commit(CommitOptions.FlushDataAndRoot);

            reads.Add(db.BeginReadOnlyBatch());
        }

        foreach (var read in reads)
        {
            for (var slot = 0; slot < storageSlots; slot++)
            {
                read.AssertStorageValue(account, GetStorageAddress(slot), value);
            }
        }

        return;

        Keccak GetStorageAddress(int i)
        {
            Keccak result = default;
            storageKeys
                .AsSpan(i, storageKeyLength)
                .CopyTo(result.BytesAsSpan);
            return result;
        }
    }

    private static void AssertPageMetadataAssigned(PagedDb db)
    {
        foreach (var page in db.UnsafeEnumerateNonRoot())
        {
            var header = page.Header;

            header.BatchId.Should().BeGreaterThan(0);
            var types = Enum.GetValues<PageType>().ToList();
            types.Remove(PageType.None);
            header.PageType.Should().BeOneOf(types);
            header.PaprikaVersion.Should().Be(1);
        }
    }

    private static Keccak GetKey(int i)
    {
        var keccak = Keccak.EmptyTreeHash;
        BinaryPrimitives.WriteInt32LittleEndian(keccak.BytesAsSpan, i);
        return keccak;
    }

    static byte[] GetValue(int i) => BitConverter.GetBytes(i);
    static int ParseValue(ReadOnlySpan<byte> b) => BitConverter.ToInt32(b);
}