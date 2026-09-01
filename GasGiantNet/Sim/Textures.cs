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

        // Specialized scalar and RG samplers avoid constructing/filling a V4
        // and avoid four TexelFetch4 calls in the hottest velocity/scalar paths.
        public float SampleLinear1(V2 uv)
        {
            float gx=uv.X*Width-0.5f, gy=uv.Y*Height-0.5f;
            int x0=(int)MathF.Floor(gx), y0=(int)MathF.Floor(gy);
            float fx=gx-x0, fy=gy-y0;

            int ax0=AddressX(x0), ax1=AddressX(x0+1);
            int ay0=AddressY(y0), ay1=AddressY(y0+1);

            int i00=((ay0*Width+ax0)*Channels);
            int i10=((ay0*Width+ax1)*Channels);
            int i01=((ay1*Width+ax0)*Channels);
            int i11=((ay1*Width+ax1)*Channels);

            float a=Data[i00], b=Data[i10];
            float c=Data[i01], d=Data[i11];
            return Glsl.Mix(Glsl.Mix(a,b,fx),Glsl.Mix(c,d,fx),fy);
        }

        public V2 SampleLinear2(V2 uv)
        {
            if(Channels<2) return new V2(SampleLinear1(uv),0.0f);

            float gx=uv.X*Width-0.5f, gy=uv.Y*Height-0.5f;
            int x0=(int)MathF.Floor(gx), y0=(int)MathF.Floor(gy);
            float fx=gx-x0, fy=gy-y0;

            int ax0=AddressX(x0), ax1=AddressX(x0+1);
            int ay0=AddressY(y0), ay1=AddressY(y0+1);

            int i00=((ay0*Width+ax0)*Channels);
            int i10=((ay0*Width+ax1)*Channels);
            int i01=((ay1*Width+ax0)*Channels);
            int i11=((ay1*Width+ax1)*Channels);

            float xA=Glsl.Mix(Data[i00],Data[i10],fx);
            float xB=Glsl.Mix(Data[i01],Data[i11],fx);
            float yA=Glsl.Mix(Data[i00+1],Data[i10+1],fx);
            float yB=Glsl.Mix(Data[i01+1],Data[i11+1],fx);

            return new V2(Glsl.Mix(xA,xB,fy),
                          Glsl.Mix(yA,yB,fy));
        }

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

            // Tracer textures are RGBA. Resolve wrapping/clamping once for the
            // entire 4x4 footprint, then index the backing array directly.
            if(Channels==4)
            {
                int x0=AddressX(bx-1), x1=AddressX(bx);
                int x2=AddressX(bx+1), x3=AddressX(bx+2);
                int y0=AddressY(by-1), y1=AddressY(by);
                int y2=AddressY(by+1), y3=AddressY(by+2);

                V4 acc4=new V4(0,0,0,0);
                acc4+=CatmullRow4(y0,x0,x1,x2,x3,wx)*wy.X;
                acc4+=CatmullRow4(y1,x0,x1,x2,x3,wx)*wy.Y;
                acc4+=CatmullRow4(y2,x0,x1,x2,x3,wx)*wy.Z;
                acc4+=CatmullRow4(y3,x0,x1,x2,x3,wx)*wy.W;
                return acc4;
            }

            V4 acc=new V4(0,0,0,0);
            for(int j=0;j<4;j++)
            {
                V4 row=new V4(0,0,0,0);
                for(int i=0;i<4;i++) row += TexelFetch4(bx+i-1, by+j-1) * wx[i];
                acc += row * wy[j];
            }
            return acc;
        }

        private V4 CatmullRow4(int y,int x0,int x1,int x2,int x3,V4 w)
        {
            int row=y*Width*4;
            int i0=row+x0*4, i1=row+x1*4, i2=row+x2*4, i3=row+x3*4;
            V4 q0=new V4(Data[i0],Data[i0+1],Data[i0+2],Data[i0+3]);
            V4 q1=new V4(Data[i1],Data[i1+1],Data[i1+2],Data[i1+3]);
            V4 q2=new V4(Data[i2],Data[i2+1],Data[i2+2],Data[i2+3]);
            V4 q3=new V4(Data[i3],Data[i3+1],Data[i3+2],Data[i3+3]);
            return q0*w.X+q1*w.Y+q2*w.Z+q3*w.W;
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
