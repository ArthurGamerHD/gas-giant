using System;
using System.Collections.Generic;
using GasGiantNet.Config;
using GasGiantNet.MathCore;
using GasGiantNet.Random;
using GasGiantNet.Sim;

namespace GasGiantNet.Render
{
    internal struct HeroInfo
    {
        public V3 Center;
        public float Radius;
        public float Spin;
        public float Aspect;
        public float WakeDir;
        public float WakeLatOff;
        public float Emergence;
    }

    internal struct CloudInfo
    {
        public V3 Center;
        public float Radius;
        public float Aspect;
    }

    internal sealed class DetailContext
    {
        public ParamTree Params;
        public CpuSimulation Sim;
        public HeroInfo[] Heroes;
        public CloudInfo[] Clouds;
        public V3 Offset;
        public V3 OffsetGate;
        public V3 OffsetSpiral;
        public V3 OffsetMottle;
        public V3 OffsetCirrus;
        public V3 OffsetBraid;
        public bool HeroEmergence;
        public bool Fx;
        public bool Spread;
    }

    internal static class DetailSynthCpu
    {
        private const float Pi = Glsl.PI;
        private const int Substeps = 6;
        private const float TauBase = 0.35f;
        private const float RouteLo = 1.1519f;
        private const float RouteHi = 1.2566f;

        public static FloatTexture Synthesize(CpuSimulation sim, int width, int height, int threads)
        {
            return Synthesize(sim, 0, 0, width, height, width, height, true, threads);
        }

        public static FloatTexture Synthesize(CpuSimulation sim, int originX, int originY, int width, int height, int fullWidth, int fullHeight, int threads)
        {
            return Synthesize(sim, originX, originY, width, height, fullWidth, fullHeight, true, threads);
        }

