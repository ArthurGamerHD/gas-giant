using System;
using GasGiantNet.MathCore;

namespace GasGiantNet.Render
{
    internal static class Worley3D
    {
        // Direct-mapped thread-local cache. A collision merely recomputes the
        // original Hash3 value, so this cannot alter the resulting noise.
        private const int CacheSize=4096;

        private struct CacheEntry
        {
            public int X,Y,Z;
            public V3 Value;
            public bool Valid;
        }

        [ThreadStatic]
        private static CacheEntry[] _cache;

        private static V3 Hash3(V3 p)
        {
            V3 q = new V3(
                Glsl.Dot(p, new V3(127.1f, 311.7f, 74.7f)),
                Glsl.Dot(p, new V3(269.5f, 183.3f, 246.1f)),
                Glsl.Dot(p, new V3(113.5f, 271.9f, 124.6f)));
            return new V3(Glsl.Fract(MathF.Sin(q.X) * 43758.5453123f),
                          Glsl.Fract(MathF.Sin(q.Y) * 43758.5453123f),
                          Glsl.Fract(MathF.Sin(q.Z) * 43758.5453123f));
        }

        private static V3 FeaturePoint(int x,int y,int z)
        {
            CacheEntry[] cache=_cache;
            if(cache==null)
            {
                cache=new CacheEntry[CacheSize];
                _cache=cache;
            }

            int hash=unchecked(x*73856093 ^ y*19349663 ^ z*83492791);
            int slot=hash&(CacheSize-1);
            CacheEntry e=cache[slot];

            if(e.Valid&&e.X==x&&e.Y==y&&e.Z==z)
                return e.Value;

            V3 value=Hash3(new V3(x,y,z));
            e.X=x;e.Y=y;e.Z=z;e.Value=value;e.Valid=true;
            cache[slot]=e;
            return value;
        }

        public static float F1(V3 p)
        {
            int ix=(int)MathF.Floor(p.X);
            int iy=(int)MathF.Floor(p.Y);
            int iz=(int)MathF.Floor(p.Z);
            V3 fp = new V3(Glsl.Fract(p.X), Glsl.Fract(p.Y), Glsl.Fract(p.Z));
            float f1 = 1e9f;
            for (int k = -1; k <= 1; k++)
                for (int j = -1; j <= 1; j++)
                    for (int i = -1; i <= 1; i++)
                    {
                        V3 o=FeaturePoint(ix+i,iy+j,iz+k);
                        V3 r=new V3(i+o.X-fp.X,
                                    j+o.Y-fp.Y,
                                    k+o.Z-fp.Z);
                        float d = Glsl.Dot(r, r);
                        if (d < f1) f1 = d;
                    }
            return MathF.Sqrt(f1);
        }
    }
}
