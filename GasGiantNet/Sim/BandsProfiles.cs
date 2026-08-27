using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using GasGiantNet.Config;
using GasGiantNet.Random;

namespace GasGiantNet.Sim
{
    internal sealed class BandLayout
    {
        public float[] Edges;
        public float[] Values;
        public float[] Heights;
        public float[] StampValues;
        public bool[] IsBelt;
        public double FadeLatLo;
        public double FadeLatHi;
        public double FadeLon;
        public double FadeHalfWidth;
        public int FadeIndex = -1;
    }

    internal sealed class LatProfiles
    {
        public double[] Lat;
        public double[] U;
        public double[] Psi;
        public double[] ShearNorm;
        public double[] BeltMask;
        public double[] T0Stamp;
        public double[] T1Stamp;
        public double[] OmegaJet;
        public double MaxSpeed;

        public float[] DynLut()
        {
            int n = Lat.Length;
            float[] r = new float[n * 4];
            for (int i = 0; i < n; i++)
            {
                int j = i * 4;
                r[j] = (float)U[i];
                r[j + 1] = (float)Psi[i];
                r[j + 2] = (float)ShearNorm[i];
                r[j + 3] = (float)BeltMask[i];
            }
            return r;
        }

        public float[] StampLut()
        {
            int n = Lat.Length;
            float[] r = new float[n * 4];
            for (int i = 0; i < n; i++)
            {
                int j = i * 4;
                r[j] = (float)T0Stamp[i];
                r[j + 1] = (float)T1Stamp[i];
                r[j + 2] = (float)Profiles.PolarFadeScalar(Lat[i]);
                r[j + 3] = 0.0f;
            }
            return r;
        }

        public float[] OmegaLut()
        {
            int n = Lat.Length;
            float[] r = new float[n * 4];
            for (int i = 0; i < n; i++) r[i * 4] = (float)OmegaJet[i];
            return r;
        }
    }

    internal static class Bands
    {
        private const double SinLatExtent = 0.97;
        private const double ZoneValue = 0.78;
        private const double BeltValue = 0.30;
        private const double ZoneHeight = 0.75;
        private const double BeltHeight = 0.35;

        public static BandLayout Generate(int seed, ParamTree p)
        {
            if (p.Has("bands.template")) return FromTemplate(seed, p);

            NumpyGenerator rng = NumpyGenerator.Subseed(seed, "bands");
            int count = p.Int("bands.count");
            double jitter = p.Double("bands.width_jitter");
            double[] widths = new double[count];
            for (int i = 0; i < count; i++)
            {
                widths[i] = 1.0 + jitter * rng.Uniform(-1.0, 1.0);
                if (widths[i] < 0.15) widths[i] = 0.15;
            }

            NumpyGenerator tail = NumpyGenerator.Subseed(seed, "width-tail");
            double widthTail = p.Double("bands.width_tail");
            for (int i = 0; i < count; i++) widths[i] *= Math.Exp(widthTail * tail.Normal(0.0, 0.9));

            double sum = 0.0;
            for (int i = 0; i < count; i++) sum += widths[i];
            double[] edges64 = new double[count + 1];
            double cumulative = 0.0;
            for (int i = 0; i <= count; i++)
            {
                double f = i == 0 ? 0.0 : cumulative / sum;
                double s = SinLatExtent - f * (2.0 * SinLatExtent);
                edges64[i] = Math.Asin(s);
                if (i < count) cumulative += widths[i];
            }
            edges64[0] = Math.PI / 2.0;
            edges64[count] = -Math.PI / 2.0;

            bool zoneFirst = rng.Integers(0, 2) != 0;
            double mid = 0.5 * (ZoneValue + BeltValue);
            double contrast = p.Double("bands.value_contrast");
            double[] values64 = new double[count];
            double[] heights64 = new double[count];
            for (int i = 0; i < count; i++)
            {
                bool parity = (i % 2) == (zoneFirst ? 0 : 1);
                double bv = parity ? ZoneValue : BeltValue;
                double bh = parity ? ZoneHeight : BeltHeight;
                values64[i] = mid + (bv - mid) * contrast + rng.Uniform(-0.06, 0.06);
                heights64[i] = bh + rng.Uniform(-0.08, 0.08);
            }

            NumpyGenerator hue = NumpyGenerator.Subseed(seed, "band-hues");
            double hueJitter = p.Double("bands.hue_jitter");
            for (int i = 0; i < count; i++) values64[i] += hueJitter * hue.Uniform(-1.0, 1.0);
            for (int i = 0; i < count; i++)
            {
                values64[i] = Clamp01(values64[i]);
                heights64[i] = Clamp01(heights64[i]);
            }
            bool[] isBelt = BelowMedian(values64);
            return Finish(seed, edges64, values64, heights64, isBelt, p);
        }

