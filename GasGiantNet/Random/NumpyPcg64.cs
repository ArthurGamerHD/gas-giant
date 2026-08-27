using System;
using System.Collections.Generic;
using System.Text;

namespace GasGiantNet.Random
{
    // todo: remove after ensure "good enough" results on System.Random()
    internal struct UInt128Pair
    {
        public ulong High;
        public ulong Low;

        public UInt128Pair(ulong high, ulong low)
        {
            High = high;
            Low = low;
        }

        public static UInt128Pair Add(UInt128Pair a, UInt128Pair b)
        {
            ulong lo = unchecked(a.Low + b.Low);
            ulong carry = lo < a.Low ? 1UL : 0UL;
            ulong hi = unchecked(a.High + b.High + carry);
            return new UInt128Pair(hi, lo);
        }

        public static UInt128Pair Multiply(UInt128Pair a, UInt128Pair b)
        {
            ulong hi0;
            ulong lo0;
            Mul64(a.Low, b.Low, out hi0, out lo0);
            ulong cross = unchecked(a.High * b.Low + a.Low * b.High);
            return new UInt128Pair(unchecked(hi0 + cross), lo0);
        }

        public static UInt128Pair ShiftLeft1OrOne(UInt128Pair x)
        {
            ulong high = unchecked((x.High << 1) | (x.Low >> 63));
            ulong low = unchecked((x.Low << 1) | 1UL);
            return new UInt128Pair(high, low);
        }

        public static void Mul64(ulong x, ulong y, out ulong high, out ulong low)
        {
            low = unchecked(x * y);
            ulong x0 = x & 0xffffffffUL;
            ulong x1 = x >> 32;
            ulong y0 = y & 0xffffffffUL;
            ulong y1 = y >> 32;
            ulong w0 = x0 * y0;
            ulong t = unchecked(x1 * y0 + (w0 >> 32));
            ulong w1 = t & 0xffffffffUL;
            ulong w2 = t >> 32;
            w1 = unchecked(w1 + x0 * y1);
            high = unchecked(x1 * y1 + w2 + (w1 >> 32));
        }
    }

