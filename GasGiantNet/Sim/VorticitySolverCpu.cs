using System;
using System.Collections.Generic;
using GasGiantNet.Config;
using GasGiantNet.MathCore;

namespace GasGiantNet.Sim
{
    // CPU translation of the upstream omega_*.comp + poisson_sor.comp +
    // psi_feather.comp vorticity path. q is ABSOLUTE vorticity; omega_rel=q-f.
    internal static class VorticitySolverCpu
    {
        private const float OmegaCeiling = 60.0f;
        private const float Deg = Glsl.PI / 180.0f;

        public static void Initialize(CpuSimulation sim, SimDomain d, int threads)
        {
            ParamTree p = sim.Params;
            VortexStampContext stamp = new VortexStampContext(p, sim.Static, sim.Vortices, sim.HeroEmergence, sim.CastLevers);
            VortexOmegaContext oc = new VortexOmegaContext(p, stamp, sim.HeroFlowRenorm);
            float f0 = p.Float("solver.coriolis_f0");
            int w = d.Width, h = d.Height;

            CpuParallel.ForRows(h, threads, delegate(int y)
            {
                for (int x = 0; x < w; x++)
                {
                    V2 ll = DomainMath.LonLatAt(d.Kind, x, y, w, h, d.RhoMax);
                    V3 sp = DomainMath.SpherePoint(ll);
                    float omegaJet = sim.ProfileOmega.Sample(DomainMath.LatProfileU(ll.Y)).X;
                    float omegaVort = VortexOmegaCpu.Accum(sp, oc);
                    float f = VortexOmegaCpu.Coriolis(ll.Y, f0);
                    float confine = Confine(d.Kind, ll.Y);
                    d.Omega.Set(x, y, 0, (omegaJet + omegaVort) * confine + f);
                }
            });
        }

        public static void ProducePsi(CpuSimulation sim, SimDomain d, FlowContext flow, float turbTime, int threads)
        {
            // Upstream ordering is intentionally preserved:
            // 1) advance q with PREVIOUS step velocity/psi
            // 2) recover omega_rel
            // 3) build analytic psi into a temporary
            // 4) warm-start from previous definitive psi
            // 5) red/black SOR
            // 6) feather solved psi with analytic psi
            AdvanceOmega(sim, d, flow, turbTime, threads);
            RecoverRelativeOmega(sim, d, threads);
            FlowKernels.BuildPsiInto(d, flow, d.PsiAnalytic, threads);
            d.PsiNext.CopyFrom(d.Psi); // psi_work warm-start
            SolvePoisson(sim, d, threads);
            FeatherPsi(d, threads);
        }

        private static void AdvanceOmega(CpuSimulation sim, SimDomain d, FlowContext flow, float turbTime, int threads)
        {
            float dt = (float)sim.Dt;
            OmegaAdvect(d, d.Omega, d.OmegaFwd, +dt, threads);
            OmegaAdvect(d, d.OmegaFwd, d.OmegaBack, -dt, threads);
            OmegaCorrect(d, +dt, threads);
            d.CommitOmega();

            ParamTree p = sim.Params;
            float[] qMean = null;
            float[] psiMean = null;
            float eddyDrag = (float)ResolutionScaling.ScaleDecayFraction(p.Double("solver.vort_eddy_drag"), sim.StepScale);
            float psiDrag = (float)ResolutionScaling.ScaleRate(p.Double("solver.vort_psi_drag"), sim.StepScale);
            if (d.Kind == DomainKind.Equirect && eddyDrag > 0.0f) qMean = ZonalMean(d.Omega);
            if (d.Kind == DomainKind.Equirect && psiDrag > 0.0f) psiMean = ZonalMean(d.Psi);

            ForceNudge(sim, d, flow, turbTime, qMean, psiMean, threads);
            d.CommitOmega();
            ComputeRelativeLaplacian(sim, d, threads);
            ForceHyperviscosity(sim, d, threads);
            d.CommitOmega();
        }

