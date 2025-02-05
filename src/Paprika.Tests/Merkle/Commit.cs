using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using Paprika.Chain;
using Paprika.Crypto;
using Paprika.Data;
using Paprika.Merkle;
using Paprika.Utils;

namespace Paprika.Tests.Merkle;

/// <summary>
/// A commit mock used to provide the data and assert them when needed.
/// </summary>
public class Commit(bool skipMemoizedRlpCheck = false) : ICommitWithStats
{
    // history <- before <- after
    private readonly Dictionary<byte[], byte[]> _history = new(Comparer);
    private readonly Dictionary<byte[], byte[]> _before = new(Comparer);
    private readonly Dictionary<byte[], byte[]> _after = new(Comparer);

    private bool _asserting;

    private static readonly BytesEqualityComparer Comparer = new();

    // stats
    private readonly HashSet<Keccak> _touchedAccounts = new();
    private readonly Dictionary<Keccak, IStorageStats> _storageSlots = new();

    public void Set(in Key key, ReadOnlySpan<byte> value)
    {
        _before[GetKey(key)] = value.ToArray();
        //to enable storage root calculation for tests

        if (key.Path.Length == NibblePath.KeccakNibbleCount)
        {
            _touchedAccounts.Add(key.Path.UnsafeAsKeccak);
        }

        if (!key.IsState)
        {
            var keccak = key.Path.UnsafeAsKeccak;

            if (key.StoragePath.Length == NibblePath.KeccakNibbleCount)
            {
                ref var stats = ref CollectionsMarshal.GetValueRefOrAddDefault(_storageSlots, keccak, out var exists);
                if (exists == false)
                {
                    stats = new StorageStats();
                }

                Unsafe.As<StorageStats>(stats!).SetStorage(key.StoragePath.UnsafeAsKeccak, value);
            }
        }
    }

    public void DeleteKey(in Key key) => Set(key, ReadOnlySpan<byte>.Empty);

    public void ShouldBeEmpty() => AssertEmpty(_after, "set of operations");

    public void ShouldHaveSquashedStateEmpty() => AssertEmpty(_history, "squashed state");

    private static void AssertEmpty(Dictionary<byte[], byte[]> dict, string txt)
    {
        if (dict.Count == 0)
            return;

        var sb = new StringBuilder();
        sb.AppendLine($"The {txt} should be empty, but it contains the following keys: ");
        foreach (var (key, value) in dict)
        {
            Key.ReadFrom(key, out Key k);

            sb.Append($"- {k.ToString()}");

            if (k.Type == DataType.Merkle)
            {
                Node.ReadFrom(out var type, out var leaf, out var ext, out var branch, value);
                switch (type)
                {
                    case Node.Type.Leaf:
                        sb.Append($" {leaf.ToString()}");
                        break;
                    case Node.Type.Extension:
                        sb.Append($" {ext.ToString()}");
                        break;
                    case Node.Type.Branch:
                        sb.Append($" {branch.ToString()}");
                        break;
                }
            }

            sb.AppendLine();
        }

        Assert.Fail(sb.ToString());
    }

    ReadOnlySpanOwnerWithMetadata<byte> ICommit.Get(scoped in Key key)
    {
        var k = GetKey(key);

        if (_after.TryGetValue(k, out var value))
        {
            return new ReadOnlySpanOwner<byte>(value, null).WithDepth(0);
        }

        if (_before.TryGetValue(k, out value))
        {
            return new ReadOnlySpanOwner<byte>(value, null).WithDepth(0);
        }

        if (_history.TryGetValue(k, out value))
        {
            return new ReadOnlySpanOwner<byte>(value, null).WithDepth(0);
        }

        return default;
    }

    private static byte[] GetKey(in Key key) => key.WriteTo(stackalloc byte[key.MaxByteLength]).ToArray();

    void ICommit.Set(in Key key, in ReadOnlySpan<byte> payload, EntryType type)
    {
        var bytes = GetKey(key);
        if (_asserting == false)
        {
            _after[bytes] = payload.ToArray();
        }
        else
        {
            _after.Remove(bytes, out var existing).Should().BeTrue($"key {key.ToString()} should exist");

            ReadOnlySpan<byte> actual = existing;
            var expected = payload;

            if (skipMemoizedRlpCheck)
            {
                if (key.Type == DataType.Merkle && existing.Length > 0 && Node.Header.GetTypeFrom(existing) == Node.Type.Branch)
                {
                    // override, removing the rlp data
                    actual = Node.Branch.GetOnlyBranchData(existing);
                    expected = Node.Branch.GetOnlyBranchData(payload);
                }
            }

            expected.SequenceEqual(actual).Should().BeTrue("The value should be equal");
        }
    }

