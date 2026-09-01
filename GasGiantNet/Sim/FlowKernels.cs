using System;
using GasGiantNet.Config;
using GasGiantNet.MathCore;

namespace GasGiantNet.Sim
{
    internal sealed class FlowContext
    {
        public ParamTree Params;
        public LatLut ProfileDyn;
        public LatLut ProfileStamp;
        public SimStaticUniforms Static;
        public VortexRegistry Vortices;
        public float FestoonLat;
        public float RibbonLat;
        public float PolyAmp;
        public float PolyK;
        public float PolyRho;
        public float PolyEps;
        public float PolyWidth;
        public float TurbTime;
        public bool HeroEmergence;
        public bool CastLevers;
        public float[] CastLeverData;
        public OutflowImpulse[] Outbreaks;
    }

    internal static class FlowKernels
    {
        public static void BuildPsi(SimDomain d, FlowContext c, int threads)
        {
            BuildPsiInto(d, c, d.Psi, threads);
        }

        public static void BuildPsiInto(SimDomain d, FlowContext c, FloatTexture target, int threads)
        {
            // Parameters are immutable for this kernel invocation. Resolve them
            // once rather than traversing ParamTree for every output pixel.
            float warpFreq=c.Params.Float("bands.warp_freq");
            float warpAmount=c.Params.Float("bands.warp_amount");
            float heroEmergence=c.Params.Float("storms.hero_emergence");
            float khAmplitude=c.Params.Float("turbulence.kh_amplitude");
            float khWavenumber=c.Params.Float("turbulence.kh_wavenumber");
            float festoonStrength=c.Params.Float("waves.festoon_strength");
            float festoonWavenumber=c.Params.Float("waves.festoon_wavenumber");
            float ribbonStrength=c.Params.Float("waves.ribbon_strength");
            float ribbonWavenumber=c.Params.Float("waves.ribbon_wavenumber");
            float turbulenceIntensity=c.Params.Float("turbulence.intensity");
            float shearCoupling=c.Params.Float("turbulence.shear_coupling");
            float beltBoost=c.Params.Float("turbulence.belt_boost");
            float wakeTurbulence=c.Params.Float("storms.wake_turbulence");
            float turbulenceScale=c.Params.Float("turbulence.scale");
            int w = d.Width, h = d.Height;
            CpuParallel.ForRows(h, threads, delegate(int y)
            {
                for (int x = 0; x < w; x++)
                {
                    V2 ll = DomainMath.LonLatAt(d.Kind, x, y, w, h, d.RhoMax);
                    V3 sp = DomainMath.SpherePoint(ll);
                    float warp = Noise3D.Fbm(sp * warpFreq + c.Static.WarpOffset, 4, 2.0f, 0.5f) * warpAmount;
                    V4 prof = c.ProfileDyn.Sample(DomainMath.LatProfileU(ll.Y + warp));
                    float psi = prof.Y;
                    float shear = prof.Z;
                    float belt = prof.W;
                    float wake = 0.0f;

                    for (int i = 0; i < c.Vortices.Vortices.Count; i++)
                    {
                        Vortex vx = c.Vortices.Vortices[i];
                        V3 center = SpherePoint(vx.Lat, vx.Lon);
                        float dist = MathF.Acos(Glsl.Clamp(Glsl.Dot(sp, center), -1.0f, 1.0f));
                        float q = VortexQ(sp, center, (float)vx.CoreRadius, (float)vx.Aspect, dist, false);
                        if (q < 4.0f) psi += (float)vx.Strength * MathF.Exp(-q * q);
                        if (vx.Kind == VortexKinds.Hero)
                        {
                            float down = (float)vx.WakeDir;
                            float rc = (float)vx.CoreRadius;
                            float vlat = (float)vx.Lat;
                            float dlon = WrapPiF(ll.X - (float)vx.Lon);
                            float along = dlon * down;
                            float across = (ll.Y - (vlat + (float)vx.WakeLatOff)) / MathF.Max(rc * 1.6f, 1e-4f);
                            float emergence = c.HeroEmergence ? EffectiveLever(c, i, 8, heroEmergence) : 0.0f;
                            float len = c.HeroEmergence ? Glsl.Mix(7.0f, 10.0f, emergence) : 7.0f;
                            if (along > 0.0f && along < rc * len && MathF.Abs(across) < 2.5f)
                            {
                                float ramp = Glsl.SmoothStep(rc * 0.5f * (float)vx.Aspect, rc * (float)vx.Aspect, along);
                                float win = 1.0f - Glsl.SmoothStep(2.0f, 2.5f, MathF.Abs(across));
                                wake += MathF.Exp(-across * across) * win * (1.0f - along / (rc * len)) * ramp;
                            }
                        }
                    }

                    if (d.Kind == DomainKind.Equirect)
                    {
                        psi += khAmplitude * 0.004f * shear * shear
                             * MathF.Sin(khWavenumber * ll.X + c.Static.KhPhase);
                        float fest = festoonStrength;
                        if (fest > 0.0f)
                            psi += fest * 0.0045f * MathF.Exp(-Sq((ll.Y - c.FestoonLat) / 0.05f))
                                 * MathF.Sin(festoonWavenumber * ll.X + c.Static.FestPhase);
                        float rib = ribbonStrength;
                        if (rib > 0.0f)
                            psi += rib * 0.005f * MathF.Exp(-Sq((ll.Y - c.RibbonLat) / 0.03f))
                                   * MathF.Sin(ribbonWavenumber * ll.X + c.Static.RibPhase);
                    }
                    else if (c.PolyAmp > 0.0f)
                    {
                        float rho = 0.5f * DomainMath.Pi - MathF.Abs(ll.Y);
                        float rho0 = c.PolyRho * (1.0f + c.PolyEps * MathF.Cos(c.PolyK * ll.X + c.Static.PolyPhase));
                        float dr = (rho - rho0) / MathF.Max(c.PolyWidth, 1e-4f);
                        psi += c.PolyAmp * MathF.Exp(-dr * dr);
                    }

                    float amp = turbulenceIntensity
                              * (1.0f + shearCoupling * shear)
                              * (1.0f + (beltBoost - 1.0f) * belt)
                              * (1.0f + wakeTurbulence * wake);
                    V3 tp = sp * turbulenceScale + c.Static.TurbOffset + new V3(0.0f, 0.0f, c.TurbTime);
                    psi += 0.0035f * amp * Noise3D.Fbm(tp, 5, 2.0f, 0.55f);
                    target.Set(x, y, 0, psi);
                }
            });
        }