        private static void OmegaAdvect(SimDomain d, FloatTexture src, FloatTexture dst, float dt, int threads)
        {
            int w = d.Width, h = d.Height;
            CpuParallel.ForRows(h, threads, delegate(int y)
            {
                for (int x = 0; x < w; x++)
                {
                    V2 pix = new V2(x + 0.5f, y + 0.5f);
                    V2 source = Backtrace(d, pix, dt);
                    dst.Set(x, y, 0, src.SampleCatmullRomPixel(source).X);
                }
            });
        }

        private static void OmegaCorrect(SimDomain d, float dt, int threads)
        {
            int w = d.Width, h = d.Height;
            CpuParallel.ForRows(h, threads, delegate(int y)
            {
                for (int x = 0; x < w; x++)
                {
                    V2 pix = new V2(x + 0.5f, y + 0.5f);
                    V2 source = Backtrace(d, pix, dt);
                    float fwd = d.OmegaFwd.Get(x, y, 0);
                    float cur = d.Omega.Get(x, y, 0);
                    float back = d.OmegaBack.Get(x, y, 0);
                    float result = fwd + 0.5f * (cur - back);
                    float lo, hi;
                    MinMax2x2Scalar(d.Omega, source, out lo, out hi);
                    if (result < lo || result > hi) result = fwd;
                    result = Glsl.Clamp(result, lo, hi);
                    d.OmegaOut.Set(x, y, 0, result);
                }
            });
        }

        private static void ForceNudge(CpuSimulation sim, SimDomain d, FlowContext flow, float turbTime,
            float[] qMean, float[] psiMean, int threads)
        {
            ParamTree p = sim.Params;
            int w = d.Width, h = d.Height;
            float f0 = p.Float("solver.coriolis_f0");
            float relaxTau = (float)ResolutionScaling.ScaleRelaxTau(p.Double("solver.vort_relax_tau"), sim.StepScale);
            float inject = (float)ResolutionScaling.ScaleStochasticAmp(p.Double("solver.vort_inject"), sim.StepScale);
            float injectFreq = p.Float("bands.detail_freq") * p.Float("solver.vort_inject_scale");
            int injectMask = InjectMaskCode(p.String("solver.vort_inject_mask"));
            float wakeTurb = (float)ResolutionScaling.ScaleStochasticAmp(p.Double("storms.wake_turbulence"), sim.StepScale);
            float wakeFreq = 0.9f / MathF.Max(p.Float("storms.hero_radius"), 0.01f);
            float rayleighDrag = (float)ResolutionScaling.ScaleDecayFraction(p.Double("solver.vort_drag"), sim.StepScale);
            float eddyDrag = (float)ResolutionScaling.ScaleDecayFraction(p.Double("solver.vort_eddy_drag"), sim.StepScale);
            float psiDrag = (float)ResolutionScaling.ScaleRate(p.Double("solver.vort_psi_drag"), sim.StepScale);
            float sceneEmergence = (float)sim.Vortices.SceneEmergence(p);

            VortexStampContext stamp = new VortexStampContext(p, sim.Static, sim.Vortices, sim.HeroEmergence, sim.CastLevers);
            VortexOmegaContext oc = new VortexOmegaContext(p, stamp, sim.HeroFlowRenorm);

            CpuParallel.ForRows(h, threads, delegate(int y)
            {
                for (int x = 0; x < w; x++)
                {
                    V2 ll = DomainMath.LonLatAt(d.Kind, x, y, w, h, d.RhoMax);
                    V3 sp = DomainMath.SpherePoint(ll);
                    float q = d.Omega.Get(x, y, 0);
                    float omegaJet = sim.ProfileOmega.Sample(DomainMath.LatProfileU(ll.Y)).X;
                    float omegaVort = VortexOmegaCpu.Accum(sp, oc);
                    float f = VortexOmegaCpu.Coriolis(ll.Y, f0);
                    float confine = Confine(d.Kind, ll.Y);
                    float target = (omegaJet + omegaVort) * confine + f;

                    if (relaxTau > 0.0f)
                    {
                        if (sim.HeroEmergence)
                        {
                            float boost = sim.CastLevers
                                ? VortexOmegaCpu.HeroAnchorBoost(sp, 60.0f, oc)
                                : 60.0f * sceneEmergence * VortexOmegaCpu.HeroAnchorWindow(sp, oc);
                            q += (target - q) * MathF.Min((1.0f + boost) / relaxTau, 0.5f);
                        }
                        else q += (target - q) / relaxTau;
                    }

                    q = f + (q - f) * Glsl.Mix(0.5f, 1.0f, confine);
                    q = Glsl.Clamp(q, -OmegaCeiling, OmegaCeiling);

                    if (inject > 0.0f)
                    {
                        float mask = 1.0f;
                        if (injectMask == 1) mask = sim.ProfileDyn.Sample(DomainMath.LatProfileU(ll.Y)).W;
                        else if (injectMask == 2) mask = sim.ProfileDyn.Sample(DomainMath.LatProfileU(ll.Y)).Z;
                        if (mask > 0.0f)
                        {
                            V3 np = sp * injectFreq + sim.Static.TurbOffset + new V3(0.0f, turbTime, 0.0f);
                            q += inject * mask * Noise3D.Fbm(np, 4, 2.0f, 0.5f);
                        }
                    }

                    if (sim.HeroEmergence && wakeTurb > 0.0f && sceneEmergence > 0.0f)
                    {
                        if (sim.CastLevers)
                        {
                            float amp = VortexOmegaCpu.HeroWakeInject(sp, 0.6f * wakeTurb, oc);
                            if (amp > 0.0f)
                            {
                                V3 np = sp * wakeFreq + sim.Static.TurbOffset.ZXY + new V3(turbTime, 0.0f, 0.0f);
                                q += amp * Noise3D.Fbm(np, 4, 2.0f, 0.5f);
                            }
                        }
                        else
                        {
                            float wm = VortexOmegaCpu.HeroWakeWindow(sp, oc);
                            if (wm > 0.0f)
                            {
                                V3 np = sp * wakeFreq + sim.Static.TurbOffset.ZXY + new V3(turbTime, 0.0f, 0.0f);
                                q += 0.6f * wakeTurb * sceneEmergence * wm * Noise3D.Fbm(np, 4, 2.0f, 0.5f);
                            }
                        }
                    }

                    if (rayleighDrag > 0.0f) q = f + (q - f) * (1.0f - rayleighDrag);
                    if (d.Kind == DomainKind.Equirect)
                    {
                        if (eddyDrag > 0.0f && qMean != null) q -= eddyDrag * (q - qMean[y]);
                        if (psiDrag > 0.0f && psiMean != null)
                        {
                            float psiEddy = d.Psi.Get(x, y, 0) - psiMean[y];
                            q += psiDrag * psiEddy;
                        }
                    }
                    d.OmegaOut.Set(x, y, 0, q);
                }
            });
        }

