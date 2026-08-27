using System;
using GasGiantNet.Config;

namespace GasGiantNet.Sim
{
    internal static class ResolutionScaling
    {
        public static double ScaleFactor(ParamTree p)
        {
            if (!p.Bool("sim.resolution_invariant")) return 1.0;
            int resolution = p.Int("sim.resolution");
            int reference = p.Int("sim.reference_resolution");
            if (reference <= 0 || resolution == reference) return 1.0;
            return (double)resolution / reference;
        }

        public static int EffectiveDevSteps(ParamTree p)
        {
            int raw = p.Int("sim.dev_steps");
            int n = ScaleDuration(raw, ScaleFactor(p));
            return raw > 0 ? Math.Max(1, n) : n;
        }

        public static int ScaleDuration(int steps, double s)
        {
            if (s == 1.0) return steps;
            return (int)Math.Round(steps * s, MidpointRounding.ToEven);
        }

        public static double ScaleDecayFraction(double f, double s)
        {
            if (s == 1.0) return f;
            double retained = 1.0 - f;
            if (retained <= 0.0) return f;
            return 1.0 - Math.Pow(retained, 1.0 / s);
        }

        public static double ScaleRate(double c, double s)
        {
            return s == 1.0 ? c : c / s;
        }

        public static double ScaleRelaxTau(double tau, double s)
        {
            if (s == 1.0 || tau <= 0.0) return tau;
            double f = ScaleDecayFraction(1.0 / tau, s);
            return f > 0.0 ? 1.0 / f : tau;
        }

        public static double ScaleStochasticAmp(double amp, double s)
        {
            return s == 1.0 ? amp : amp / Math.Sqrt(s);
        }
    }
}
