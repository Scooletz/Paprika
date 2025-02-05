using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Paprika.Chain;

namespace Paprika.Benchmarks;

[MemoryDiagnoser]
[DisassemblyDiagnoser]
public class PooledSpanDictionaryBenchmarks
{
    private readonly BufferPool _pool = new(1, BufferPool.PageTracking.None);
    private readonly PooledSpanDictionary _bigDict;

    private const int BigDictCount = 32_000;

    private static ReadOnlySpan<byte> Value32Bytes => new byte[]
    {
        0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77,
        0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF,
        0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77,
        0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF,
    };

    private readonly PooledSpanDictionary _varLengthKeys;
    private readonly PooledSpanDictionary _readWrite;
    private const int VarLengthKeyCollisions = 8;
    private const ulong VarLengthKeyCollisionHash = 2348598349058394;

    private static ReadOnlySpan<byte> VarLengthKey => new byte[]
    {
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
    };

    public PooledSpanDictionaryBenchmarks()
    {
        _bigDict = new PooledSpanDictionary(new BufferPool(128, BufferPool.PageTracking.None, null));

        Span<byte> key = stackalloc byte[4];

        for (uint i = 0; i < BigDictCount; i++)
        {
            Unsafe.WriteUnaligned(ref key[0], i);
            _bigDict.Set(key, i, Value32Bytes, 1);
        }

        _varLengthKeys = new PooledSpanDictionary(new BufferPool(32, BufferPool.PageTracking.None, null));

        for (uint i = 0; i < VarLengthKeyCollisions; i++)
        {
            _bigDict.Set(VarLengthKey[..VarLengthKeyCollisions], VarLengthKeyCollisionHash, Value32Bytes, 1);
        }

        _readWrite = new PooledSpanDictionary(new BufferPool(128, BufferPool.PageTracking.None, null));
    }

    [Benchmark]
    public int Read_from_big_dict()
    {
        Span<byte> key = stackalloc byte[4];

        var count = 0;
        for (uint i = 0; i < BigDictCount; i++)
        {
            Unsafe.WriteUnaligned(ref key[0], i);
            if (_bigDict.TryGet(key, i, out var data))
            {
                count++;
            }
        }

        return count;
    }

    [Benchmark]
    public int Enumerate_big_dict()
    {
        var count = 0;

        foreach (var item in _bigDict)
        {
            count += item.Metadata;
        }

        return count;
    }


    [Benchmark]
    public int Read_missing_with_hash_collisions()
    {
        Span<byte> key = stackalloc byte[4];

        var count = 0;
        for (uint i = 0; i < BigDictCount; i++)
        {
            Unsafe.WriteUnaligned(ref key[0], i + 1_000_000);
            if (_bigDict.TryGet(key, i, out var data))
            {
                count++;
            }
        }

        return count;
    }

    [Benchmark]
    public int Read_missing_with_no_hash_collisions()
    {
        Span<byte> key = stackalloc byte[4];

        var count = 0;
        for (uint i = 0; i < BigDictCount; i++)
        {
            Unsafe.WriteUnaligned(ref key[0], i);
            if (_bigDict.TryGet(key, i + 1_000_000, out var data))
            {
                count++;
            }
        }

        return count;
    }

    [Benchmark]
    public int Read_write_small()
    {
        Span<byte> key = stackalloc byte[2];

        var count = 0;
        for (byte i = 0; i < 255; i++)
        {
            key[0] = i;

            _readWrite.Set(key, i, key, 1);
            _readWrite.TryGet(key, i, out var result);

            count += result[0];
        }

        return count;
    }

    [Benchmark(OperationsPerInvoke = VarLengthKeyCollisions)]
    public int Read_colliding_hashes_different_lengths()
    {
        var count = 0;

        for (var i = 0; i < VarLengthKeyCollisions; i++)
        {
            _varLengthKeys.TryGet(VarLengthKey[..(i + 1)], VarLengthKeyCollisionHash, out var data);
            count += data.Length;
        }

        return count;
    }
}
