using System;
using GasGiantNet.MathCore;
using GasGiantNet.Sim;

namespace GasGiantNet.Export
{
    internal static class FlowResampleCpu
    {
        private const float BlendLo=64.0f*Glsl.PI/180.0f;
        private const float BlendHi=67.0f*Glsl.PI/180.0f;

        public static FloatTexture Resample(CpuSimulation sim,int originX,int originY,int width,int height,int fullWidth,int fullHeight,int threads)
        {
            FloatTexture output=new FloatTexture(width,height,4);
            CpuParallel.ForRows(height,threads,delegate(int py)
            {
                for(int px=0;px<width;px++)
                {
                    V2 uv=new V2((px+originX+0.5f)/fullWidth,(py+originY+0.5f)/fullHeight);
                    V2 vel=sim.Equirect.Velocity.SampleLinear2(uv);
                    float lat=0.5f*Glsl.PI-uv.Y*Glsl.PI;
                    float fw=Glsl.SmoothStep(BlendLo,BlendHi,MathF.Abs(lat));
                    if(fw>0.0f)
                    {
                        float lon=uv.X*2.0f*Glsl.PI-Glsl.PI;
                        float rho=0.5f*Glsl.PI-MathF.Abs(lat);
                        V2 st=new V2(rho*MathF.Cos(lon),rho*MathF.Sin(lon));
                        V2 puv=st/sim.Equirect.RhoMax*0.5f+new V2(0.5f,0.5f);
                        V2 vp=(lat>=0.0f?sim.North.Velocity:sim.South.Velocity).SampleLinear2(puv);
                        vel=Glsl.Mix(vel,vp,fw);
                    }
                    output.Set4(px,py,new V4(vel.X,vel.Y,0.0f,1.0f));
                }
            });
            return output;
        }
    }
}
