﻿using Nethermind.Int256;

namespace Paprika.Pages;

/// <summary>
/// See <see cref="AccountFrame"/> for more considerations about narrowing types.
/// </summary>
public readonly ref struct SetContext
{
    public readonly NibblePath Key;
    public readonly UInt128 Balance;
    public readonly uint Nonce;

    public SetContext(in Keccak keccak, in UInt256 balance, in UInt256 nonce)
    {
        Key = NibblePath.FromKey(keccak);
        Balance = new UInt128(balance[1], balance[0]);
        Nonce = (uint)nonce;
    }
}