using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using GasGiantNet.Config;
using GasGiantNet.MathCore;
using GasGiantNet.Random;

namespace GasGiantNet.Sim
{
    internal static class VortexKinds
    {
        public const float Oval = 0.0f;
        public const float Hero = 1.0f;
        public const float Barge = 2.0f;
        public const float Pearl = 3.0f;
        public const float Kh = 4.0f;
        public const float Polar = 5.0f;
        public const float Outbreak = 6.0f;
        public const float Debris = 7.0f;
    }

    internal sealed class Vortex
    {
        public double Lat;
        public double Lon;
        public double CoreRadius;
        public double Strength;
        public float Kind;
        public double Tint;
        public double Brightness;
        public double WakeDir;
        public double WakeLatOff;
        public double BowGain;
        public double Aspect = 1.0;
        public int Cooldown;
        public int Ttl = -1;
        public string Origin = "seeded";
        public int CastRef = -1;

        public Vortex(double lat, double lon, double coreRadius, double strength, float kind)
        {
            Lat = lat;
            Lon = lon;
            CoreRadius = coreRadius;
            Strength = strength;
            Kind = kind;
        }
    }

    internal sealed class MergeResolution
    {
        public Vortex A;
        public Vortex B;
        public Vortex Product;
    }

    internal sealed class VortexRegistry
    {
        public readonly List<Vortex> Vortices = new List<Vortex>();
        public double StepScale = 1.0;

        public List<Vortex> Heroes()
        {
            List<Vortex> r = new List<Vortex>();
            for (int i = 0; i < Vortices.Count; i++) if (Vortices[i].Kind == VortexKinds.Hero) r.Add(Vortices[i]);
            return r;
        }

        public float[] PackSsbo()
        {
            int n = Vortices.Count;
            if (n == 0) return new float[12];
            float[] result = new float[n * 12];
            for (int i = 0; i < n; i++)
            {
                Vortex v = Vortices[i];
                double cl = Math.Cos(v.Lat);
                int o = i * 12;
                result[o] = (float)(cl * Math.Cos(v.Lon));
                result[o + 1] = (float)Math.Sin(v.Lat);
                result[o + 2] = (float)(cl * Math.Sin(v.Lon));
                result[o + 3] = (float)v.CoreRadius;
                result[o + 4] = (float)v.Strength;
                result[o + 5] = v.Kind;
                result[o + 6] = (float)v.Tint;
                result[o + 7] = (float)v.Brightness;
                result[o + 8] = (float)v.WakeDir;
                result[o + 9] = (float)v.Aspect;
                result[o + 10] = (float)v.WakeLatOff;
                result[o + 11] = (float)v.BowGain;
            }
            return result;
        }

        public void Drift(LatProfiles profiles, double dt)
        {
            for (int i = 0; i < Vortices.Count; i++)
            {
                Vortex v = Vortices[i];
                if (v.Kind == VortexKinds.Polar) continue;
                v.Lon = GasGiantNet.Sim.Vortices.WrapPi(v.Lon + GasGiantNet.Sim.Vortices.ZonalRate(profiles, v.Lat) * dt);
            }
        }

        public double SceneEmergence(ParamTree p)
        {
            List<Vortex> heroes = Heroes();
            if (heroes.Count == 0) return p.Double("storms.hero_emergence");
            double m = double.NegativeInfinity;
            for (int i = 0; i < heroes.Count; i++) m = Math.Max(m, GasGiantNet.Sim.Vortices.EffectiveCastLever(p, heroes[i].CastRef, "emergence"));
            return m;
        }

        public float[] PackCastLeversSsbo(ParamTree p)
        {
            int n = Math.Max(1, Vortices.Count);
            float[] outData = new float[n * 12];
            for (int i = 0; i < Vortices.Count; i++)
            {
                Vortex v = Vortices[i];
                int o = i * 12;
                outData[o] = (float)EffectiveCastLever(p, v.CastRef, "rim_contrast");
                outData[o + 1] = (float)EffectiveCastLever(p, v.CastRef, "rim_tint");
                outData[o + 2] = (float)EffectiveCastLever(p, v.CastRef, "rim_warp");
                outData[o + 3] = (float)EffectiveCastLever(p, v.CastRef, "mottle");
                outData[o + 4] = (float)EffectiveCastLever(p, v.CastRef, "tint_var");
                outData[o + 5] = (float)EffectiveCastLever(p, v.CastRef, "wake_detail");
                outData[o + 6] = (float)EffectiveCastLever(p, v.CastRef, "solid_core");
                outData[o + 7] = 0.0f;
                outData[o + 8] = (float)EffectiveCastLever(p, v.CastRef, "emergence");
                outData[o + 9] = (float)EffectiveCastLever(p, v.CastRef, "shape");
                outData[o + 10] = (float)EffectiveCastLever(p, v.CastRef, "taper");
                outData[o + 11] = 0.0f;
            }
            return outData;
        }

        private static double EffectiveCastLever(ParamTree p, int castRef, string attr)
        {
            return GasGiantNet.Sim.Vortices.EffectiveCastLever(p, castRef, attr);
        }
    }

    internal static class Vortices
    {
        public const double MaxVortexLat = 68.0 * Math.PI / 180.0;
        public const int MaxVortices = 400;
        public const double MergeCaptureCoef = 1.5;
        public const int MergeCooldown = 25;
        public const double MergeMaxR = 0.08;
        public const double MergeVMax = 0.40;
        public static readonly double VPeakCoef = Math.Sqrt(2.0) * Math.Exp(-0.5);
        public const int MergeDebrisLifetime = 250;
        public const int MergeDebrisRamp = 15;
        public const double MergeDebrisBright = 0.9;
        private static readonly double ExchangeFloor = 63.0 * Math.PI / 180.0;

