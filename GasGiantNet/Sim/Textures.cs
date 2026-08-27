using System;
using GasGiantNet.MathCore;

namespace GasGiantNet.Sim
{
    internal sealed class FloatTexture
    {
        public readonly int Width;
        public readonly int Height;
        public readonly int Channels;
        public readonly float[] Data;
        public bool RepeatX;
        public bool RepeatY;

        public FloatTexture(int width, int height, int channels)
        {
            Width = width;
            Height = height;
            Channels = channels;
            Data = new float[checked(width * height * channels)];
        }

        public int Index(int x, int y, int c) { return ((y * Width + x) * Channels) + c; }
        public float Get(int x, int y, int c) { return Data[Index(x, y, c)]; }
        public void Set(int x, int y, int c, float value) { Data[Index(x, y, c)] = value; }

        private int AddressX(int x)
        {
            if (RepeatX) return Glsl.Mod(x, Width);
            return Glsl.Clamp(x, 0, Width - 1);
        }
        private int AddressY(int y)
        {
            if (RepeatY) return Glsl.Mod(y, Height);
            return Glsl.Clamp(y, 0, Height - 1);
        }

        public V4 TexelFetch4(int x, int y)
        {
            x = AddressX(x); y = AddressY(y);
            int i = Index(x, y, 0);
            return new V4(Data[i], Channels > 1 ? Data[i + 1] : 0f, Channels > 2 ? Data[i + 2] : 0f, Channels > 3 ? Data[i + 3] : 0f);
        }
        public float TexelFetch1(int x, int y) { return Get(AddressX(x), AddressY(y), 0); }
        public V2 TexelFetch2(int x, int y)
        {
            x = AddressX(x); y = AddressY(y); int i = Index(x,y,0);
            return new V2(Data[i], Channels > 1 ? Data[i+1] : 0f);
        }

        public V2 Get2(int x, int y) { return TexelFetch2(x,y); }
        public V3 Get3(int x, int y)
        {
            V4 v = TexelFetch4(x,y); return new V3(v.X,v.Y,v.Z);
        }
        public V4 Get4(int x, int y) { return TexelFetch4(x,y); }
        public void Set2(int x, int y, V2 v) { int i=Index(x,y,0); Data[i]=v.X; if(Channels>1)Data[i+1]=v.Y; }
        public void Set4(int x, int y, V4 v)
        {
            int i = Index(x,y,0); Data[i]=v.X; if(Channels>1)Data[i+1]=v.Y; if(Channels>2)Data[i+2]=v.Z; if(Channels>3)Data[i+3]=v.W;
        }

        // OpenGL normalized texture() linear-filter semantics: normalized uv is
        // mapped to pixel space uv*size - 0.5, then the 2x2 texel neighborhood
        // is filtered under the texture's wrap modes.
        public V4 SampleLinear(V2 uv)
        {
            return SampleLinearPixel(new V2(uv.X * Width, uv.Y * Height));
        }

        // Continuous pixel-center convention used by the shaders: pixel center i
        // has coordinate i+0.5.
        public V4 SampleLinearPixel(V2 pix)
        {
            float gx = pix.X - 0.5f;
            float gy = pix.Y - 0.5f;
            int x0 = (int)MathF.Floor(gx);
            int y0 = (int)MathF.Floor(gy);
            float fx = gx - x0;
            float fy = gy - y0;
            V4 a = TexelFetch4(x0, y0);
            V4 b = TexelFetch4(x0 + 1, y0);
            V4 c = TexelFetch4(x0, y0 + 1);
            V4 d = TexelFetch4(x0 + 1, y0 + 1);
            return Glsl.Mix(Glsl.Mix(a,b,fx), Glsl.Mix(c,d,fx), fy);
        }

        public float SampleLinear1(V2 uv) { return SampleLinear(uv).X; }
        public V2 SampleLinear2(V2 uv) { V4 q=SampleLinear(uv); return new V2(q.X,q.Y); }

        private static V4 CrWeights(float t)
        {
            float t2=t*t, t3=t2*t;
            return new V4(-0.5f*t3+t2-0.5f*t, 1.5f*t3-2.5f*t2+1f, -1.5f*t3+2f*t2+0.5f*t, 0.5f*t3-0.5f*t2);
        }

        public V4 SampleCatmullRomPixel(V2 pos)
        {
            float gx=pos.X-0.5f, gy=pos.Y-0.5f;
            int bx=(int)MathF.Floor(gx), by=(int)MathF.Floor(gy);
            float fx=gx-bx, fy=gy-by;
            V4 wx=CrWeights(fx), wy=CrWeights(fy);
            V4 acc=new V4(0,0,0,0);
            for(int j=0;j<4;j++)
            {
                V4 row=new V4(0,0,0,0);
                for(int i=0;i<4;i++) row += TexelFetch4(bx+i-1, by+j-1) * wx[i];
                acc += row * wy[j];
            }
            return acc;
        }

        public void MinMax2x2Pixel(V2 pos, out V4 lo, out V4 hi)
        {
            int bx=(int)MathF.Floor(pos.X-0.5f), by=(int)MathF.Floor(pos.Y-0.5f);
            lo=new V4(float.PositiveInfinity,float.PositiveInfinity,float.PositiveInfinity,float.PositiveInfinity);
            hi=new V4(float.NegativeInfinity,float.NegativeInfinity,float.NegativeInfinity,float.NegativeInfinity);
            for(int j=0;j<2;j++) for(int i=0;i<2;i++)
            {
                V4 q=TexelFetch4(bx+i,by+j); lo=Glsl.Min(lo,q); hi=Glsl.Max(hi,q);
            }
        }

        public void Clear(float value) { for(int i=0;i<Data.Length;i++) Data[i]=value; }
        public void CopyFrom(FloatTexture other)
        {
            if(other.Width!=Width||other.Height!=Height||other.Channels!=Channels) throw new ArgumentException("texture shape mismatch");
            Array.Copy(other.Data,Data,Data.Length);
        }
    }

    internal sealed class LatLut
    {
        private readonly float[] _data;
        private readonly int _samples;
        private readonly int _channels;
        public LatLut(float[] data,int samples,int channels){_data=data;_samples=samples;_channels=channels;}
        public V4 Sample(float u)
        {
            u=Glsl.Clamp(u,0f,1f); float pos=u*_samples-0.5f; int i0=(int)MathF.Floor(pos); float f=pos-i0;
            if(i0<0){i0=0;f=0f;} int i1=i0+1; if(i1>=_samples){i1=_samples-1;if(i0>=_samples)i0=_samples-1;}
            return Glsl.Mix(At(i0),At(i1),f);
        }
        private V4 At(int i)
        {
            int p=i*_channels; return new V4(_data[p],_channels>1?_data[p+1]:0f,_channels>2?_data[p+2]:0f,_channels>3?_data[p+3]:0f);
        }
    }
}