    internal static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1U) != 0 ? 0xedb88320U ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        public static uint Compute(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            uint c = 0xffffffffU;
            for (int i = 0; i < bytes.Length; i++) c = Table[(c ^ bytes[i]) & 0xffU] ^ (c >> 8);
            return c ^ 0xffffffffU;
        }
    }

    /// <summary>Bit-compatible implementation of numpy.random.SeedSequence for integer entropy.</summary>
    internal sealed class NumpySeedSequence
    {
        private const uint InitA = 0x43b0d7e5U;
        private const uint MultA = 0x931e8875U;
        private const uint InitB = 0x8b51f9ddU;
        private const uint MultB = 0x58f38dedU;
        private const uint MixMultL = 0xca01f9ddU;
        private const uint MixMultR = 0x4973f715U;
        private const int XShift = 16;

        private readonly uint[] _pool;

        public NumpySeedSequence(IList<uint> entropy)
        {
            _pool = new uint[4];
            uint[] assembled = new uint[entropy.Count];
            for (int i = 0; i < assembled.Length; i++) assembled[i] = entropy[i];
            MixEntropy(_pool, assembled);
        }

        private static uint HashMix(uint value, ref uint hashConst)
        {
            value ^= hashConst;
            hashConst = unchecked(hashConst * MultA);
            value = unchecked(value * hashConst);
            value ^= value >> XShift;
            return value;
        }

        private static uint Mix(uint x, uint y)
        {
            uint result = unchecked(MixMultL * x - MixMultR * y);
            result ^= result >> XShift;
            return result;
        }

        private static void MixEntropy(uint[] mixer, uint[] entropy)
        {
            uint hc = InitA;
            for (int i = 0; i < mixer.Length; i++) mixer[i] = HashMix(i < entropy.Length ? entropy[i] : 0U, ref hc);
            for (int src = 0; src < mixer.Length; src++)
            {
                for (int dst = 0; dst < mixer.Length; dst++)
                {
                    if (src != dst) mixer[dst] = Mix(mixer[dst], HashMix(mixer[src], ref hc));
                }
            }
            for (int src = mixer.Length; src < entropy.Length; src++)
            {
                for (int dst = 0; dst < mixer.Length; dst++) mixer[dst] = Mix(mixer[dst], HashMix(entropy[src], ref hc));
            }
        }

        public uint[] GenerateState32(int count)
        {
            uint[] state = new uint[count];
            uint hc = InitB;
            for (int i = 0; i < count; i++)
            {
                uint v = _pool[i % _pool.Length];
                v ^= hc;
                hc = unchecked(hc * MultB);
                v = unchecked(v * hc);
                v ^= v >> XShift;
                state[i] = v;
            }
            return state;
        }

        public ulong[] GenerateState64(int count)
        {
            uint[] words = GenerateState32(count * 2);
            ulong[] state = new ulong[count];
            for (int i = 0; i < count; i++) state[i] = (ulong)words[i * 2] | ((ulong)words[i * 2 + 1] << 32);
            return state;
        }
    }

    /// <summary>NumPy PCG64 (XSL-RR 128/64) plus Generator-compatible uniform/integer primitives.</summary>
    internal sealed class NumpyGenerator
    {
        private static readonly UInt128Pair Multiplier = new UInt128Pair(0x2360ed051fc65da4UL, 0x4385df649fccf645UL);
        private UInt128Pair _state;
        private UInt128Pair _inc;
        private bool _hasUInt32;
        private uint _cachedUInt32;

        public static NumpyGenerator Subseed(int masterSeed, string name)
        {
            uint tag = Crc32.Compute(name);
            return new NumpyGenerator(new uint[] { unchecked((uint)masterSeed), tag });
        }

        public NumpyGenerator(IList<uint> entropy)
        {
            NumpySeedSequence ss = new NumpySeedSequence(entropy);
            ulong[] seed = ss.GenerateState64(4);
            UInt128Pair initState = new UInt128Pair(seed[0], seed[1]);
            UInt128Pair initSeq = new UInt128Pair(seed[2], seed[3]);
            _state = new UInt128Pair(0, 0);
            _inc = UInt128Pair.ShiftLeft1OrOne(initSeq);
            Step();
            _state = UInt128Pair.Add(_state, initState);
            Step();
        }

        private void Step()
        {
            _state = UInt128Pair.Add(UInt128Pair.Multiply(_state, Multiplier), _inc);
        }

        private static ulong RotR(ulong value, int rot)
        {
            rot &= 63;
            return (value >> rot) | (value << ((-rot) & 63));
        }

        public ulong NextUInt64()
        {
            Step();
            return RotR(_state.High ^ _state.Low, (int)(_state.High >> 58));
        }

        public uint NextUInt32()
        {
            if (_hasUInt32)
            {
                _hasUInt32 = false;
                return _cachedUInt32;
            }
            ulong next = NextUInt64();
            _hasUInt32 = true;
            _cachedUInt32 = (uint)(next >> 32);
            return (uint)next;
        }

        public double Random()
        {
            return (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);
        }

        public double Uniform(double low, double high)
        {
            return low + (high - low) * Random();
        }

        public void Uniform(double low, double high, double[] output)
        {
            for (int i = 0; i < output.Length; i++) output[i] = Uniform(low, high);
        }

        public long Integers(long low, long high)
        {
            if (high <= low) throw new ArgumentException("high must be greater than low");
            ulong range = unchecked((ulong)(high - low - 1));
            return unchecked((long)BoundedUInt64(unchecked((ulong)low), range));
        }

        private ulong BoundedUInt64(ulong off, ulong range)
        {
            if (range == 0UL) return off;
            if (range == ulong.MaxValue) return unchecked(NextUInt64() + off);
            ulong n = unchecked(range + 1UL);
            ulong hi;
            ulong lo;
            UInt128Pair.Mul64(NextUInt64(), n, out hi, out lo);
            if (lo < n)
            {
                ulong threshold = unchecked(0UL - n) % n;
                while (lo < threshold) UInt128Pair.Mul64(NextUInt64(), n, out hi, out lo);
            }
            return unchecked(hi + off);
        }

        // NumPy uses a Ziggurat transform for normal/poisson. These methods are
        // implemented in NumpyDistributions.cs so all distribution transforms stay
        // separate from the bit generator and can be tested against NumPy vectors.
        public double StandardNormal() { return NumpyDistributions.StandardNormal(this); }
        public double Normal(double mean, double sigma) { return mean + sigma * StandardNormal(); }
        public long Poisson(double lambda) { return NumpyDistributions.Poisson(this, lambda); }
    }
}
