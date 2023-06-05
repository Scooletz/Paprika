using Nethermind.Int256;
using NUnit.Framework;
using Paprika.Crypto;
using Paprika.Data;
using Paprika.Tree;

namespace Paprika.Tests;

public class MerkleKeccakTests
{
    [Test]
    [TestCaseSource(nameof(_keysBalancesNoncesHexStrings))]
    public void Leaf_keccak(Keccak key, UInt256 balance, UInt256 nonce, string hexString)
    {
        var account = new Account(balance, nonce);

        var computedHash = Leaf.ComputeKeccakOrRlp(NibblePath.FromKey(key), account);
        var expectedHash = new Keccak(Convert.FromHexString(hexString));

        Assert.That(computedHash.DataType, Is.EqualTo(KeccakOrRlp.Type.Keccak));
        Assert.That(new Keccak(computedHash.Data), Is.EqualTo(expectedHash));
    }

    private static object[] _keysBalancesNoncesHexStrings =
    {
        new object[]
        {
            Values.Key0, Values.Balance0, Values.Nonce0,
            "E2533A0A0C4F1DDB72FEB7BFAAD12A83853447DEAAB6F28FA5C443DD2D37C3FB"
        },
        new object[]
        {
            Values.Key1a, Values.Balance1, Values.Nonce1,
            "DD358AF6B1D8E875FBA0E585710D054F14DD9D06FA3C8C5F2BAF66F413178F82"
        },
        new object[]
        {
            Values.Key2, Values.Balance2, Values.Nonce2,
            "A654F039A5F9E9F30C89F21555C92F1CB1E739AF11A9E9B12693DEDC6E76F628"
        },
    };
}
