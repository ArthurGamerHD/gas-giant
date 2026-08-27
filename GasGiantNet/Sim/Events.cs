using System;
using System.Collections.Generic;
using GasGiantNet.Config;
using GasGiantNet.Random;

namespace GasGiantNet.Sim
{
    internal sealed class Outbreak
    {
        public int Step;
        public double Lat;
        public double Lon;
        public double Radius = EventSchedule.Radius;
        public double BrightMul = 1.0;
        public Vortex Vortex;
    }

    internal struct OutflowImpulse
    {
        public double Lon;
        public double Lat;
        public double Radius;
        public double Strength;
    }

    internal sealed class EventSchedule
    {
        public const int Lifetime = 300;
        public const int Ramp = 16;
        public const double Radius = 0.048;
        public const double Brightness = 1.9;
        public const double Outflow = 0.18;
        public const int TrainN = 6;
        public const double TrainLatSpread = 0.035;
        public const double TrainLonStep = 0.06;
        public const double LeadBright = 1.8;
        public const double LeadRadius = 1.2;

        public readonly List<Outbreak> Outbreaks = new List<Outbreak>();
        public double Strength = 1.0;
        public double StepScale = 1.0;

        public static EventSchedule Generate(int seed, ParamTree p, BandLayout bands, LatProfiles profiles, double? dt)
        {
            RandomGenerator rng = RandomGenerator.Subseed(seed, "events");
            double s = ResolutionScaling.ScaleFactor(p);
            EventSchedule sched = new EventSchedule();
            sched.Strength = p.Double("storms.outbreak_strength");
            sched.StepScale = s;
            int count = p.Int("storms.outbreak_count");
            int rawDevSteps = p.Int("sim.dev_steps");
            if (count == 0 || rawDevSteps < 50) return sched;
            int eff = ResolutionScaling.EffectiveDevSteps(p);

            List<double[]> belts = new List<double[]>();
            double latMin = p.Double("storms.outbreak_lat_min");
            double? pinLat = NullableDouble(p, "storms.outbreak_latitude");
            for (int j = 0; j < bands.Values.Length; j++)
            {
                double center = 0.5 * (bands.Edges[j] + bands.Edges[j + 1]);
                if (bands.IsBelt[j] && latMin < Math.Abs(center) && Math.Abs(center) < 1.0)
                    belts.Add(new double[] { center, bands.Values[j] });
            }
            if (belts.Count == 0 && !pinLat.HasValue) return sched;
            belts.Sort(delegate(double[] a, double[] b) { return a[1].CompareTo(b[1]); });
            int darkN = Math.Max(1, (belts.Count + 1) / 2);
            if (belts.Count > darkN) belts.RemoveRange(darkN, belts.Count - darkN);

            for (int e = 0; e < count; e++)
            {
                double drawPhase = rng.Uniform(0.55, 0.85);
                double? pinnedPhase = NullableDouble(p, "storms.outbreak_phase");
                double phase = pinnedPhase.HasValue ? pinnedPhase.Value : drawPhase;
                int step0 = (int)(phase * eff);
                double center = belts.Count > 0 ? belts[(int)rng.Integers(0, belts.Count)][0] : 0.0;
                if (pinLat.HasValue) center = pinLat.Value * Math.PI / 180.0;
                double baseLon = rng.Uniform(-Math.PI, Math.PI);
                double? pinLon = NullableDouble(p, "storms.outbreak_longitude");
                if (pinLon.HasValue)
                {
                    int remaining = eff - step0;
                    baseLon = Vortices.DriftCompensatedLon(profiles, center, pinLon.Value, profiles == null ? (double?)null : dt, remaining);
                }
                for (int k = 0; k < TrainN; k++)
                {
                    double frac = (double)k / Math.Max(TrainN - 1, 1) - 0.5;
                    double lat = Clip(center + frac * TrainLatSpread, -Vortices.MaxVortexLat, Vortices.MaxVortexLat);
                    double lon = Vortices.WrapPi(baseLon + k * TrainLonStep + rng.Normal(0.0, 0.02));
                    double radius = Radius * (1.0 - 0.45 * k / Math.Max(TrainN - 1.0, 1.0));
                    double brightMul = k == 0 ? LeadBright : 1.0;
                    if (k == 0) radius *= LeadRadius;
                    int step = step0 + k * (int)(0.015 * eff) + (int)(rng.Uniform(0.0, 0.04) * eff);
                    sched.Outbreaks.Add(new Outbreak { Step = step, Lat = lat, Lon = lon, Radius = radius, BrightMul = brightMul });
                }
            }
            return sched;
        }

        public List<OutflowImpulse> Apply(int step, VortexRegistry registry)
        {
            List<OutflowImpulse> impulses = new List<OutflowImpulse>();
            int lifetime = ResolutionScaling.ScaleDuration(Lifetime, StepScale);
            int rampSteps = ResolutionScaling.ScaleDuration(Ramp, StepScale);
            for (int i = 0; i < Outbreaks.Count; i++)
            {
                Outbreak ob = Outbreaks[i];
                int age = step - ob.Step;
                if (age < 0) continue;
                if (age > lifetime)
                {
                    if (ob.Vortex != null)
                    {
                        for (int j = registry.Vortices.Count - 1; j >= 0; j--) if (object.ReferenceEquals(registry.Vortices[j], ob.Vortex)) registry.Vortices.RemoveAt(j);
                        ob.Vortex = null;
                    }
                    continue;
                }
                if (ob.Vortex == null)
                {
                    Vortex v = new Vortex(ob.Lat, ob.Lon, ob.Radius, 0.0, VortexKinds.Outbreak);
                    v.Brightness = Brightness * Strength * ob.BrightMul;
                    ob.Vortex = v;
                    registry.Vortices.Add(v);
                }
                double decay = 1.0 - (double)age / lifetime;
                ob.Vortex.Brightness = Brightness * Strength * ob.BrightMul * decay;
                if (impulses.Count < 2)
                {
                    double ramp = Math.Min((double)age / rampSteps, 1.0) * decay;
                    impulses.Add(new OutflowImpulse { Lon = ob.Vortex.Lon, Lat = ob.Vortex.Lat, Radius = ob.Radius * 1.5, Strength = Outflow * Strength * ramp });
                }
            }
            return impulses;
        }

        private static double Clip(double x, double lo, double hi) { return x < lo ? lo : (x > hi ? hi : x); }
        private static double? NullableDouble(ParamTree p, string path) { return p.Has(path) ? (double?)p.Double(path) : null; }
    }
}