        public static FloatTexture Synthesize(CpuSimulation sim, int originX, int originY, int width, int height, int fullWidth, int fullHeight, bool polarRoute, int threads)
        {
            DetailContext c = BuildContext(sim);
            FloatTexture output = new FloatTexture(width, height, 1);
            ParamTree p = sim.Params;
            float freq = p.Float("detail.frequency");
            float stretch = p.Float("detail.flow_stretch");
            int phases = p.Int("detail.flow_phases");
            float cellAmount = p.Float("detail.cellular_amount");
            float striationAmount = p.Float("detail.striation_amount");
            float striationFreq = p.Float("detail.striation_frequency");
            float polarStipple = p.Float("detail.polar_stipple");
            float heroCalm = p.Float("detail.hero_calm");
            float spread = p.Float("detail.spread");

            CpuParallel.ForRows(height, threads, delegate(int y)
            {
                for (int x = 0; x < width; x++)
                {
                    float gx = x + originX + 0.5f;
                    float gy = y + originY + 0.5f;
                    V2 ll = new V2(gx / fullWidth * 2.0f * Pi - Pi,
                                   0.5f * Pi - gy / fullHeight * Pi);
                    V3 pc = SpherePt(ll);
                    V4 tr = sim.Equirect.Cur.SampleLinear(EqUv(ll));
                    V4 prof = sim.ProfileDyn.Sample(DomainMath.LatProfileU(ll.Y));
                    V2 vel = sim.Equirect.Velocity.SampleLinear2(EqUv(ll));
                    float speedN = Glsl.Clamp(Glsl.Length(vel) / 1.2f, 0.0f, 1.0f);
                    float shearN = prof.Z;
                    float belt = prof.W;
                    float hero = HeroMask(pc, c.Heroes);
                    float calm = 1.0f - heroCalm * hero;
                    float heroQ = hero;
                    if (c.HeroEmergence)
                    {
                        heroQ = HeroMaskFaded(pc, c.Heroes);
                        calm = MathF.Min(calm, HeroCalmFloor(pc, c.Heroes));
                    }

                    float routeW = polarRoute ? Glsl.SmoothStep(RouteLo, RouteHi, MathF.Abs(ll.Y)) : 0.0f;
                    float streak = 0.0f;
                    float stria = 0.0f;
                    if (routeW < 1.0f)
                    {
                        V2 ns = NoiseStack(sim.Equirect.Velocity, false, ll, heroQ, freq, stretch, phases, striationAmount, striationFreq, c.Offset, sim.Equirect.RhoMax);
                        streak = ns.X;
                        stria = ns.Y;
                    }
                    if (routeW > 0.0f)
                    {
                        SimDomain patch = ll.Y >= 0.0f ? sim.North : sim.South;
                        V2 nsp = NoiseStack(patch.Velocity, true, ll, heroQ, freq, stretch, phases, striationAmount, striationFreq, c.Offset, patch.RhoMax);
                        streak = Glsl.Mix(streak, nsp.X, routeW);
                        stria = Glsl.Mix(stria, nsp.Y, routeW);
                        V2 velP = VelAt(patch.Velocity, true, ll, patch.RhoMax);
                        float t2P = patch.Cur.SampleLinear(PatchUv(ll, patch.RhoMax)).Z;
                        float speedP = Glsl.Clamp(Glsl.Length(velP) / 1.2f, 0.0f, 1.0f);
                        speedN = Glsl.Mix(speedN, speedP, routeW);
                        shearN = Glsl.Mix(shearN, 0.0f, routeW);
                        belt = Glsl.Mix(belt, 0.0f, routeW);
                        tr.Z = Glsl.Mix(tr.Z, t2P, routeW);
                    }

                    float driveEff = 1.0f - routeW;
                    float fdCell = spread;
                    float fdLace = spread;
                    float foldPlace = spread;
                    float shearDrv = spread;
                    float beltPlace = c.Spread ? Glsl.Mix(belt, foldPlace, driveEff) : belt;

                    float f1 = Worley3D.F1(pc * (freq * 0.45f) + c.Offset.YZX);
                    float cells = 0.5f - (f1 - 0.55f);
                    float wStreak, wCell;
                    if (c.Spread)
                    {
                        float fdSh = Glsl.Mix(shearN, shearDrv, driveEff);
                        wStreak = Glsl.Clamp(0.2f + 0.8f * (fdSh + speedN), 0.0f, 1.0f) * (0.4f + 0.6f * tr.Z) * (1.0f + 1.4f * heroQ);
                        wCell = cellAmount * Glsl.Mix(1.0f - belt, fdCell, driveEff) * (1.0f - speedN) * (1.0f - fdSh) * (1.0f - 0.6f * routeW);
                    }
                    else
                    {
                        wStreak = Glsl.Clamp(0.2f + 0.8f * (shearN + speedN), 0.0f, 1.0f) * (0.4f + 0.6f * tr.Z) * (1.0f + 1.4f * heroQ);
                        wCell = cellAmount * (1.0f - belt) * (1.0f - speedN) * (1.0f - shearN) * (1.0f - 0.6f * routeW);
                    }

                    float gate = 1.0f;
                    if (c.Fx)
                    {
                        float intermittency = p.Float("detail.intermittency");
                        if (intermittency > 0.0f)
                        {
                            V2 llg = ll;
                            float adv = 0.5f * (1.0f - routeW);
                            llg.X -= adv * vel.X / MathF.Max(MathF.Cos(ll.Y), 0.05f);
                            llg.Y -= adv * vel.Y;
                            float g = Noise3D.Fbm(SpherePt(llg) * (freq * 0.22f) + c.OffsetGate, 3, 2.0f, 0.5f);
                            gate = Glsl.Mix(1.0f, 0.25f + 1.45f * Glsl.SmoothStep(-0.25f, 0.45f, g), intermittency);
                        }
                        wStreak *= gate;
                        wStreak += p.Float("detail.belt_texture") * 0.45f * belt * gate * (1.0f - routeW);
                        wStreak *= 1.0f - p.Float("detail.streak_mute");
                    }
                    wStreak *= calm;

                    float d = 0.5f + 0.5f * streak * wStreak + 0.35f * (cells - 0.5f) * wCell;

                    if (striationAmount > 0.0f)
                    {
                        float stretchEst = 1.0f + 5.0f * speedN * stretch;
                        float wavelengthPx = fullWidth / MathF.Max(striationFreq * 4.0f * stretchEst, 1.0f);
                        float atten = Glsl.SmoothStep(1.5f, 3.0f, wavelengthPx);
                        float wStria = striationAmount * (0.3f + 0.7f * MathF.Max(c.Spread ? beltPlace : belt, routeW * 0.5f)) * (0.25f + 0.75f * speedN) * atten;
                        if (c.Fx) wStria *= gate;
                        wStria *= calm;
                        d += 0.30f * stria * wStria;
                    }

                    if (c.Fx)
                    {
                        d = ApplyFlowFx(c, ll, pc, tr, vel, belt, beltPlace, routeW, driveEff, fdCell, fdLace, gate, calm, d, freq, stretch, fullWidth);
                        d = ApplyHeroFx(c, pc, d, freq);
                        d = ApplyPolarFilaments(c, ll, routeW, d, freq, stretch);
                        d = ApplyCirrus(c, ll, pc, tr, vel, routeW, d, freq, stretch, fullWidth);
                        d = ApplyWakeBraid(c, ll, pc, tr, routeW, d);
                    }

                    if (!polarRoute)
                    {
                        float fade = Glsl.SmoothStep(0.98f, 1.15f, MathF.Abs(ll.Y));
                        d = Glsl.Mix(d, 0.5f, fade);
                    }

                    if (polarStipple > 0.0f)
                    {
                        float pw = Glsl.SmoothStep(0.96f, 1.25f, MathF.Abs(ll.Y));
                        float f1p = Worley3D.F1(pc * (freq * 0.9f) + c.Offset.XZY() * 1.7f);
                        float speck = Glsl.Clamp(0.42f - f1p, 0.0f, 1.0f) * 2.2f;
                        d += polarStipple * pw * 0.30f * speck;
                    }
                    output.Set(x, y, 0, Glsl.Clamp(d, 0.0f, 1.0f));
                }
            });
            return output;
        }

