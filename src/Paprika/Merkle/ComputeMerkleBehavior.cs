using System.Diagnostics;
using Paprika.Chain;
using Paprika.Crypto;
using Paprika.Data;
using Paprika.RLP;

namespace Paprika.Merkle;

public class ComputeMerkleBehavior : IPreCommitBehavior
{
    private readonly bool _fullMerkle;

    public ComputeMerkleBehavior(bool fullMerkle = false)
    {
        _fullMerkle = fullMerkle;
    }

    public void BeforeCommit(ICommit commit)
    {
        // run the visitor on the commit
        commit.Visit(OnKey);

        if (_fullMerkle)
        {
            var root = Key.Merkle(NibblePath.Empty);
            var keccakOrRlp = Compute(root, commit, stackalloc byte[64]);

            Debug.Assert(keccakOrRlp.DataType == KeccakOrRlp.Type.Keccak);

            RootHash = new Keccak(keccakOrRlp.AsSpan());
        }
    }

    public Keccak RootHash { get; private set; }

    private static KeccakOrRlp Compute(in Key key, ICommit commit, Span<byte> span)
    {
        using var owner = commit.Get(key);
        if (owner.IsEmpty)
        {
            // empty tree, return empty
            return Keccak.EmptyTreeHash;
        }

        Node.ReadFrom(owner.Span, out var type, out var leaf, out var ext, out var branch);
        switch (type)
        {
            case Node.Type.Leaf:
                var leafPath = key.Path.Append(leaf.Path, span);
                using (var leafData = commit.Get(Key.Account(leafPath)))
                {
                    Account.ReadFrom(leafData.Span, out var account);
                    Node.Leaf.KeccakOrRlp(leaf.Path, account, out var keccakOrRlp);
                    return keccakOrRlp;
                }
            case Node.Type.Extension:
                throw new NotImplementedException("Extension is not implemented");
            case Node.Type.Branch:
                throw new NotImplementedException("Branch is not implemented");
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private static void OnKey(in Key key, ICommit commit)
    {
        if (key.Type == DataType.Account)
        {
            MarkAccountPathDirty(in key.Path, commit);
        }
        else
        {
            throw new Exception("Not implemented for other types now.");
        }
    }

    private static void MarkAccountPathDirty(in NibblePath path, ICommit commit)
    {
        Span<byte> span = stackalloc byte[33];

        for (int i = 0; i < path.Length; i++)
        {
            var slice = path.SliceTo(i);
            var key = Key.Merkle(slice);

            var leftoverPath = path.SliceFrom(i);

            using var owner = commit.Get(key);

            if (owner.IsEmpty)
            {
                // no value set now, create one
                commit.SetLeaf(key, leftoverPath);
                return;
            }

            // read the existing one
            Node.ReadFrom(owner.Span, out var type, out var leaf, out var ext, out var branch);
            switch (type)
            {
                case Node.Type.Leaf:
                    {
                        var diffAt = leaf.Path.FindFirstDifferentNibble(leftoverPath);

                        if (diffAt == leaf.Path.Length)
                        {
                            // update in place, mark in parent as dirty, beside that, do from from the Merkle pov
                            return;
                        }

                        if (diffAt > 0)
                        {
                            // diff is not on the 0th position, so it will be a branch but preceded with an extension
                            commit.SetExtension(key, leftoverPath.SliceTo(diffAt));
                        }

                        var nibbleA = leaf.Path[diffAt];
                        var nibbleB = leftoverPath[diffAt];

                        // create branch, truncate both leaves, add them at the end
                        var branchKey = Key.Merkle(path.SliceTo(i + diffAt));
                        commit.SetBranch(branchKey,
                            new NibbleSet(nibbleA, nibbleB),
                            new NibbleSet(nibbleA, nibbleB));

                        // nibbleA, deep copy to write in an unsafe manner
                        var pathA = path.SliceTo(i + diffAt).AppendNibble(nibbleA, span);
                        commit.SetLeaf(Key.Merkle(pathA), leaf.Path.SliceFrom(diffAt + 1));

                        // nibbleB, set the newly set leaf, slice to the next nibble
                        var pathB = path.SliceTo(i + 1 + diffAt);
                        commit.SetLeaf(Key.Merkle(pathB), leftoverPath.SliceFrom(diffAt + 1));

                        return;
                    }
                case Node.Type.Extension:
                    {
                        var diffAt = ext.Path.FindFirstDifferentNibble(leftoverPath);
                        if (diffAt == ext.Path.Length)
                        {
                            // the path overlaps with what is there, move forward
                            i += ext.Path.Length - 1;
                            continue;
                        }

                        if (diffAt == 0)
                        {
                            if (ext.Path.Length == 1)
                            {
                                // special case of an extension being only 1 nibble long
                                // 1. replace an extension with a branch
                                // 2. leave the next branch as is
                                // 3. add a new leaf
                                var set = new NibbleSet(ext.Path[0], leftoverPath[0]);
                                commit.SetBranch(key, set, set);
                                commit.SetLeaf(Key.Merkle(path.SliceTo(i + 1)), path.SliceFrom(i + 1));
                                return;
                            }

                            {
                                // the extension is at least 2 nibbles long
                                // 1. replace it with a branch
                                // 2. create a new, shorter extension that the branch points to
                                // 3. create a new leaf

                                var ext0Th = ext.Path[0];

                                var set = new NibbleSet(ext0Th, leftoverPath[0]);
                                commit.SetBranch(key, set, set);

                                commit.SetExtension(Key.Merkle(key.Path.AppendNibble(ext0Th, span)),
                                    ext.Path.SliceFrom(1));

                                commit.SetLeaf(Key.Merkle(path.SliceTo(i + 1)), path.SliceFrom(i + 1));
                                return;
                            }
                        }

                        var lastNibblePos = ext.Path.Length - 1;
                        if (diffAt == lastNibblePos)
                        {
                            // the last nibble is different
                            // 1. trim the end of the extension.path by 1
                            // 2. add a branch at the end with nibbles set to the last and the leaf
                            // 3. add a new leaf

                            commit.SetExtension(key, ext.Path.SliceTo(lastNibblePos));

                            var splitAt = i + ext.Path.Length - 1;
                            var set = new NibbleSet(path[splitAt], ext.Path[lastNibblePos]);

                            commit.SetBranchAllDirty(Key.Merkle(path.SliceTo(splitAt)), set);
                            commit.SetLeaf(Key.Merkle(path.SliceTo(splitAt + 1)), path.SliceFrom(splitAt + 1));

                            return;
                        }

                        // the diff is not at the 0th nibble, it's not a full match as well
                        // this means that E0->B0 will turn into E1->B1->E2->B0
                        //                                             ->L0
                        var extPath = ext.Path.SliceTo(diffAt);
                        commit.SetExtension(key, extPath);

                        // B1
                        var branch1 = key.Path.Append(extPath, span);
                        var existingNibble = ext.Path[diffAt];
                        var addedNibble = path[i + diffAt];
                        var children = new NibbleSet(existingNibble, addedNibble);
                        commit.SetBranchAllDirty(Key.Merkle(branch1), children);

                        // E2
                        var extension2 = branch1.AppendNibble(existingNibble, span);
                        if (extension2.Length < key.Path.Length + ext.Path.Length)
                        {
                            // there are some bytes to be set in the extension path, create one
                            var e2Path = ext.Path.SliceFrom(extension2.Length);
                            commit.SetExtension(Key.Merkle(extension2), e2Path);
                        }

                        // L0
                        var leafPath = branch1.AppendNibble(addedNibble, span);
                        commit.SetLeaf(Key.Merkle(leafPath), path.SliceFrom(leafPath.Length));

                        return;
                    }
                case Node.Type.Branch:
                    {
                        var nibble = path[i];
                        if (branch.HasKeccak)
                        {
                            // branch has keccak, this means it was not written yet, needs to be dirtied
                            commit.SetBranch(key, branch.Children.Set(nibble), new NibbleSet(nibble));
                        }
                        else
                        {
                            if (branch.Children[nibble] && branch.Dirty[nibble])
                            {
                                // everything set as needed, continue
                                continue;
                            }

                            commit.SetBranch(key, branch.Children.Set(nibble), branch.Dirty.Set(nibble));
                        }
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}

public static class CommitExtensions
{
    public static void SetLeaf(this ICommit commit, in Key key, in NibblePath leafPath)
    {
        var leaf = new Node.Leaf(leafPath);
        commit.Set(key, leaf.WriteTo(stackalloc byte[leaf.MaxByteLength]));
    }

    public static void SetBranch(this ICommit commit, in Key key, NibbleSet.Readonly children,
        NibbleSet.Readonly dirtyChildren)
    {
        var branch = new Node.Branch(children, dirtyChildren);
        commit.Set(key, branch.WriteTo(stackalloc byte[branch.MaxByteLength]));
    }

    public static void SetBranchAllDirty(this ICommit commit, in Key key, NibbleSet.Readonly children)
    {
        var branch = new Node.Branch(children, children);
        commit.Set(key, branch.WriteTo(stackalloc byte[branch.MaxByteLength]));
    }

    public static void SetExtension(this ICommit commit, in Key key, in NibblePath path)
    {
        var extension = new Node.Extension(path);
        commit.Set(key, extension.WriteTo(stackalloc byte[extension.MaxByteLength]));
    }
}