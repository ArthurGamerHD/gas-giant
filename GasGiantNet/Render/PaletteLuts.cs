using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using GasGiantNet.Config;
using GasGiantNet.MathCore;
using GasGiantNet.Sim;

namespace GasGiantNet.Render
{
    internal struct GradientStopCpu
    {
        public float Pos;
        public V3 Color;
    }

    internal struct PaletteRowCpu
    {
        public float Latitude;
        public GradientStopCpu[] Stops;
    }

    internal sealed class AppearanceLuts
    {
        public FloatTexture Palette;
        public FloatTexture Storm;
        public FloatTexture BandTint;
    }

    internal static class PaletteLuts
    {
        public static AppearanceLuts Bake(ParamTree p)
        {
            AppearanceLuts r=new AppearanceLuts();
            PaletteRowCpu[] rows=ParseRows(p.Array("appearance.palette_rows"));
            r.Palette=BakeRows(rows,256,64);
            r.Storm=BakeLut(ParseStops(p.Array("appearance.storm_tints")),256);
            r.BandTint=BakeLut(ParseStops(p.Array("appearance.band_tint_stops")),256);
            return r;
        }

        public static FloatTexture BakeLut(GradientStopCpu[] stops,int size)
        {
            if(stops==null||stops.Length==0)throw new ArgumentException("at least one gradient stop required");
            Array.Sort(stops,delegate(GradientStopCpu a,GradientStopCpu b){return a.Pos.CompareTo(b.Pos);});
            FloatTexture tex=new FloatTexture(size,1,4);
            for(int i=0;i<size;i++)
            {
                float x=(i+0.5f)/size;
                V3 c=Interp(stops,x);
                tex.Set4(i,0,new V4(c.X,c.Y,c.Z,1.0f));
            }
            return tex;
        }

        public static FloatTexture BakeRows(PaletteRowCpu[] rows,int size,int height)
        {
            if(rows==null||rows.Length==0)throw new ArgumentException("at least one palette row required");
            Array.Sort(rows,delegate(PaletteRowCpu a,PaletteRowCpu b){return a.Latitude.CompareTo(b.Latitude);});
            FloatTexture[] luts=new FloatTexture[rows.Length];
            for(int i=0;i<rows.Length;i++)luts[i]=BakeLut(rows[i].Stops,size);
            FloatTexture output=new FloatTexture(size,height,4);
            for(int y=0;y<height;y++)
            {
                float lat=-90.0f+(y+0.5f)/height*180.0f;
                int j=Search(rows,lat)-1;
                if(j<0||rows.Length==1)CopyRow(output,y,luts[0]);
                else if(j>=rows.Length-1)CopyRow(output,y,luts[rows.Length-1]);
                else
                {
                    float t=(lat-rows[j].Latitude)/(rows[j+1].Latitude-rows[j].Latitude);
                    float w=t*t*(3.0f-2.0f*t);
                    if(w<=0.0f||RowsEqual(luts[j],luts[j+1])){CopyRow(output,y,luts[j]);continue;}
                    if(w>=1.0f){CopyRow(output,y,luts[j+1]);continue;}
                    for(int x=0;x<size;x++)
                    {
                        V3 a=luts[j].Get3(x,0),b=luts[j+1].Get3(x,0);
                        double La,Aa,Ba,Lb,Ab,Bb;
                        Oklab.SrgbToOklabDouble(a.X,a.Y,a.Z,out La,out Aa,out Ba);
                        Oklab.SrgbToOklabDouble(b.X,b.Y,b.Z,out Lb,out Ab,out Bb);
                        double rr,gg,bb;
                        Oklab.OklabToSrgbDouble(La+(Lb-La)*w,Aa+(Ab-Aa)*w,Ba+(Bb-Ba)*w,out rr,out gg,out bb);
                        output.Set4(x,y,new V4((float)rr,(float)gg,(float)bb,1.0f));
                    }
                }
            }
            return output;
        }

        public static GradientStopCpu[] ParseStops(JsonArray a)
        {
            GradientStopCpu[] r=new GradientStopCpu[a.Count];
            for(int i=0;i<a.Count;i++)
            {
                JsonObject o=(JsonObject)a[i];
                JsonArray c=(JsonArray)o["color"];
                r[i]=new GradientStopCpu{Pos=(float)o["pos"].GetValue<double>(),Color=new V3((float)c[0].GetValue<double>(),(float)c[1].GetValue<double>(),(float)c[2].GetValue<double>())};
            }
            return r;
        }

        private static PaletteRowCpu[] ParseRows(JsonArray a)
        {
            PaletteRowCpu[] r=new PaletteRowCpu[a.Count];
            for(int i=0;i<a.Count;i++)
            {
                JsonObject o=(JsonObject)a[i];
                r[i]=new PaletteRowCpu{Latitude=(float)o["latitude"].GetValue<double>(),Stops=ParseStops((JsonArray)o["stops"])};
            }
            return r;
        }

        private static int Search(PaletteRowCpu[] rows,float lat)
        {
            int lo=0,hi=rows.Length;
            while(lo<hi){int m=(lo+hi)/2;if(rows[m].Latitude<lat)lo=m+1;else hi=m;}
            return lo;
        }

        private static void CopyRow(FloatTexture dst,int y,FloatTexture src)
        {
            for(int x=0;x<dst.Width;x++)dst.Set4(x,y,src.Get4(x,0));
        }

        private static bool RowsEqual(FloatTexture a,FloatTexture b)
        {
            if(a.Data.Length!=b.Data.Length)return false;
            for(int i=0;i<a.Data.Length;i++)if(a.Data[i]!=b.Data[i])return false;
            return true;
        }

        private static V3 Interp(GradientStopCpu[] s,float x)
        {
            if(x<=s[0].Pos)return s[0].Color;
            if(x>=s[s.Length-1].Pos)return s[s.Length-1].Color;
            int hi=1;while(hi<s.Length&&x>s[hi].Pos)hi++;
            int lo=hi-1;double span=(double)s[hi].Pos-s[lo].Pos;
            if(span<=0.0)return s[hi].Color;
            double t=((double)x-s[lo].Pos)/span;
            return new V3((float)(s[lo].Color.X+(s[hi].Color.X-s[lo].Color.X)*t),(float)(s[lo].Color.Y+(s[hi].Color.Y-s[lo].Color.Y)*t),(float)(s[lo].Color.Z+(s[hi].Color.Z-s[lo].Color.Z)*t));
        }
    }
}