        private static float ApplyFlowFx(DetailContext c, V2 ll, V3 pc, V4 tr, V2 vel, float belt, float beltPlace,
            float routeW, float driveEff, float fdCell, float fdLace, float gate, float calm, float d, float freq, float stretch, int fullWidth)
        {
            ParamTree p = c.Params;
            float beltTexture = p.Float("detail.belt_texture");
            if (beltTexture > 0.0f && routeW < 1.0f && (c.Spread ? MathF.Max(belt, p.Float("detail.spread") * driveEff) : belt) > 0.02f)
            {
                V2 srcF = BacktraceLl(c.Sim.Equirect.Velocity, false, ll, stretch * TauBase * 1.6f, c.Sim.Equirect.RhoMax);
                float fold = Noise3D.Fbm(SpherePt(srcF) * (freq * 0.30f) + c.OffsetGate.YZX, 4, 2.0f, 0.5f);
                d += 0.78f * beltTexture * fold * beltPlace * gate * (1.0f - routeW) * calm;
            }

            float fineAmt = p.Float("detail.belt_texture_fine");
            if (fineAmt > 0.0f && routeW < 1.0f && (c.Spread ? MathF.Max(belt, p.Float("detail.spread") * driveEff) : belt) > 0.02f)
            {
                V2 s1 = BacktraceLl(c.Sim.Equirect.Velocity, false, ll, stretch * TauBase * 1.2f, c.Sim.Equirect.RhoMax);
                V2 s2 = BacktraceLl(c.Sim.Equirect.Velocity, false, s1, stretch * TauBase * 1.2f, c.Sim.Equirect.RhoMax);
                float fold2 = Noise3D.Fbm(SpherePt(s2) * (freq * 0.85f) + c.OffsetGate.ZXY, 3, 2.0f, 0.5f);
                d += 0.62f * fineAmt * fold2 * beltPlace * gate * (1.0f - routeW) * calm;
            }

            float zoneTexture = p.Float("detail.zone_texture");
            float zonePlace = c.Spread ? Glsl.Mix(1.0f - belt, fdCell, driveEff) : 1.0f - belt;
            if (zoneTexture > 0.0f && routeW < 1.0f && zonePlace > 0.02f)
            {
                V2 srcZ = BacktraceLl(c.Sim.Equirect.Velocity, false, ll, stretch * TauBase * 1.4f, c.Sim.Equirect.RhoMax);
                float foldZ = Noise3D.Fbm(SpherePt(srcZ) * (freq * 0.42f) + c.OffsetMottle.YXZ + new V3(41.0f,41.0f,41.0f), 4, 2.0f, 0.5f);
                d += 0.55f * zoneTexture * foldZ * zonePlace * gate * (1.0f - routeW);
            }

            float mottle = p.Float("detail.mottle");
            if (mottle > 0.0f)
            {
                float aw = Glsl.SmoothStep(0.52f, 0.70f, MathF.Abs(ll.Y)) * (1.0f - Glsl.SmoothStep(1.10f, 1.22f, MathF.Abs(ll.Y)));
                float awEff = c.Spread ? Glsl.Mix(aw, fdLace, driveEff) : aw;
                if (aw > 0.0f || (c.Spread && fdLace * driveEff > 0.0f))
                {
                    V2 srcM = BacktraceLl(c.Sim.Equirect.Velocity, false, ll, stretch * TauBase * 0.8f, c.Sim.Equirect.RhoMax);
                    V3 pm = SpherePt(srcM);
                    float granule = Glsl.Clamp(0.45f - Worley3D.F1(pm * (freq * 0.7f) + c.OffsetMottle), 0.0f, 1.0f) * 2.0f;
                    float dots = Glsl.Clamp(0.30f - Worley3D.F1(pm * (freq * 1.1f) + c.OffsetMottle.ZXY), 0.0f, 1.0f) * 2.0f;
                    float lace = Noise3D.Fbm(pm * (freq * 0.5f) + c.OffsetMottle.YZX, 3, 2.0f, 0.5f);
                    d += mottle * awEff * (0.30f * granule - 0.22f * dots + 0.18f * lace);
                }
            }
            return d;
        }

