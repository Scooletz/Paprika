using System.Buffers.Binary;
using FluentAssertions;
using Paprika.Crypto;
using Paprika.Data;
using Paprika.Store;

namespace Paprika.Tests.Store;

public class MultiHeadChainTests
{
    private const long Mb = 1024 * 1024;
    private const int Seed = 17;

    [Test]
    public void SimpleTest()
    {
        const int accounts = 1;
        const int merkleCount = 31;

        ushort counter = 0;

        using var db = PagedDb.NativeMemoryDb(16 * Mb, 2);

        var random = new Random(Seed);

        using var multi = db.OpenMultiHeadChain();

        using var head = multi.Begin(Keccak.Zero);

        for (var i = 0; i < accounts; i++)
        {
            var keccak = random.NextKeccak();

            // account first & data
            head.SetRaw(Key.Account(keccak), GetData());
            head.SetRaw(Key.StorageCell(NibblePath.FromKey(keccak), keccak), GetData());

            for (var j = 0; j < merkleCount; j++)
            {
                // all the Merkle values
                head.SetRaw(Key.Merkle(NibblePath.FromKey(keccak).SliceTo(j)), GetData());
            }
        }

        head.Commit();

        // reset
        counter = 0;

        // Assert();
        //
        // void Assert()
        // {
        //
        //     for (var i = 0; i < accounts; i++)
        //     {
        //         var keccak = random.NextKeccak();
        //
        //         head.TryGet(Key.Account(keccak), out var value).Should().BeTrue("The account should exist");
        //         value.SequenceEqual(GetData()).Should().BeTrue("The account should have data right");
        //
        //         head.TryGet(Key.StorageCell(NibblePath.FromKey(keccak), keccak), out value).Should()
        //             .BeTrue("The storage cell should exist");
        //         value.SequenceEqual(GetData()).Should().BeTrue("The storage cell should have data right");
        //
        //         for (var j = 0; j < merkleCount; j++)
        //         {
        //             // all the Merkle values
        //             head.TryGet(Key.Merkle(NibblePath.FromKey(keccak).SliceTo(j)), out value).Should()
        //                 .BeTrue("The Merkle should exist");
        //
        //             var actual = value.ToArray();
        //             var expected = GetData();
        //
        //             actual.SequenceEqual(expected).Should()
        //                 .BeTrue($"The Merkle @{j} of {i}th account should have data right");
        //         }
        //     }
        // }

        byte[] GetData()
        {
            var bytes = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, counter);
            counter++;
            return bytes;
        }
    }
}