        public static VortexRegistry Generate(int seed, BandLayout bands, LatProfiles profiles, ParamTree p, double? dt, int devSteps, double stepScale)
        {
            RandomGenerator rng = RandomGenerator.Subseed(seed, "storms");
            VortexRegistry reg = new VortexRegistry();
            reg.StepScale = stepScale;
            int eff = ResolutionScaling.ScaleDuration(devSteps, stepScale);

            List<double[]> zones = BandCenters(bands, false);
            List<double[]> belts = BandCenters(bands, true);
            List<double[]> tropical = FilterCenters(zones, 0.15, 0.75);
            if (tropical.Count == 0) tropical = zones;

            int heroCount = p.Int("storms.hero_count");
            for (int heroIndex = 0; heroIndex < heroCount; heroIndex++)
            {
                if (tropical.Count == 0) break;
                double[] band = tropical[(int)rng.Integers(0, tropical.Count)];
                double lat = Clip(band[0] + rng.Normal(0.0, 0.02), -MaxVortexLat, MaxVortexLat);
                double? pinnedLat = NullableDouble(p, "storms.hero_latitude");
                if (pinnedLat.HasValue) lat = pinnedLat.Value * Math.PI / 180.0;
                double r = p.Double("storms.hero_radius") * (1.0 + 0.2 * rng.Uniform(-1.0, 1.0));
                double strength = -AmbientSign(profiles, lat) * p.Double("storms.hero_strength") * 0.045;
                double lon = rng.Uniform(-Math.PI, Math.PI); // unconditional draw
                double? pinnedLon = NullableDouble(p, "storms.hero_longitude");
                if (pinnedLon.HasValue) lon = DriftCompensatedLon(profiles, lat, pinnedLon.Value, dt, eff);
                double woff = 0.5 * r * (lat < 0.0 ? 1.0 : -1.0);
                double wdir = -1.0;
                double bow = 0.0;
                if (p.Double("storms.hero_emergence") > 0.0)
                {
                    double[] frame = HeroWakeFrame(profiles, lat, r);
                    woff = frame[0];
                    wdir = frame[1];
                    bow = HeroBowGain(profiles, lat, r);
                }
                string forced = p.String("storms.hero_wake_dir");
                if (forced == "east") wdir = 1.0;
                else if (forced == "west") wdir = -1.0;
                Vortex hero = new Vortex(lat, lon, r, strength, VortexKinds.Hero);
                hero.Tint = p.Double("storms.hero_tint");
                hero.Brightness = p.Double("storms.hero_brightness");
                hero.WakeDir = wdir;
                hero.WakeLatOff = woff;
                hero.BowGain = bow;
                hero.Aspect = p.Double("storms.hero_aspect");
                reg.Vortices.Add(hero);
            }

            for (int z = 0; z < zones.Count; z++)
            {
                double center = zones[z][0];
                double width = zones[z][1];
                int count = (int)rng.Poisson(p.Double("storms.oval_density") * 2.2);
                if (count == 0) continue;
                List<double> lons = PoissonLons(rng, count, 0.35);
                for (int i = 0; i < lons.Count; i++)
                {
                    double u01 = rng.Random();
                    double r = 0.018 + (0.055 - 0.018) * u01 * u01;
                    double lat = Clip(center + rng.Normal(0.0, 0.15 * width), -MaxVortexLat, MaxVortexLat);
                    double strength = -AmbientSign(profiles, lat) * 0.012 * (r / 0.03);
                    Vortex v = new Vortex(lat, lons[i], r, strength, VortexKinds.Oval);
                    v.Tint = 0.1;
                    v.Brightness = 0.22;
                    reg.Vortices.Add(v);
                }
            }

            for (int z = 0; z < belts.Count; z++)
            {
                double center = belts[z][0];
                double width = belts[z][1];
                int count = (int)rng.Poisson(p.Double("storms.barge_density") * 1.2);
                if (count == 0) continue;
                List<double> lons = PoissonLons(rng, count, 0.5);
                for (int i = 0; i < lons.Count; i++)
                {
                    double lat = Clip(center + rng.Normal(0.0, 0.1 * width), -MaxVortexLat, MaxVortexLat);
                    double r = rng.Uniform(0.02, 0.045);
                    Vortex v = new Vortex(lat, lons[i], r, -AmbientSign(profiles, lat) * 0.006, VortexKinds.Barge);
                    v.Tint = 0.35;
                    v.Brightness = -0.28;
                    reg.Vortices.Add(v);
                }
            }

            int pearls = p.Int("storms.pearls_count");
            if (pearls > 0 && zones.Count > 0)
            {
                List<double[]> temperate = FilterCenters(zones, 0.4, 1.0);
                if (temperate.Count == 0) temperate = zones;
                double center = temperate[(int)rng.Integers(0, temperate.Count)][0];
                double lat = Clip(center, -MaxVortexLat, MaxVortexLat);
                double baseLon = rng.Uniform(-Math.PI, Math.PI);
                for (int i = 0; i < pearls; i++)
                {
                    double lon = WrapPi(baseLon + (2.0 * Math.PI * i) / pearls + rng.Normal(0.0, 0.04));
                    Vortex v = new Vortex(lat, lon, 0.02, -AmbientSign(profiles, lat) * 0.008, VortexKinds.Pearl);
                    v.Tint = 0.05;
                    v.Brightness = 0.25;
                    reg.Vortices.Add(v);
                }
            }

            if (p.Double("storms.small_density") > 0.0)
                AddSmallStorms(reg, RandomGenerator.Subseed(seed, "small-storms"), zones, belts, profiles, p.Double("storms.small_density"));

            double stampContrast = p.Double("storms.stamp_contrast");
            double? tintOverride = NullableDouble(p, "storms.stamp_tint_contrast");
            double tintContrast = tintOverride.HasValue ? tintOverride.Value : stampContrast;
            if (stampContrast != 1.0 || tintContrast != 1.0)
            {
                for (int i = 0; i < reg.Vortices.Count; i++)
                {
                    Vortex v = reg.Vortices[i];
                    if (v.Kind == VortexKinds.Hero) continue;
                    v.Brightness *= stampContrast;
                    v.Tint *= tintContrast;
                }
            }

            if (p.Has("poles"))
            {
                RandomGenerator polar = RandomGenerator.Subseed(seed, "poles");
                AddPolarVortices(reg, polar, +1.0, p.String("poles.north.style"), p.Int("poles.north.cyclone_count"), p.Double("poles.north.strength"), p.Double("poles.north.field_density"));
                AddPolarVortices(reg, polar, -1.0, p.String("poles.south.style"), p.Int("poles.south.cyclone_count"), p.Double("poles.south.strength"), p.Double("poles.south.field_density"));
            }

            EnforceCap(reg);

            if (p.Double("storms.merge_rate") > 0.0 && dt.HasValue && devSteps > 0)
                SeedConvergentPairs(reg, RandomGenerator.Subseed(seed, "mergers"), zones, profiles, p.Double("storms.merge_rate"), dt.Value, eff, stepScale);

            if (p.Int("storms.accent_count") > 0)
                AddAccentOvals(reg, RandomGenerator.Subseed(seed, "accent-ovals"), zones, profiles, p, dt, eff);
            if (p.Int("storms.hero_companions") > 0)
                AddHeroCompanions(reg, RandomGenerator.Subseed(seed, "hero-companions"), profiles, p.Int("storms.hero_companions"), p.Double("storms.companion_aspect"), p.Double("storms.companion_brightness"));
            JsonArray cast = p.Array("storms.cast");
            if (cast.Count > 0) AddCast(reg, profiles, p, dt, eff);

            if (reg.Vortices.Count > MaxVortices)
            {
                int nCast = 0;
                for (int i = 0; i < reg.Vortices.Count; i++) if (reg.Vortices[i].Origin == "cast") nCast++;
                if (nCast > MaxVortices) throw new InvalidOperationException("cast list exceeds vortex cap");
                while (reg.Vortices.Count > MaxVortices)
                {
                    for (int i = reg.Vortices.Count - 1; i >= 0; i--)
                    {
                        if (reg.Vortices[i].Origin != "cast") { reg.Vortices.RemoveAt(i); break; }
                    }
                }
            }
            return reg;
        }