        private static BandLayout FromTemplate(int seed, ParamTree p)
        {
            JsonObject t = p.Object("bands.template");
            JsonArray ea = (JsonArray)t["edges_deg"];
            JsonArray va = (JsonArray)t["values"];
            JsonArray ha = (JsonArray)t["heights"];
            double[] edges = new double[ea.Count];
            double[] values = new double[va.Count];
            double[] heights = new double[ha.Count];
            for (int i = 0; i < edges.Length; i++) edges[i] = ea[i].GetValue<double>() * Math.PI / 180.0;
            for (int i = 0; i < values.Length; i++) values[i] = va[i].GetValue<double>();
            for (int i = 0; i < heights.Length; i++) heights[i] = ha[i].GetValue<double>();
            return Finish(seed, edges, values, heights, BelowMedian(values), p);
        }

        private static BandLayout Finish(int seed, double[] edges64, double[] values64, double[] heights64, bool[] isBelt, ParamTree p)
        {
            NumpyGenerator rng = NumpyGenerator.Subseed(seed, "faded-sector");
            double lon = rng.Uniform(-Math.PI, Math.PI);
            double halfWidth = rng.Uniform(38.0, 58.0) * Math.PI / 180.0;
            int fadeIndex = -1;
            int? overrideIndex = p.NullableInt("bands.faded_band_index");
            if (overrideIndex.HasValue)
            {
                fadeIndex = overrideIndex.Value;
            }
            else
            {
                double bestWidth = double.NegativeInfinity;
                for (int i = 0; i < values64.Length; i++)
                {
                    double center = 0.5 * (edges64[i] + edges64[i + 1]);
                    double width = edges64[i] - edges64[i + 1];
                    if (isBelt[i] && Math.Abs(center) < 0.9 && width > bestWidth)
                    {
                        bestWidth = width;
                        fadeIndex = i;
                    }
                }
            }

            BandLayout r = new BandLayout();
            r.Edges = ToFloat32(edges64);
            r.Values = ToFloat32(values64);
            r.Heights = ToFloat32(heights64);
            r.IsBelt = isBelt;
            r.FadeIndex = fadeIndex;
            r.FadeLon = lon;
            r.FadeHalfWidth = halfWidth;
            if (fadeIndex >= 0)
            {
                r.FadeLatLo = edges64[fadeIndex + 1];
                r.FadeLatHi = edges64[fadeIndex];
            }
            r.StampValues = ApplyBeltFade(r.Values, fadeIndex, p.Double("bands.belt_fade"));
            return r;
        }

        private static float[] ApplyBeltFade(float[] values, int fadeIndex, double amount)
        {
            if (amount <= 0.0 || fadeIndex < 0) return values;
            List<double> neighbors = new List<double>();
            if (fadeIndex - 1 >= 0) neighbors.Add(values[fadeIndex - 1]);
            if (fadeIndex + 1 < values.Length) neighbors.Add(values[fadeIndex + 1]);
            if (neighbors.Count == 0) return values;
            double target = 0.0;
            for (int i = 0; i < neighbors.Count; i++) target += neighbors[i];
            target /= neighbors.Count;
            float[] faded = (float[])values.Clone();
            faded[fadeIndex] = (float)((double)values[fadeIndex] + amount * (target - values[fadeIndex]));
            return faded;
        }

        private static bool[] BelowMedian(double[] values)
        {
            double[] sorted = (double[])values.Clone();
            Array.Sort(sorted);
            double med;
            int n = sorted.Length;
            if ((n & 1) != 0) med = sorted[n / 2];
            else med = 0.5 * (sorted[n / 2 - 1] + sorted[n / 2]);
            bool[] b = new bool[n];
            for (int i = 0; i < n; i++) b[i] = values[i] < med;
            return b;
        }

        private static float[] ToFloat32(double[] a)
        {
            float[] r = new float[a.Length];
            for (int i = 0; i < a.Length; i++) r[i] = (float)a[i];
            return r;
        }

