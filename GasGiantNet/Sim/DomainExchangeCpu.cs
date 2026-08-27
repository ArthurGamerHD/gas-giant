using System;
using GasGiantNet.MathCore;

namespace GasGiantNet.Sim
{
    internal static class DomainExchangeCpu
    {
        public static void EquirectToPatch(SimDomain patch, FloatTexture equirect, float exLo, float exHi, int threads)
        {
            int w=patch.Width,h=patch.Height;
            CpuParallel.ForRows(h,threads,delegate(int y)
            {
                for(int x=0;x<w;x++)
                {
                    V2 ll=DomainMath.LonLatAt(patch.Kind,x,y,w,h,patch.RhoMax);
                    float weight=1.0f-Glsl.SmoothStep(exLo,exHi,MathF.Abs(ll.Y));
                    if(weight<=0.0f)continue;
                    V2 uv=new V2((ll.X+Glsl.PI)/(2.0f*Glsl.PI),(0.5f*Glsl.PI-ll.Y)/Glsl.PI);
                    V4 eq=equirect.SampleLinear(uv);
                    V4 own=patch.Cur.Get4(x,y);
                    patch.Cur.Set4(x,y,Glsl.Mix(own,eq,weight));
                }
            });
        }

        public static void PatchToEquirect(SimDomain eq,SimDomain north,SimDomain south,float patchRhoMax,float exLo,float exHi,int threads)
        {
            int w=eq.Width,h=eq.Height;
            CpuParallel.ForRows(h,threads,delegate(int y)
            {
                for(int x=0;x<w;x++)
                {
                    V2 ll=DomainMath.LonLatAt(eq.Kind,x,y,w,h,eq.RhoMax);
                    float weight=Glsl.SmoothStep(exLo,exHi,MathF.Abs(ll.Y));
                    if(weight<=0.0f)continue;
                    float rho=0.5f*Glsl.PI-MathF.Abs(ll.Y);
                    V2 st=new V2(rho*MathF.Cos(ll.X),rho*MathF.Sin(ll.X));
                    V2 uv=st/patchRhoMax*0.5f+new V2(0.5f,0.5f);
                    V4 polar=(ll.Y>=0.0f?north.Cur:south.Cur).SampleLinear(uv);
                    V4 own=eq.Cur.Get4(x,y);
                    eq.Cur.Set4(x,y,Glsl.Mix(own,polar,weight));
                }
            });
        }
    }
}