        public static double DriftCompensatedLon(LatProfiles profiles, double lat, double targetDeg, double? dt, int nSteps)
        {
            double target = targetDeg * Math.PI / 180.0;
            if (dt.HasValue && nSteps > 0) target -= ZonalRate(profiles, lat) * dt.Value * nSteps;
            return WrapPi(target);
        }

        public static double ZonalRate(LatProfiles profiles, double lat)
        {
            double u = InterpDescending(profiles.Lat, profiles.U, lat);
            return u / Math.Max(Math.Cos(lat), 0.2);
        }

        public static List<MergeResolution> ResolveMergers(VortexRegistry reg, LatProfiles profiles, ParamTree p)
        {
            List<MergeResolution> resolved = new List<MergeResolution>();
            double rate = p.Double("storms.merge_rate");
            if (rate <= 0.0) return resolved;
            AgeTransients(reg, p);
            int cooldown = ResolutionScaling.ScaleDuration(MergeCooldown, reg.StepScale);
            int debrisLife = ResolutionScaling.ScaleDuration(MergeDebrisLifetime, reg.StepScale);
            for (int i = 0; i < reg.Vortices.Count; i++) if (reg.Vortices[i].Cooldown > 0) reg.Vortices[i].Cooldown--;

            List<int[]> pairs = new List<int[]>();
            for (int i = 0; i < reg.Vortices.Count; i++)
            {
                Vortex a = reg.Vortices[i];
                for (int j = i + 1; j < reg.Vortices.Count; j++)
                {
                    Vortex b = reg.Vortices[j];
                    if (!MergeEligible(a, b)) continue;
                    if (a.Cooldown != 0 || b.Cooldown != 0 || a.Strength * b.Strength <= 0.0) continue;
                    double d = GreatCircleDistance(a.Lat, a.Lon, b.Lat, b.Lon);
                    double capture = MergeCaptureCoef * rate * (a.CoreRadius + b.CoreRadius);
                    if (!(d < capture)) continue;
                    double gap = WrapPi(b.Lon - a.Lon);
                    double closing = -Math.Sign(gap) * (ZonalRate(profiles, b.Lat) - ZonalRate(profiles, a.Lat));
                    if (!(closing > 0.0)) continue;
                    pairs.Add(new int[] { i, j, BitConverter.DoubleToInt64Bits(d).GetHashCode() });
                }
            }
            pairs.Sort(delegate(int[] x, int[] y)
            {
                double dx = GreatCircleDistance(reg.Vortices[x[0]].Lat, reg.Vortices[x[0]].Lon, reg.Vortices[x[1]].Lat, reg.Vortices[x[1]].Lon);
                double dy = GreatCircleDistance(reg.Vortices[y[0]].Lat, reg.Vortices[y[0]].Lon, reg.Vortices[y[1]].Lat, reg.Vortices[y[1]].Lon);
                int c = dx.CompareTo(dy); if (c != 0) return c;
                c = x[0].CompareTo(y[0]); return c != 0 ? c : x[1].CompareTo(y[1]);
            });
            HashSet<int> consumed = new HashSet<int>();
            HashSet<int> removed = new HashSet<int>();
            List<Vortex> products = new List<Vortex>();
            for (int k = 0; k < pairs.Count; k++)
            {
                int i = pairs[k][0], j = pairs[k][1];
                if (consumed.Contains(i) || consumed.Contains(j)) continue;
                consumed.Add(i); consumed.Add(j);
                Vortex a = reg.Vortices[i], b = reg.Vortices[j];
                if (a.Kind == VortexKinds.Hero || b.Kind == VortexKinds.Hero)
                {
                    Vortex hero = a.Kind == VortexKinds.Hero ? a : b;
                    Vortex victim = a.Kind == VortexKinds.Hero ? b : a;
                    removed.Add(a.Kind == VortexKinds.Hero ? j : i);
                    resolved.Add(new MergeResolution { A = hero, B = victim, Product = null });
                    Vortex debris = SpawnDebris(victim.Lat, victim.Lon, victim.CoreRadius, p, debrisLife);
                    if (debris != null) products.Add(debris);
                }
                else
                {
                    removed.Add(i); removed.Add(j);
                    Vortex product = MergePair(a, b, profiles, cooldown);
                    products.Add(product);
                    resolved.Add(new MergeResolution { A = a, B = b, Product = product });
                    Vortex debris = SpawnDebris(product.Lat, product.Lon, product.CoreRadius, p, debrisLife);
                    if (debris != null) products.Add(debris);
                }
            }
            List<Vortex> rebuilt = new List<Vortex>();
            for (int i = 0; i < reg.Vortices.Count; i++) if (!removed.Contains(i)) rebuilt.Add(reg.Vortices[i]);
            rebuilt.AddRange(products);
            reg.Vortices.Clear(); reg.Vortices.AddRange(rebuilt);
            return resolved;
        }