        private static float ApplyHeroFx(DetailContext c, V3 pc, float d, float freq)
        {
            ParamTree p = c.Params;
            float heroSpiral = p.Float("detail.hero_spiral");
            float collar = p.Float("detail.hero_collar_wrap");
            if (heroSpiral <= 0.0f && collar <= 0.0f) return d;
            float sp = 0.0f, spw = 0.0f;
            for (int i = 0; i < c.Heroes.Length; i++)
            {
                HeroInfo h = c.Heroes[i];
                V3 center = h.Center;
                float rc = MathF.Max(h.Radius, 1e-4f);
                float asp = h.Aspect;
                V3 ew = Glsl.Cross(new V3(0,1,0), center);
                float ewl = Glsl.Length(ew);
                if (ewl < 1e-4f) continue;
                V3 e1 = ew / ewl;
                V3 e2 = Glsl.Cross(center, e1);
                float q = asp == 1.0f
                    ? MathF.Acos(Glsl.Clamp(Glsl.Dot(pc, center), -1.0f, 1.0f)) / rc
                    : (Glsl.Dot(pc, center) > 0.0f ? Glsl.Length(new V2(Glsl.Dot(pc,e1)/asp, Glsl.Dot(pc,e2))) / rc : 1e3f);
                if (q < 0.05f || q >= 1.9f) continue;
                float theta = MathF.Atan2(Glsl.Dot(pc,e2), Glsl.Dot(pc,e1));
                float k = (c.HeroEmergence ? Glsl.Mix(-20.0f, -6.0f, h.Emergence) : -20.0f) * h.Spin;
                float qc = MathF.Max(q, 0.08f);
                float jig = Noise3D.Fbm(pc * (freq * 1.5f) + c.OffsetSpiral, 3, 2.0f, 0.5f);
                float lane = MathF.Cos(2.0f * theta + k * MathF.Log(qc) + 2.4f * jig) * (0.55f + 0.45f * jig);
                float win = Glsl.SmoothStep(0.12f,0.34f,q) * (1.0f-Glsl.SmoothStep(0.72f,0.98f,q));
                if (c.HeroEmergence) win *= 1.0f - 0.55f * h.Emergence;
                sp += win * lane;
                float wino = c.HeroEmergence
                    ? Glsl.SmoothStep(0.98f,1.12f,q) * (1.0f-Glsl.SmoothStep(Glsl.Mix(1.5f,1.26f,h.Emergence),Glsl.Mix(1.85f,1.40f,h.Emergence),q))
                    : Glsl.SmoothStep(0.98f,1.12f,q) * (1.0f-Glsl.SmoothStep(1.5f,1.85f,q));
                float rj = Noise3D.Fbm(pc * (freq * 0.9f) + c.OffsetSpiral.ZXY, 2, 2.0f, 0.5f);
                sp += 0.55f * wino * MathF.Cos(q * 28.0f + 5.0f * theta + 4.0f * rj);
                if (collar > 0.0f)
                {
                    float kc = -34.0f * h.Spin;
                    spw += wino * MathF.Cos(2.0f * theta + kc * MathF.Log(MathF.Max(q,0.5f)) + 3.0f * rj) * (0.5f + 0.5f * rj);
                }
            }
            d += 0.22f * heroSpiral * Glsl.Clamp(sp, -1.5f, 1.5f);
            if (collar > 0.0f) d += 0.22f * collar * Glsl.Clamp(spw, -1.5f, 1.5f);
            return d;
        }

