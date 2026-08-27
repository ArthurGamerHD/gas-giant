using System;

namespace GasGiantNet.Random
{
    // todo: remove after ensure "good enough" results on System.Random()
    internal static class NumpyParitySelfTest
    {
        public static void Run()
        {
            TestRawPcg64();
            TestNormals();
            TestPoissonSmall();
            TestPoissonPtrs();
        }

        private static void TestRawPcg64()
        {
            ulong[] expected = new ulong[] {
                16791602701691438111UL, 17047169590823768830UL,
                11278973418819949955UL, 12897504882119008203UL,
                1983984723009747222UL, 14423020591956739393UL,
                13902315248224121344UL, 4857294955338877973UL
            };
            NumpyGenerator r = NumpyGenerator.Subseed(42, "storms");
            for (int i = 0; i < expected.Length; i++)
            {
                ulong actual = r.NextUInt64();
                if (actual != expected[i])
                    throw new InvalidOperationException("NumPy PCG64 parity failed at raw sample " + i + ".");
            }
        }

        private static void TestNormals()
        {
            double[] expected = new double[] {
                0.22979477844719126, 1.3556744097674227,
                -1.3868979430130195, -1.2666070958856348,
                -0.6136594175872753, -0.2813900003460732,
                0.11406960559849959, 0.07463162463405316
            };
            NumpyGenerator r = NumpyGenerator.Subseed(42, "storms");
            for (int i = 0; i < expected.Length; i++)
            {
                double actual = r.StandardNormal();
                if (BitConverter.DoubleToInt64Bits(actual) != BitConverter.DoubleToInt64Bits(expected[i]))
                    throw new InvalidOperationException("NumPy normal parity failed at sample " + i + ".");
            }
        }

        private static void TestPoissonSmall()
        {
            long[] expected = new long[] { 7, 7, 2, 2, 2, 9, 4, 6, 5, 4, 7, 6 };
            NumpyGenerator r = NumpyGenerator.Subseed(42, "events");
            for (int i = 0; i < expected.Length; i++)
            {
                long actual = r.Poisson(5.0);
                if (actual != expected[i])
                    throw new InvalidOperationException("NumPy Poisson-small parity failed at sample " + i + ".");
            }
        }

        private static void TestPoissonPtrs()
        {
            long[] expected = new long[] { 26, 21, 22, 21, 19, 25, 17, 24, 22, 13, 19, 24 };
            NumpyGenerator r = NumpyGenerator.Subseed(42, "events");
            for (int i = 0; i < expected.Length; i++)
            {
                long actual = r.Poisson(20.0);
                if (actual != expected[i])
                    throw new InvalidOperationException("NumPy Poisson-PTRS parity failed at sample " + i + ".");
            }
        }
    }
}