        private static bool MergeEligible(Vortex a, Vortex b)
        {
            if (a.Origin == "cast" || b.Origin == "cast") return false;
            if (Math.Abs(a.Strength) <= 1e-6 || Math.Abs(b.Strength) <= 1e-6) return false;
            bool peerA = a.Kind == VortexKinds.Oval || a.Kind == VortexKinds.Pearl;
            bool peerB = b.Kind == VortexKinds.Oval || b.Kind == VortexKinds.Pearl;
            if (peerA && peerB && a.Kind == b.Kind) return true;
            return (a.Kind == VortexKinds.Hero && b.Kind == VortexKinds.Oval) || (b.Kind == VortexKinds.Hero && a.Kind == VortexKinds.Oval);
        }

        private static Vortex MergePair(Vortex a, Vortex b, LatProfiles profiles, int cooldown)
        {
            double w1 = Math.Abs(a.Strength) * a.CoreRadius * a.CoreRadius;
            double w2 = Math.Abs(b.Strength) * b.CoreRadius * b.CoreRadius;
            double wt = w1 + w2;
            double r = Math.Min(Math.Sqrt(a.CoreRadius * a.CoreRadius + b.CoreRadius * b.CoreRadius), MergeMaxR);
            double smag = (Math.Abs(a.Strength) * a.CoreRadius + Math.Abs(b.Strength) * b.CoreRadius) / r;
            smag = Math.Min(smag, MergeVMax * r / VPeakCoef);
            double sign = a.Strength > 0.0 ? 1.0 : -1.0;
            double lat = (w1 * a.Lat + w2 * b.Lat) / wt;
            double dlon = WrapPi(b.Lon - a.Lon);
            double lon = WrapPi(a.Lon + (w2 / wt) * dlon);
            double u = InterpDescending(profiles.Lat, profiles.U, lat);
            Vortex v = new Vortex(lat, lon, r, sign * smag, VortexKinds.Oval);
            v.Tint = (w1 * a.Tint + w2 * b.Tint) / wt;
            v.Brightness = (w1 * a.Brightness + w2 * b.Brightness) / wt;
            v.WakeDir = u >= 0.0 ? 1.0 : -1.0;
            v.Cooldown = cooldown;
            return v;
        }

        private static Vortex SpawnDebris(double lat, double lon, double radius, ParamTree p, int lifetime)
        {
            if (p.Double("storms.merge_debris") <= 0.0) return null;
            if (Math.Abs(lat) + 3.0 * radius > ExchangeFloor) return null;
            Vortex v = new Vortex(lat, lon, radius, 0.0, VortexKinds.Debris);
            v.Ttl = lifetime;
            return v;
        }

        private static void AgeTransients(VortexRegistry reg, ParamTree p)
        {
            double baseBright = MergeDebrisBright * p.Double("storms.merge_debris") * p.Double("storms.stamp_contrast");
            int lifetime = ResolutionScaling.ScaleDuration(MergeDebrisLifetime, reg.StepScale);
            int ramp = ResolutionScaling.ScaleDuration(MergeDebrisRamp, reg.StepScale);
            for (int i = reg.Vortices.Count - 1; i >= 0; i--)
            {
                Vortex v = reg.Vortices[i];
                if (v.Ttl < 0) continue;
                v.Ttl--;
                if (v.Ttl <= 0) { reg.Vortices.RemoveAt(i); continue; }
                int age = lifetime - v.Ttl;
                double rampIn = Math.Min((double)age / ramp, 1.0);
                v.Brightness = baseBright * rampIn * ((double)v.Ttl / lifetime);
            }
        }