        private static void ComputeRelativeLaplacian(CpuSimulation sim, SimDomain d, int threads)
        {
            int w = d.Width, h = d.Height;
            float f0 = sim.Params.Float("solver.coriolis_f0");
            CpuParallel.ForRows(h, threads, delegate(int y)
            {
                for (int x = 0; x < w; x++)
                    d.OmegaLap.Set(x, y, 0, LaplacianRelativeOmega(d, x, y, f0));
            });
        }

        private static void ForceHyperviscosity(CpuSimulation sim, SimDomain d, int threads)
        {
            int w = d.Width, h = d.Height;
            float f0 = sim.Params.Float("solver.coriolis_f0");
            float hyper = sim.Params.Float("solver.vort_hypervisc");
            float dx4;
            if (d.Kind == DomainKind.Equirect)
            {
                float dphi = Glsl.PI / h;
                dx4 = dphi * dphi * dphi * dphi / 64.0f;
            }
            else
            {
                float ds = 2.0f * d.RhoMax / w;
                dx4 = ds * ds * ds * ds / 64.0f;
            }

            CpuParallel.ForRows(h, threads, delegate(int y)
            {
                for (int x = 0; x < w; x++)
                {
                    V2 ll = DomainMath.LonLatAt(d.Kind, x, y, w, h, d.RhoMax);
                    float q = d.Omega.Get(x, y, 0);
                    if (hyper > 0.0f)
                    {
                        float lap2 = Laplacian(d, d.OmegaLap, x, y);
                        q += hyper * (-lap2) * dx4;
                    }
                    float f = VortexOmegaCpu.Coriolis(ll.Y, f0);
                    q = f + (q - f) * Confine(d.Kind, ll.Y);
                    q = Glsl.Clamp(q, -OmegaCeiling, OmegaCeiling);
                    d.OmegaOut.Set(x, y, 0, q);
                }
            });
        }