        private static double Clamp01(double x) { return x < 0.0 ? 0.0 : (x > 1.0 ? 1.0 : x); }
    }

    internal static class Profiles
    {
        public const int Samples = 2048;
        public static readonly double PolarFadeStart = 74.0 * Math.PI / 180.0;
        public static readonly double PolarFadeEnd = 84.0 * Math.PI / 180.0;

        public static double PolarFadeScalar(double lat)
        {
            double x = (Math.Abs(lat) - PolarFadeStart) / (PolarFadeEnd - PolarFadeStart);
            if (x < 0.0) x = 0.0;
            if (x > 1.0) x = 1.0;
            return 1.0 - x * x * (3.0 - 2.0 * x);
        }

        public static LatProfiles Build(int seed, BandLayout bands, ParamTree p, double? heroLatDeg, double heroCoreRad)
        {
            int n = Samples;
            double[] lat = Linspace(Math.PI / 2.0, -Math.PI / 2.0, n);
            NumpyGenerator rng = NumpyGenerator.Subseed(seed, "jets");
            double[] edges = ToDouble(bands.Edges);
            double[] widths = new double[edges.Length - 1];
            for (int i = 0; i < widths.Length; i++) widths[i] = edges[i] - edges[i + 1];
            double[] u = new double[n];

            for (int j = 0; j < edges.Length - 2; j++)
            {
                double edgeLat = edges[j + 1];
                double wAdj = Math.Min(widths[j], widths[j + 1]);
                double jetWidth = Math.Max(0.25 * wAdj, 0.015);
                double sign = (j % 2 == 0) ? 1.0 : -1.0;
                double amp = 0.55 * (1.0 + 0.5 * rng.Uniform(-1.0, 1.0));
                double c = Math.Cos(edgeLat);
                double decay = c * c * p.Double("jets.polar_decay") + (1.0 - p.Double("jets.polar_decay"));
                for (int i = 0; i < n; i++)
                {
                    double q = (lat[i] - edgeLat) / jetWidth;
                    u[i] += sign * amp * decay * Math.Exp(-(q * q));
                }
            }

            double eqSpeed = p.Double("jets.equatorial_speed");
            double eqWidth = p.Double("jets.equatorial_width");
            for (int i = 0; i < n; i++)
            {
                double q = lat[i] / eqWidth;
                u[i] += eqSpeed * Math.Exp(-(q * q));
            }

            double localSpeed = p.Double("jets.local_jet_speed");
            if (localSpeed != 0.0)
            {
                double lat0 = p.Double("jets.local_jet_latitude") * Math.PI / 180.0;
                double width = p.Double("jets.local_jet_width");
                for (int i = 0; i < n; i++)
                {
                    double q = (lat[i] - lat0) / width;
                    u[i] += localSpeed * Math.Exp(-(q * q));
                }
            }

            double strength = p.Double("jets.strength");
            for (int i = 0; i < n; i++) u[i] *= strength * PolarFadeScalar(lat[i]);

            double bracketN = p.Double("jets.hero_bracket_north");
            double bracketS = p.Double("jets.hero_bracket_south");
            if (heroLatDeg.HasValue && (bracketN != 0.0 || bracketS != 0.0))
            {
                if (heroCoreRad <= 0.0) throw new ArgumentException("heroCoreRad must be > 0 while hero bracket is active");
                double hero = heroLatDeg.Value * Math.PI / 180.0;
                double full = p.Double("jets.hero_bracket_window") * heroCoreRad;
                double outer = (p.Double("jets.hero_bracket_window") + p.Double("jets.hero_bracket_feather")) * heroCoreRad;
                double pedestal = InterpAscending(hero, Reverse(lat), Reverse(u));
                double northC = hero + p.Double("jets.hero_bracket_north_offset") * heroCoreRad;
                double southC = hero + p.Double("jets.hero_bracket_south_offset") * heroCoreRad;
                double northW = p.Double("jets.hero_bracket_north_width") * heroCoreRad;
                double southW = p.Double("jets.hero_bracket_south_width") * heroCoreRad;
                for (int i = 0; i < n; i++)
                {
                    double x = (Math.Abs(lat[i] - hero) - full) / Math.Max(outer - full, 1e-9);
                    x = Clamp01(x);
                    double window = 1.0 - x * x * (3.0 - 2.0 * x);
                    double qn = (lat[i] - northC) / northW;
                    double qs = (lat[i] - southC) / southW;
                    double bracket = strength * (bracketN * Math.Exp(-(qn * qn)) + bracketS * Math.Exp(-(qs * qs)));
                    u[i] = u[i] * (1.0 - window) + (pedestal + bracket) * window;
                }
            }

            double[] psi = new double[n];
            for (int i = 1; i < n; i++)
            {
                double dlat = lat[i] - lat[i - 1];
                psi[i] = psi[i - 1] - 0.5 * (u[i] + u[i - 1]) * dlat;
            }

            double[] du = Gradient(u, lat);
            double maxShear = 1e-9;
            for (int i = 0; i < n; i++) if (Math.Abs(du[i]) > maxShear) maxShear = Math.Abs(du[i]);
            double[] shearNorm = new double[n];
            for (int i = 0; i < n; i++) shearNorm[i] = Math.Abs(du[i]) / maxShear;

            int edgeSoftCount = Math.Max(bands.Values.Length - 1, 1);
            double[] softMult = new double[edgeSoftCount];
            NumpyGenerator soft = NumpyGenerator.Subseed(seed, "edge-softness");
            double diversity = p.Double("bands.edge_diversity");
            for (int i = 0; i < softMult.Length; i++) softMult[i] = Math.Exp(diversity * soft.Uniform(-1.2, 1.2));

            double[] t0;
            double[] t1;
            double[] belt;
            StampProfiles(lat, bands, p, softMult, out t0, out t1, out belt);

            double[] omega = JetVorticity(u, lat);
            double maxSpeed = 0.0;
            for (int i = 0; i < n; i++) maxSpeed = Math.Max(maxSpeed, Math.Abs(u[i]));

            LatProfiles r = new LatProfiles();
            r.Lat = lat;
            r.U = u;
            r.Psi = psi;
            r.ShearNorm = shearNorm;
            r.BeltMask = belt;
            r.T0Stamp = t0;
            r.T1Stamp = t1;
            r.OmegaJet = omega;
            r.MaxSpeed = maxSpeed;
            return r;
        }

