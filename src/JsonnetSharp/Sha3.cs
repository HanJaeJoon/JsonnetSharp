// SHA3-512 as golang.org/x/crypto/sha3.Sum512 computes it (FIPS 202), for std.sha3.

using System.Security.Cryptography;

namespace JsonnetSharp;

internal sealed class Sha3 : HashAlgorithm
{
    private static readonly ulong[] RoundConstants =
    {
        0x0000000000000001UL, 0x0000000000008082UL, 0x800000000000808AUL, 0x8000000080008000UL,
        0x000000000000808BUL, 0x0000000080000001UL, 0x8000000080008081UL, 0x8000000000008009UL,
        0x000000000000008AUL, 0x0000000000000088UL, 0x0000000080008009UL, 0x000000008000000AUL,
        0x000000008000808BUL, 0x800000000000008BUL, 0x8000000000008089UL, 0x8000000000008003UL,
        0x8000000000008002UL, 0x8000000000000080UL, 0x000000000000800AUL, 0x800000008000000AUL,
        0x8000000080008081UL, 0x8000000000008080UL, 0x0000000080000001UL, 0x8000000080008008UL,
    };

    private static readonly int[] RotationOffsets =
    {
        1, 3, 6, 10, 15, 21, 28, 36, 45, 55, 2, 14, 27, 41, 56, 8, 25, 43, 62, 18, 39, 61, 20, 44,
    };

    private static readonly int[] PiLanes =
    {
        10, 7, 11, 17, 18, 3, 5, 16, 8, 21, 24, 4, 15, 23, 19, 13, 12, 2, 20, 14, 22, 9, 6, 1,
    };

    private const int Rate = 72; // (1600 - 2 * 512) / 8

    private readonly ulong[] _state = new ulong[25];
    private readonly byte[] _block = new byte[Rate];
    private int _fill;

    public Sha3()
    {
        HashSizeValue = 512;
    }

    // Returns the 64-byte SHA3-512 digest of data.
    public static byte[] Sum512(byte[] data)
    {
        using Sha3 h = new();
        return h.ComputeHash(data);
    }

    public override void Initialize()
    {
        Array.Clear(_state, 0, _state.Length);
        _fill = 0;
    }

    protected override void HashCore(byte[] array, int ibStart, int cbSize)
    {
        while (cbSize > 0)
        {
            int n = Math.Min(Rate - _fill, cbSize);
            Buffer.BlockCopy(array, ibStart, _block, _fill, n);
            _fill += n;
            ibStart += n;
            cbSize -= n;
            if (_fill == Rate)
            {
                Absorb(_state, _block, 0, Rate);
                KeccakF1600(_state);
                _fill = 0;
            }
        }
    }

    protected override byte[] HashFinal()
    {
        // Pad the last block: SHA-3 domain bits 01, then pad10*1.
        Array.Clear(_block, _fill, Rate - _fill);
        _block[_fill] ^= 0x06;
        _block[Rate - 1] ^= 0x80;
        Absorb(_state, _block, 0, Rate);
        KeccakF1600(_state);

        var digest = new byte[64];
        for (int i = 0; i < 8; i++)
        {
            ulong lane = _state[i];
            for (int b = 0; b < 8; b++)
            {
                digest[i * 8 + b] = (byte)(lane >> (8 * b));
            }
        }
        return digest;
    }

    private static void Absorb(ulong[] state, byte[] src, int offset, int rate)
    {
        for (int i = 0; i < rate / 8; i++)
        {
            ulong lane = 0;
            for (int b = 0; b < 8; b++)
            {
                lane |= (ulong)src[offset + i * 8 + b] << (8 * b);
            }
            state[i] ^= lane;
        }
    }

    private static ulong Rotl(ulong x, int n) => (x << n) | (x >> (64 - n));

    private static void KeccakF1600(ulong[] a)
    {
        var c = new ulong[5];
        for (int round = 0; round < 24; round++)
        {
            // Theta
            for (int x = 0; x < 5; x++)
            {
                c[x] = a[x] ^ a[x + 5] ^ a[x + 10] ^ a[x + 15] ^ a[x + 20];
            }
            for (int x = 0; x < 5; x++)
            {
                ulong d = c[(x + 4) % 5] ^ Rotl(c[(x + 1) % 5], 1);
                for (int y = 0; y < 25; y += 5)
                {
                    a[y + x] ^= d;
                }
            }

            // Rho and pi
            ulong current = a[1];
            for (int i = 0; i < 24; i++)
            {
                int j = PiLanes[i];
                ulong next = a[j];
                a[j] = Rotl(current, RotationOffsets[i]);
                current = next;
            }

            // Chi
            for (int y = 0; y < 25; y += 5)
            {
                for (int x = 0; x < 5; x++)
                {
                    c[x] = a[y + x];
                }
                for (int x = 0; x < 5; x++)
                {
                    a[y + x] = c[x] ^ (~c[(x + 1) % 5] & c[(x + 2) % 5]);
                }
            }

            // Iota
            a[0] ^= RoundConstants[round];
        }
    }
}