        private static void RecoverRelativeOmega(CpuSimulation sim, SimDomain d, int threads)
        {
            int w = d.Width, h = d.Height;
            float f0 = sim.Params.Float("solver.coriolis_f0");
            FloatTexture ext = d.Kind == DomainKind.Equirect ? sim.ExternalOmega : null;
            float gain = d.Kind == DomainKind.Equirect ? sim.ExternalOmegaGain : 0.0f;
            CpuParallel.ForRows(h, threads, delegate(int y)
            {
                for (int x = 0; x < w; x++)
                {
                    V2 ll = DomainMath.LonLatAt(d.Kind, x, y, w, h, d.RhoMax);
                    float rel = d.Omega.Get(x, y, 0) - VortexOmegaCpu.Coriolis(ll.Y, f0);
                    if (gain != 0.0f && ext != null)
                    {
                        V2 uv = new V2((x + 0.5f) / w, (y + 0.5f) / h);
                        rel += gain * f0 * ext.SampleLinear1(uv);
                    }
                    d.OmegaRel.Set(x, y, 0, rel);
                }
            });
        }

        private static void SolvePoisson(CpuSimulation sim, SimDomain d, int threads)
        {
            ParamTree p = sim.Params;
            int iters = p.Int("solver.poisson_iters");
            float omega = p.Float("solver.sor_omega");
            float ld = p.Float("solver.deformation_radius");
            float invLd2 = ld > 0.0f ? 1.0f / (ld * ld) : 0.0f;

            for (int i = 0; i < iters; i++)
            {
                SorSweep(d, 0, omega, invLd2, threads);
                SorSweep(d, 1, omega, invLd2, threads);
            }
        }

        private static void SorSweep(SimDomain d, int color, float sorOmega, float invLd2, int threads)
        {
            // Equirect 5-point RB-SOR has no same-color dependencies and can be
            // parallelized exactly by row. The AE 9-point stencil reads corner
            // cells of the same color; OpenGL gives no cross-workgroup execution
            // order, so use a fixed CPU scan for deterministic behavior.
            if (d.Kind != DomainKind.Equirect)
            {
                for (int y = 0; y < d.Height; y++)
                    for (int x = 0; x < d.Width; x++)
                        if (((x + y) & 1) == color) SorCell(d, x, y, sorOmega, invLd2);
                return;
            }

            CpuParallel.ForRows(d.Height, threads, delegate(int y)
            {
                for (int x = 0; x < d.Width; x++)
                    if (((x + y) & 1) == color) SorCell(d, x, y, sorOmega, invLd2);
            });
        }