        private static void AddPolarVortices(VortexRegistry reg, RandomGenerator rng, double poleSign, string style, int cycloneCount, double strength, double fieldDensity)
        {
            if (style == "calm" || strength <= 0.0) return;
            double poleLat = poleSign * Math.PI / 2.0;
            double central = -poleSign * 0.032 * strength;
            if (style == "plain_vortex")
            {
                Vortex v = new Vortex(poleLat, 0.0, 0.09, central * 1.4, VortexKinds.Polar); v.Tint = 0.25; v.Brightness = -0.22; reg.Vortices.Add(v);
            }
            else if (style == "polygon_jet")
            {
                Vortex v = new Vortex(poleLat, 0.0, 0.05, central, VortexKinds.Polar); v.Tint = 0.15; v.Brightness = -0.14; reg.Vortices.Add(v);
            }
            else
            {
                Vortex center = new Vortex(poleLat, 0.0, 0.055, central, VortexKinds.Polar); center.Tint = 0.3; center.Brightness = -0.26; reg.Vortices.Add(center);
                double ringColat = 0.135;
                double baseTheta = rng.Uniform(0.0, 2.0 * Math.PI);
                for (int i = 0; i < cycloneCount; i++)
                {
                    double theta = baseTheta + 2.0 * Math.PI * i / cycloneCount;
                    double lat = poleSign * (Math.PI / 2.0 - ringColat);
                    Vortex v = new Vortex(lat, WrapPi(theta), 0.05, central * 0.85, VortexKinds.Polar); v.Tint = 0.25; v.Brightness = -0.22; reg.Vortices.Add(v);
                }
            }
            if (fieldDensity > 0.0)
            {
                int count = (int)rng.Poisson(fieldDensity * 14.0);
                for (int i = 0; i < count; i++)
                {
                    double colat = rng.Uniform(0.06, Math.PI / 2.0 - 70.0 * Math.PI / 180.0);
                    double lat = poleSign * (Math.PI / 2.0 - colat);
                    double lon = rng.Uniform(-Math.PI, Math.PI);
                    double u01 = rng.Random();
                    double r = 0.012 + (0.038 - 0.012) * u01 * u01;
                    Vortex v = new Vortex(lat, lon, r, central * (0.25 + 0.5 * u01), VortexKinds.Polar); v.Tint = 0.18; v.Brightness = -(0.10 + 3.5 * r); reg.Vortices.Add(v);
                }
            }
        }

