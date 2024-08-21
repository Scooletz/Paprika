using Paprika.Crypto;
using Paprika.Data;

namespace Paprika.Chain;

/// <summary>
/// Allows raw data state for syncing purposes.
/// </summary>
public interface IRawState : IReadOnlyWorldState
{
    void SetBoundary(in NibblePath account, in Keccak boundaryNodeKeccak);
    void SetBoundary(in Keccak account, in NibblePath storage, in Keccak boundaryNodeKeccak);

    void SetAccount(in Keccak address, in Account account);

    void SetStorage(in Keccak address, in Keccak storage, ReadOnlySpan<byte> value);

    void DestroyAccount(in Keccak address);

    Keccak GetHash(in NibblePath path, bool ignoreCache);

    Keccak GetStorageHash(in Keccak account, in NibblePath path);

    void RemoveBoundaryProof(in Keccak account, in NibblePath storagePath);
    void RemoveBoundaryProof(in NibblePath path);

    void CreateProofBranch(in Keccak account, in NibblePath storagePath, byte[] childNibbles, Keccak[] childHashes, bool persist = true);
    void CreateProofExtension(in Keccak account, in NibblePath storagePath, in NibblePath extPath, bool persist = true);
    void CreateProofLeaf(in Keccak account, in NibblePath storagePath, in NibblePath leafPath);

    /// <summary>
    /// Commits the pending changes.
    /// </summary>
    void Commit(bool ensureHash = true);

    /// <summary>
    /// Finalizes the raw state flushing the metadata.
    /// </summary>
    void Finalize(uint blockNumber);

    /// <summary>
    /// Enforces root hash calculation without actual commit
    /// </summary>
    /// <returns></returns>
    Keccak RefreshRootHash();

    /// <summary>
    /// Recalculates storage roots and returns new storage root hash for a given account 
    /// </summary>
    /// <param name="accountAddress"></param>
    /// <returns></returns>
    Keccak RecalculateStorageRoot(in Keccak accountAddress);

    /// <summary>
    /// Cleans current data
    /// </summary>
    void Discard();

    string DumpTrie();
}