        public static List<double[]> SelectLanes(int seed, BandLayout bands, double density)
        {
            List<double[]> lanes = new List<double[]>();
            if (density <= 0.0) return lanes;
            NumpyGenerator rng = NumpyGenerator.Subseed(seed, "lanes");
            for (int i = 1; i < bands.Edges.Length - 1; i++)
            {
                double roll = rng.Uniform(0.0, 1.0);
                double strength = rng.Uniform(0.12, 0.30);
                double edge = bands.Edges[i];
                if (roll < density && Math.Abs(edge) < 1.1 && lanes.Count < 16)
                    lanes.Add(new double[] { edge, strength });
            }
            return lanes;
        }

        public static void SelectWaveLatitudes(BandLayout bands, LatProfiles profiles, out double festoon, out double ribbon)
        {
            int m = Math.Max(0, bands.Edges.Length - 2);
            if (m == 0) { festoon = 0.12; ribbon = 0.82; return; }
            int bestSigned = 0;
            int bestAbs = 0;
            double signedDist = double.PositiveInfinity;
            double absDist = double.PositiveInfinity;
            for (int j = 0; j < m; j++)
            {
                double e = bands.Edges[j + 1];
                double ds = Math.Abs(e - 0.12);
                double da = Math.Abs(Math.Abs(e) - 0.12);
                if (ds < signedDist) { signedDist = ds; bestSigned = j; }
                if (da < absDist) { absDist = da; bestAbs = j; }
            }
            festoon = bands.Edges[(signedDist <= 0.1 ? bestSigned : bestAbs) + 1];

            double bestSpeed = double.NegativeInfinity;
            ribbon = 0.82;
            bool found = false;
            for (int j = 0; j < m; j++)
            {
                double e = bands.Edges[j + 1];
                if (Math.Abs(e) > 0.6 && Math.Abs(e) < 1.0)
                {
                    double speed = Math.Abs(InterpAscending(-e, Reverse(profiles.Lat), Reverse(profiles.U)));
                    if (speed > bestSpeed) { bestSpeed = speed; ribbon = e; found = true; }
                }
            }
            if (!found) ribbon = 0.82;
        }

