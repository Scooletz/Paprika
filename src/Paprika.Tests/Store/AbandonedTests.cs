using System.Runtime.InteropServices;
using FluentAssertions;
using NUnit.Framework;
using Paprika.Crypto;
using Paprika.Data;
using Paprika.Store;

namespace Paprika.Tests.Store;

public class AbandonedTests : BasePageTests
{
    [Test]
    public void Page_capacity()
    {
        const int expected = 2000;

        var addresses = Enumerable.Range(1, expected).Select(i => new DbAddress((uint)i)).ToArray();

        var page = new AbandonedPage(Page.DevOnlyNativeAlloc());
        page.AsPage().Clear();

        var added = new Stack<DbAddress>();

        foreach (var address in addresses)
        {
            if (page.TryPush(address))
            {
                added.Push(address);
            }
        }

        added.Count.Should().Be(expected);

        while (added.TryPop(out var dequeued))
        {
            page.TryPop(out var popped).Should().BeTrue();
            popped.Should().Be(dequeued);
        }
    }

    [Test]
    public void Properly_handles_page_addresses_that_are_packed_1()
    {
        var list = new AbandonedList();

        const uint batchId = 10;

        var batch = new TestBatchContext(batchId);
        batch.GetNewPage(out var a, false);
        batch.GetNewPage(out var b, false);

        list.Register([a, b], batch);

        var next = batch.Next();

        list.TryGet(out var oneOfReused, next.BatchId, next).Should().BeTrue();

        var addresses = new HashSet<DbAddress> { a, b };
        addresses.Remove(oneOfReused).Should().BeTrue($"The {oneOfReused} should be in the set");
        var last = addresses.Single();

        list.GetCurrentForTest().Should().Be(last);
    }

    [Test]
    public void Properly_handles_page_addresses_that_are_packed_2()
    {
        using var db = PagedDb.NativeMemoryDb(32 * Page.PageSize, 2);

        var value = new byte[1024];
        var account = Key.Account(Keccak.OfAnEmptySequenceRlp);

        for (int i = 0; i < 1000; i++)
        {
            using var batch = db.BeginNextBatch();

            // should modify 2 pages per commit
            batch.SetRaw(account, value);
            batch.Commit(CommitOptions.FlushDataOnly);
        }
    }

    private const int HistoryDepth = 2;

    [TestCase(20, 1, 10_000, false, TestName = "Accounts - 1")]
    [TestCase(428, 100, 10_000, false, TestName = "Accounts - 100")]
    [TestCase(16841, 4000, 200, false,
        TestName = "Accounts - 4000 to get a bit reuse",
        Category = Categories.LongRunning)]
    [TestCase(43153, 10_000, 50, false,
        TestName = "Accounts - 10000 to breach the AbandonedPage",
        Category = Categories.LongRunning)]
    [TestCase(118364, 20_000, 50, true,
        TestName = "Storage - 20_000 accounts with a single storage slot",
        Category = Categories.LongRunning)]
    public async Task Reuse_in_limited_environment(int pageCount, int accounts, int repeats, bool isStorage)
    {
        var keccaks = Initialize(accounts);

        // set big value
        var accountValue = new byte[2900];
        new Random(17).NextBytes(accountValue);

        using var db = PagedDb.NativeMemoryDb(pageCount * Page.PageSize, HistoryDepth);

        for (var i = 0; i < repeats; i++)
        {
            using var block = db.BeginNextBatch();
            foreach (var keccak in keccaks)
            {
                if (isStorage)
                {
                    block.SetStorage(keccak, keccak, accountValue);
                }
                else
                {
                    block.SetAccount(keccak, accountValue);
                }
            }

            await block.Commit(CommitOptions.FlushDataAndRoot);
        }

        db.NextFreePage.Should().Be((uint)pageCount,
            "Ensure that the page count is minimal. " +
            "After running the test they should mach the allocated space.");

        var oldPages = new List<uint>();

        for (uint at = 0; at < db.NextFreePage; at++)
        {
            var page = db.GetAt(DbAddress.Page(at));
            if (page.Header.BatchId < (uint)(repeats - HistoryDepth - 1))
            {
                oldPages.Add(at);
            }
        }

        if (oldPages.Count > 0)
        {
            var counters = new int[byte.MaxValue];

            var ages = new Dictionary<uint, uint>();

            foreach (var addr in oldPages)
            {
                var page = db.GetAt(DbAddress.Page(addr));

                if (page.Header.PageType == PageType.Abandoned)
                {
                    ages[addr] = page.Header.BatchId;
                }

                counters[(int)page.Header.PageType]++;
            }

            Console.WriteLine("Abandoned addr->batch: ");
            foreach (var age in ages)
            {
                Console.WriteLine($"  @{age.Key}: {age.Value}");
            }

            foreach (var type in Enum.GetValues<PageType>())
            {
                Console.WriteLine($"{type}: {counters[(int)type]}");
            }
        }

        return;

    }

