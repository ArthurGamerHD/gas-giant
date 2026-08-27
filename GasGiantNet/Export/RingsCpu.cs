using System;
using GasGiantNet.Config;
using GasGiantNet.MathCore;
using GasGiantNet.Random;
using GasGiantNet.Sim;

namespace GasGiantNet.Export
{
    internal static class RingsCpu
    {
        public const int RingWidth=2048;  // radial axis: upstream ndarray axis 0
        public const int RingHeight=64;  // tangential axis: upstream ndarray axis 1
        private static readonly double[,] Table=new double[,] {
            {0.000,0.00},{0.020,0.08},{0.250,0.12},{0.281,0.90},{0.360,1.80},{0.500,2.20},
            {0.620,1.70},{0.690,1.30},{0.696,0.10},{0.730,0.08},{0.762,0.12},{0.766,0.60},
            {0.850,0.90},{0.945,0.80},{0.949,0.10},{0.956,0.80},{0.990,0.50},{1.000,0.00}
        };

        // Returns a texture with file dimensions 64x2048, matching the actual
        // upstream ndarray shape (2048,64,4) passed to the EXR writer.
        public static FloatTexture Build(ParamTree p)
        {
            float[] tau=new float[RingWidth];
            for(int r=0;r<RingWidth;r++)tau[r]=(float)Interp((r+0.5)/(double)RingWidth);
            NumpyGenerator rng=NumpyGenerator.Subseed(p.Int("seed"),"rings");
            float grain=p.Float("rings.fine_grain");
            float[] rg=new float[RingWidth],yg=new float[RingHeight];
            for(int i=0;i<RingWidth;i++)rg[i]=1.0f+grain*0.6f*((float)rng.Random()-0.5f);
            for(int i=0;i<RingHeight;i++)yg[i]=1.0f+grain*0.15f*((float)rng.Random()-0.5f);
            float opacity=p.Float("rings.opacity"),brightness=p.Float("rings.brightness");
            float[] tint=p.FloatArray("rings.tint_color");
            FloatTexture tex=new FloatTexture(RingHeight,RingWidth,4);
            for(int radial=0;radial<RingWidth;radial++)
            {
                for(int tang=0;tang<RingHeight;tang++)
                {
                    float tg=MathF.Max(0.0f,tau[radial]*rg[radial]*yg[tang]);
                    float alpha=Glsl.Clamp((1.0f-MathF.Exp(-tg))*opacity,0.0f,1.0f);
                    float reflect=Glsl.Clamp(1.0f-MathF.Exp(-1.3f*tg),0.0f,1.0f);
                    tex.Set4(tang,radial,new V4(
                        Glsl.Clamp(reflect*tint[0]*brightness,0.0f,1.0f),
                        Glsl.Clamp(reflect*tint[1]*brightness,0.0f,1.0f),
                        Glsl.Clamp(reflect*tint[2]*brightness,0.0f,1.0f),alpha));
                }
            }
            return tex;
        }

        private static double Interp(double x)
        {
            int n=Table.GetLength(0);if(x<=Table[0,0])return Table[0,1];if(x>=Table[n-1,0])return Table[n-1,1];
            int hi=1;while(hi<n&&Table[hi,0]<x)hi++;int lo=hi-1;double t=(x-Table[lo,0])/(Table[hi,0]-Table[lo,0]);return Table[lo,1]+(Table[hi,1]-Table[lo,1])*t;
        }
    }
}
