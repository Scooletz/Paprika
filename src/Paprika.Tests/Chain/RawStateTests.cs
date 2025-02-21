using FluentAssertions;
using Nethermind.Int256;
using Paprika.Chain;
using Paprika.Crypto;
using Paprika.Data;
using Paprika.Merkle;
using Paprika.Store;
using Paprika.RLP;

namespace Paprika.Tests.Chain;

public class RawStateTests
{
    [Test]
    public async Task Raw_access_spin()
    {
        var account = Values.Key1;

        using var db = PagedDb.NativeMemoryDb(256 * 1024, 2);
        var merkle = new ComputeMerkleBehavior();

        await using var blockchain = new Blockchain(db, merkle);

        using var raw = blockchain.StartRaw();

        for (uint i = 0; i < 10_000; i++)
        {
            raw.SetAccount(account, new Account(i, i));
        }

        raw.DestroyAccount(account);
        raw.Commit(keepOpened: true);

        raw.Finalize(1);

        raw.Hash.Should().Be(Keccak.EmptyTreeHash);
    }


    [Test]
    public async Task Metadata_are_preserved()
    {
        var a = Values.Key1;
        var b = Values.Key2;
        var c = Values.Key3;

        UInt256 valueA = 1;
        UInt256 valueB = 1;
        UInt256 valueC = 1;

        using var db = PagedDb.NativeMemoryDb(256 * 1024, 2);
        var merkle = new ComputeMerkleBehavior();

        await using var blockchain = new Blockchain(db, merkle);

        using var raw = blockchain.StartRaw();
        raw.SetAccount(a, new Account(valueA, valueA));
        raw.Commit(keepOpened: true);

        raw.SetAccount(b, new Account(valueB, valueB));
        raw.Commit(keepOpened: true);

        raw.SetAccount(c, new Account(valueC, valueC));
        raw.Commit(keepOpened: true);

        var root = raw.Hash;

        raw.Finalize(1);

        using var read = db.BeginReadOnlyBatch(root);

        read.Metadata.StateHash.Should().Be(root);
        read.GetAccount(a).Should().Be(new Account(valueA, valueA));
        read.GetAccount(b).Should().Be(new Account(valueB, valueB));
        read.GetAccount(c).Should().Be(new Account(valueC, valueC));
    }

    [Test]
    public async Task Disposal()
    {
        var account = Values.Key1;

        using var db = PagedDb.NativeMemoryDb(256 * 1024, 2);
        var merkle = new ComputeMerkleBehavior();

        await using var blockchain = new Blockchain(db, merkle);

        using var raw = blockchain.StartRaw();

        for (uint i = 0; i < 1_000; i++)
        {
            raw.SetAccount(account, new Account(i, i));
            raw.Commit(keepOpened: true);
        }

        raw.DestroyAccount(account);
        raw.Commit(keepOpened: true);

        raw.Finalize(1);

        raw.Hash.Should().Be(Keccak.EmptyTreeHash);
    }

    [Test]
    public async Task DeleteByPrefix()
    {
        var account = Values.Key1;

        using var db = PagedDb.NativeMemoryDb(256 * 1024, 2);
        var merkle = new ComputeMerkleBehavior();

        await using var blockchain = new Blockchain(db, merkle);

        using var raw = blockchain.StartRaw();

        raw.SetAccount(account, new Account(1, 1));
        raw.Commit(keepOpened: true);

        raw.RegisterDeleteByPrefix(Key.Account(account));
        raw.Commit(keepOpened: true);

        raw.Finalize(1);

        using var read = db.BeginReadOnlyBatch();
        read.TryGet(Key.Account(account), out _).Should().BeFalse();
    }

    [Test]
    public async Task DeleteByShortPrefix()
    {
        var account1 = new Keccak(new byte[]
            { 1, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7, });

        var account2 = new Keccak(new byte[]
            { 18, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7, });

        using var db = PagedDb.NativeMemoryDb(64 * 1024, 2);
        var merkle = new ComputeMerkleBehavior();

        await using var blockchain = new Blockchain(db, merkle);

        using var raw = blockchain.StartRaw();

        raw.SetAccount(account1, new Account(1, 1));
        raw.SetAccount(account2, new Account(2, 2));
        raw.Commit(keepOpened: true);

        raw.RegisterDeleteByPrefix(Key.Account(NibblePath.FromKey(account2).SliceTo(1)));
        raw.Commit(keepOpened: true);

        raw.Finalize(1);

        //check account 1 is still present and account 2 is deleted
        using var read = db.BeginReadOnlyBatch();
        read.TryGet(Key.Account(account1), out _).Should().BeTrue();
        read.TryGet(Key.Account(account2), out _).Should().BeFalse();

        //let's re-add 2nd account and delete using empty prefix
        using var raw2 = blockchain.StartRaw();

        raw2.SetAccount(account2, new Account(2, 2));
        raw2.Commit(keepOpened: true);

        raw2.RegisterDeleteByPrefix(Key.Account(NibblePath.Empty));
        raw2.Commit(keepOpened: true);

        raw2.Finalize(2);

        //no accounts should be present
        using var read2 = db.BeginReadOnlyBatch();
        read2.TryGet(Key.Account(account1), out _).Should().BeFalse();
        read2.TryGet(Key.Account(account2), out _).Should().BeFalse();
    }

