using System;
using GasGiantNet.Config;
using GasGiantNet.MathCore;

namespace GasGiantNet.Sim
{
    internal sealed class TracerKernelContext
    {
        public ParamTree Params;
        public BandLayout Bands;
        public LatLut ProfileStamp;
        public LatLut ProfileDyn;
        public SimStaticUniforms Static;
        public VortexStampContext VortexStamp;
        public float FestoonLat;
        public float RibbonLat;
        public float HeroFestoonLat;
        public bool Festoon2;
        public float PolyAmp;
        public float PolyK;
        public float PolyRho;
        public float PolyEps;
        public float PolyPhase;
        public float PolyWidth;
        public float TurbTime;
        public float RelaxK;
        public float Replenish;
        public float BeltReplenish;
        public float BeltScale;
    }

    internal static class TracerKernels
    {
        public static void Init(SimDomain d, TracerKernelContext c, int threads)
        {
            int w = d.Width, h = d.Height;
            CpuParallel.ForRows(h, threads, delegate(int y)
            {
                for (int x = 0; x < w; x++)
                {
                    V2 ll = DomainMath.LonLatAt(d.Kind, x, y, w, h, d.RhoMax);
                    V3 sp = DomainMath.SpherePoint(ll);
                    float warp = Noise3D.Fbm(sp * c.Params.Float("bands.warp_freq") + c.Static.WarpOffset, 4, 2.0f, 0.5f) * c.Params.Float("bands.warp_amount");
                    float stampLat = c.VortexStamp != null && c.VortexStamp.HeroEmergence
                        ? VortexStampCpu.HeroBandDeflect(sp, ll.Y, c.VortexStamp) + warp
                        : ll.Y + warp;
                    V4 stamp = c.ProfileStamp.Sample(DomainMath.LatProfileU(stampLat));
                    float s0 = stamp.X;
                    float s1 = stamp.Y;
                    BandStampCpu.Mod(ref s0, ref s1, sp, ll, c.Params, c.Bands, c.Static);

                    float perturb = Noise3D.Fbm(sp * c.Params.Float("bands.detail_freq") + c.Static.DetailOffset, 5, 2.0f, 0.5f);
                    float t0 = s0 + perturb * c.Params.Float("bands.detail_amount");
                    float t1 = s1 + perturb * c.Params.Float("bands.detail_amount") * 0.5f;
                    float t2 = 0.5f + 0.5f * Noise3D.Fbm(sp * (c.Params.Float("bands.detail_freq") * 2.0f) + c.Static.DetailOffset.ZXY, 5, 2.0f, 0.5f);
                    V3 vs = c.VortexStamp != null ? VortexStampCpu.Stamp(sp, c.VortexStamp) : new V3(0,0,0);
                    t0 += vs.X;
                    t1 += vs.Y;
                    float t3 = vs.Z;

                    if (d.Kind == DomainKind.Equirect)
                    {
                        V3 ws = WaveStampCpu.Stamp(ll, c.Params, c.FestoonLat, c.RibbonLat, c.HeroFestoonLat, c.Festoon2, c.Static);
                        t0 += ws.X;
                        t1 += ws.Y;
                        t3 += ws.Z;
                    }
                    else if (c.PolyAmp > 0.0f)
                    {
                        float rho = 0.5f * Glsl.PI - MathF.Abs(ll.Y);
                        float rho0 = c.PolyRho * (1.0f + c.PolyEps * MathF.Cos(c.PolyK * ll.X + c.PolyPhase));
                        float dr = (rho - rho0) / MathF.Max(c.PolyWidth * 0.7f, 1e-4f);
                        t0 -= 0.12f * MathF.Exp(-dr * dr);
                    }
                    d.Cur.Set4(x, y, new V4(Glsl.Clamp(t0,0,1), Glsl.Clamp(t1,0,1), Glsl.Clamp(t2,0,1), Glsl.Clamp(t3,-1,1)));
                }
            });
        }

