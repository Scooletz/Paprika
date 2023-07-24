﻿using FluentAssertions;
using NUnit.Framework;
using Paprika.Chain;
using Paprika.Data;
using Paprika.Merkle;
using Paprika.Utils;

using static Paprika.Tests.Values;

namespace Paprika.Tests.Merkle;

public class DirtyTests
{
    [Test(Description = "No values set, no changes tracked")]
    public void Empty()
    {
        var merkle = new ComputeMerkleBehavior();
        var commit = new Commit();

        merkle.BeforeCommit(commit);

        commit.ShouldBeEmpty();
    }

    [Test(Description = "Single account should create only a single leaf")]
    public void Single_account()
    {
        var account = Key.Account(Key2);

        var merkle = new ComputeMerkleBehavior();
        var commit = new Commit();
        
        commit.Set(account, new byte[] { 1 });

        merkle.BeforeCommit(commit);

        commit.StartAssert();
        commit.SetLeaf(Key.Merkle(NibblePath.Empty), NibblePath.FromKey(Key2));
        commit.ShouldBeEmpty();
    }

    class Commit : ICommit
    {
        private readonly Dictionary<byte[], byte[]> _before = new(new BytesEqualityComparer());
        private readonly Dictionary<byte[], byte[]> _after = new(new BytesEqualityComparer());
        private bool _asserting;

        public void Set(in Key key, ReadOnlySpan<byte> value)
        {
            _before[GetKey(key)] = value.ToArray();
        }

        public void ShouldBeEmpty() => _after.Count.Should().Be(0);

        ReadOnlySpanOwner<byte> ICommit.Get(in Key key)
        {
            var k = GetKey(key);
            if (_before.TryGetValue(k, out var value))
            {
                return new ReadOnlySpanOwner<byte>(value, null);
            }
            if (_after.TryGetValue(k, out value))
            {
                return new ReadOnlySpanOwner<byte>(value, null);
            }

            return default;
        }

        private static byte[] GetKey(in Key key) => key.WriteTo(stackalloc byte[key.MaxByteLength]).ToArray();

        void ICommit.Set(in Key key, in ReadOnlySpan<byte> payload)
        {
            var bytes = GetKey(key);
            if (_asserting == false)
            {
                _after[bytes] = payload.ToArray();    
            }
            else
            {
                _after.Remove(bytes, out var existing).Should().BeTrue();
                payload.SequenceEqual(existing).Should().BeTrue();
            }
        }

        void ICommit.Visit(CommitAction action)
        {
            foreach (var (k, _) in _before)
            {
                Key.ReadFrom(k, out var key);
                action(key, this);
            }
        }

        /// <summary>
        /// Sets the commit into an asserting mode, where all the sets will be removing and asserting values from it.
        /// </summary>
        public void StartAssert()
        {
            _asserting = true;
        }
    }
}