    [Test]
    public void DeleteByPrefixStorage()
    {
        var account = Values.Key1;

        using var db = PagedDb.NativeMemoryDb(256 * 1024, 2);
        var merkle = new ComputeMerkleBehavior();

        var blockchain = new Blockchain(db, merkle);

        using var raw = blockchain.StartRaw();

        raw.SetAccount(account, new Account(1, 1));
        raw.SetStorage(account, Values.Key2, new byte[] { 1, 2, 3, 4, 5 });
        raw.Commit(keepOpened: true);

        using var read = db.BeginReadOnlyBatch();
        read.TryGet(Key.StorageCell(NibblePath.FromKey(account), Values.Key2), out _).Should().BeTrue();

        raw.RegisterDeleteByPrefix(Key.StorageCell(NibblePath.FromKey(account), NibblePath.Empty));
        raw.Commit(keepOpened: true);

        raw.Finalize(1);

        using var read2 = db.BeginReadOnlyBatch();
        read2.TryGet(Key.StorageCell(NibblePath.FromKey(account), Values.Key2), out _).Should().BeFalse();
    }

    [Test]
    public void CalcRootFromRlpMemoDataState()
    {
        using var db = PagedDb.NativeMemoryDb(256 * 1024, 2);
        var merkle = new ComputeMerkleBehavior();

        var blockchain = new Blockchain(db, merkle);

        using var raw = blockchain.StartRaw();

        var random = GetRandom();

        Span<byte> rlp = stackalloc byte[1024];
        RlpStream stream = new RlpStream(rlp);
        stream.StartSequence(529);

        //all children to trigger parallel branch hash calculation
        byte[] children = new byte[16];
        Keccak[] childHashes = new Keccak[16];

        for (int i = 0; i < 16; i++)
        {
            children[i] = (byte)i;
            childHashes[i] = random.NextKeccak();
            stream.Encode(childHashes[i]);
        }
        stream.EncodeEmptyArray();
        KeccakOrRlp.FromSpan(rlp.Slice(0, stream.Position), out var checkKeccakOrRlp);

        raw.CreateMerkleBranch(Keccak.Zero, NibblePath.Empty, children, childHashes);
        Keccak newRootHash = raw.RefreshRootHash(true);

        newRootHash.Should().Be(checkKeccakOrRlp.Keccak);
    }

    [Test]
    public void CalcRootFromRlpMemoDataStorage()
    {
        using var db = PagedDb.NativeMemoryDb(256 * 1024, 2);
        var merkle = new ComputeMerkleBehavior();

        var blockchain = new Blockchain(db, merkle);

        using var raw = blockchain.StartRaw();

        var account = Values.Key1;

        var random = GetRandom();

        Span<byte> rlp = stackalloc byte[1024];
        RlpStream stream = new RlpStream(rlp);
        stream.StartSequence(529);

        //all children to trigger parallel branch hash calculation
        byte[] children = new byte[16];
        Keccak[] childHashes = new Keccak[16];

        for (int i = 0; i < 16; i++)
        {
            children[i] = (byte)i;
            childHashes[i] = random.NextKeccak();
            stream.Encode(childHashes[i]);
        }
        stream.EncodeEmptyArray();
        KeccakOrRlp.FromSpan(rlp.Slice(0, stream.Position), out var checkKeccakOrRlp);

        raw.CreateMerkleBranch(account, NibblePath.Empty, children, childHashes);
        Keccak newRootHash = raw.RecalculateStorageRoot(account, true);

        newRootHash.Should().Be(checkKeccakOrRlp.Keccak);
    }

