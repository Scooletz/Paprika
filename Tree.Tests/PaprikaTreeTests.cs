﻿using System.Buffers.Binary;
using NUnit.Framework;

namespace Tree.Tests;

public class PaprikaTreeTests
{
    [Test]
    public void Extension()
    {
        using var db = new MemoryDb(64 * 1024);

        var tree = new PaprikaTree(db);

        var key1 = new byte[32];
        key1[0] = 0x01;
        key1[31] = 0xA;
        var key2 = new byte[32];
        key2[0] = 0x21;
        key1[31] = 0xB;
        
        var batch = tree.Begin();
        batch.Set(key1, key1);
        batch.Set(key2, key2);
        batch.Commit();
        
        Assert(tree, key1);
        Assert(tree, key2);
        
        void Assert(PaprikaTree paprikaTree, byte[] bytes)
        {
            NUnit.Framework.Assert.True(paprikaTree.TryGet(bytes.AsSpan(), out var retrieved));
            NUnit.Framework.Assert.True(retrieved.SequenceEqual(bytes.AsSpan()));
        }
    }
    
    [Test]
    public void NonUpdatableTest()
    {
        using var db = new MemoryDb(1024 * 1024 * 1024);

        var tree = new PaprikaTree(db);

        const int count = 1_200_000;

        foreach (var (key, value) in Build(count))
        {
            tree.Set(key.AsSpan(), value.AsSpan());
        }

        foreach (var (key, value) in Build(count))
        {
            Assert.True(tree.TryGet(key.AsSpan(), out var retrieved), $"for key {key.Field0}");
            Assert.True(retrieved.SequenceEqual(value.AsSpan()));
        }

        var percentage = (int)(((double)db.Position) / db.Size * 100);

        Console.WriteLine($"used {percentage}%");
    }
    
    [Test]
    public void UpdatableTest()
    {
        using var db = new MemoryDb(1024 * 1024 * 1024);

        var tree = new PaprikaTree(db);

        const int count = 1_200_000;

        int i = 0;
        int batchSize = 10000;
        
        var batch = tree.Begin();
        foreach (var (key, value) in Build(count))
        {
            batch.Set(key.AsSpan(), value.AsSpan());
            i++;
            if (i > batchSize)
            {
                batch.Commit();
                batch = tree.Begin();
                i = 0;
            }
        }
        
        batch.Commit();

        foreach (var (key, value) in Build(count))
        {
            Assert.True(tree.TryGet(key.AsSpan(), out var retrieved), $"for key {key.Field0}");
            Assert.True(retrieved.SequenceEqual(value.AsSpan()));
        }

        var percentage = (int)(((double)db.Position) / db.Size * 100);

        Console.WriteLine($"used {percentage}%");
    }

    private static IEnumerable<KeyValuePair<Keccak, Keccak>> Build(int number)
    {
        // builds the values so no extensions in the tree are required
        for (long i = 1; i < number + 1; i++)
        {
            // set nibbles, so that no extensions happen
            // var n = (int)(((i & 0xF) << NibbleSize) |
            //               ((i & 0xF0) >> NibbleSize) |
            //               ((i & 0xF00) << NibbleSize) |
            //               ((i & 0xF000) >> NibbleSize) |
            //               ((i & 0xF0000) << NibbleSize) |
            //               ((i & 0xF00000) >> NibbleSize) |
            //               ((i & 0xF000000) << NibbleSize) |
            //               ((i & 0xF0000000) >> NibbleSize));

            var n = i;

            Keccak key = default;
            Keccak value = default;

            BinaryPrimitives.WriteInt32LittleEndian(key.AsSpan(), (int)n);
            BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(), (int)i);

            yield return new KeyValuePair<Keccak, Keccak>(key, value);
        }
    }
}