        private static float ApplyPolarFilaments(DetailContext c, V2 ll, float routeW, float d, float freq, float stretch)
        {
            float amt = c.Params.Float("detail.polar_filaments");
            if (amt <= 0.0f || routeW <= 0.0f) return d;
            SimDomain patch = ll.Y >= 0.0f ? c.Sim.North : c.Sim.South;
            V2 srcF = BacktraceLl(patch.Velocity, true, ll, stretch * TauBase * 1.15f, patch.RhoMax);
            V3 pf = SpherePt(srcF);
            float a1 = 1.0f - MathF.Abs(Noise3D.Fbm(pf * (freq * 0.30f) + c.OffsetMottle.ZYX + new V3(17,17,17), 4, 2.0f, 0.5f));
            float a2 = 1.0f - MathF.Abs(Noise3D.Fbm(pf * (freq * 0.82f) + c.OffsetMottle.XZY() + new V3(53,53,53), 3, 2.0f, 0.5f));
            float ridge = Glsl.SmoothStep(0.80f,0.97f,0.70f*a1+0.30f*a2);
            float lace = Glsl.Clamp(ridge - 0.13f, -0.5f, 0.42f);
            return d + amt * routeW * 2.8f * lace;
        }

        private static float ApplyCirrus(DetailContext c, V2 ll, V3 pc, V4 tr, V2 vel, float routeW, float d, float freq, float stretch, int fullWidth)
        {
            float amt = c.Params.Float("detail.cirrus_fibers");
            if (amt <= 0.0f || c.Clouds.Length == 0 || routeW >= 1.0f) return d;
            V2 srcC = BacktraceLl(c.Sim.Equirect.Velocity, false, ll, stretch * TauBase * 0.7f, c.Sim.Equirect.RhoMax);
            V3 pcC = SpherePt(srcC);
            float ang = 0.0f;
            if (Glsl.Dot(vel,vel) > 1e-6f)
            {
                float aa = 2.0f * MathF.Atan2(vel.Y, vel.X);
                ang = 0.25f * MathF.Atan2(MathF.Sin(aa), MathF.Cos(aa));
            }
            float ca=MathF.Cos(ang), sa=MathF.Sin(ang);
            float t0base = c.Sim.ProfileStamp.Sample(DomainMath.LatProfileU(ll.Y)).X;
            float excess = Glsl.SmoothStep(0.03f,0.14f,tr.X-t0base);
            if (excess <= 0.0f) return d;
            float num=0,den=0,winMax=0;
            for(int i=0;i<c.Clouds.Length;i++)
            {
                CloudInfo cloud=c.Clouds[i]; V3 cc=cloud.Center;
                if(Glsl.Dot(pc,cc)<=0.0f)continue;
                float rc=MathF.Max(cloud.Radius,1e-4f),asp=MathF.Max(cloud.Aspect,1.0f);
                V3 ew=Glsl.Cross(new V3(0,1,0),cc);float ewl=Glsl.Length(ew);if(ewl<1e-4f)continue;
                V3 e1=ew/ewl,e2=Glsl.Cross(cc,e1);
                float q=Glsl.Length(new V2(Glsl.Dot(pc,e1)/(1.8f*asp),Glsl.Dot(pc,e2)))/rc;if(q>=2.2f)continue;
                float win=MathF.Exp(-0.8f*q*q);
                float freqI=c.Params.Float("detail.cirrus_fiber_freq")*(0.75f+0.5f*Glsl.Fract(i*0.618034f));
                float wlPx=rc/freqI*fullWidth/(2.0f*Pi);float atten=Glsl.SmoothStep(1.5f,3.0f,wlPx);if(atten<=0)continue;
                V3 f1=e1*ca+e2*sa,f2=e2*ca-e1*sa;
                float along=Glsl.Dot(pcC,f1)/rc,across=Glsl.Dot(pcC,f2)/rc;
                float z=11.0f+i*3.7f;
                float rag=Noise3D.Fbm(new V3(along*1.4f,across*4.0f,z)+c.OffsetCirrus.ZXY,2,2,0.5f);
                float wav=Noise3D.Fbm(new V3(along*0.7f,across*0.7f,5.0f+i*3.7f)+c.OffsetCirrus.YZX,2,2,0.5f);
                float ff=Noise3D.Fbm(new V3(along*freqI*0.14f,(across+wav)*freqI,i*3.7f)+c.OffsetCirrus,3,2,0.5f);
                float strand=Glsl.SmoothStep(-0.10f,0.50f,ff)-0.5f;if(strand<=0)strand*=0.45f;
                float wgt=win*win;num+=wgt*atten*strand*(0.35f+0.65f*Glsl.SmoothStep(-0.35f,0.30f,rag));den+=wgt;winMax=MathF.Max(winMax,win);
            }
            if(den>1e-5f)d+=0.9f*amt*(1.0f-routeW)*excess*winMax*Glsl.Clamp(num/den,-1,1);
            return d;
        }