        public static void StepMacCormack(SimDomain d, TracerKernelContext c, float dt, int threads)
        {
            AdvectPass(d, d.Cur, d.Fwd, dt, threads);
            AdvectPass(d, d.Fwd, d.Back, -dt, threads);
            CorrectPass(d, c, dt, threads);
            d.CommitTracer();
        }

        private static void AdvectPass(SimDomain d, FloatTexture src, FloatTexture dst, float dt, int threads)
        {
            int w=d.Width,h=d.Height;
            CpuParallel.ForRows(h,threads,delegate(int y)
            {
                for(int x=0;x<w;x++)
                {
                    V2 pos=new V2(x+0.5f,y+0.5f);
                    V2 source=Backtrace(d,pos,dt);
                    dst.Set4(x,y,src.SampleCatmullRomPixel(source));
                }
            });
        }

        private static void CorrectPass(SimDomain d, TracerKernelContext c, float dt, int threads)
        {
            int w=d.Width,h=d.Height;
            CpuParallel.ForRows(h,threads,delegate(int y)
            {
                for(int x=0;x<w;x++)
                {
                    V2 pixPos=new V2(x+0.5f,y+0.5f);
                    V2 source=Backtrace(d,pixPos,dt);
                    V4 fwd=d.Fwd.Get4(x,y);
                    V4 cur=d.Cur.Get4(x,y);
                    V4 back=d.Back.Get4(x,y);
                    V4 result=fwd+(cur-back)*0.5f;
                    V4 lo,hi;
                    d.Cur.MinMax2x2Pixel(source,out lo,out hi);
                    for(int ch=0;ch<4;ch++) if(result[ch]<lo[ch]||result[ch]>hi[ch]) result[ch]=fwd[ch];
                    result=Clamp4(result,lo,hi);

                    V2 ll=DomainMath.LonLatAt(d.Kind,x,y,w,h,d.RhoMax);
                    V3 sp=DomainMath.SpherePoint(ll);
                    float warp=Noise3D.Fbm(sp*c.Params.Float("bands.warp_freq")+c.Static.WarpOffset,4,2.0f,0.5f)*c.Params.Float("bands.warp_amount");
                    float stampLat=c.VortexStamp!=null&&c.VortexStamp.HeroEmergence
                        ? VortexStampCpu.HeroBandDeflect(sp,ll.Y,c.VortexStamp)+warp : ll.Y+warp;
                    V4 stamp=c.ProfileStamp.Sample(DomainMath.LatProfileU(stampLat));
                    float s0=stamp.X,s1=stamp.Y;
                    BandStampCpu.Mod(ref s0,ref s1,sp,ll,c.Params,c.Bands,c.Static);
                    V3 vs=c.VortexStamp!=null?VortexStampCpu.Stamp(sp,c.VortexStamp):new V3(0,0,0);
                    float ring=0.0f;
                    V3 ws=new V3(0,0,0);
                    if(d.Kind!=DomainKind.Equirect) ring=PolyRing(ll,c);
                    else ws=WaveStampCpu.Stamp(ll,c.Params,c.FestoonLat,c.RibbonLat,c.HeroFestoonLat,c.Festoon2,c.Static);

                    float rk=c.RelaxK;
                    if(c.VortexStamp!=null&&c.VortexStamp.HeroEmergence) rk*=VortexStampCpu.HeroRelaxWeight(sp,c.VortexStamp);
                    result.X+=(s0+vs.X+ws.X+ring-result.X)*rk;
                    result.Y+=(s1+vs.Y+ws.Y-result.Y)*rk;

                    float fresh=0.5f+0.5f*Noise3D.Fbm(
                        sp*(c.Params.Float("bands.detail_freq")*2.0f)+c.Static.DetailOffset.ZXY+new V3(0,c.TurbTime,0),4,2.0f,0.5f);
                    result.Z=Glsl.Mix(result.Z,fresh,c.Replenish);

                    if(c.BeltReplenish>0.0f)
                    {
                        float beltm=c.ProfileDyn.Sample(DomainMath.LatProfileU(ll.Y)).W;
                        if(beltm>0.02f)
                        {
                            float fine=0.5f+0.5f*Noise3D.Fbm(
                                sp*(c.Params.Float("bands.detail_freq")*2.0f*c.BeltScale)+c.Static.DetailOffset.YXZ+new V3(c.TurbTime,0,0),3,2.0f,0.5f);
                            result.Z=Glsl.Mix(result.Z,fine,c.BeltReplenish*beltm);
                        }
                    }
                    result.W+=(vs.Z+ws.Z-result.W)*rk*0.6f;
                    d.Out.Set4(x,y,result);
                }
            });
        }

