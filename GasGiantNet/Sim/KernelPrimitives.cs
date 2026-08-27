using System;
using GasGiantNet.Config;
using GasGiantNet.MathCore;
using GasGiantNet.Random;

namespace GasGiantNet.Sim
{
    internal sealed class SimStaticUniforms
    {
        public V3 VarianceOffset;
        public V3 WarpOffset;
        public V3 DetailOffset;
        public V3 TurbOffset;
        public V3 HeroNoiseOffset;
        public V3 HeroShapePhase;
        public float KhPhase;
        public float PolyPhase;
        public float FestPhase;
        public float RibPhase;
        public float Fest2Phase;

        public static SimStaticUniforms Build(int seed, int heroShapeSeed)
        {
            SimStaticUniforms u = new SimStaticUniforms();
            u.VarianceOffset = Draw3(RandomGenerator.Subseed(seed, "band-variance"), -100.0, 100.0);
            u.WarpOffset = Draw3(RandomGenerator.Subseed(seed, "warp-noise"), -100.0, 100.0);
            u.DetailOffset = Draw3(RandomGenerator.Subseed(seed, "detail-noise"), -100.0, 100.0);
            u.HeroNoiseOffset = u.DetailOffset;
            u.TurbOffset = Draw3(RandomGenerator.Subseed(seed, "turbulence"), -100.0, 100.0);
            u.KhPhase = (float)RandomGenerator.Subseed(seed, "kh-wave").Uniform(0.0, 2.0 * Math.PI);
            u.PolyPhase = (float)RandomGenerator.Subseed(seed, "poly-jet").Uniform(0.0, 2.0 * Math.PI);
            RandomGenerator waves = RandomGenerator.Subseed(seed, "eq-waves");
            u.FestPhase = (float)waves.Uniform(0.0, 2.0 * Math.PI);
            u.RibPhase = (float)waves.Uniform(0.0, 2.0 * Math.PI);
            u.Fest2Phase = (float)waves.Uniform(0.0, 2.0 * Math.PI);
            RandomGenerator shape = RandomGenerator.Subseed(seed, "hero-shape:" + heroShapeSeed.ToString(System.Globalization.CultureInfo.InvariantCulture));
            u.HeroShapePhase = Draw3(shape, 0.0, 2.0 * Math.PI);
            return u;
        }

        private static V3 Draw3(RandomGenerator rng, double lo, double hi)
        {
            return new V3((float)rng.Uniform(lo, hi), (float)rng.Uniform(lo, hi), (float)rng.Uniform(lo, hi));
        }
    }

    internal static class BandStampCpu
    {
        private const float EnvStart = 0.7854f;
        private const float EnvEnd = 1.2566f;
        private const float T0Mid = 0.54f;
        private const float T1Mid = 0.55f;

        public static void Mod(ref float t0, ref float t1, V3 sphere, V2 ll, ParamTree p, BandLayout bands, SimStaticUniforms u)
        {
            float variance = p.Float("bands.variance_amount");
            if (variance > 0f)
            {
                float drift = Noise3D.Fbm(sphere * new V3(0.9f, 4.0f, 0.9f) + u.VarianceOffset, 3, 2f, 0.5f);
                t0 += variance * drift;
            }
            float fadeAmp = p.Float("bands.faded_sector");
            if (fadeAmp > 0f && ll.Y > (float)bands.FadeLatLo && ll.Y < (float)bands.FadeLatHi)
            {
                float dlon = MathF.Abs(MathF.Atan2(MathF.Sin(ll.X - (float)bands.FadeLon), MathF.Cos(ll.X - (float)bands.FadeLon)));
                float fw = 1f - Glsl.SmoothStep(0.55f * (float)bands.FadeHalfWidth, (float)bands.FadeHalfWidth, dlon);
                t0 = Glsl.Mix(t0, T0Mid + 0.10f, fadeAmp * fw);
            }
            float envStrength = p.Float("bands.contrast_envelope");
            if (envStrength > 0f)
            {
                float env = 1f - envStrength * Glsl.SmoothStep(EnvStart, EnvEnd, MathF.Abs(ll.Y));
                t0 = T0Mid + (t0 - T0Mid) * env;
                t1 = T1Mid + (t1 - T1Mid) * env;
            }
        }
    }

    internal static class WaveStampCpu
    {
        public static V3 Stamp(V2 ll, ParamTree p, float festLat, float ribLat, float heroFestLat, bool festoon2, SimStaticUniforms u)
        {
            V3 d = new V3(0, 0, 0);
            float festAmp = p.Float("waves.festoon_strength");
            if (festAmp > 0f)
            {
                float k = p.Float("waves.festoon_wavenumber");
                float crest = MathF.Sin(k * ll.X + u.FestPhase);
                float plumeCenter = festLat - Glsl.Sign(festLat) * 0.045f;
                float plume = MathF.Exp(-Sq((ll.Y - plumeCenter) / 0.05f));
                float c = MathF.Max(crest, 0f);
                d.Z -= festAmp * 0.7f * plume * c * c;
                float hole = MathF.Exp(-Sq((ll.Y - festLat) / 0.025f));
                float tr = MathF.Max(-crest, 0f);
                float spot = festAmp * p.Float("waves.hotspot_depth") * hole * tr * tr * tr * tr;
                d.Y -= 0.5f * spot;
                d.X -= 0.35f * spot;
            }
            if (festoon2)
            {
                float a2 = p.Float("waves.festoon_hero_strength");
                float k2 = p.Float("waves.festoon_hero_wavenumber");
                float crest2 = MathF.Sin(k2 * ll.X + u.Fest2Phase);
                float pj0 = 0.5f + 0.5f * MathF.Sin(3f * ll.X + u.Fest2Phase * 1.7f);
                float pj = 0.4f + 0.6f * pj0 * pj0;
                float pc2 = heroFestLat - Glsl.Sign(heroFestLat) * 0.045f;
                float plume2 = MathF.Exp(-Sq((ll.Y - pc2) / 0.05f));
                float c2 = MathF.Max(crest2, 0f);
                d.Z -= a2 * 0.7f * pj * plume2 * c2 * c2;
            }
            float ribAmp = p.Float("waves.ribbon_strength");
            if (ribAmp > 0f)
            {
                float line = MathF.Exp(-Sq((ll.Y - ribLat) / 0.008f));
                d.X -= ribAmp * 0.14f * line;
            }
            return d;
        }
        private static float Sq(float x) { return x * x; }
    }
}
