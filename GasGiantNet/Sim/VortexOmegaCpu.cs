using System;
using System.Collections.Generic;
using GasGiantNet.Config;
using GasGiantNet.MathCore;

namespace GasGiantNet.Sim
{
    internal sealed class VortexOmegaContext
    {
        public readonly ParamTree Params;
        public readonly VortexStampContext VortexStamp;
        public readonly float HeroFlowRenorm;
        public VortexOmegaContext(ParamTree p,VortexStampContext stamp,float renorm)
        {
            Params=p;VortexStamp=stamp;HeroFlowRenorm=renorm;
        }
    }

    internal static class VortexOmegaCpu
    {
        private const float Hero=1.0f;
        private const float Oval=0.0f;
        private const float OvalSolidMinR=0.035f;

        public static float Coriolis(float lat,float f0){return f0*MathF.Sin(lat);}

        private static float DOverTanD(float d)
        {
            if(d<1e-4f)return 1.0f-d*d/3.0f;
            return d/MathF.Tan(d);
        }

        public static float HeroAnchorWindow(V3 p,VortexOmegaContext c)
        {
            if(!c.VortexStamp.HeroEmergence)return 0.0f;
            float w=0.0f;
            for(int i=0;i<c.VortexStamp.Count;i++)
            {
                if(c.VortexStamp.VortexData[3*i+1].Y!=Hero)continue;
                w=MathF.Max(w,1.0f-Glsl.SmoothStep(1.6f,2.8f,HeroEllipQ(p,i,2.8f,c.VortexStamp)));
            }
            return w;
        }

        public static float HeroAnchorBoost(V3 p,float k,VortexOmegaContext c)
        {
            if(!c.VortexStamp.HeroEmergence)return 0.0f;
            float w=0.0f;
            for(int i=0;i<c.VortexStamp.Count;i++)
            {
                if(c.VortexStamp.VortexData[3*i+1].Y!=Hero)continue;
                float e=c.VortexStamp.CastLevers?c.VortexStamp.CastLeverData[3*i+2].X:c.Params.Float("storms.hero_emergence");
                w=MathF.Max(w,(k*e)*(1.0f-Glsl.SmoothStep(1.6f,2.8f,HeroEllipQ(p,i,2.8f,c.VortexStamp))));
            }
            return w;
        }

        public static float HeroWakeWindow(V3 p,VortexOmegaContext c)
        {
            if(!c.VortexStamp.HeroEmergence)return 0.0f;
            float w=0.0f;
            for(int i=0;i<c.VortexStamp.Count;i++)
            {
                if(c.VortexStamp.VortexData[3*i+1].Y!=Hero)continue;
                float cand=WakeWindowOne(p,i,c.VortexStamp);
                w=MathF.Max(w,cand);
            }
            return w;
        }

        public static float HeroWakeInject(V3 p,float k,VortexOmegaContext c)
        {
            if(!c.VortexStamp.HeroEmergence)return 0.0f;
            float w=0.0f;
            for(int i=0;i<c.VortexStamp.Count;i++)
            {
                if(c.VortexStamp.VortexData[3*i+1].Y!=Hero)continue;
                float e=c.VortexStamp.CastLevers?c.VortexStamp.CastLeverData[3*i+2].X:c.Params.Float("storms.hero_emergence");
                w=MathF.Max(w,(k*e)*WakeWindowOne(p,i,c.VortexStamp));
            }
            return w;
        }

        private static float WakeWindowOne(V3 p,int i,VortexStampContext c)
        {
            V4 a=c.VortexData[3*i];V4 m=c.VortexData[3*i+2];
            float rc=a.W,down=m.X,woff=m.Z;
            float vlat=MathF.Asin(Glsl.Clamp(a.Y,-1,1)),vlon=MathF.Atan2(a.Z,a.X);
            float plat=MathF.Asin(Glsl.Clamp(p.Y,-1,1)),plon=MathF.Atan2(p.Z,p.X);
            float dlon=Glsl.Mod(plon-vlon+3.0f*Glsl.PI,2.0f*Glsl.PI)-Glsl.PI;
            float an=dlon*down/MathF.Max(rc,1e-4f);
            float across=(plat-(vlat+woff))/MathF.Max(rc*1.8f,1e-4f);
            if(an>0.8f&&an<9.0f&&MathF.Abs(across)<2.0f)
            {
                float rise=Glsl.SmoothStep(0.8f,1.8f,an);
                float fall=1.0f-Glsl.SmoothStep(6.0f,9.0f,an);
                float aw=(1.0f-Glsl.SmoothStep(1.4f,2.0f,MathF.Abs(across)))*MathF.Exp(-across*across);
                return rise*fall*aw;
            }
            return 0.0f;
        }