        private static V2 Backtrace(SimDomain d,V2 pixPos,float dt)
        {
            int w=d.Width,h=d.Height;
            V2 uvScale=new V2(1.0f/w,1.0f/h);
            if(d.Kind==DomainKind.Equirect)
            {
                V2 ll=new V2((pixPos.X/w)*2.0f*Glsl.PI-Glsl.PI,0.5f*Glsl.PI-(pixPos.Y/h)*Glsl.PI);
                V2 vel=d.Velocity.SampleLinear2(pixPos*uvScale);
                float cosl=MathF.Max(MathF.Cos(ll.Y),0.017f);
                V2 mid=ll+new V2(-0.5f*dt*vel.X/cosl,-0.5f*dt*vel.Y);
                V2 midPix=new V2((mid.X+Glsl.PI)/(2.0f*Glsl.PI)*w,(0.5f*Glsl.PI-mid.Y)/Glsl.PI*h);
                V2 velMid=d.Velocity.SampleLinear2(midPix*uvScale);
                float cosMid=MathF.Max(MathF.Cos(mid.Y),0.017f);
                V2 dest=ll+new V2(-dt*velMid.X/cosMid,-dt*velMid.Y);
                return new V2((dest.X+Glsl.PI)/(2.0f*Glsl.PI)*w,(0.5f*Glsl.PI-dest.Y)/Glsl.PI*h);
            }
            V2 st=DomainMath.PatchStFromPix(pixPos,w,h,d.RhoMax);
            V2 v0=d.Velocity.SampleLinear2(pixPos*uvScale);
            V2 stMid=st-DomainMath.PatchVelocity(d.Kind,st,v0)*(0.5f*dt);
            V2 patchMidPix=DomainMath.PatchPixFromSt(stMid,w,h,d.RhoMax);
            V2 vm=d.Velocity.SampleLinear2(patchMidPix*uvScale);
            V2 patchDest=stMid; // temporary to keep exact operand sequencing obvious
            patchDest=st-DomainMath.PatchVelocity(d.Kind,stMid,vm)*dt;
            return DomainMath.PatchPixFromSt(patchDest,w,h,d.RhoMax);
        }

        private static float PolyRing(V2 ll,TracerKernelContext c)
        {
            if(c.PolyAmp<=0.0f)return 0.0f;
            float rho=0.5f*Glsl.PI-MathF.Abs(ll.Y);
            float rho0=c.PolyRho*(1.0f+c.PolyEps*MathF.Cos(c.PolyK*ll.X+c.PolyPhase));
            float dr=(rho-rho0)/MathF.Max(c.PolyWidth*0.7f,1e-4f);
            return -0.12f*MathF.Exp(-dr*dr);
        }

        private static V4 Clamp4(V4 x,V4 lo,V4 hi)
        {
            return new V4(Glsl.Clamp(x.X,lo.X,hi.X),Glsl.Clamp(x.Y,lo.Y,hi.Y),Glsl.Clamp(x.Z,lo.Z,hi.Z),Glsl.Clamp(x.W,lo.W,hi.W));
        }
    }
}
