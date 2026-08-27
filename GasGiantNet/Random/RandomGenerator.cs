using System;

namespace GasGiantNet.Random
{
    /// <summary>Named deterministic random stream backed by System.Random.</summary>
    internal sealed class RandomGenerator
    {
        private readonly System.Random _random;

        public static RandomGenerator Subseed(int masterSeed, string name)
        {
            if (name == null) throw new ArgumentNullException("name");
            return new RandomGenerator(DeriveSeed(masterSeed, name));
        }

        private RandomGenerator(int seed)
        {
            _random = new System.Random(seed);
        }

        private static int DeriveSeed(int masterSeed, string name)
        {
            // Do not use string.GetHashCode(): its value is deliberately randomized
            // between processes. FNV-1a plus an avalanche keeps named streams stable.
            uint value = 2166136261U;
            value = unchecked((value ^ (uint)masterSeed) * 16777619U);
            for (int i = 0; i < name.Length; i++)
            {
                char ch = name[i];
                value = unchecked((value ^ (byte)ch) * 16777619U);
                value = unchecked((value ^ (byte)(ch >> 8)) * 16777619U);
            }
            value ^= value >> 16;
            value = unchecked(value * 0x7feb352dU);
            value ^= value >> 15;
            value = unchecked(value * 0x846ca68bU);
            value ^= value >> 16;
            return unchecked((int)value);
        }

        public double Random()
        {
            return _random.NextDouble();
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
            return _random.NextInt64(low, high);
        }

        public double StandardNormal() { return RandomDistributions.StandardNormal(this); }
        public double Normal(double mean, double sigma) { return mean + sigma * StandardNormal(); }
        public long Poisson(double lambda) { return RandomDistributions.Poisson(this, lambda); }
    }
}
