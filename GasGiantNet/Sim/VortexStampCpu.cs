using System;
using GasGiantNet.Config;
using GasGiantNet.MathCore;

namespace GasGiantNet.Sim
{
    internal sealed class VortexStampContext
    {
        public readonly ParamTree Params;
        public readonly SimStaticUniforms Static;
        public readonly V4[] VortexData;
        public readonly V4[] CastLeverData;
        public readonly int Count;
        public readonly bool HeroEmergence;
        public readonly bool CastLevers;

        public VortexStampContext(ParamTree p, SimStaticUniforms statics, VortexRegistry registry, bool heroEmergence, bool castLevers)
        {
            Params = p;
            Static = statics;
            HeroEmergence = heroEmergence;
            CastLevers = castLevers;
            float[] packed = registry.PackSsbo();
            Count = registry.Vortices.Count;
            VortexData = new V4[Math.Max(1, Count * 3)];
            for (int i = 0; i < Count * 3; i++)
            {
                int o = i * 4;
                VortexData[i] = new V4(packed[o], packed[o + 1], packed[o + 2], packed[o + 3]);
            }
            if (castLevers)
            {
                float[] cl = registry.PackCastLeversSsbo(p);
                CastLeverData = new V4[Math.Max(1, Count * 3)];
                for (int i = 0; i < Count * 3; i++)
                {
                    int o = i * 4;
                    CastLeverData[i] = new V4(cl[o], cl[o + 1], cl[o + 2], cl[o + 3]);
                }
            }
        }
    }

    internal static class VortexStampCpu
    {
        private const float Hero = 1.0f;
        private const float Barge = 2.0f;
        private const float Polar = 5.0f;
        private const float Outbreak = 6.0f;
        private const float Debris = 7.0f;
        private const float Pi = Glsl.PI;