    [Test]
    public async Task Work_proper_bookkeeping_when_lots_of_reads()
    {
        const int repeats = 1_000;
        const int multiplier = 2 + 1; // fanout page + data page + abandoned page per commit
        const int historyDepth = 2;

        var account = Keccak.EmptyTreeHash;

        byte[] value = [13];

        using var db = PagedDb.NativeMemoryDb((multiplier * repeats + historyDepth) * Page.PageSize);

        var reads = new List<IReadOnlyBatch>();

        for (var i = 0; i < repeats; i++)
        {
            reads.Add(db.BeginReadOnlyBatch());

            using var block = db.BeginNextBatch();
            block.SetAccount(account, value);
            block.VerifyDbPagesOnCommit();

            await block.Commit(CommitOptions.FlushDataAndRoot);
        }

        foreach (var read in reads)
        {
            read.Dispose();
        }
    }

    [Test]
    [Category(Categories.LongRunning)]
    public async Task Reuse_in_grow_and_shrink()
    {
        const int repeats = 200_000;
        const int spikeEvery = 100;
        const int spikeSize = 1000;

        var account = Keccak.EmptyTreeHash;
        var spikeAccounts = new Keccak[spikeSize];

        new Random(13).NextBytes(MemoryMarshal.Cast<Keccak, byte>(spikeAccounts.AsSpan()));

        byte[] value = [13];

        using var db = PagedDb.NativeMemoryDb(2048 * Page.PageSize);

        for (var i = 0; i < repeats; i++)
        {
            using var block = db.BeginNextBatch();
            block.SetAccount(account, value);

            if (i % spikeEvery == 0)
            {
                // spike set
                foreach (var spikeAccount in spikeAccounts)
                {
                    block.SetAccount(spikeAccount, value);
                }
            }
            else if (i % spikeEvery == 1)
            {
                // spike delete
                foreach (var spikeAccount in spikeAccounts)
                {
                    block.SetAccount(spikeAccount, ReadOnlySpan<byte>.Empty);
                }
            }

            await block.Commit(CommitOptions.FlushDataAndRoot);
        }
    }

    [Test]
    public async Task Abandoned_chain_creation_with_overflow()
    {
        const int delta = 10;

        // Minimum iterations required to overflow the abandoned list.
        const int minIterations = AbandonedList.MaxCount + delta;

        // Minimum accounts required to overflow an abandoned page.
        int numAccounts = AbandonedPage.MaxCount * 2 + delta;

        var keccaks = Initialize(numAccounts);

        // Set big value.
        var accountValue = new byte[2900];
        new Random(17).NextBytes(accountValue);

        using var db = PagedDb.NativeMemoryDb(350_000 * Page.PageSize, HistoryDepth);

        // Ensure that AbandonedList can pack maximum number of abandoned page addresses.
        const int estimatedMetadataSize = 32;
        AbandonedList.MaxCount.Should().BeGreaterThan(AbandonedList.Size / DbAddress.Size - estimatedMetadataSize);

        // Start read only batch to ensure that new pages are allocated instead of reusing
        // the abandoned pages.
        using (var read = db.BeginReadOnlyBatch())
        {
            for (var i = 0; i < minIterations; i++)
            {
                using var block = db.BeginNextBatch();
                foreach (var keccak in keccaks)
                {
                    block.SetAccount(keccak, accountValue);
                }

                block.VerifyDbPagesOnCommit();
                await block.Commit(CommitOptions.FlushDataAndRoot);
            }
        }

        // Try reusing the abandoned pages.
        for (var i = 0; i < minIterations; i++)
        {
            using var block = db.BeginNextBatch();
            foreach (var keccak in keccaks)
            {
                block.SetAccount(keccak, accountValue);
            }

            block.VerifyDbPagesOnCommit();
            await block.Commit(CommitOptions.FlushDataAndRoot);
        }
    }

    private static Keccak[] Initialize(int accounts)
    {
        var keccaks = new Keccak[accounts];
        const int seed = 13;
        new Random(seed).NextBytes(MemoryMarshal.Cast<Keccak, byte>(keccaks.AsSpan()));
        return keccaks;
    }
}