        private static float ApplyWakeBraid(DetailContext c,V2 ll,V3 pc,V4 tr,float routeW,float d)
        {
            float amt=c.Params.Float("detail.hero_wake_braid");if(amt<=0||c.Heroes.Length==0||routeW>=1)return d;
            FloatTexture tt=c.Sim.Equirect.Cur;V2 uv=EqUv(ll);float dx=1.5f/tt.Width,dy=1.5f/tt.Height;
            float tE=tt.SampleLinear(new V2(uv.X+dx,uv.Y)).X,tW=tt.SampleLinear(new V2(uv.X-dx,uv.Y)).X;
            float tN=tt.SampleLinear(new V2(uv.X,uv.Y-dy)).X,tS=tt.SampleLinear(new V2(uv.X,uv.Y+dy)).X;
            float gmag=Glsl.Length(new V2(tE-tW,tN-tS));float rcm=0;
            for(int i=0;i<c.Heroes.Length;i++)if(c.Heroes[i].WakeDir!=0){rcm=MathF.Max(c.Heroes[i].Radius,1e-4f);break;}
            float duM=0.5f*rcm/(2*Pi),dvM=0.5f*rcm/Pi;
            float mE=tt.SampleLinear(new V2(uv.X+duM,uv.Y)).X,mW=tt.SampleLinear(new V2(uv.X-duM,uv.Y)).X;
            float mN=tt.SampleLinear(new V2(uv.X,uv.Y-dvM)).X,mS=tt.SampleLinear(new V2(uv.X,uv.Y+dvM)).X;
            float core=Glsl.SmoothStep(0.01f,0.10f,tr.X-0.25f*(mE+mW+mN+mS));
            float rim=Glsl.SmoothStep(0.04f,0.16f,gmag)*(0.55f+0.45f*Noise3D.Fbm(pc*30.0f+c.OffsetBraid,2,2,0.5f));
            float sig=Glsl.Clamp(1.1f*core-1.5f*rim,-1,1),winMax=0;
            for(int i=0;i<c.Heroes.Length;i++)
            {
                HeroInfo h=c.Heroes[i];if(h.WakeDir==0||Glsl.Dot(pc,h.Center)<=0)continue;float rc=MathF.Max(h.Radius,1e-4f),asp=MathF.Max(h.Aspect,1);
                float vlat=MathF.Asin(Glsl.Clamp(h.Center.Y,-1,1)),vlon=MathF.Atan2(h.Center.Z,h.Center.X);
                float dlon=Glsl.Mod(ll.X-vlon+3*Pi,2*Pi)-Pi;float an=dlon*h.WakeDir/rc;if(an<=0)continue;
                float wwin=Glsl.SmoothStep(asp,1.5f*asp,an)*(1-Glsl.SmoothStep(14,19,an));if(wwin<=0)continue;
                float beltSign=h.WakeLatOff!=0?Glsl.Sign(h.WakeLatOff):(h.Center.Y>0?-1:1);
                float alat=(ll.Y-(vlat+h.WakeLatOff))/rc,b=alat*beltSign;
                float lwin=b>=0?1-Glsl.SmoothStep(1.8f,2.4f,b):1-Glsl.SmoothStep(0.25f,0.80f,-b);
                winMax=MathF.Max(winMax,wwin*lwin);
            }
            if(winMax>1e-4f){d=Glsl.Mix(d,0.5f,Glsl.Clamp(0.55f*amt,0,0.65f)*winMax);d+=0.85f*amt*(1-routeW)*winMax*sig;}
            return d;
        }

