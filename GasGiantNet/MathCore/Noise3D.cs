using System;

namespace GasGiantNet.MathCore
{
    // Line-for-line scalar port of sim/kernels/noise3d.glsl (Ashima 3D simplex noise).
    internal static class Noise3D
    {
        private static float Mod289(float x) { return x - MathF.Floor(x * (1.0f / 289.0f)) * 289.0f; }
        private static float Permute(float x) { return Mod289(((x * 34.0f) + 10.0f) * x); }
        private static float TaylorInvSqrt(float r) { return 1.79284291400159f - 0.85373472095314f * r; }

        public static float SNoise(V3 v)
        {
            const float Cx = 1.0f / 6.0f;
            const float Cy = 1.0f / 3.0f;

            float dotv = (v.X + v.Y + v.Z) * Cy;
            float ix = MathF.Floor(v.X + dotv);
            float iy = MathF.Floor(v.Y + dotv);
            float iz = MathF.Floor(v.Z + dotv);
            float doti = (ix + iy + iz) * Cx;
            float x0x = v.X - ix + doti;
            float x0y = v.Y - iy + doti;
            float x0z = v.Z - iz + doti;

            float gx = Glsl.Step(x0y, x0x);
            float gy = Glsl.Step(x0z, x0y);
            float gz = Glsl.Step(x0x, x0z);
            float lx = 1.0f - gx;
            float ly = 1.0f - gy;
            float lz = 1.0f - gz;
            float i1x = MathF.Min(gx, lz);
            float i1y = MathF.Min(gy, lx);
            float i1z = MathF.Min(gz, ly);
            float i2x = MathF.Max(gx, lz);
            float i2y = MathF.Max(gy, lx);
            float i2z = MathF.Max(gz, ly);

            float x1x = x0x - i1x + Cx;
            float x1y = x0y - i1y + Cx;
            float x1z = x0z - i1z + Cx;
            float x2x = x0x - i2x + Cy;
            float x2y = x0y - i2y + Cy;
            float x2z = x0z - i2z + Cy;
            float x3x = x0x - 0.5f;
            float x3y = x0y - 0.5f;
            float x3z = x0z - 0.5f;

            ix = Mod289(ix); iy = Mod289(iy); iz = Mod289(iz);
            float p0 = Permute(Permute(Permute(iz + 0.0f) + iy + 0.0f) + ix + 0.0f);
            float p1 = Permute(Permute(Permute(iz + i1z) + iy + i1y) + ix + i1x);
            float p2 = Permute(Permute(Permute(iz + i2z) + iy + i2y) + ix + i2x);
            float p3 = Permute(Permute(Permute(iz + 1.0f) + iy + 1.0f) + ix + 1.0f);

            const float n_ = 0.142857142857f;
            // GLSL: ns = n_ * D.wyz - D.xzx, D=(0,0.5,1,2)
            //        = (2/7, 0.5/7 - 1, 1/7)
            float nsx = n_ * 2.0f;
            float nsy = n_ * 0.5f - 1.0f;
            float nsz = n_;

            float j0 = p0 - 49.0f * MathF.Floor(p0 * nsz * nsz);
            float j1 = p1 - 49.0f * MathF.Floor(p1 * nsz * nsz);
            float j2 = p2 - 49.0f * MathF.Floor(p2 * nsz * nsz);
            float j3 = p3 - 49.0f * MathF.Floor(p3 * nsz * nsz);

            float x_0 = MathF.Floor(j0 * nsz), x_1 = MathF.Floor(j1 * nsz), x_2 = MathF.Floor(j2 * nsz), x_3 = MathF.Floor(j3 * nsz);
            float y_0 = MathF.Floor(j0 - 7.0f * x_0), y_1 = MathF.Floor(j1 - 7.0f * x_1), y_2 = MathF.Floor(j2 - 7.0f * x_2), y_3 = MathF.Floor(j3 - 7.0f * x_3);
            float xx0 = x_0 * nsx + nsy, xx1 = x_1 * nsx + nsy, xx2 = x_2 * nsx + nsy, xx3 = x_3 * nsx + nsy;
            float yy0 = y_0 * nsx + nsy, yy1 = y_1 * nsx + nsy, yy2 = y_2 * nsx + nsy, yy3 = y_3 * nsx + nsy;
            float h0 = 1.0f - MathF.Abs(xx0) - MathF.Abs(yy0);
            float h1 = 1.0f - MathF.Abs(xx1) - MathF.Abs(yy1);
            float h2 = 1.0f - MathF.Abs(xx2) - MathF.Abs(yy2);
            float h3 = 1.0f - MathF.Abs(xx3) - MathF.Abs(yy3);

            float s00 = MathF.Floor(xx0) * 2.0f + 1.0f;
            float s01 = MathF.Floor(yy0) * 2.0f + 1.0f;
            float s10 = MathF.Floor(xx1) * 2.0f + 1.0f;
            float s11 = MathF.Floor(yy1) * 2.0f + 1.0f;
            float s20 = MathF.Floor(xx2) * 2.0f + 1.0f;
            float s21 = MathF.Floor(yy2) * 2.0f + 1.0f;
            float s30 = MathF.Floor(xx3) * 2.0f + 1.0f;
            float s31 = MathF.Floor(yy3) * 2.0f + 1.0f;
            float sh0 = -Glsl.Step(h0, 0.0f), sh1 = -Glsl.Step(h1, 0.0f), sh2 = -Glsl.Step(h2, 0.0f), sh3 = -Glsl.Step(h3, 0.0f);

            float a00 = xx0 + s00 * sh0, a01 = yy0 + s01 * sh0;
            float a10 = xx1 + s10 * sh1, a11 = yy1 + s11 * sh1;
            float a20 = xx2 + s20 * sh2, a21 = yy2 + s21 * sh2;
            float a30 = xx3 + s30 * sh3, a31 = yy3 + s31 * sh3;

            float norm0 = TaylorInvSqrt(a00 * a00 + a01 * a01 + h0 * h0);
            float norm1 = TaylorInvSqrt(a10 * a10 + a11 * a11 + h1 * h1);
            float norm2 = TaylorInvSqrt(a20 * a20 + a21 * a21 + h2 * h2);
            float norm3 = TaylorInvSqrt(a30 * a30 + a31 * a31 + h3 * h3);
            a00 *= norm0; a01 *= norm0; h0 *= norm0;
            a10 *= norm1; a11 *= norm1; h1 *= norm1;
            a20 *= norm2; a21 *= norm2; h2 *= norm2;
            a30 *= norm3; a31 *= norm3; h3 *= norm3;

            float m0 = MathF.Max(0.5f - (x0x * x0x + x0y * x0y + x0z * x0z), 0.0f);
            float m1 = MathF.Max(0.5f - (x1x * x1x + x1y * x1y + x1z * x1z), 0.0f);
            float m2 = MathF.Max(0.5f - (x2x * x2x + x2y * x2y + x2z * x2z), 0.0f);
            float m3 = MathF.Max(0.5f - (x3x * x3x + x3y * x3y + x3z * x3z), 0.0f);
            m0 *= m0; m1 *= m1; m2 *= m2; m3 *= m3;
            float d0 = a00 * x0x + a01 * x0y + h0 * x0z;
            float d1 = a10 * x1x + a11 * x1y + h1 * x1z;
            float d2 = a20 * x2x + a21 * x2y + h2 * x2z;
            float d3 = a30 * x3x + a31 * x3y + h3 * x3z;
            return 105.0f * (m0 * m0 * d0 + m1 * m1 * d1 + m2 * m2 * d2 + m3 * m3 * d3);
        }

        public static float Fbm(V3 p, int octaves, float lacunarity, float gain)
        {
            float sum = 0.0f;
            float amp = 0.5f;
            float norm = 0.0f;
            for (int i = 0; i < octaves; i++)
            {
                sum += amp * SNoise(p);
                norm += amp;
                p = p * lacunarity;
                amp *= gain;
            }
            return sum / MathF.Max(norm, 1e-6f);
        }
    }
}
