using System;
using GasGiantNet.MathCore;

namespace GasGiantNet.Render
{
    internal static class Worley3D
    {
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

        public static float F1(V3 p)
        {
            V3 ip = new V3(MathF.Floor(p.X), MathF.Floor(p.Y), MathF.Floor(p.Z));
            V3 fp = new V3(Glsl.Fract(p.X), Glsl.Fract(p.Y), Glsl.Fract(p.Z));
            float f1 = 1e9f;
            for (int k = -1; k <= 1; k++)
                for (int j = -1; j <= 1; j++)
                    for (int i = -1; i <= 1; i++)
                    {
                        V3 g = new V3(i, j, k);
                        V3 o = Hash3(ip + g);
                        V3 r = g + o - fp;
                        float d = Glsl.Dot(r, r);
                        if (d < f1) f1 = d;
                    }
            return MathF.Sqrt(f1);
        }
    }
}