        private static void SorCell(SimDomain d, int x, int y, float sorOmega, float invLd2)
        {
            int w = d.Width, h = d.Height;
            float rhs = d.OmegaRel.Get(x, y, 0);
            float old = d.PsiNext.Get(x, y, 0);
            float centerCoeff;
            float rhsNbrs;

            if (d.Kind == DomainKind.Equirect)
            {
                float dlam = 2.0f * Glsl.PI / w;
                float dphi = Glsl.PI / h;
                float lat = 0.5f * Glsl.PI - (y + 0.5f) / h * Glsl.PI;
                float cosl = MathF.Max(MathF.Cos(lat), 0.017f);
                float tanl = MathF.Tan(lat);
                float wLam = 1.0f / (cosl * cosl) / (dlam * dlam);
                float wPhi = 1.0f / (dphi * dphi);
                float wTanHalf = tanl / (2.0f * dphi);
                centerCoeff = -2.0f * wLam - 2.0f * wPhi - invLd2;
                float xp = PsiWorkAt(d, x + 1, y);
                float xm = PsiWorkAt(d, x - 1, y);
                float yp = PsiWorkAt(d, x, y + 1);
                float ym = PsiWorkAt(d, x, y - 1);
                rhsNbrs = wLam * (xp + xm) + (wPhi + wTanHalf) * yp + (wPhi - wTanHalf) * ym;
            }
            else
            {
                float ds = 2.0f * d.RhoMax / w;
                V2 st = DomainMath.PatchStFromPix(new V2(x + 0.5f, y + 0.5f), w, h, d.RhoMax);
                float ss = st.X, tt = st.Y;
                float rho = MathF.Max(Glsl.Length(st), 1e-6f);
                float sinr = MathF.Max(MathF.Sin(rho), 1e-6f);
                float rho2 = rho * rho, sin2 = sinr * sinr;
                float cSs = ss * ss / rho2 + tt * tt / sin2;
                float cTt = tt * tt / rho2 + ss * ss / sin2;
                float cSt = 2.0f * ss * tt * (1.0f / rho2 - 1.0f / sin2);
                float cG = MathF.Cos(rho) / sinr - rho / sin2;
                centerCoeff = -2.0f * (cSs + cTt) / (ds * ds) - invLd2;

                float xp = PsiWorkAt(d, x + 1, y);
                float xm = PsiWorkAt(d, x - 1, y);
                float yp = PsiWorkAt(d, x, y + 1);
                float ym = PsiWorkAt(d, x, y - 1);
                float xpyp = PsiWorkAt(d, x + 1, y + 1);
                float xpym = PsiWorkAt(d, x + 1, y - 1);
                float xmyp = PsiWorkAt(d, x - 1, y + 1);
                float xmym = PsiWorkAt(d, x - 1, y - 1);
                float psiSt = (xpyp - xpym - xmyp + xmym) / (4.0f * ds * ds);
                float psiS = (xp - xm) / (2.0f * ds);
                float psiT = (yp - ym) / (2.0f * ds);
                float psiRhoTerm = cG * (ss * psiS + tt * psiT) / rho;
                rhsNbrs = cSs * (xp + xm) / (ds * ds) + cTt * (yp + ym) / (ds * ds) + cSt * psiSt + psiRhoTerm;
            }

            float gs = (rhs - rhsNbrs) / centerCoeff;
            float next = (1.0f - sorOmega) * old + sorOmega * gs;
            d.PsiNext.Set(x, y, 0, next);
        }

        private static void FeatherPsi(SimDomain d, int threads)
        {
            int w = d.Width, h = d.Height;
            CpuParallel.ForRows(h, threads, delegate(int y)
            {
                for (int x = 0; x < w; x++)
                {
                    V2 ll = DomainMath.LonLatAt(d.Kind, x, y, w, h, d.RhoMax);
                    float absLat = MathF.Abs(ll.Y);
                    float alpha = d.Kind == DomainKind.Equirect
                        ? Glsl.SmoothStep(60.0f * Deg, 64.0f * Deg, absLat)
                        : 1.0f - Glsl.SmoothStep(60.0f * Deg, 67.0f * Deg, absLat);
                    float solved = d.PsiNext.Get(x, y, 0);
                    float analytic = d.PsiAnalytic.Get(x, y, 0);
                    d.Psi.Set(x, y, 0, Glsl.Mix(solved, analytic, alpha));
                }
            });
        }