    [Test]
    public void ProcessProofNodes()
    {
        using var remoteDb = PagedDb.NativeMemoryDb(32 * 1024, 2);
        var merkle = new ComputeMerkleBehavior();

        var remoteBlockchain = new Blockchain(remoteDb, merkle);

        using var raw = remoteBlockchain.StartRaw();

        var account1 = new Keccak(Convert.FromHexString("000aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaabbbbbbbbbbbbbbbbbb11"));
        var account2 = new Keccak(Convert.FromHexString("003aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaabbbbbbbbbbbbbbbbbb22"));
        var account3 = new Keccak(Convert.FromHexString("100aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaabbbbbbbbbbbbbbbbbb33"));
        var account4 = new Keccak(Convert.FromHexString("020aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaabbbbbbbbbbbbbbbbbb44"));

        raw.SetAccount(account1, new Account(Values.Balance1, Values.Nonce1));
        raw.SetAccount(account2, new Account(Values.Balance2, Values.Nonce2));
        raw.SetAccount(account3, new Account(Values.Balance3, Values.Nonce3));
        raw.SetAccount(account4, new Account(Values.Balance4, Values.Nonce4));

        raw.Commit(true, true);

        var hashAccount3 = raw.GetHash(NibblePath.Parse("1"), false);
        var hashAccount4 = raw.GetHash(NibblePath.Parse("02"), false);
        var hashBranch2 = raw.GetHash(NibblePath.Parse("0"), false);

        using var localDb = PagedDb.NativeMemoryDb(64 * 1024, 2);
        var localBlockchain = new Blockchain(localDb, merkle);

        using var syncRaw = localBlockchain.StartRaw();

        syncRaw.CreateMerkleBranch(Keccak.Zero, NibblePath.Empty, [0, 1], [hashBranch2, hashAccount3], false);
        syncRaw.CreateMerkleBranch(Keccak.Zero, NibblePath.Parse("0"), [0, 2], [Keccak.Zero, hashAccount4], false);

        syncRaw.SetAccount(account1, new Account(Values.Balance1, Values.Nonce1));
        syncRaw.SetAccount(account2, new Account(Values.Balance2, Values.Nonce2));

        var localHash = syncRaw.RefreshRootHash();

        localHash.Should().Be(raw.Hash);

        Span<byte> packed = stackalloc byte[3 * 33];

        NibblePath proofPath = NibblePath.Parse("0");

        for (int i = proofPath.Length; i >= 0; i--)
        {
            var currentPath = proofPath.SliceTo(i);
            packed[i * 33] = currentPath.Length;
            currentPath.RawSpan.CopyTo(packed.Slice(i * 33 + 1));
        }
        syncRaw.ProcessProofNodes(Keccak.Zero, packed, 2);

        syncRaw.Commit(false);

        //2nd pass
        using var syncRaw2 = localBlockchain.StartRaw();

        //check proof nodes from 1st pass were not persisted during commit
        syncRaw2.GetHash(NibblePath.Parse("0"), false).Should().Be(Keccak.EmptyTreeHash);

        var hashBranch3 = raw.GetHash(NibblePath.Parse("00"), false);

        syncRaw2.CreateMerkleBranch(Keccak.Zero, NibblePath.Empty, [0, 1], [hashBranch2, Keccak.Zero], false);
        syncRaw2.CreateMerkleBranch(Keccak.Zero, NibblePath.Parse("0"), [0, 2], [hashBranch3, Keccak.Zero], false);

        syncRaw2.SetAccount(account3, new Account(Values.Balance3, Values.Nonce3));
        syncRaw2.SetAccount(account4, new Account(Values.Balance4, Values.Nonce4));

        localHash = syncRaw2.RefreshRootHash();
        localHash.Should().Be(raw.Hash);

        syncRaw2.ProcessProofNodes(Keccak.Zero, packed, 2);
        syncRaw2.Commit(false);

        //check
        using var syncRaw3 = localBlockchain.StartRaw();
        //check proof nodes from 2nd pass were persisted during commit - part from root node - only persisted for storage tries
        syncRaw3.GetHash(NibblePath.Parse("0"), false).Should().Be(hashBranch2);
    }