        public static V3 Stamp(V3 p, VortexStampContext c)
        {
            float dT0 = 0.0f;
            float dT1 = 0.0f;
            float dT3 = 0.0f;
            ParamTree prm = c.Params;
            V3 noiseOff = c.Static.HeroNoiseOffset;

            for (int i = 0; i < c.Count; ++i)
            {
                V4 a = c.VortexData[3 * i];
                V4 b = c.VortexData[3 * i + 1];
                V4 meta = c.VortexData[3 * i + 2];
                float d = MathF.Acos(Glsl.Clamp(Glsl.Dot(p, a.XYZ), -1.0f, 1.0f));
                float asp = meta.Y;
                float q;
                if (asp == 1.0f)
                {
                    q = d / a.W;
                }
                else
                {
                    V3 cc = a.XYZ;
                    V3 ew = Glsl.Cross(new V3(0.0f, 1.0f, 0.0f), cc);
                    float ewl = Glsl.Length(ew);
                    if (ewl < 1e-4f) q = d / a.W;
                    else
                    {
                        V3 e1 = ew / ewl;
                        V3 e2 = Glsl.Cross(cc, e1);
                        q = Glsl.Dot(p, cc) > 0.0f
                            ? Glsl.Length(new V2(Glsl.Dot(p, e1) / asp, Glsl.Dot(p, e2))) / a.W
                            : 1e3f;
                    }
                }

                if (q < 3.0f)
                {
                    float core = MathF.Exp(-q * q);
                    if (b.Y == Debris)
                    {
                        float ring = MathF.Exp(-(q - 1.5f) * (q - 1.5f) * 6.0f);
                        dT0 += b.W * ring;
                        dT3 -= 0.5f * b.W * ring;
                        dT1 += 0.04f * b.W * (core - 0.5f * ring);
                        continue;
                    }
                    if (b.Y == Outbreak)
                    {
                        float ring = MathF.Exp(-(q - 1.0f) * (q - 1.0f) * 9.0f);
                        dT0 += b.W * (core + ring);
                        dT3 -= 0.07f * b.W * (core + ring);
                        dT1 += 0.05f * b.W * core;
                        continue;
                    }

                    float dome = (b.Y == Barge || b.Y == Polar) ? -1.0f : 1.0f;
                    dT1 += dome * 0.15f * core;
                    dT3 += b.Z * core;

                    if (b.Y == Hero)
                    {
                        float rimC = prm.Float("storms.rim_contrast");
                        float rimTint = prm.Float("storms.hero_rim_tint");
                        float rimWarp = prm.Float("storms.hero_rim_warp");
                        float mottle = prm.Float("storms.hero_mottle");
                        float tintVar = prm.Float("storms.hero_tint_var");
                        float emergence = c.HeroEmergence ? prm.Float("storms.hero_emergence") : 0.0f;
                        float shape = c.HeroEmergence ? prm.Float("storms.hero_shape") : 0.0f;
                        float taper = c.HeroEmergence ? prm.Float("storms.hero_taper") : 0.0f;
                        if (c.CastLevers)
                        {
                            V4 cl0 = c.CastLeverData[3 * i];
                            V4 cl1 = c.CastLeverData[3 * i + 1];
                            rimC = cl0.X;
                            rimTint = cl0.Y;
                            rimWarp = cl0.Z;
                            mottle = cl0.W;
                            tintVar = cl1.X;
                            if (c.HeroEmergence)
                            {
                                V4 cl2 = c.CastLeverData[3 * i + 2];
                                emergence = cl2.X;
                                shape = cl2.Y;
                                taper = cl2.Z;
                            }
                        }

                        float qrim = q;
                        float qcol = q;
                        float hth = 0.0f;
                        bool hthOk = false;
                        if (rimWarp > 0.0f || rimTint > 0.0f || (c.HeroEmergence && emergence > 0.0f))
                        {
                            V3 hc = a.XYZ;
                            V3 hew = Glsl.Cross(new V3(0.0f, 1.0f, 0.0f), hc);
                            float hewl = Glsl.Length(hew);
                            if (hewl > 1e-4f)
                            {
                                V3 h1 = hew / hewl;
                                V3 h2 = Glsl.Cross(hc, h1);
                                hth = MathF.Atan2(Glsl.Dot(p, h2), Glsl.Dot(p, h1));
                                hthOk = true;
                            }
                        }

                        if (c.HeroEmergence && hthOk && emergence > 0.0f && (shape > 0.0f || taper > 0.0f))
                        {
                            float rr = 1.0f;
                            if (shape > 0.0f)
                            {
                                float thp = Pi - hth;
                                float neq = a.Y < 0.0f ? MathF.Max(MathF.Sin(hth), 0.0f) : MathF.Max(-MathF.Sin(hth), 0.0f);
                                V3 sph = c.Static.HeroShapePhase;
                                rr -= shape * emergence * (0.11f * neq * neq - 0.075f * MathF.Sin(2.0f * thp + sph.X) - 0.055f * MathF.Sin(3.0f * thp + sph.Y));
                            }
                            if (taper > 0.0f)
                            {
                                V3 tew = Glsl.Cross(new V3(0.0f, 1.0f, 0.0f), a.XYZ);
                                float tewl = Glsl.Length(tew);
                                float wdirH = meta.X;
                                if (tewl > 1e-4f)
                                {
                                    float uct = Glsl.Clamp(wdirH * Glsl.Dot(p, tew / tewl) / (asp * MathF.Max(a.W * q, 1e-5f)), -1.0f, 1.0f);
                                    float tc = MathF.Max(uct, 0.0f);
                                    float tc2 = tc * tc;
                                    float tw = 6.75f * tc2 * tc2 * (1.0f - tc2);
                                    rr -= 0.25f * taper * emergence * tw;
                                    rr = MathF.Max(rr, 0.4f);
                                }
                            }
                            q /= rr;
                            qrim /= rr;
                            qcol /= rr;
                        }

                        if (rimWarp > 0.0f && hthOk)
                        {
                            V3 ph = noiseOff * 6.2831853f;
                            float wr = 0.55f * MathF.Sin(2.0f * hth + ph.X) + 0.30f * MathF.Sin(3.0f * hth + ph.Y) + 0.20f * MathF.Sin(5.0f * hth + ph.Z);
                            float wc = 0.55f * MathF.Sin(2.0f * hth + ph.Y + 1.7f) + 0.30f * MathF.Sin(3.0f * hth + ph.Z + 0.6f) + 0.20f * MathF.Sin(5.0f * hth + ph.X + 2.9f);
                            qrim += rimWarp * 0.20f * wr;
                            qcol += rimWarp * 0.20f * wc;
                        }

                        float fill = core;
                        float carve = 1.0f;
                        if (c.HeroEmergence)
                        {
                            float plate = 1.0f - Glsl.SmoothStep(0.62f, 1.0f, qrim);
                            if (qrim < 1.7f)
                            {
                                float ffrq = Glsl.Mix(5.0f, 8.0f, emergence);
                                float efray = Noise3D.Fbm(p * (a.W > 0.0f ? ffrq / a.W : ffrq) + noiseOff.YXZ, 3, 2.0f, 0.5f);
                                plate = Glsl.Clamp(plate + 0.6f * emergence * efray * MathF.Exp(-(qrim - 0.84f) * (qrim - 0.84f) * 6.0f), 0.0f, 1.0f);
                            }
                            fill = Glsl.Mix(core, plate, emergence);
                            dT3 += b.Z * (fill - core);
                            dT1 += 0.15f * (fill - core);
                            dT0 += 0.10f * emergence * plate;
                            dT3 -= 0.30f * emergence * b.Z * Glsl.SmoothStep(0.45f, 0.97f, q) * plate;
                            dT0 += 0.06f * emergence * Glsl.SmoothStep(0.45f, 0.97f, q) * plate;
                            if (hthOk)
                            {
                                V3 lph = noiseOff * 9.42f;
                                float lane = MathF.Sin(q * 6.0f + hth + 1.1f * MathF.Sin(hth + lph.X) + lph.Y);
                                dT0 += 0.09f * emergence * lane * plate * Glsl.SmoothStep(0.16f, 0.32f, q);
                                float lane3 = MathF.Sin(q * 13.0f + hth + 1.1f * MathF.Sin(hth + lph.Y) + lph.Z);
                                float wq = Glsl.SmoothStep(0.12f, 0.28f, q) * (1.0f - Glsl.SmoothStep(0.82f, 1.0f, q));
                                dT3 -= 0.30f * emergence * b.Z * (0.5f + 0.5f * lane3) * plate * wq;
                                float qOff2 = q * q + 0.09f - 0.6f * q * MathF.Cos(hth - lph.Z);
                                float knot = MathF.Exp(-3.0f * qOff2);
                                dT0 += 0.18f * emergence * knot * plate;
                                dT3 += 0.32f * emergence * knot * plate;
                                float qOff2b = q * q + 0.0625f - 0.5f * q * MathF.Cos(hth - lph.Z - 2.6f);
                                dT3 -= 0.45f * emergence * b.Z * MathF.Exp(-6.0f * qOff2b) * plate;
                            }

                            float ringQ = Glsl.Mix(1.0f, 1.30f, emergence);
                            float colQ = Glsl.Mix(1.55f, 1.12f, emergence);
                            float ringK = Glsl.Mix(16.0f, 12.0f, emergence);
                            float colK = Glsl.Mix(5.0f, 34.0f, emergence);
                            float ringMod = 1.0f;
                            if (hthOk)
                            {
                                float wdir = meta.X;
                                float polew = a.Y < 0.0f ? MathF.Max(-MathF.Sin(hth), 0.0f) : MathF.Max(MathF.Sin(hth), 0.0f);
                                float equw = a.Y < 0.0f ? MathF.Max(MathF.Sin(hth), 0.0f) : MathF.Max(-MathF.Sin(hth), 0.0f);
                                float eastw = MathF.Max(MathF.Cos(hth) * wdir, 0.0f);
                                float wakew = Glsl.SmoothStep(0.1f, 0.8f, -MathF.Cos(hth) * wdir);
                                colQ += emergence * (0.10f * polew + 0.06f * eastw);
                                colK *= 1.0f + 0.9f * emergence * equw;
                                carve = 1.0f - 0.8f * emergence * wakew;
                                V3 cph = noiseOff * 17.3f;
                                carve *= 0.78f + 0.22f * MathF.Sin(2.0f * hth + cph.X) * MathF.Sin(hth + cph.Y);
                                ringK *= 1.0f + 0.45f * emergence * MathF.Sin(3.0f * hth + cph.Z);
                                ringMod = (0.55f + 0.45f * MathF.Sin(2.0f * hth + cph.Y + 2.1f) * MathF.Sin(3.0f * hth + cph.X + 0.7f))
                                        * (1.0f - 0.6f * emergence * wakew)
                                        * (1.0f - 0.55f * emergence * equw);
                            }
                            float quiet = 1.0f - 0.5f * emergence;
                            dT0 += b.W * fill
                                 - Glsl.Mix(0.16f, 0.125f, emergence) * quiet * ringMod * rimC * MathF.Exp(-(qrim - ringQ) * (qrim - ringQ) * ringK)
                                 + Glsl.Mix(0.22f, 0.31f, emergence) * quiet * carve * rimC * MathF.Exp(-(qcol - colQ) * (qcol - colQ) * colK);
                        }
                        else
                        {
                            dT0 += b.W * core
                                 - 0.16f * rimC * MathF.Exp(-(qrim - 1.0f) * (qrim - 1.0f) * 16.0f)
                                 + 0.22f * rimC * MathF.Exp(-(qcol - 1.55f) * (qcol - 1.55f) * 5.0f);
                        }

                        if (rimTint > 0.0f)
                        {
                            float rtQ = c.HeroEmergence ? Glsl.Mix(1.08f, 1.30f, emergence) : 1.08f;
                            float rtK = c.HeroEmergence ? Glsl.Mix(11.0f, 12.0f, emergence) : 11.0f;
                            float rring = MathF.Exp(-(qrim - rtQ) * (qrim - rtQ) * rtK);
                            float azw = 1.0f;
                            if (hthOk)
                            {
                                V3 tph = noiseOff * 6.2831853f;
                                float lobe = 0.6f * MathF.Sin(hth + tph.X) + 0.3f * MathF.Sin(2.0f * hth + tph.Y) + 0.2f * MathF.Sin(3.0f * hth + tph.Z);
                                azw = Glsl.Clamp(0.35f + 0.65f * (0.5f + 0.5f * lobe), 0.35f, 1.0f);
                            }
                            if (c.HeroEmergence)
                            {
                                float moat = (1.0f - 0.6f * emergence) * carve;
                                dT3 += rimTint * 0.55f * rring * moat;
                                dT0 -= rimTint * 0.16f * rring * azw * moat;
                            }
                            else
                            {
                                dT3 += rimTint * 0.55f * rring;
                                dT0 -= rimTint * 0.16f * rring * azw;
                            }
                        }

                        if (mottle > 0.0f)
                        {
                            float win = core * (1.0f - Glsl.SmoothStep(0.6f, 1.0f, q));
                            float fscale;
                            if (c.HeroEmergence)
                            {
                                win = MathF.Max(win, fill * (1.0f - Glsl.SmoothStep(0.78f, 1.04f, qrim))) * (1.0f - 0.35f * emergence);
                                fscale = (a.W > 0.0f ? 9.0f / a.W : 9.0f) * (1.0f + 0.4f * emergence);
                            }
                            else fscale = a.W > 0.0f ? 9.0f / a.W : 9.0f;
                            dT0 += 0.15f * mottle * win * Noise3D.Fbm(p * fscale + noiseOff.YZX, 4, 2.0f, 0.5f);
                        }

                        if (tintVar > 0.0f)
                        {
                            float winT = core * (1.0f - Glsl.SmoothStep(0.55f, 1.0f, q));
                            float fscaleT;
                            if (c.HeroEmergence)
                            {
                                winT = MathF.Max(winT, fill * (1.0f - Glsl.SmoothStep(0.75f, 1.0f, qrim))) * (1.0f - 0.5f * emergence);
                                fscaleT = (a.W > 0.0f ? 7.0f / a.W : 7.0f) * (1.0f + 0.55f * emergence);
                            }
                            else fscaleT = a.W > 0.0f ? 7.0f / a.W : 7.0f;
                            dT3 += b.Z * tintVar * winT * Noise3D.Fbm(p * fscaleT + noiseOff.ZXY + new V3(13.0f, 13.0f, 13.0f), 3, 2.0f, 0.5f);
                        }
                    }
                    else if (b.Y == Polar)
                    {
                        dT0 += b.W * core - 0.10f * MathF.Exp(-q * q * 9.0f) + 0.14f * MathF.Exp(-(q - 2.0f) * (q - 2.0f) * 2.2f);
                        dT1 -= 0.06f * MathF.Exp(-q * q * 9.0f);
                    }
                    else if (asp > 1.0f)
                    {
                        float glow = 0.6f * core + 0.4f * MathF.Exp(-q * 1.3f);
                        V3 cc = a.XYZ;
                        V3 few = Glsl.Cross(new V3(0.0f, 1.0f, 0.0f), cc);
                        float fewl = Glsl.Length(few);
                        if (fewl > 1e-4f)
                        {
                            V3 f1 = few / fewl;
                            V3 f2 = Glsl.Cross(cc, f1);
                            float strand = Noise3D.Fbm(new V3(Glsl.Dot(p, f1) * 6.0f, Glsl.Dot(p, f2) * 44.0f, 3.1f) + new V3(11.3f, 4.7f, 8.1f), 3, 2.0f, 0.5f);
                            glow *= Glsl.Clamp(0.4f + 0.95f * strand, 0.0f, 1.35f);
                        }
                        dT0 += b.W * glow;
                    }
                    else
                    {
                        float ring = MathF.Exp(-(q - 1.2f) * (q - 1.2f) * 4.0f);
                        dT0 += b.W * core - 0.3f * MathF.Abs(b.W) * ring;
                    }
                }

                if (b.Y == Hero)
                {
                    float wakeDetail = prm.Float("storms.hero_wake_detail");
                    float emergence = c.HeroEmergence ? prm.Float("storms.hero_emergence") : 0.0f;
                    if (c.CastLevers)
                    {
                        wakeDetail = c.CastLeverData[3 * i + 1].Y;
                        if (c.HeroEmergence) emergence = c.CastLeverData[3 * i + 2].X;
                    }
                    float down = meta.X;
                    float woff = meta.Z;
                    float rc = a.W;
                    float vlat = MathF.Asin(Glsl.Clamp(a.Y, -1.0f, 1.0f));
                    float vlon = MathF.Atan2(a.Z, a.X);
                    float plat = MathF.Asin(Glsl.Clamp(p.Y, -1.0f, 1.0f));
                    float plon = MathF.Atan2(p.Z, p.X);
                    float dlon = Glsl.Mod(plon - vlon + 3.0f * Pi, 2.0f * Pi) - Pi;
                    float along = dlon * down;
                    float across = (plat - (vlat + woff)) / MathF.Max(rc * 1.6f, 1e-4f);
                    float wseed = noiseOff.X * 6.3f;
                    float an = along / MathF.Max(rc, 1e-4f);
                    if (wakeDetail > 0.0f)
                        across += wakeDetail * 0.30f * Noise3D.Fbm(new V3(an * 0.5f, 0.0f, wseed + 11.0f), 2, 2.0f, 0.5f);
                    float wlen = c.HeroEmergence ? Glsl.Mix(6.0f, 9.0f, emergence) : 6.0f;
                    float wdim = c.HeroEmergence ? 1.0f - 0.6f * emergence : 1.0f;
                    if (along > 0.0f && along < rc * wlen && MathF.Abs(across) < 2.5f)
                    {
                        float ramp = Glsl.SmoothStep(rc * 0.5f * asp, rc * asp, along);
                        float win = 1.0f - Glsl.SmoothStep(2.0f, 2.5f, MathF.Abs(across));
                        float ww = MathF.Exp(-across * across) * win * (1.0f - along / (rc * wlen)) * ramp;
                        if (wakeDetail > 0.0f)
                        {
                            float sh = across + 0.25f * an;
                            float fil = Noise3D.Fbm(new V3(an * 0.30f, sh * 1.7f, wseed), 4, 2.0f, 0.5f);
                            float streak = Glsl.Clamp(Glsl.SmoothStep(-0.2f, 0.6f, fil), 0.0f, 1.0f);
                            ww *= Glsl.Mix(1.0f, streak, wakeDetail);
                        }
                        dT0 += 0.16f * ww * wdim;
                        dT3 -= 0.20f * ww * wdim;
                    }
                }
            }
            return new V3(dT0, dT1, dT3);
        }