        private static float LaplacianRelativeOmega(SimDomain d, int x, int y, float f0)
        {
            if (d.Kind == DomainKind.Equirect)
            {
                int w = d.Width, h = d.Height;
                float dlam = 2.0f * Glsl.PI / w;
                float dphi = Glsl.PI / h;
                float lat = 0.5f * Glsl.PI - (y + 0.5f) / h * Glsl.PI;
                float cosl = MathF.Max(MathF.Cos(lat), 0.017f);
                float tanl = MathF.Tan(lat);
                float c = RelativeAt(d, x, y, f0);
                float xp = RelativeAt(d, x + 1, y, f0);
                float xm = RelativeAt(d, x - 1, y, f0);
                float yp = RelativeAt(d, x, y + 1, f0);
                float ym = RelativeAt(d, x, y - 1, f0);
                float d2lam = (xp - 2.0f * c + xm) / (dlam * dlam);
                float d2phi = (yp - 2.0f * c + ym) / (dphi * dphi);
                float d1desc = (yp - ym) / (2.0f * dphi);
                return d2lam / (cosl * cosl) + d2phi + tanl * d1desc;
            }

            int pw = d.Width, ph = d.Height;
            float ds = 2.0f * d.RhoMax / pw;
            V2 st = DomainMath.PatchStFromPix(new V2(x + 0.5f, y + 0.5f), pw, ph, d.RhoMax);
            float ss = st.X, tt = st.Y;
            float rho = MathF.Max(Glsl.Length(st), 1e-6f);
            float sinr = MathF.Max(MathF.Sin(rho), 1e-6f);
            float rho2 = rho * rho, sin2 = sinr * sinr;
            float cSs = ss * ss / rho2 + tt * tt / sin2;
            float cTt = tt * tt / rho2 + ss * ss / sin2;
            float cSt = 2.0f * ss * tt * (1.0f / rho2 - 1.0f / sin2);
            float cG = MathF.Cos(rho) / sinr - rho / sin2;
            float c0 = RelativeAt(d, x, y, f0);
            float xp0 = RelativeAt(d, x + 1, y, f0);
            float xm0 = RelativeAt(d, x - 1, y, f0);
            float yp0 = RelativeAt(d, x, y + 1, f0);
            float ym0 = RelativeAt(d, x, y - 1, f0);
            float xpyp = RelativeAt(d, x + 1, y + 1, f0);
            float xpym = RelativeAt(d, x + 1, y - 1, f0);
            float xmyp = RelativeAt(d, x - 1, y + 1, f0);
            float xmym = RelativeAt(d, x - 1, y - 1, f0);
            float ps = (xp0 - xm0) / (2.0f * ds);
            float pt = (yp0 - ym0) / (2.0f * ds);
            float pss = (xp0 - 2.0f * c0 + xm0) / (ds * ds);
            float ptt = (yp0 - 2.0f * c0 + ym0) / (ds * ds);
            float pst = (xpyp - xpym - xmyp + xmym) / (4.0f * ds * ds);
            float pr = (ss * ps + tt * pt) / rho;
            return cSs * pss + cTt * ptt + cSt * pst + cG * pr;
        }

        internal static float Laplacian(SimDomain d, FloatTexture field, int x, int y)
        {
            if (d.Kind == DomainKind.Equirect)
            {
                int w = d.Width, h = d.Height;
                float dlam = 2.0f * Glsl.PI / w;
                float dphi = Glsl.PI / h;
                float lat = 0.5f * Glsl.PI - (y + 0.5f) / h * Glsl.PI;
                float cosl = MathF.Max(MathF.Cos(lat), 0.017f);
                float tanl = MathF.Tan(lat);
                float c = ScalarAt(d, field, x, y);
                float xp = ScalarAt(d, field, x + 1, y);
                float xm = ScalarAt(d, field, x - 1, y);
                float yp = ScalarAt(d, field, x, y + 1);
                float ym = ScalarAt(d, field, x, y - 1);
                return (xp - 2.0f * c + xm) / (dlam * dlam) / (cosl * cosl)
                     + (yp - 2.0f * c + ym) / (dphi * dphi)
                     + tanl * (yp - ym) / (2.0f * dphi);
            }

            int pw = d.Width, ph = d.Height;
            float ds = 2.0f * d.RhoMax / pw;
            V2 st = DomainMath.PatchStFromPix(new V2(x + 0.5f, y + 0.5f), pw, ph, d.RhoMax);
            float ss = st.X, tt = st.Y;
            float rho = MathF.Max(Glsl.Length(st), 1e-6f);
            float sinr = MathF.Max(MathF.Sin(rho), 1e-6f);
            float rho2 = rho * rho, sin2 = sinr * sinr;
            float cSs = ss * ss / rho2 + tt * tt / sin2;
            float cTt = tt * tt / rho2 + ss * ss / sin2;
            float cSt = 2.0f * ss * tt * (1.0f / rho2 - 1.0f / sin2);
            float cG = MathF.Cos(rho) / sinr - rho / sin2;
            float pc = ScalarAt(d, field, x, y);
            float pxp = ScalarAt(d, field, x + 1, y);
            float pxm = ScalarAt(d, field, x - 1, y);
            float pyp = ScalarAt(d, field, x, y + 1);
            float pym = ScalarAt(d, field, x, y - 1);
            float xpyp = ScalarAt(d, field, x + 1, y + 1);
            float xpym = ScalarAt(d, field, x + 1, y - 1);
            float xmyp = ScalarAt(d, field, x - 1, y + 1);
            float xmym = ScalarAt(d, field, x - 1, y - 1);
            float ps = (pxp - pxm) / (2.0f * ds);
            float pt = (pyp - pym) / (2.0f * ds);
            float pss = (pxp - 2.0f * pc + pxm) / (ds * ds);
            float ptt = (pyp - 2.0f * pc + pym) / (ds * ds);
            float pst = (xpyp - xpym - xmyp + xmym) / (4.0f * ds * ds);
            float pr = (ss * ps + tt * pt) / rho;
            return cSs * pss + cTt * ptt + cSt * pst + cG * pr;
        }

