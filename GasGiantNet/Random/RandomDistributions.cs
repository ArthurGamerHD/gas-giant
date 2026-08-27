using System;

namespace GasGiantNet.Random
{
    internal static class RandomDistributions
    {
        public static double StandardNormal(RandomGenerator rng)
        {
            // Box-Muller transform. 1-u stays in (0, 1], avoiding log(0).
            double u1 = 1.0 - rng.Random();
            double u2 = rng.Random();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        public static long Poisson(RandomGenerator rng, double lambda)
        {
            if (lambda < 0.0 || double.IsNaN(lambda) || double.IsInfinity(lambda))
                throw new ArgumentException("lambda must be finite and nonnegative");
            if (lambda == 0.0) return 0;
            if (lambda >= 10.0) return PoissonPtrs(rng, lambda);

            double limit = Math.Exp(-lambda);
            long value = 0;
            double product = 1.0;
            for (;;)
            {
                product *= rng.Random();
                if (product > limit) value++;
                else return value;
            }
        }

        // Hoermann transformed rejection for lambda >= 10.
        private static long PoissonPtrs(RandomGenerator rng, double lambda)
        {
            double sqrtLambda = Math.Sqrt(lambda);
            double logLambda = Math.Log(lambda);
            double b = 0.931 + 2.53 * sqrtLambda;
            double a = -0.059 + 0.02483 * b;
            double inverseAlpha = 1.1239 + 1.1328 / (b - 3.4);
            double vr = 0.9277 - 3.6224 / (b - 2.0);
            for (;;)
            {
                double u = rng.Random() - 0.5;
                double v = rng.Random();
                double us = 0.5 - Math.Abs(u);
                long k = (long)Math.Floor((2.0 * a / us + b) * u + lambda + 0.43);
                if (us >= 0.07 && v <= vr) return k;
                if (k < 0 || (us < 0.013 && v > us)) continue;
                if (Math.Log(v * inverseAlpha / (a / (us * us) + b))
                    <= -lambda + k * logLambda - LogFactorial(k)) return k;
            }
        }

        private static double LogFactorial(long value)
        {
            if (value < 2) return 0.0;
            double result = 0.0;
            for (long i = 2; i <= value; i++) result += Math.Log(i);
            return result;
        }
    }
}