        public static float HeroRelaxWeight(V3 p, VortexStampContext c)
        {
            if (!c.HeroEmergence) return 1.0f;
            float infl = 0.0f, flush = 0.0f, wrel = 0.0f;
            float inflE = 0.0f, flushE = 0.0f, wrelE = 0.0f;
            ParamTree prm = c.Params;
            V3 noiseOff = c.Static.HeroNoiseOffset;

            for (int i = 0; i < c.Count; ++i)
            {
                V4 b = c.VortexData[3 * i + 1];
                if (b.Y != Hero) continue;
                float emergence = prm.Float("storms.hero_emergence");
                float shape = prm.Float("storms.hero_shape");
                float taper = prm.Float("storms.hero_taper");
                if (c.CastLevers)
                {
                    V4 cl2 = c.CastLeverData[3 * i + 2];
                    emergence = cl2.X; shape = cl2.Y; taper = cl2.Z;
                }
                if (emergence <= 0.0f) continue;

                V4 wa = c.VortexData[3 * i];
                V4 wm = c.VortexData[3 * i + 2];
                float rc = wa.W, asp = wm.Y, down = wm.X, woff = wm.Z;
                float vlat = MathF.Asin(Glsl.Clamp(wa.Y, -1.0f, 1.0f));
                float vlon = MathF.Atan2(wa.Z, wa.X);
                float plat = MathF.Asin(Glsl.Clamp(p.Y, -1.0f, 1.0f));
                float plon = MathF.Atan2(p.Z, p.X);
                float dlon = Glsl.Mod(plon - vlon + 3.0f * Pi, 2.0f * Pi) - Pi;
                {
                    float an = dlon * down / MathF.Max(rc, 1e-4f);
                    float across = (plat - (vlat + woff)) / MathF.Max(rc * 1.8f, 1e-4f);
                    if (an > 1.5f && an < 9.0f && MathF.Abs(across) < 2.0f)
                    {
                        float rise = Glsl.SmoothStep(1.5f, 2.5f, an);
                        float fall = 1.0f - Glsl.SmoothStep(6.0f, 9.0f, an);
                        float aw = (1.0f - Glsl.SmoothStep(1.4f, 2.0f, MathF.Abs(across))) * MathF.Exp(-across * across);
                        float cand = rise * fall * aw;
                        if (emergence * cand > wrelE * wrel || (emergence * cand == wrelE * wrel && cand > wrel)) { wrel = cand; wrelE = emergence; }
                    }
                }

                float q = HeroEllipQ(p, i, 4.2f, c);
                if (q > 4.2f) continue;
                float xe = dlon * down / MathF.Max(asp, 1.0f);
                float yn = plat - vlat;
                float den = MathF.Max(Glsl.Length(new V2(xe, yn)), 1e-5f);
                float upw = Glsl.SmoothStep(0.15f, 0.7f, -xe / den);
                float m = Glsl.Clamp(yn / MathF.Max(rc * q, 1e-5f), -1.0f, 1.0f);
                float eqs = wa.Y < 0.0f ? 1.0f : -1.0f;
                float beltw = Glsl.SmoothStep(0.15f, 0.7f, m * eqs);
                float zonew = Glsl.SmoothStep(0.15f, 0.7f, -m * eqs);
                float az = q > 0.05f ? MathF.Atan2(yn, dlon) : 0.0f;
                V3 fph = noiseOff * 23.1f;
                float twr = 0.0f;
                if (shape > 0.0f || taper > 0.0f)
                {
                    float rr = 1.0f;
                    if (shape > 0.0f)
                    {
                        float neq = MathF.Max(m * eqs, 0.0f);
                        V3 sph = c.Static.HeroShapePhase;
                        rr -= shape * emergence * (0.11f * neq * neq - 0.075f * MathF.Sin(2.0f * az + sph.X) - 0.055f * MathF.Sin(3.0f * az + sph.Y));
                    }
                    if (taper > 0.0f)
                    {
                        float uct = Glsl.Clamp(-xe / MathF.Max(rc * q, 1e-5f), -1.0f, 1.0f);
                        float tc = MathF.Max(uct, 0.0f), tc2 = tc * tc;
                        float tw = 6.75f * tc2 * tc2 * (1.0f - tc2);
                        rr -= 0.25f * taper * emergence * tw;
                        rr = MathF.Max(rr, 0.4f);
                        twr = MathF.Min(taper, 1.0f) * tw;
                    }
                    q /= rr;
                }

                float rl = 0.55f * MathF.Sin(2.0f * az + fph.Z) + 0.45f * MathF.Sin(3.0f * az + fph.X + 1.9f);
                float rl2 = 0.6f * MathF.Sin(2.0f * az + fph.Y + 0.8f) + 0.4f * MathF.Sin(4.0f * az + fph.Z + 2.4f);
                float rq = 0.95f - 0.10f * emergence * MathF.Max(rl, 0.0f);
                float rk = 10.0f * (1.0f + 0.4f * emergence * rl2);
                float rimBump = MathF.Exp(-(q - rq) * (q - rq) * rk);
                float brk = Glsl.SmoothStep(0.5f, 0.9f, MathF.Sin(az + fph.Y * 1.3f));
                rimBump = MathF.Max(rimBump, brk * MathF.Exp(-(q - 1.14f) * (q - 1.14f) * 14.0f));
                if (q < 2.2f)
                {
                    float fscale = rc > 0.0f ? 9.0f / rc : 9.0f;
                    float ero = Glsl.Clamp(0.15f + 1.4f * Noise3D.Fbm(p * fscale + noiseOff.ZYX + new V3(5.0f, 5.0f, 5.0f), 4, 2.0f, 0.5f), 0.0f, 1.4f);
                    ero = Glsl.Clamp(ero * (0.95f + 0.30f * emergence * rl2), 0.0f, 1.4f);
                    ero *= 1.0f - 0.7f * emergence * twr;
                    ero *= 1.0f - 0.65f * upw;
                    float cand = rimBump * ero;
                    if (emergence * cand > inflE * infl || (emergence * cand == inflE * infl && cand > infl)) { infl = cand; inflE = emergence; }
                }

                float fl = 0.6f * MathF.Sin(2.0f * az + fph.X) + 0.4f * MathF.Sin(3.0f * az + fph.Y);
                float qin = 1.55f + emergence * (-0.40f * beltw + 0.12f * zonew + 0.08f * fl * (1.0f - beltw));
                float rise2 = Glsl.Mix(0.35f, 0.20f, beltw);
                float shaped = Glsl.SmoothStep(qin, qin + rise2, q) * (1.0f - Glsl.SmoothStep(2.7f, 3.4f, q));
                float floorf = Glsl.SmoothStep(2.05f, 2.35f, q) * (1.0f - Glsl.SmoothStep(2.7f, 3.4f, q));
                float fcand = MathF.Max(shaped, floorf);
                if (emergence * fcand > flushE * flush || (emergence * fcand == flushE * flush && fcand > flush)) { flush = fcand; flushE = emergence; }
                float tcand = twr * Glsl.SmoothStep(1.02f, 1.25f, q) * (1.0f - Glsl.SmoothStep(2.7f, 3.4f, q));
                if (emergence * tcand > flushE * flush || (emergence * tcand == flushE * flush && tcand > flush)) { flush = tcand; flushE = emergence; }
            }

            float rcand = 0.75f * wrel;
            if (wrelE * rcand > inflE * infl || (wrelE * rcand == inflE * infl && rcand > infl)) { infl = rcand; inflE = wrelE; }
            flush *= 1.0f - wrel;
            return Glsl.Clamp(1.0f - inflE * infl, 0.0f, 1.0f) + 11.0f * flushE * flush;
        }