        private static DetailContext BuildContext(CpuSimulation sim)
        {
            DetailContext c=new DetailContext();c.Params=sim.Params;c.Sim=sim;
            c.Offset=Draw3(RandomGenerator.Subseed(sim.Params.Int("seed"),"detail-synth"));
            c.OffsetGate=Draw3(RandomGenerator.Subseed(sim.Params.Int("seed"),"detail-intermittency"));
            c.OffsetSpiral=Draw3(RandomGenerator.Subseed(sim.Params.Int("seed"),"detail-hero-spiral"));
            c.OffsetMottle=Draw3(RandomGenerator.Subseed(sim.Params.Int("seed"),"detail-mottle"));
            c.OffsetCirrus=Draw3(RandomGenerator.Subseed(sim.Params.Int("seed"),"detail-cirrus"));
            c.OffsetBraid=Draw3(RandomGenerator.Subseed(sim.Params.Int("seed"),"detail-wake-braid"));
            c.Heroes=BuildHeroes(sim);c.Clouds=BuildClouds(sim);c.HeroEmergence=sim.Vortices.SceneEmergence(sim.Params)>0&&c.Heroes.Length>0;
            c.Spread=sim.Params.Float("detail.spread")>0;
            c.Fx=sim.Params.Float("detail.intermittency")>0||sim.Params.Float("detail.hero_spiral")>0||sim.Params.Float("detail.hero_collar_wrap")>0||sim.Params.Float("detail.belt_texture")>0||sim.Params.Float("detail.belt_texture_fine")>0||sim.Params.Float("detail.zone_texture")>0||sim.Params.Float("detail.mottle")>0||sim.Params.Float("detail.polar_filaments")>0||sim.Params.Float("detail.cirrus_fibers")>0||sim.Params.Float("detail.streak_mute")>0||sim.Params.Float("detail.hero_wake_braid")>0;
            return c;
        }

        private static HeroInfo[] BuildHeroes(CpuSimulation sim)
        {
            List<Vortex> list=sim.Vortices.Heroes();int n=Math.Min(3,list.Count);HeroInfo[] outv=new HeroInfo[n];
            for(int i=0;i<n;i++){Vortex v=list[i];float cl=(float)Math.Cos(v.Lat);outv[i]=new HeroInfo{Center=new V3(cl*(float)Math.Cos(v.Lon),(float)Math.Sin(v.Lat),cl*(float)Math.Sin(v.Lon)),Radius=(float)v.CoreRadius,Spin=v.Strength>=0?1:-1,Aspect=(float)v.Aspect,WakeDir=(float)v.WakeDir,WakeLatOff=(float)v.WakeLatOff,Emergence=(float)Vortices.EffectiveCastLever(sim.Params,v.CastRef,"emergence")};}
            return outv;
        }

        private static CloudInfo[] BuildClouds(CpuSimulation sim)
        {
            List<CloudInfo> r=new List<CloudInfo>();
            for(int i=0;i<sim.Vortices.Vortices.Count&&r.Count<12;i++){Vortex v=sim.Vortices.Vortices[i];if(v.Kind==VortexKinds.Hero||v.Aspect<=1||v.Brightness<=0)continue;float cl=(float)Math.Cos(v.Lat);r.Add(new CloudInfo{Center=new V3(cl*(float)Math.Cos(v.Lon),(float)Math.Sin(v.Lat),cl*(float)Math.Sin(v.Lon)),Radius=(float)v.CoreRadius,Aspect=(float)v.Aspect});}
            return r.ToArray();
        }