        public static float Accum(V3 p,VortexOmegaContext c)
        {
            float omega=0.0f;
            const float eps=1e-6f;
            ParamTree prm=c.Params;
            VortexStampContext vs=c.VortexStamp;
            for(int i=0;i<vs.Count;i++)
            {
                V4 a=vs.VortexData[3*i];V4 b=vs.VortexData[3*i+1];V4 meta=vs.VortexData[3*i+2];
                float strength=b.X,r=a.W;
                float d=MathF.Acos(Glsl.Clamp(Glsl.Dot(p,a.XYZ),-1,1));
                float asp=meta.Y,q;
                if(asp==1.0f)q=d/r;
                else
                {
                    V3 cc=a.XYZ;V3 ew=Glsl.Cross(new V3(0,1,0),cc);float ewl=Glsl.Length(ew);
                    if(ewl<1e-4f)q=d/r;
                    else
                    {
                        V3 e1=ew/ewl,e2=Glsl.Cross(cc,e1);
                        q=Glsl.Dot(p,cc)>0.0f?Glsl.Length(new V2(Glsl.Dot(p,e1)/asp,Glsl.Dot(p,e2)))/r:1e3f;
                    }
                }
                float expq=MathF.Exp(-q*q);
                float scale=strength/(r*r);
                float term1=scale*(4.0f*q*q-2.0f)*expq;
                float term2=-2.0f*scale*DOverTanD(d)*expq;
                float contrib=term1+term2;
                float solid=prm.Float("storms.hero_solid_core");
                if(vs.CastLevers)solid=vs.CastLeverData[3*i+1].Z;
                if(b.Y==Hero&&solid>0.0f)
                {
                    float disk=-2.5f*scale*(1.0f-Glsl.SmoothStep(0.80f,1.15f,q));
                    if(vs.HeroEmergence)
                    {
                        float emergence=prm.Float("storms.hero_emergence");
                        float shape=prm.Float("storms.hero_shape");
                        float taper=prm.Float("storms.hero_taper");
                        if(vs.CastLevers)
                        {
                            V4 cl2=vs.CastLeverData[3*i+2];emergence=cl2.X;shape=cl2.Y;taper=cl2.Z;
                        }
                        float qh=q,tcomp=1.0f;
                        float flowAsp=prm.Float("storms.hero_flow_aspect");
                        if(shape>0.0f||taper>0.0f||flowAsp!=1.0f)
                        {
                            V3 hcs=a.XYZ;V3 hews=Glsl.Cross(new V3(0,1,0),hcs);float hewls=Glsl.Length(hews);
                            if(hewls>1e-4f)
                            {
                                V3 hs1=hews/hewls;float qb=q,aspf=asp;
                                if(flowAsp!=1.0f)
                                {
                                    aspf=asp*flowAsp;V3 hs2b=Glsl.Cross(hcs,hs1);
                                    float xq=Glsl.Dot(p,hs1)/aspf,yq=Glsl.Dot(p,hs2b);
                                    qb=Glsl.Dot(p,hcs)>0.0f?Glsl.Length(new V2(xq,yq))/r:1e3f;
                                    tcomp=c.HeroFlowRenorm;
                                }
                                float rr=1.0f;
                                if(shape>0.0f)
                                {
                                    V3 hs2=Glsl.Cross(hcs,hs1);float hth=MathF.Atan2(Glsl.Dot(p,hs2),Glsl.Dot(p,hs1));
                                    float thp=3.14159265f-hth;
                                    float neq=a.Y<0.0f?MathF.Max(MathF.Sin(hth),0):MathF.Max(-MathF.Sin(hth),0);
                                    V3 sph=vs.Static.HeroShapePhase;
                                    rr-=shape*emergence*(0.11f*neq*neq-0.075f*MathF.Sin(2.0f*thp+sph.X)-0.055f*MathF.Sin(3.0f*thp+sph.Y));
                                }
                                if(taper>0.0f)
                                {
                                    float uct=Glsl.Clamp(meta.X*Glsl.Dot(p,hs1)/(aspf*MathF.Max(r*qb,1e-5f)),-1,1);
                                    float tc=MathF.Max(uct,0),tc2=tc*tc,tw=6.75f*tc2*tc2*(1.0f-tc2);
                                    rr-=0.25f*taper*emergence*tw;rr=MathF.Max(rr,0.4f);
                                    tcomp*=1.0f/(1.0f-0.105f*taper*emergence);
                                }
                                qh=qb/rr;
                            }
                        }
                        float ring=-6.0f*scale*(Glsl.SmoothStep(0.29f,0.55f,qh)-Glsl.SmoothStep(0.78f,1.04f,qh));
                        ring+=scale*(Glsl.SmoothStep(1.05f,1.35f,qh)-Glsl.SmoothStep(1.8f,2.4f,qh));
                        ring*=tcomp;
                        disk=Glsl.Mix(disk,ring,emergence);
                    }
                    contrib=Glsl.Mix(contrib,disk,solid);
                }
                else if(b.Y==Oval&&prm.Float("storms.oval_solid_core")>0.0f&&r>=OvalSolidMinR)
                {
                    float disk=-2.5f*scale*(1.0f-Glsl.SmoothStep(0.80f,1.15f,q));
                    contrib=Glsl.Mix(contrib,disk,prm.Float("storms.oval_solid_core"));
                }
                if(MathF.Abs(contrib)<eps*MathF.Abs(scale))continue;
                omega+=contrib;
            }
            return omega;
        }

        private static float HeroEllipQ(V3 p,int i,float qmax,VortexStampContext c)
        {
            V4 a=c.VortexData[3*i];float asp=c.VortexData[3*i+2].Y;float cd=Glsl.Dot(p,a.XYZ);
            float s2=1.0f-cd*cd,lim=qmax*MathF.Max(asp,1.0f)*a.W;if(s2>lim*lim)return 1e3f;
            if(asp==1.0f)return MathF.Acos(Glsl.Clamp(cd,-1,1))/a.W;
            V3 cc=a.XYZ,ew=Glsl.Cross(new V3(0,1,0),cc);float ewl=Glsl.Length(ew);
            if(ewl<1e-4f)return MathF.Acos(Glsl.Clamp(cd,-1,1))/a.W;
            V3 e1=ew/ewl,e2=Glsl.Cross(cc,e1);
            return cd>0.0f?Glsl.Length(new V2(Glsl.Dot(p,e1)/asp,Glsl.Dot(p,e2)))/a.W:1e3f;
        }
    }
}