        public static float HeroBandDeflect(V3 p, float lat, VortexStampContext c)
        {
            if (!c.HeroEmergence) return lat;
            float latS = lat;
            ParamTree prm = c.Params;
            V3 noiseOff = c.Static.HeroNoiseOffset;
            for (int i = 0; i < c.Count; ++i)
            {
                V4 b = c.VortexData[3 * i + 1];
                if (b.Y != Hero) continue;
                V4 meta = c.VortexData[3 * i + 2];
                float gate = meta.W;
                if (gate <= 0.0f) continue;
                float emergence = prm.Float("storms.hero_emergence");
                float taper = prm.Float("storms.hero_taper");
                if (c.CastLevers)
                {
                    V4 cl2 = c.CastLeverData[3 * i + 2];
                    emergence = cl2.X; taper = cl2.Z;
                }
                float q = HeroEllipQ(p, i, 2.3f, c);
                if (q > 2.3f) continue;
                V4 a = c.VortexData[3 * i];
                float vlat = MathF.Asin(Glsl.Clamp(a.Y, -1.0f, 1.0f));
                float wdir = meta.X;
                float plat = MathF.Asin(Glsl.Clamp(p.Y, -1.0f, 1.0f));
                float plon = MathF.Atan2(p.Z, p.X);
                float vlon = MathF.Atan2(a.Z, a.X);
                float dlon = Glsl.Mod(plon - vlon + 3.0f * Pi, 2.0f * Pi) - Pi;
                float asp = meta.Y;
                float xe = dlon * wdir / MathF.Max(asp, 1.0f);
                float yn = plat - vlat;
                float az = q > 0.05f ? MathF.Atan2(yn, xe) : 0.0f;
                float eqs2 = a.Y < 0.0f ? 1.0f : -1.0f;
                float mm = Glsl.Clamp(yn / MathF.Max(a.W * q, 1e-5f), -1.0f, 1.0f);
                float beltw2 = Glsl.SmoothStep(0.15f, 0.7f, mm * eqs2);
                float ob1 = Glsl.Mix(1.45f, 1.25f, beltw2);
                float ob2 = Glsl.Mix(2.0f, 1.6f, beltw2);
                if (taper > 0.0f)
                {
                    float uct = Glsl.Clamp(-xe / MathF.Max(a.W * q, 1e-5f), -1.0f, 1.0f);
                    float tc = MathF.Max(uct, 0.0f), tc2 = tc * tc;
                    float tw = 6.75f * tc2 * tc2 * (1.0f - tc2);
                    float hold = 0.35f * MathF.Min(taper, 1.0f) * emergence * tw;
                    ob1 *= 1.0f - hold;
                    ob2 *= 1.0f - hold;
                }
                float bw = Glsl.SmoothStep(0.8f, 1.2f, q) * (1.0f - Glsl.SmoothStep(ob1, ob2, q));
                {
                    float flank = MathF.Abs(MathF.Cos(az));
                    float downw = Glsl.SmoothStep(0.2f, 0.9f, xe / MathF.Max(Glsl.Length(new V2(xe, yn)), 1e-5f));
                    V3 bph = noiseOff * 11.7f;
                    float lobes = 0.5f + 0.5f * (0.6f * MathF.Sin(2.0f * az + bph.X) + 0.4f * MathF.Sin(3.0f * az + bph.Y));
                    bw *= 1.0f - flank * (0.6f * downw + 0.35f * lobes * (1.0f - downw));
                }
                float pull = emergence * 0.75f * gate * bw * (lat - vlat);
                float cap = 1.1f * a.W;
                latS -= Glsl.Clamp(pull, -cap, cap);
            }
            return latS;
        }

        private static float HeroEllipQ(V3 p, int i, float qmax, VortexStampContext c)
        {
            V4 a = c.VortexData[3 * i];
            float asp = c.VortexData[3 * i + 2].Y;
            float cd = Glsl.Dot(p, a.XYZ);
            float s2 = 1.0f - cd * cd;
            float lim = qmax * MathF.Max(asp, 1.0f) * a.W;
            if (s2 > lim * lim) return 1e3f;
            if (asp == 1.0f) return MathF.Acos(Glsl.Clamp(cd, -1.0f, 1.0f)) / a.W;
            V3 cc = a.XYZ;
            V3 ew = Glsl.Cross(new V3(0.0f, 1.0f, 0.0f), cc);
            float ewl = Glsl.Length(ew);
            if (ewl < 1e-4f) return MathF.Acos(Glsl.Clamp(cd, -1.0f, 1.0f)) / a.W;
            V3 e1 = ew / ewl;
            V3 e2 = Glsl.Cross(cc, e1);
            return cd > 0.0f ? Glsl.Length(new V2(Glsl.Dot(p, e1) / asp, Glsl.Dot(p, e2))) / a.W : 1e3f;
        }
    }
}