        private static float RelativeAt(SimDomain d, int x, int y, float f0)
        {
            x = DomainMath.WrapX(d.Kind, x, d.Width);
            y = DomainMath.ClampY(y, d.Height);
            V2 ll = DomainMath.LonLatAt(d.Kind, x, y, d.Width, d.Height, d.RhoMax);
            return d.Omega.Get(x, y, 0) - VortexOmegaCpu.Coriolis(ll.Y, f0);
        }

        private static float ScalarAt(SimDomain d, FloatTexture field, int x, int y)
        {
            x = DomainMath.WrapX(d.Kind, x, d.Width);
            y = DomainMath.ClampY(y, d.Height);
            return field.Get(x, y, 0);
        }

        private static float PsiWorkAt(SimDomain d, int x, int y)
        {
            x = DomainMath.WrapX(d.Kind, x, d.Width);
            y = DomainMath.ClampY(y, d.Height);
            return d.PsiNext.Get(x, y, 0);
        }

        private static V2 Backtrace(SimDomain d, V2 pixPos, float dt)
        {
            int w = d.Width, h = d.Height;
            V2 uvScale = new V2(1.0f / w, 1.0f / h);
            if (d.Kind == DomainKind.Equirect)
            {
                V2 ll = new V2((pixPos.X / w) * 2.0f * Glsl.PI - Glsl.PI,
                               0.5f * Glsl.PI - (pixPos.Y / h) * Glsl.PI);
                V2 vel = d.Velocity.SampleLinear2(pixPos * uvScale);
                float cosl = MathF.Max(MathF.Cos(ll.Y), 0.017f);
                V2 mid = ll + new V2(-0.5f * dt * vel.X / cosl, -0.5f * dt * vel.Y);
                V2 midPix = new V2((mid.X + Glsl.PI) / (2.0f * Glsl.PI) * w,
                                   (0.5f * Glsl.PI - mid.Y) / Glsl.PI * h);
                V2 velMid = d.Velocity.SampleLinear2(midPix * uvScale);
                float cosMid = MathF.Max(MathF.Cos(mid.Y), 0.017f);
                V2 dest = ll + new V2(-dt * velMid.X / cosMid, -dt * velMid.Y);
                return new V2((dest.X + Glsl.PI) / (2.0f * Glsl.PI) * w,
                              (0.5f * Glsl.PI - dest.Y) / Glsl.PI * h);
            }
            V2 st = DomainMath.PatchStFromPix(pixPos, w, h, d.RhoMax);
            V2 vel0 = d.Velocity.SampleLinear2(pixPos * uvScale);
            V2 stMid = st - DomainMath.PatchVelocity(d.Kind, st, vel0) * (0.5f * dt);
            V2 patchMidPix = DomainMath.PatchPixFromSt(stMid, w, h, d.RhoMax);
            V2 patchVelMid = d.Velocity.SampleLinear2(patchMidPix * uvScale);
            V2 stDest = st - DomainMath.PatchVelocity(d.Kind, stMid, patchVelMid) * dt;
            return DomainMath.PatchPixFromSt(stDest, w, h, d.RhoMax);
        }

        private static void MinMax2x2Scalar(FloatTexture tex, V2 pos, out float lo, out float hi)
        {
            int bx = (int)MathF.Floor(pos.X - 0.5f);
            int by = (int)MathF.Floor(pos.Y - 0.5f);
            lo = float.PositiveInfinity;
            hi = float.NegativeInfinity;
            for (int j = 0; j < 2; j++)
                for (int i = 0; i < 2; i++)
                {
                    float q = tex.TexelFetch1(bx + i, by + j);
                    if (q < lo) lo = q;
                    if (q > hi) hi = q;
                }
        }