    [Test]
    public void ProcessProofNodesStorageWithExtension()
    {
        //setup remote trie
        using var remoteDb = PagedDb.NativeMemoryDb(32 * 1024, 2);
        var merkle = new ComputeMerkleBehavior();

        var remoteBlockchain = new Blockchain(remoteDb, merkle);

        using var raw = remoteBlockchain.StartRaw();

        Span<byte> leafValue = stackalloc byte[32];
        GetRandom().NextBytes(leafValue);
        var accountHash = Values.Key2;

        //E -> B -> L1
        //       -> L2
        //       -> L3
        //       -> L4
        var storage1 = new Keccak(Convert.FromHexString("000aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab1bbbbbbbbbbbbbbbb11"));
        var storage2 = new Keccak(Convert.FromHexString("000aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab2bbbbbbbbbbbbbbbb22"));
        var storage3 = new Keccak(Convert.FromHexString("000aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab3bbbbbbbbbbbbbbbb33"));
        var storage4 = new Keccak(Convert.FromHexString("000aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab4bbbbbbbbbbbbbbbb44"));

        raw.SetStorage(accountHash, storage1, leafValue);
        raw.SetStorage(accountHash, storage2, leafValue);
        raw.SetStorage(accountHash, storage3, leafValue);
        raw.SetStorage(accountHash, storage4, leafValue);

        raw.Commit(true, true);
        var remoteStorageHash = raw.GetStorageHash(in accountHash, NibblePath.Empty, false);

        //1st sync pass
        var hashStorage3 = raw.GetStorageHash(in accountHash, NibblePath.Parse("000aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab3"), false);
        var hashStorage4 = raw.GetStorageHash(in accountHash, NibblePath.Parse("000aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab4"), false);

        using var localDb = PagedDb.NativeMemoryDb(64 * 1024, 2);
        var localBlockchain = new Blockchain(localDb, merkle);

        using var syncRaw = localBlockchain.StartRaw();

        var branchPath = NibblePath.Parse("000aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab");
        syncRaw.CreateMerkleExtension(accountHash, NibblePath.Empty, branchPath, false);
        syncRaw.CreateMerkleBranch(accountHash, NibblePath.Parse("000aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab"), [1, 2, 3, 4], [Keccak.Zero, Keccak.Zero, hashStorage3, hashStorage4], false);

        syncRaw.SetStorage(accountHash, storage1, leafValue);
        syncRaw.SetStorage(accountHash, storage2, leafValue);

        var localHash = syncRaw.RecalculateStorageRoot(in accountHash, true);

        localHash.Should().Be(remoteStorageHash);

        Span<byte> packed = stackalloc byte[2 * 33];
        packed[0 * 33] = branchPath.Length;
        branchPath.RawSpan.CopyTo(packed.Slice(0 * 33 + 1));
        packed[1 * 33] = NibblePath.Empty.Length;
        NibblePath.Empty.RawSpan.CopyTo(packed.Slice(1 * 33 + 1));

        syncRaw.ProcessProofNodes(accountHash, packed, 1);
        syncRaw.Commit(false);

        //2nd sync pass
        using var syncRaw2 = localBlockchain.StartRaw();

        //check proof nodes from 1st pass were not persisted during commit
        //neither branch nor it's parent extension should have been persisted as not all dependent children are
        using var rootExtensionNodeData = syncRaw2.Get(Key.Raw(NibblePath.FromKey(accountHash), DataType.Merkle, NibblePath.Empty));
        rootExtensionNodeData.IsEmpty.Should().BeTrue();
        syncRaw2.GetHash(branchPath, false).Should().Be(Keccak.EmptyTreeHash);

        var hashStorage1 = raw.GetStorageHash(in accountHash, NibblePath.Parse("000aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab1"), false);
        var hashStorage2 = raw.GetStorageHash(in accountHash, NibblePath.Parse("000aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab2"), false);

        syncRaw2.CreateMerkleExtension(accountHash, NibblePath.Empty, branchPath, false);
        syncRaw2.CreateMerkleBranch(accountHash, NibblePath.Parse("000aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab"), [1, 2, 3, 4], [hashStorage1, hashStorage2, Keccak.Zero, Keccak.Zero], false);

        syncRaw2.SetStorage(accountHash, storage3, leafValue);
        syncRaw2.SetStorage(accountHash, storage4, leafValue);

        localHash = syncRaw2.RecalculateStorageRoot(in accountHash, true);
        localHash.Should().Be(remoteStorageHash);

        syncRaw2.ProcessProofNodes(accountHash, packed, 2);
        syncRaw2.Commit(false);

        //check the root extension is correctly persisted
        using var syncRaw3 = localBlockchain.StartRaw();
        using var rootExtensionNodeData2 = syncRaw3.Get(Key.Raw(NibblePath.FromKey(accountHash), DataType.Merkle, NibblePath.Empty));
        rootExtensionNodeData2.IsEmpty.Should().BeFalse();

        Node.ReadFrom(out var type, out var _, out var ext, out var _, rootExtensionNodeData2.Span);
        type.Should().Be(Node.Type.Extension);

        //recalculate ignoring RlpMemo cache to verify correctness
        var noCacheRootHash = syncRaw3.RecalculateStorageRoot(in accountHash, true);
        noCacheRootHash.Should().Be(remoteStorageHash);
    }

    private static Random GetRandom() => new(13);
}