        private static void AddSmallStorms(VortexRegistry reg, RandomGenerator rng, List<double[]> zones, List<double[]> belts, LatProfiles profiles, double density)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                List<double[]> bands = pass == 0 ? zones : belts;
                bool isBelt = pass == 1;
                for (int b = 0; b < bands.Count; b++)
                {
                    double center = bands[b][0], width = bands[b][1];
                    int count = (int)rng.Poisson(density * 3.5);
                    if (count == 0) continue;
                    List<double> lons = PoissonLons(rng, count, 0.12);
                    for (int i = 0; i < lons.Count; i++)
                    {
                        double u01 = rng.Random();
                        double r = 0.007 + (0.020 - 0.007) * u01 * u01;
                        double lat = Clip(center + rng.Normal(0.0, 0.30 * width), -MaxVortexLat, MaxVortexLat);
                        double baseB = (0.08 + 5.0 * r) * Veil(lat);
                        double bright = isBelt ? -0.8 * baseB : baseB;
                        double s = -AmbientSign(profiles, lat) * (isBelt ? 0.5 : 1.0) * 0.004 * (r / 0.012);
                        Vortex v = new Vortex(lat, lons[i], r, s, VortexKinds.Oval); v.Brightness = bright; reg.Vortices.Add(v);
                        if (rng.Random() < 0.3)
                        {
                            double trail = WrapPi(lons[i] + rng.Normal(2.2, 0.6) * r);
                            Vortex twin = new Vortex(lat, trail, r * 1.3, s * 0.4, VortexKinds.Oval); twin.Brightness = bright * 0.55; reg.Vortices.Add(twin);
                        }
                    }
                }
            }
        }

        private static void AddAccentOvals(VortexRegistry reg, RandomGenerator rng, List<double[]> zones, LatProfiles profiles, ParamTree p, double? dt, int devSteps)
        {
            double radius = p.Double("storms.accent_radius");
            double cap = Math.Min(MaxVortexLat, (63.0 - 206.3 * radius) * Math.PI / 180.0);
            double lat;
            double? pinnedLat = NullableDouble(p, "storms.accent_latitude");
            if (pinnedLat.HasValue) lat = pinnedLat.Value * Math.PI / 180.0;
            else
            {
                List<double[]> cands = new List<double[]>();
                for (int i = 0; i < zones.Count; i++) if (Math.Abs(zones[i][0]) > 0.15 && Math.Abs(zones[i][0]) < Math.Min(1.0, cap)) cands.Add(zones[i]);
                if (cands.Count == 0) cands = zones;
                if (cands.Count == 0) return;
                lat = Clip(cands[(int)rng.Integers(0, cands.Count)][0], -cap, cap);
            }
            double s = -AmbientSign(profiles, lat) * 0.012 * (radius / 0.03);
            double minSep = 0.6;
            int count = p.Int("storms.accent_count");
            List<double> lons = PoissonLons(rng, count, minSep);
            double? pinBase = null;
            double? pinnedLon = NullableDouble(p, "storms.accent_longitude");
            if (pinnedLon.HasValue) pinBase = DriftCompensatedLon(profiles, lat, pinnedLon.Value, dt, devSteps);
            double relOff = rng.Uniform(0.3, 0.55); // unconditional appended draw
            List<Vortex> heroes = reg.Heroes();
            if (pinnedLat.HasValue && !pinBase.HasValue && heroes.Count > 0) pinBase = WrapPi(heroes[0].Lon + heroes[0].WakeDir * relOff);
            for (int i = 0; i < lons.Count; i++)
            {
                double lon = pinBase.HasValue ? WrapPi(pinBase.Value + i * minSep) : lons[i];
                Vortex v = new Vortex(lat, lon, radius, s, VortexKinds.Oval);
                v.Tint = p.Double("storms.accent_tint"); v.Brightness = p.Double("storms.accent_brightness"); v.Aspect = p.Double("storms.accent_aspect"); reg.Vortices.Add(v);
            }
        }

        private static void AddHeroCompanions(VortexRegistry reg, RandomGenerator rng, LatProfiles profiles, int count, double aspect, double brightness)
        {
            List<Vortex> heroes = reg.Heroes();
            for (int h = 0; h < heroes.Count; h++)
            {
                Vortex hero = heroes[h];
                double side = hero.WakeDir != 0.0 ? -hero.WakeDir : 1.0;
                double eq = hero.Lat < 0.0 ? 1.0 : -1.0;
                for (int i = 0; i < count; i++)
                {
                    double dist = (1.7 + 0.8 * i) * hero.CoreRadius;
                    double dlat = eq * (0.6 + 0.5 * rng.Random()) * hero.CoreRadius * (i % 2 == 0 ? 1.0 : -0.8);
                    double lat = Clip(hero.Lat + dlat, -MaxVortexLat, MaxVortexLat);
                    double dlon = side * dist / Math.Max(Math.Cos(lat), 0.2) + rng.Normal(0.0, 0.2 * hero.CoreRadius);
                    double lon = WrapPi(hero.Lon + dlon);
                    double r = Clip(0.30 * hero.CoreRadius, 0.015, 0.035);
                    Vortex v = new Vortex(lat, lon, r, -AmbientSign(profiles, lat) * 0.008, VortexKinds.Pearl); v.Brightness = brightness; v.Aspect = aspect; reg.Vortices.Add(v);
                }
            }
        }

        private static void AddCast(VortexRegistry reg, LatProfiles profiles, ParamTree p, double? dt, int devSteps)
        {
            JsonArray cast = p.Array("storms.cast");
            for (int castIndex = 0; castIndex < cast.Count; castIndex++)
            {
                JsonObject e = (JsonObject)cast[castIndex];
                string kind = e["kind"].GetValue<string>();
                double radius = e["radius"].GetValue<double>();
                double lat = e["lat_deg"].GetValue<double>() * Math.PI / 180.0;
                double lon = DriftCompensatedLon(profiles, lat, e["lon_deg"].GetValue<double>(), dt, devSteps);
                double sign = -AmbientSign(profiles, lat);
                float k;
                double baseStrength, dTint, dBright;
                if (kind == "hero") { k = VortexKinds.Hero; baseStrength = 0.045 * p.Double("storms.hero_strength"); dTint = p.Double("storms.hero_tint"); dBright = p.Double("storms.hero_brightness"); }
                else if (kind == "oval") { k = VortexKinds.Oval; baseStrength = 0.012 * (radius / 0.03); dTint = 0.1; dBright = 0.22; }
                else if (kind == "barge") { k = VortexKinds.Barge; baseStrength = 0.006; dTint = 0.35; dBright = -0.28; }
                else { k = VortexKinds.Pearl; baseStrength = 0.008; dTint = 0.05; dBright = 0.25; }
                double strengthScale = e["strength_scale"].GetValue<double>();
                double tint = JsonNullableDouble(e, "tint") ?? dTint;
                double bright = JsonNullableDouble(e, "brightness") ?? dBright;
                double wakeDir = kind == "hero" ? -1.0 : 0.0;
                double wakeOff = kind == "hero" ? 0.5 * radius * (lat < 0.0 ? 1.0 : -1.0) : 0.0;
                double bow = 0.0;
                if (kind == "hero" && EffectiveCastLever(p, castIndex, "emergence") > 0.0)
                {
                    double[] frame = HeroWakeFrame(profiles, lat, radius); wakeOff = frame[0]; wakeDir = frame[1]; bow = HeroBowGain(profiles, lat, radius);
                }
                if (kind == "hero")
                {
                    string ew = JsonNullableString(e, "wake_dir") ?? p.String("storms.hero_wake_dir");
                    if (ew == "east") wakeDir = 1.0; else if (ew == "west") wakeDir = -1.0;
                }
                Vortex v = new Vortex(lat, lon, radius, sign * baseStrength * strengthScale, k);
                v.Tint = tint; v.Brightness = bright; v.WakeDir = wakeDir; v.WakeLatOff = wakeOff; v.BowGain = bow; v.Aspect = e["aspect"].GetValue<double>(); v.Origin = "cast"; v.CastRef = kind == "hero" ? castIndex : -1; reg.Vortices.Add(v);
                if (kind == "hero")
                {
                    int companions = e["companions"].GetValue<int>();
                    if (companions > 0)
                    {
                        double ca = JsonNullableDouble(e, "companion_aspect") ?? p.Double("storms.companion_aspect");
                        double cb = JsonNullableDouble(e, "companion_brightness") ?? p.Double("storms.companion_brightness");
                        AddCastCompanions(reg, profiles, lat, lon, radius, wakeDir, companions, ca, cb);
                    }
                }
            }
        }

        private static void AddCastCompanions(VortexRegistry reg, LatProfiles profiles, double heroLat, double heroLon, double radius, double wakeDir, int count, double aspect, double brightness)
        {
            double side = wakeDir != 0.0 ? -wakeDir : 1.0;
            double eq = heroLat < 0.0 ? 1.0 : -1.0;
            for (int i = 0; i < count; i++)
            {
                double dist = (1.7 + 0.8 * i) * radius;
                double dlat = eq * 0.85 * radius * (i % 2 == 0 ? 1.0 : -0.8);
                double lat = Clip(heroLat + dlat, -MaxVortexLat, MaxVortexLat);
                double lon = WrapPi(heroLon + side * dist / Math.Max(Math.Cos(lat), 0.2));
                double r = Clip(0.30 * radius, 0.015, 0.035);
                Vortex v = new Vortex(lat, lon, r, -AmbientSign(profiles, lat) * 0.008, VortexKinds.Pearl); v.Brightness = brightness; v.Aspect = aspect; v.Origin = "cast"; reg.Vortices.Add(v);
            }
        }

        private static void SeedConvergentPairs(VortexRegistry reg, RandomGenerator rng, List<double[]> zones, LatProfiles profiles, double mergeRate, double dt, int devSteps, double stepScale)
        {
            int pairIndex = 0;
            for (int z = 0; z < zones.Count; z++)
            {
                double center = zones[z][0], width = zones[z][1];
                if (rng.Random() >= 0.5 * mergeRate) continue;
                List<Vortex> hosts = new List<Vortex>();
                for (int i = 0; i < reg.Vortices.Count; i++) if (reg.Vortices[i].Kind == VortexKinds.Oval && Math.Abs(reg.Vortices[i].Lat - center) < 0.5 * width) hosts.Add(reg.Vortices[i]);
                if (hosts.Count == 0) continue;
                Vortex host = hosts[(int)rng.Integers(0, hosts.Count)];
                double u01 = rng.Random();
                double rc = 0.018 + (0.045 - 0.018) * u01 * u01;
                double capture = MergeCaptureCoef * mergeRate * (host.CoreRadius + rc);
                double dlat = Math.Min(0.5 * (host.CoreRadius + rc), 0.75 * capture);
                double cand0 = Clip(host.Lat + dlat, -MaxVortexLat, MaxVortexLat);
                double cand1 = Clip(host.Lat - dlat, -MaxVortexLat, MaxVortexLat);
                double hostRate = ZonalRate(profiles, host.Lat);
                double dr0 = ZonalRate(profiles, cand0) - hostRate;
                double dr1 = ZonalRate(profiles, cand1) - hostRate;
                double compLat = Math.Abs(dr0) >= Math.Abs(dr1) ? cand0 : cand1;
                double drate = Math.Abs(dr0) >= Math.Abs(dr1) ? dr0 : dr1;
                double dlatActual = compLat - host.Lat;
                double dlonCapture = Math.Sqrt(Math.Max(capture * capture - dlatActual * dlatActual, 0.0)) / Math.Max(Math.Cos(host.Lat), 0.2);
                double closure = Math.Abs(drate) * dt;
                int offHi = ResolutionScaling.ScaleDuration(220, stepScale), offLo = ResolutionScaling.ScaleDuration(80, stepScale), earlyHi = ResolutionScaling.ScaleDuration(280, stepScale);
                int lo, hi;
                if ((pairIndex & 1) == 0) { lo = Math.Max(devSteps - offHi, offLo); hi = Math.Max(devSteps - offLo, offLo + 1); }
                else { lo = offLo; hi = Math.Max(Math.Min(earlyHi, devSteps), offLo + 1); }
                int targetStep = (int)rng.Integers(lo, hi + 1);
                double gap = closure * devSteps < 0.02 ? 1.05 * dlonCapture : 1.02 * dlonCapture + closure * targetStep;
                pairIndex++;
                double signedGap = drate != 0.0 ? -Math.Sign(drate) * gap : gap;
                double compLon = WrapPi(host.Lon + signedGap);
                double sign = host.Strength > 0.0 ? 1.0 : -1.0;
                Vortex v = new Vortex(compLat, compLon, rc, sign * 0.012 * (rc / 0.03), VortexKinds.Oval); v.Tint = host.Tint; v.Brightness = host.Brightness; reg.Vortices.Add(v);
            }
            while (reg.Vortices.Count > MaxVortices) reg.Vortices.RemoveAt(reg.Vortices.Count - 1);
        }

        private static void EnforceCap(VortexRegistry reg)
        {
            int excess = reg.Vortices.Count - MaxVortices;
            if (excess <= 0) return;
            List<Vortex> ovals = new List<Vortex>();
            for (int i = 0; i < reg.Vortices.Count; i++) if (reg.Vortices[i].Kind == VortexKinds.Oval) ovals.Add(reg.Vortices[i]);
            ovals.Sort(delegate(Vortex a, Vortex b) { int c = a.CoreRadius.CompareTo(b.CoreRadius); return c != 0 ? c : Math.Abs(a.Brightness).CompareTo(Math.Abs(b.Brightness)); });
            HashSet<Vortex> drop = new HashSet<Vortex>();
            for (int i = 0; i < Math.Min(excess, ovals.Count); i++) drop.Add(ovals[i]);
            reg.Vortices.RemoveAll(delegate(Vortex v) { return drop.Contains(v); });
        }

        private static double HeroBowGain(LatProfiles profiles, double lat, double r)
        {
            double lo = lat - 1.6 * r, hi = lat + 1.6 * r;
            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            bool any = false;
            for (int i = 0; i < profiles.Lat.Length; i++) if (profiles.Lat[i] >= lo && profiles.Lat[i] <= hi) { min = Math.Min(min, profiles.T0Stamp[i]); max = Math.Max(max, profiles.T0Stamp[i]); any = true; }
            if (!any) return 0.0;
            double x = Clip((max - min - 0.04) / 0.10, 0.0, 1.0);
            return x * x * (3.0 - 2.0 * x);
        }

        private static double[] HeroWakeFrame(LatProfiles profiles, double lat, double r)
        {
            double eq = lat < 0.0 ? 1.0 : -1.0;
            double legacyOff = 0.5 * r * eq;
            double a = lat + eq * 0.4 * r, b = lat + eq * 2.5 * r;
            double lo = Math.Min(a, b), hi = Math.Max(a, b);
            int best = -1; double bestAbs = -1.0;
            for (int i = 0; i < profiles.Lat.Length; i++) if (profiles.Lat[i] >= lo && profiles.Lat[i] <= hi && Math.Abs(profiles.U[i]) > bestAbs) { best = i; bestAbs = Math.Abs(profiles.U[i]); }
            if (best < 0 || bestAbs < 0.05) return new double[] { legacyOff, -1.0 };
            return new double[] { profiles.Lat[best] - lat, profiles.U[best] > 0.0 ? 1.0 : -1.0 };
        }

        private static double AmbientSign(LatProfiles profiles, double lat)
        {
            int n = profiles.Lat.Length;
            double[] du = new double[n];
            for (int i = 0; i < n; i++)
            {
                int im = i == 0 ? 0 : i - 1, ip = i == n - 1 ? n - 1 : i + 1;
                double dx = profiles.Lat[ip] - profiles.Lat[im];
                du[i] = dx == 0.0 ? 0.0 : (profiles.U[ip] - profiles.U[im]) / dx;
            }
            double s = -InterpDescending(profiles.Lat, du, lat);
            return s >= 0.0 ? 1.0 : -1.0;
        }

        private static List<double[]> BandCenters(BandLayout bands, bool wantBelt)
        {
            List<double[]> result = new List<double[]>();
            for (int i = 0; i < bands.Values.Length; i++)
            {
                double center = 0.5 * (bands.Edges[i] + bands.Edges[i + 1]);
                double width = bands.Edges[i] - bands.Edges[i + 1];
                if (Math.Abs(center) > MaxVortexLat) continue;
                if (bands.IsBelt[i] == wantBelt) result.Add(new double[] { center, width });
            }
            return result;
        }

        private static List<double[]> FilterCenters(List<double[]> source, double absLo, double absHi)
        {
            List<double[]> result = new List<double[]>();
            for (int i = 0; i < source.Count; i++) { double a = Math.Abs(source[i][0]); if (a > absLo && a < absHi) result.Add(source[i]); }
            return result;
        }

        private static List<double> PoissonLons(RandomGenerator rng, int count, double minSep)
        {
            List<double> lons = new List<double>();
            for (int i = 0; i < count * 8 && lons.Count < count; i++)
            {
                double cand = rng.Uniform(-Math.PI, Math.PI);
                bool ok = true;
                for (int j = 0; j < lons.Count; j++) if (Math.Abs(WrapPi(cand - lons[j])) <= minSep) { ok = false; break; }
                if (ok) lons.Add(cand);
            }
            return lons;
        }

        internal static double EffectiveCastLever(ParamTree p, int castRef, string attr)
        {
            string global;
            if (attr == "rim_contrast") global = "storms.rim_contrast";
            else if (attr == "rim_tint") global = "storms.hero_rim_tint";
            else if (attr == "rim_warp") global = "storms.hero_rim_warp";
            else if (attr == "mottle") global = "storms.hero_mottle";
            else if (attr == "tint_var") global = "storms.hero_tint_var";
            else if (attr == "wake_detail") global = "storms.hero_wake_detail";
            else if (attr == "solid_core") global = "storms.hero_solid_core";
            else if (attr == "emergence") global = "storms.hero_emergence";
            else if (attr == "shape") global = "storms.hero_shape";
            else if (attr == "taper") global = "storms.hero_taper";
            else throw new ArgumentException(attr);
            if (castRef >= 0)
            {
                JsonArray cast = p.Array("storms.cast");
                if (castRef < cast.Count)
                {
                    JsonObject e = (JsonObject)cast[castRef];
                    double? value = JsonNullableDouble(e, attr);
                    if (value.HasValue) return value.Value;
                }
            }
            return p.Double(global);
        }

        private static double Veil(double lat)
        {
            double x = Clip((Math.Abs(lat) - 0.6) / (1.15 - 0.6), 0.0, 1.0);
            return 1.0 - 0.55 * (x * x * (3.0 - 2.0 * x));
        }

        private static double InterpDescending(double[] x, double[] y, double q)
        {
            int n = x.Length;
            if (q >= x[0]) return y[0];
            if (q <= x[n - 1]) return y[n - 1];
            int lo = 0, hi = n - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (x[mid] >= q) lo = mid; else hi = mid;
            }
            double t = (q - x[lo]) / (x[hi] - x[lo]);
            return y[lo] + (y[hi] - y[lo]) * t;
        }

        private static double GreatCircleDistance(double lat0, double lon0, double lat1, double lon1)
        {
            double c0 = Math.Cos(lat0), c1 = Math.Cos(lat1);
            double dot = c0 * c1 * Math.Cos(lon0 - lon1) + Math.Sin(lat0) * Math.Sin(lat1);
            return Math.Acos(Clip(dot, -1.0, 1.0));
        }

        internal static double WrapPi(double x)
        {
            double t = (x + Math.PI) % (2.0 * Math.PI);
            if (t < 0.0) t += 2.0 * Math.PI;
            return t - Math.PI;
        }

        private static double Clip(double x, double lo, double hi) { return x < lo ? lo : (x > hi ? hi : x); }
        private static double? NullableDouble(ParamTree p, string path) { return p.Has(path) ? (double?)p.Double(path) : null; }
        private static double? JsonNullableDouble(JsonObject o, string name) { JsonNode n; if (!o.TryGetPropertyValue(name, out n) || n == null) return null; return n.GetValue<double>(); }
        private static string JsonNullableString(JsonObject o, string name) { JsonNode n; if (!o.TryGetPropertyValue(name, out n) || n == null) return null; return n.GetValue<string>(); }
    }
}
