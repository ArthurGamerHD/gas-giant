using System;

namespace GasGiantNet.Random
{
    internal static class RandomSelfTest
    {
        public static void Run()
        {
            TestDeterminismAndSubstreams();
            TestIntegerBounds();
            TestNormalMoments();
            TestPoissonMoments(5.0, 30000, 0.08, 0.16);
            TestPoissonMoments(20.0, 30000, 0.16, 0.55);
        }

        private static void TestDeterminismAndSubstreams()
        {
            RandomGenerator first = RandomGenerator.Subseed(42, "storms");
            RandomGenerator replay = RandomGenerator.Subseed(42, "storms");
            RandomGenerator other = RandomGenerator.Subseed(42, "events");
            bool streamsDiffer = false;
            for (int i = 0; i < 64; i++)
            {
                double actual = first.Random();
                double repeated = replay.Random();
                double separate = other.Random();
                if (BitConverter.DoubleToInt64Bits(actual) != BitConverter.DoubleToInt64Bits(repeated))
                    throw new InvalidOperationException("System.Random stream is not repeatable at sample " + i + ".");
                if (BitConverter.DoubleToInt64Bits(actual) != BitConverter.DoubleToInt64Bits(separate))
                    streamsDiffer = true;
            }
            if (!streamsDiffer) throw new InvalidOperationException("Named random substreams are not separated.");
        }

        private static void TestIntegerBounds()
        {
            RandomGenerator rng = RandomGenerator.Subseed(42, "integer-bounds");
            for (int i = 0; i < 10000; i++)
            {
                long value = rng.Integers(-3, 7);
                if (value < -3 || value >= 7)
                    throw new InvalidOperationException("System.Random integer sample is outside [low, high).");
            }
        }

        private static void TestNormalMoments()
        {
            const int count = 50000;
            RandomGenerator rng = RandomGenerator.Subseed(42, "normal-moments");
            double sum = 0.0;
            double sumSquares = 0.0;
            for (int i = 0; i < count; i++)
            {
                double value = rng.StandardNormal();
                sum += value;
                sumSquares += value * value;
            }
            double mean = sum / count;
            double variance = sumSquares / count - mean * mean;
            if (Math.Abs(mean) > 0.03 || Math.Abs(variance - 1.0) > 0.04)
                throw new InvalidOperationException("Normal distribution moment check failed.");
        }

        private static void TestPoissonMoments(double lambda, int count, double meanTolerance, double varianceTolerance)
        {
            RandomGenerator rng = RandomGenerator.Subseed(42, "poisson-moments:" + lambda);
            double sum = 0.0;
            double sumSquares = 0.0;
            for (int i = 0; i < count; i++)
            {
                double value = rng.Poisson(lambda);
                sum += value;
                sumSquares += value * value;
            }
            double mean = sum / count;
            double variance = sumSquares / count - mean * mean;
            if (Math.Abs(mean - lambda) > meanTolerance || Math.Abs(variance - lambda) > varianceTolerance)
                throw new InvalidOperationException("Poisson distribution moment check failed for lambda=" + lambda + ".");
        }
    }
}