        public static void BuildVelocity(SimDomain d, FlowContext c, int threads)
        {
            int w = d.Width, h = d.Height;
            CpuParallel.ForRows(h, threads, delegate(int y)
            {
                for (int x = 0; x < w; x++)
                {
                    V2 ll = DomainMath.LonLatAt(d.Kind, x, y, w, h, d.RhoMax);
                    float u, v;
                    if (d.Kind == DomainKind.Equirect)
                    {
                        float dlat = DomainMath.Pi / h;
                        float dlonStep = 2.0f * DomainMath.Pi / w;
                        float dphi = (PsiAt(d, x, y - 1) - PsiAt(d, x, y + 1)) / (2.0f * dlat);
                        float dlam = (PsiAt(d, x + 1, y) - PsiAt(d, x - 1, y)) / (2.0f * dlonStep);
                        float cosl = MathF.Max(MathF.Cos(ll.Y), 0.017f);
                        u = -dphi;
                        v = dlam / cosl;
                        float fade = c.ProfileStamp.Sample(DomainMath.LatProfileU(ll.Y)).Z;
                        u *= fade; v *= fade;
                        if (c.Outbreaks != null)
                        {
                            int n = Math.Min(2, c.Outbreaks.Length);
                            for (int i = 0; i < n; i++)
                            {
                                OutflowImpulse ob = c.Outbreaks[i];
                                float dl = WrapPiF(ll.X - (float)ob.Lon);
                                V2 dxy = new V2(dl * cosl, ll.Y - (float)ob.Lat);
                                float r = Glsl.Length(dxy);
                                if (r > 1e-5f && r < (float)ob.Radius * 3.0f)
                                {
                                    float q = r / (float)ob.Radius;
                                    float kick = (float)ob.Strength * q * MathF.Exp(-q * q);
                                    u += kick * dxy.X / r;
                                    v += kick * dxy.Y / r;
                                }
                            }
                        }
                    }
                    else
                    {
                        float dsStep = 2.0f * d.RhoMax / w;
                        V2 ds = new V2(
                            (PsiAt(d, x + 1, y) - PsiAt(d, x - 1, y)) / (2.0f * dsStep),
                            (PsiAt(d, x, y + 1) - PsiAt(d, x, y - 1)) / (2.0f * dsStep));
                        V2 st = DomainMath.PatchStFromPix(new V2(x + 0.5f, y + 0.5f), w, h, d.RhoMax);
                        float rho = Glsl.Length(st);
                        u = 0.0f; v = 0.0f;
                        if (rho > 1e-4f)
                        {
                            V2 er = st / rho;
                            V2 et = new V2(-er.Y, er.X);
                            float dpsiRho = Glsl.Dot(ds, er);
                            float dpsiTheta = rho * Glsl.Dot(ds, et);
                            float poleSign = d.Kind == DomainKind.NorthPatch ? 1.0f : -1.0f;
                            u = poleSign * dpsiRho;
                            v = dpsiTheta / MathF.Max(MathF.Sin(rho), 1e-4f);
                        }
                    }
                    d.Velocity.Set2(x, y, new V2(u, v));
                }
            });
        }