        public static double? SelectHeroFestoonLatitude(BandLayout bands, double heroLat, double primaryFestoonLat)
        {
            if (bands.Edges.Length <= 2) return null;
            int best = 1;
            double dmin = double.PositiveInfinity;
            for (int i = 1; i < bands.Edges.Length - 1; i++)
            {
                double d = Math.Abs(bands.Edges[i] - heroLat);
                if (d < dmin) { dmin = d; best = i; }
            }
            if (dmin > 0.15) return null;
            if (Math.Abs(bands.Edges[best] - primaryFestoonLat) < 1e-6) return null;
            return bands.Edges[best];
        }

        private static void StampProfiles(double[] lat, BandLayout bands, ParamTree p, double[] softMult, out double[] t0, out double[] t1, out double[] belt)
        {
            int n = lat.Length;
            double[] values = ToDouble(bands.StampValues);
            double[] heights = ToDouble(bands.Heights);
            t0 = new double[n];
            t1 = new double[n];
            belt = new double[n];
            for (int i = 0; i < n; i++)
            {
                t0[i] = values[0];
                t1[i] = heights[0];
                belt[i] = bands.IsBelt[0] ? 1.0 : 0.0;
            }
            double baseSoft = Math.Max(p.Double("bands.edge_softness"), 1e-4);
            for (int j = 1; j < values.Length; j++)
            {
                double e = bands.Edges[j];
                double softness = baseSoft * softMult[j - 1];
                for (int i = 0; i < n; i++)
                {
                    double x = (e + softness - lat[i]) / (2.0 * softness);
                    x = Clamp01(x);
                    double t = x * x * (3.0 - 2.0 * x);
                    t0[i] = t0[i] * (1.0 - t) + values[j] * t;
                    t1[i] = t1[i] * (1.0 - t) + heights[j] * t;
                    belt[i] = belt[i] * (1.0 - t) + (bands.IsBelt[j] ? 1.0 : 0.0) * t;
                }
            }
        }

        private static double[] JetVorticity(double[] uDescending, double[] latDescending)
        {
            int n = uDescending.Length;
            double[] uc = new double[n];
            for (int i = 0; i < n; i++) uc[i] = uDescending[i] * Math.Cos(latDescending[i]);
            double[] g = Gradient(uc, latDescending);
            double[] outv = new double[n];
            const double cosFloor = 1e-6;
            for (int i = 0; i < n; i++) outv[i] = -g[i] / Math.Max(Math.Cos(latDescending[i]), cosFloor);
            return outv;
        }

        private static double[] Gradient(double[] f, double[] x)
        {
            int n = f.Length;
            double[] g = new double[n];
            if (n == 1) return g;
            g[0] = (f[1] - f[0]) / (x[1] - x[0]);
            g[n - 1] = (f[n - 1] - f[n - 2]) / (x[n - 1] - x[n - 2]);
            for (int i = 1; i < n - 1; i++)
            {
                double dx1 = x[i] - x[i - 1];
                double dx2 = x[i + 1] - x[i];
                double a = -(dx2) / (dx1 * (dx1 + dx2));
                double b = (dx2 - dx1) / (dx1 * dx2);
                double c = dx1 / (dx2 * (dx1 + dx2));
                g[i] = a * f[i - 1] + b * f[i] + c * f[i + 1];
            }
            return g;
        }

        private static double[] Linspace(double a, double b, int n)
        {
            double[] r = new double[n];
            if (n == 1) { r[0] = a; return r; }
            double d = (b - a) / (n - 1);
            for (int i = 0; i < n; i++) r[i] = a + d * i;
            return r;
        }

        private static double[] ToDouble(float[] a)
        {
            double[] r = new double[a.Length];
            for (int i = 0; i < a.Length; i++) r[i] = a[i];
            return r;
        }

        private static double[] Reverse(double[] a)
        {
            double[] r = (double[])a.Clone();
            Array.Reverse(r);
            return r;
        }

        private static double InterpAscending(double x, double[] xp, double[] fp)
        {
            if (x <= xp[0]) return fp[0];
            if (x >= xp[xp.Length - 1]) return fp[fp.Length - 1];
            int lo = 0, hi = xp.Length - 1;
            while (hi - lo > 1)
            {
                int m = (lo + hi) >> 1;
                if (xp[m] <= x) lo = m; else hi = m;
            }
            double t = (x - xp[lo]) / (xp[hi] - xp[lo]);
            return fp[lo] + (fp[hi] - fp[lo]) * t;
        }

        private static double Clamp01(double x) { return x < 0.0 ? 0.0 : (x > 1.0 ? 1.0 : x); }
    }
}