        private static float[] ZonalMean(FloatTexture tex)
        {
            float[] means = new float[tex.Height];
            for (int y = 0; y < tex.Height; y++)
            {
                float acc = 0.0f;
                for (int x = 0; x < tex.Width; x++) acc += tex.Get(x, y, 0);
                means[y] = acc / tex.Width;
            }
            return means;
        }

        private static int InjectMaskCode(string name)
        {
            if (name == "belts") return 1;
            if (name == "shear") return 2;
            return 0;
        }

        private static float Confine(DomainKind kind, float lat)
        {
            float a = MathF.Abs(lat);
            return kind == DomainKind.Equirect
                ? 1.0f - Glsl.SmoothStep(60.0f * Deg, 64.0f * Deg, a)
                : Glsl.SmoothStep(60.0f * Deg, 67.0f * Deg, a);
        }
    }

    internal static class HeroFlowRenormCpu
    {
        private const double SupportQ = 2.4;

        public static float Compute(ParamTree p, VortexRegistry registry)
        {
            double k = p.Double("storms.hero_flow_aspect");
            if (k == 1.0) return 1.0f;
            List<Vortex> heroes = registry.Heroes();
            if (heroes.Count == 0) return 1.0f;

            bool ringBranch = false;
            for (int i = 0; i < heroes.Count; i++)
            {
                Vortex h = heroes[i];
                if (Vortices.EffectiveCastLever(p, h.CastRef, "solid_core") > 0.0 &&
                    Vortices.EffectiveCastLever(p, h.CastRef, "emergence") > 0.0)
                { ringBranch = true; break; }
            }
            if (!ringBranch) return 1.0f;

            double rc = 0.0, asp = 0.0;
            for (int i = 0; i < heroes.Count; i++) { rc += heroes[i].CoreRadius; asp += heroes[i].Aspect; }
            rc /= heroes.Count; asp /= heroes.Count;
            return (float)ComputeOne(rc, asp, k, 1601);
        }

        private static double ComputeOne(double rCore, double aspect, double flowAspect, int n)
        {
            if (flowAspect == 1.0) return 1.0;
            double net1 = Net(rCore, aspect, 1.0, n);
            double netk = Net(rCore, aspect, flowAspect, n);
            double fallback = 1.0 / flowAspect;
            if (netk == 0.0) return fallback;
            double raw = net1 / netk;
            if (!(0.95 * fallback <= raw && raw <= 1.0)) return fallback;
            return raw;
        }

        private static double Net(double rCore, double aspect, double k, int n)
        {
            double aspf = aspect * k;
            double hx = Math.Min(SupportQ * aspf * rCore * 1.02, 0.999);
            double hy = Math.Min(SupportQ * rCore * 1.02, 0.999);
            int ny = (n / 2) * 2 + 1;
            double dx = 2.0 * hx / (n - 1);
            double dy = 2.0 * hy / (ny - 1);
            double sum = 0.0;
            for (int iy = 0; iy < ny; iy++)
            {
                double y = -hy + iy * dy;
                for (int ix = 0; ix < n; ix++)
                {
                    double x = -hx + ix * dx;
                    double z2 = 1.0 - x * x - y * y;
                    if (z2 <= 1e-9) continue;
                    double qh = Math.Sqrt((x / aspf) * (x / aspf) + y * y) / rCore;
                    sum += Profile(qh) / Math.Sqrt(z2);
                }
            }
            return sum * dx * dy;
        }

        private static double Profile(double q)
        {
            return -6.0 * (Smooth(0.29, 0.55, q) - Smooth(0.78, 1.04, q))
                 +        (Smooth(1.05, 1.35, q) - Smooth(1.8, 2.4, q));
        }

        private static double Smooth(double e0, double e1, double x)
        {
            double t = (x - e0) / (e1 - e0);
            if (t < 0.0) t = 0.0; else if (t > 1.0) t = 1.0;
            return t * t * (3.0 - 2.0 * t);
        }
    }
}