        internal static float VortexQ(V3 p, V3 c, float rc, float aspect, float greatCircleD, bool nearHemisphereGate)
        {
            if (aspect == 1.0f) return greatCircleD / rc;
            V3 ew = Glsl.Cross(new V3(0.0f, 1.0f, 0.0f), c);
            float ewl = Glsl.Length(ew);
            if (ewl < 1e-4f) return greatCircleD / rc;
            if (nearHemisphereGate && Glsl.Dot(p, c) <= 0.0f) return 1000.0f;
            V3 e1 = ew / ewl;
            V3 e2 = Glsl.Cross(c, e1);
            return Glsl.Length(new V2(Glsl.Dot(p, e1) / aspect, Glsl.Dot(p, e2))) / rc;
        }

        internal static V3 SpherePoint(double lat, double lon)
        {
            float cl = (float)Math.Cos(lat);
            return new V3(cl * (float)Math.Cos(lon), (float)Math.Sin(lat), cl * (float)Math.Sin(lon));
        }

        private static float EffectiveLever(FlowContext c, int vortexIndex, int slot, float global)
        {
            if (!c.CastLevers || c.CastLeverData == null) return global;
            int o = vortexIndex * 12 + slot;
            return o >= 0 && o < c.CastLeverData.Length ? c.CastLeverData[o] : global;
        }

        private static float PsiAt(SimDomain d, int x, int y)
        {
            x = DomainMath.WrapX(d.Kind, x, d.Width);
            y = DomainMath.ClampY(y, d.Height);
            return d.Psi.Get(x, y, 0);
        }

        internal static float WrapPiF(float x)
        {
            float t = (x + 3.0f * DomainMath.Pi) % (2.0f * DomainMath.Pi);
            if (t < 0.0f) t += 2.0f * DomainMath.Pi;
            return t - DomainMath.Pi;
        }
        private static float Sq(float x) { return x * x; }
    }
}
