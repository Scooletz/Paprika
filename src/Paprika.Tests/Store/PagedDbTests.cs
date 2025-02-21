using System.Buffers.Binary;
using FluentAssertions;
using Paprika.Crypto;
using Paprika.Data;
using Paprika.Store;

namespace Paprika.Tests.Store;

public class PagedDbTests
{
    private const long Mb = 1024 * 1024;
    private const int Seed = 17;

    [Test]
    public async Task BiggerTest()
    {
        const int accounts = 1;
        const int merkleCount = 31;

        ushort counter = 0;

        using var db = PagedDb.NativeMemoryDb(256 * Mb, 2);

        var random = new Random(Seed);

        using var batch = db.BeginNextBatch();

        for (var i = 0; i < accounts; i++)
        {
            var keccak = random.NextKeccak();

            // account first & data
            batch.SetRaw(Key.Account(keccak), GetData());
            batch.SetRaw(Key.StorageCell(NibblePath.FromKey(keccak), keccak), GetData());

            for (var j = 0; j < merkleCount; j++)
            {
                // all the Merkle values
                batch.SetRaw(Key.Merkle(NibblePath.FromKey(keccak).SliceTo(j)), GetData());
            }
        }

        await batch.Commit(CommitOptions.FlushDataAndRoot);

        // reset
        random = new Random(Seed);
        counter = 0;

        Assert();
        return;

        void Assert()
        {
            using var read = db.BeginReadOnlyBatch();

            for (var i = 0; i < accounts; i++)
            {
                var keccak = random.NextKeccak();

                read.TryGet(Key.Account(keccak), out var value).Should().BeTrue("The account should exist");
                value.SequenceEqual(GetData()).Should().BeTrue("The account should have data right");

                read.TryGet(Key.StorageCell(NibblePath.FromKey(keccak), keccak), out value).Should()
                    .BeTrue("The storage cell should exist");
                value.SequenceEqual(GetData()).Should().BeTrue("The storage cell should have data right");

                for (var j = 0; j < merkleCount; j++)
                {
                    // all the Merkle values
                    read.TryGet(Key.Merkle(NibblePath.FromKey(keccak).SliceTo(j)), out value).Should()
                        .BeTrue("The Merkle should exist");

                    var actual = value.ToArray();
                    var expected = GetData();

                    actual.SequenceEqual(expected).Should()
                        .BeTrue($"The Merkle @{j} of {i}th account should have data right");
                }
            }
        }

        byte[] GetData()
        {
            var bytes = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, counter);
            counter++;
            return bytes;
        }
    }

    [Test]
    public async Task DeleteByPrefix_Accounts()
    {
        using var db = PagedDb.NativeMemoryDb(16 * Mb, 2);

        var keccak0 = Values.Key0;
        var keccak1 = Values.Key0;
        var keccak2 = Values.Key0;
        var keccak3 = Values.Key0;
        var prefix = Values.Key0;

        keccak0.BytesAsSpan[^1] = 0x01;
        keccak1.BytesAsSpan[^1] = 0x02;
        keccak2.BytesAsSpan[^1] = 0x03;
        keccak3.BytesAsSpan[^1] = 0x04;
        prefix.BytesAsSpan[^1] = 0x00;

        // Set data
        using var batch = db.BeginNextBatch();

        var v = new byte[] { 1 };
        batch.SetRaw(Key.Account(keccak0), v);
        batch.SetRaw(Key.Account(keccak1), v);
        batch.SetRaw(Key.Account(keccak2), v);
        batch.SetRaw(Key.Account(keccak3), v);

        await batch.Commit(CommitOptions.FlushDataAndRoot);

        // Delete by prefix
        using var batch2 = db.BeginNextBatch();
        batch.DeleteByPrefix(Key.Merkle(NibblePath.FromKey(prefix).SliceTo(NibblePath.KeccakNibbleCount - 1)));
        await batch2.Commit(CommitOptions.FlushDataAndRoot);

        using var read = db.BeginReadOnlyBatch();

        read.TryGet(Key.Account(keccak0), out _).Should().BeFalse();
        read.TryGet(Key.Account(keccak1), out _).Should().BeFalse();
        read.TryGet(Key.Account(keccak2), out _).Should().BeFalse();
        read.TryGet(Key.Account(keccak3), out _).Should().BeFalse();
    }

    [Test]
    public async Task DeleteByPrefix_Storage()
    {
        using var db = PagedDb.NativeMemoryDb(16 * Mb, 2);

        var account = Values.Key2;

        var keccak0 = Values.Key0;
        var keccak1 = Values.Key0;
        var keccak2 = Values.Key0;
        var keccak3 = Values.Key0;
        var prefix = Values.Key0;

        keccak0.BytesAsSpan[^1] = 0x01;
        keccak1.BytesAsSpan[^1] = 0x02;
        keccak2.BytesAsSpan[^1] = 0x03;
        keccak3.BytesAsSpan[^1] = 0x04;
        prefix.BytesAsSpan[^1] = 0x00;

        // Set data
        using var batch = db.BeginNextBatch();

        var v = new byte[] { 1 };
        batch.SetRaw(Key.StorageCell(NibblePath.FromKey(account), keccak0), v);
        batch.SetRaw(Key.StorageCell(NibblePath.FromKey(account), keccak1), v);
        batch.SetRaw(Key.StorageCell(NibblePath.FromKey(account), keccak2), v);
        batch.SetRaw(Key.StorageCell(NibblePath.FromKey(account), keccak3), v);

        await batch.Commit(CommitOptions.FlushDataAndRoot);

        // Delete by prefix
        using var batch2 = db.BeginNextBatch();
        batch.DeleteByPrefix(Key.Raw(NibblePath.FromKey(account), DataType.Merkle,
            NibblePath.FromKey(prefix).SliceTo(NibblePath.KeccakNibbleCount - 1)));
        await batch2.Commit(CommitOptions.FlushDataAndRoot);

        using var read = db.BeginReadOnlyBatch();

        read.TryGet(Key.StorageCell(NibblePath.FromKey(account), keccak0), out _).Should().BeFalse();
        read.TryGet(Key.StorageCell(NibblePath.FromKey(account), keccak1), out _).Should().BeFalse();
        read.TryGet(Key.StorageCell(NibblePath.FromKey(account), keccak2), out _).Should().BeFalse();
        read.TryGet(Key.StorageCell(NibblePath.FromKey(account), keccak3), out _).Should().BeFalse();
    }

    [Test]
    public async Task Multiple_storages_per_commit()
    {
        var account = Values.Key0;

        const int accounts = 512 * 1024;
        const int size = 10_000;

        using var db = PagedDb.NativeMemoryDb(8 * 1024 * Mb, 2);

        var value = new byte[1] { 13 };

        using var batch = db.BeginNextBatch();

        // First, set the account that will be tested later
        batch.SetRaw(Key.StorageCell(NibblePath.FromKey(account), Keccak.EmptyTreeHash), value);

        // Then flood the id cache so that the account is flushed down
        for (var i = 0; i < accounts; i++)
        {
            Keccak keccak = default;
            BinaryPrimitives.WriteInt32LittleEndian(keccak.BytesAsSpan, i);

            batch.SetRaw(Key.StorageCell(NibblePath.FromKey(keccak), keccak), value);
        }

        await batch.Commit(CommitOptions.FlushDataAndRoot);

        await InsertLoadsOfStorages(db, account);

        static async Task InsertLoadsOfStorages(IDb db, Keccak account)
        {
            var value = new byte[4];

            using var batch2 = db.BeginNextBatch();

            // Now try to store many
            for (var i = 0; i < size; i++)
            {
                Keccak keccak = default;
                BinaryPrimitives.WriteInt32LittleEndian(keccak.BytesAsSpan, i);
                BinaryPrimitives.WriteInt32LittleEndian(value, i);

                batch2.SetRaw(Key.StorageCell(NibblePath.FromKey(account), keccak), value);
            }

            await batch2.Commit(CommitOptions.FlushDataAndRoot);
        }
    }

    [Test]
    [Ignore("Heavily dependent on the WriteId method in the root. For smaller networks it might be much better, " +
            "clustering the values in smaller number of buckets")]
    [Category(Categories.LongRunning)]
    public async Task FanOut()
    {
        // To saturate fan out
        const int size = DbAddressList.Of1024.Count * DbAddressList.Of1024.Count;

        using var db = PagedDb.NativeMemoryDb((long)8 * 1024 * Mb, 2);

        var value = new byte[4];

        using var batch = db.BeginNextBatch();

        for (var i = 0; i < size; i++)
        {
            Keccak keccak = default;
            BinaryPrimitives.WriteInt32LittleEndian(keccak.BytesAsSpan, i);
            BinaryPrimitives.WriteInt32LittleEndian(value, i);

            batch.SetRaw(Key.StorageCell(NibblePath.FromKey(keccak), keccak), value);
        }

        await batch.Commit(CommitOptions.FlushDataAndRoot);

        Assert(db);
        return;

        static void Assert(PagedDb db)
        {
            var expected = new byte[4];

            using var read = db.BeginReadOnlyBatch();

            for (var i = 0; i < size; i++)
            {
                Keccak keccak = default;
                BinaryPrimitives.WriteInt32LittleEndian(keccak.BytesAsSpan, i);
                BinaryPrimitives.WriteInt32LittleEndian(expected, i);

                var storageCell = Key.StorageCell(NibblePath.FromKey(keccak), keccak);
                var retrieved = read.TryGet(storageCell, out var actual);
                retrieved.Should().BeTrue();

                actual.SequenceEqual(expected).Should().BeTrue();
            }

            var stats = new StatisticsVisitor(db);
            read.Accept(stats);

            // stats.AbandonedCount.Should().BeGreaterThan(0);
            stats.Ids.PageCount.Should().BeGreaterThan(0);
            stats.Storage.PageCount.Should().BeGreaterThan(0);
            stats.Storage.DataPagePageCountPerNibblePathDepth[0].Should().Be(size);
        }
    }

    [Test]
    public async Task HasState_queries_state_properly()
    {
        var keccak = Values.Key0;

        const byte historyDepth = 16;
        const byte spins = 51;

        using var db = PagedDb.NativeMemoryDb(2 * Mb, historyDepth);

        var value = new byte[4];

        for (uint i = 1; i < spins; i++)
        {
            using var batch = db.BeginNextBatch();

            Keccak hash = default;
            BinaryPrimitives.WriteUInt32LittleEndian(hash.BytesAsSpan, i);
            BinaryPrimitives.WriteUInt32LittleEndian(value, i);

            batch.SetRaw(Key.StorageCell(NibblePath.FromKey(keccak), keccak), value);
            batch.SetMetadata(i, hash);
            await batch.Commit(CommitOptions.FlushDataAndRoot);
        }

        for (uint i = 1; i < spins; i++)
        {
            Keccak hash = default;
            BinaryPrimitives.WriteUInt32LittleEndian(hash.BytesAsSpan, i);

            var shouldHaveState = i >= spins - historyDepth;
            db.HasState(hash).Should().Be(shouldHaveState, $"Failed at {i}th spin");

            if (shouldHaveState)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(value, i);

                using var read = db.BeginReadOnlyBatch(hash);
                read.Metadata.StateHash.Should().Be(hash);
                read.Metadata.BlockNumber.Should().Be(i);
                AssertRead(value, keccak, read);
            }
        }

        return;

        static void AssertRead(byte[] bytes, in Keccak key, IReadOnlyBatch read)
        {
            read.TryGet(Key.StorageCell(NibblePath.FromKey(key), key), out var existing).Should().BeTrue();
            existing.SequenceEqual(bytes).Should().BeTrue();
        }
    }

    [Test]
    public async Task Snapshotting()
    {
        const byte historyDepth = 16;

        var data = new byte[32];

        using var db = PagedDb.NativeMemoryDb(32 * Mb, historyDepth);

        for (uint i = 0; i < historyDepth * 2; i++)
        {
            var keccak = default(Keccak);

            BinaryPrimitives.WriteUInt32LittleEndian(keccak.BytesAsSpan, i);

            using var batch = db.BeginNextBatch();

            // account first & data
            batch.SetRaw(Key.Account(keccak), data);
            batch.SetRaw(Key.StorageCell(NibblePath.FromKey(keccak), keccak), data);

            batch.SetMetadata(i, Keccak.Compute(keccak.BytesAsSpan));

            await batch.Commit(CommitOptions.FlushDataAndRoot);
        }

        // Asserts
        var snapshot = db.SnapshotAll();

        snapshot.Length.Should().Be(historyDepth);

        Array.Sort(snapshot, (a, b) => a.Metadata.BlockNumber.CompareTo(b.Metadata.BlockNumber));
        var start = snapshot[0].Metadata.BlockNumber;

        for (uint i = 0; i < historyDepth; i++)
        {
            snapshot[i].Metadata.BlockNumber.Should().Be(start + i);
        }

        foreach (var batch in snapshot)
        {
            batch.Dispose();
        }
    }
}