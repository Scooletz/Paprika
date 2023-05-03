﻿namespace Paprika.Pages;

/// <summary>
/// Represents an intent to set the account data.
/// </summary>
public readonly ref struct SetContext
{
    public readonly FixedMap.Key Key;
    public readonly IBatchContext Batch;
    public readonly ReadOnlySpan<byte> Data;

    public SetContext(FixedMap.Key key, ReadOnlySpan<byte> data, IBatchContext batch)
    {
        Key = key;
        Batch = batch;
        Data = data;
    }

    /// <summary>
    /// Creates the set context with the <see cref="Key"/> sliced from the given nibble.
    /// </summary>
    public SetContext SliceFrom(int nibbleCount) => new(Key.SliceFrom(nibbleCount), Data, Batch);
}