    void ICommit.Set(in Key key, in ReadOnlySpan<byte> payload0, in ReadOnlySpan<byte> payload1, EntryType type)
    {
        ((ICommit)this).Set(key, Concat(payload0, payload1));
    }

    void ICommit.Visit(CommitAction action, TrieType type)
    {
        foreach (var (k, v) in _before)
        {
            Key.ReadFrom(k, out var key);

            var isStorageType = type == TrieType.Storage;
            var isStorageKey =
                key.Type == DataType.StorageCell ||
                (key.Type == DataType.Merkle && !key.StoragePath.IsEmpty);

            if (isStorageType != isStorageKey)
                continue;

            action(key, v);
        }
    }

    public IChildCommit GetChild() => new ChildCommit(this);

    private static byte[] Concat(in ReadOnlySpan<byte> payload0, in ReadOnlySpan<byte> payload1)
    {
        var bytes = new byte[payload0.Length + payload1.Length];
        payload0.CopyTo(bytes);
        payload1.CopyTo(bytes.AsSpan(payload0.Length));
        return bytes;
    }

    class ChildCommit(ICommit commit) : IChildCommit
    {
        private readonly Dictionary<byte[], byte[]> _data = new(Comparer);

        public void Dispose() => _data.Clear();

        public ReadOnlySpanOwnerWithMetadata<byte> Get(scoped in Key key)
        {
            return _data.TryGetValue(GetKey(key), out var value)
                ? new ReadOnlySpanOwner<byte>(value, null).WithDepth(0)
                : commit.Get(key);
        }

        public void Set(in Key key, in ReadOnlySpan<byte> payload, EntryType type)
        {
            _data[GetKey(key)] = payload.ToArray();
        }

        public void Set(in Key key, in ReadOnlySpan<byte> payload0, in ReadOnlySpan<byte> payload1, EntryType type)
        {
            _data[GetKey(key)] = Concat(payload0, payload1);
        }

        public void Commit()
        {
            foreach (var kvp in _data)
            {
                Key.ReadFrom(kvp.Key, out var key);
                commit.Set(key, kvp.Value);
            }
        }

        public IChildCommit GetChild() => new ChildCommit(this);
        public IReadOnlyDictionary<Keccak, int> Stats => throw new NotImplementedException("Child commit has no stats");
    }

    public KeyEnumerator GetSnapshotOfBefore() => new(_before.Keys.ToArray());

    public ref struct KeyEnumerator(byte[][] keys)
    {
        private int _index = -1;

        public bool MoveNext()
        {
            var index = _index + 1;
            if (index < keys.Length)
            {
                _index = index;
                Key.ReadFrom(keys[index], out var key);
                Current = key;

                return true;
            }

            return false;
        }

        public KeyEnumerator GetEnumerator() => this;

        public Key Current { get; private set; }
    }

    /// <summary>
    /// Sets the commit into an asserting mode, where all the sets will be removing and asserting values from it.
    /// </summary>
    public void StartAssert()
    {
        _asserting = true;
    }

    /// <summary>
    /// Generates next commit, folding history into the current state.
    /// </summary>
    public Commit Squash(bool removeEmpty = false)
    {
        var commit = new Commit();

        var dict = commit._history;

        foreach (var kvp in _before)
        {
            dict[kvp.Key] = kvp.Value;
        }

        foreach (var kvp in _after)
        {
            dict[kvp.Key] = kvp.Value;
        }

        if (removeEmpty)
        {
            var emptyKeys = dict
                .Where((kvp) => kvp.Value.Length == 0)
                .Select(kvp => kvp.Key)
                .ToArray();

            foreach (var emptyKey in emptyKeys)
            {
                dict.Remove(emptyKey);
            }
        }

        _touchedAccounts.Clear();
        _storageSlots.Clear();

        return commit;
    }

    public void MergeAfterToBefore()
    {
        foreach (var kvp in _after)
        {
            _before[kvp.Key] = kvp.Value;
        }

        _after.Clear();
    }

    public IReadOnlySet<Keccak> TouchedAccounts => _touchedAccounts;
    public IReadOnlyDictionary<Keccak, IStorageStats> TouchedStorageSlots => Unsafe.As<IReadOnlyDictionary<Keccak, IStorageStats>>(_storageSlots);
}