        private static V3 Draw3(RandomGenerator rng){return new V3((float)rng.Uniform(-100,100),(float)rng.Uniform(-100,100),(float)rng.Uniform(-100,100));}
        private static V3 SpherePt(V2 ll){float cl=MathF.Cos(ll.Y);return new V3(cl*MathF.Cos(ll.X),MathF.Sin(ll.Y),cl*MathF.Sin(ll.X));}
        private static V2 EqUv(V2 ll){return new V2((ll.X+Pi)/(2*Pi),(0.5f*Pi-ll.Y)/Pi);}
        private static V2 PatchUv(V2 ll,float rhoMax){float rho=0.5f*Pi-MathF.Abs(ll.Y);V2 st=new V2(rho*MathF.Cos(ll.X),rho*MathF.Sin(ll.X));return st/rhoMax*0.5f+new V2(0.5f,0.5f);}
        private static V2 VelAt(FloatTexture tex,bool patch,V2 ll,float rhoMax){return tex.SampleLinear2(patch?PatchUv(ll,rhoMax):EqUv(ll));}
        private static V2 BacktraceLl(FloatTexture tex,bool patch,V2 ll,float tau,float rhoMax){float hh=tau/Substeps;float floor=patch?0.01f:0.05f;for(int i=0;i<Substeps;i++){V2 vel=VelAt(tex,patch,ll,rhoMax);float cos=MathF.Max(MathF.Cos(ll.Y),floor);ll.X-=hh*vel.X/cos;ll.Y-=hh*vel.Y;ll.Y=Glsl.Clamp(ll.Y,-0.5f*Pi,0.5f*Pi);}return ll;}
        private static V2 NoiseStack(FloatTexture tex,bool patch,V2 ll,float hero,float freq,float stretch,int phases,float strAmt,float strFreq,V3 off,float rhoMax){float streak=0,wsum=0;for(int ph=0;ph<phases;ph++){float tau=stretch*TauBase*(ph+1.0f)/phases*(1+1.2f*hero);V2 src=BacktraceLl(tex,patch,ll,tau,rhoMax);float w=1-0.5f*ph/Math.Max(phases-1,1);streak+=w*Noise3D.Fbm(SpherePt(src)*freq+off,4,2,0.5f);wsum+=w;}streak/=MathF.Max(wsum,1e-5f);float stria=0;if(strAmt>0){float ts=stretch*TauBase*2.6f*(1+1.2f*hero);V2 src=BacktraceLl(tex,patch,ll,ts,rhoMax);stria=Noise3D.Fbm(SpherePt(src)*strFreq+off.ZXY,3,2,0.5f);}return new V2(streak,stria);}
        private static float HeroTerm(V3 p,HeroInfo h){V3 cc=h.Center;float rc=MathF.Max(h.Radius*1.4f,1e-4f),asp=h.Aspect,q;if(asp==1)q=MathF.Acos(Glsl.Clamp(Glsl.Dot(p,cc),-1,1))/rc;else{V3 ew=Glsl.Cross(new V3(0,1,0),cc);float ewl=Glsl.Length(ew);if(ewl<1e-4f)q=MathF.Acos(Glsl.Clamp(Glsl.Dot(p,cc),-1,1))/rc;else{V3 e1=ew/ewl,e2=Glsl.Cross(cc,e1);q=Glsl.Dot(p,cc)>0?Glsl.Length(new V2(Glsl.Dot(p,e1)/asp,Glsl.Dot(p,e2)))/rc:1e3f;}}return MathF.Exp(-q*q);}
        private static float HeroMask(V3 p,HeroInfo[] h){float m=0;for(int i=0;i<h.Length;i++)m+=HeroTerm(p,h[i]);return Glsl.Clamp(m,0,1);}
        private static float HeroMaskFaded(V3 p,HeroInfo[] h){float m=0;for(int i=0;i<h.Length;i++)m+=HeroTerm(p,h[i])*(1-h[i].Emergence);return Glsl.Clamp(m,0,1);}
        private static float HeroCalmFloor(V3 p,HeroInfo[] h){float f=1;for(int i=0;i<h.Length;i++)f=MathF.Min(f,1-0.85f*h[i].Emergence*Glsl.SmoothStep(0.05f,0.45f,HeroTerm(p,h[i])));return f;}
    }

    internal static class DetailVectorExtensions
    {
        public static V3 XZY(this V3 v){return new V3(v.X,v.Z,v.Y);}
    }
}
