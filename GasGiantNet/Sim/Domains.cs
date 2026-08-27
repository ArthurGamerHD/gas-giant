using System;
using GasGiantNet.MathCore;

namespace GasGiantNet.Sim
{
    internal enum DomainKind
    {
        Equirect = 0,
        NorthPatch = 1,
        SouthPatch = 2
    }

    internal sealed class SimDomain
    {
        public readonly DomainKind Kind;
        public readonly int Width;
        public readonly int Height;
        public readonly float RhoMax;
        public FloatTexture Cur;
        public FloatTexture Fwd;
        public FloatTexture Back;
        public FloatTexture Out;
        public FloatTexture Psi;
        public FloatTexture Velocity;
        public FloatTexture Omega;
        public FloatTexture OmegaFwd;
        public FloatTexture OmegaBack;
        public FloatTexture OmegaOut;
        public FloatTexture OmegaRel;
        public FloatTexture OmegaLap;
        public FloatTexture PsiAnalytic;
        public FloatTexture PsiNext;

        public SimDomain(DomainKind kind, int width, int height, float rhoMax)
        {
            Kind = kind;
            Width = width;
            Height = height;
            RhoMax = rhoMax;
            Cur = new FloatTexture(width, height, 4);
            Fwd = new FloatTexture(width, height, 4);
            Back = new FloatTexture(width, height, 4);
            Out = new FloatTexture(width, height, 4);
            Psi = new FloatTexture(width, height, 1);
            Velocity = new FloatTexture(width, height, 2);
            Omega = new FloatTexture(width, height, 1);
            OmegaFwd = new FloatTexture(width, height, 1);
            OmegaBack = new FloatTexture(width, height, 1);
            OmegaOut = new FloatTexture(width, height, 1);
            OmegaRel = new FloatTexture(width, height, 1);
            OmegaLap = new FloatTexture(width, height, 1);
            PsiAnalytic = new FloatTexture(width, height, 1);
            PsiNext = new FloatTexture(width, height, 1);
            bool repeat = kind == DomainKind.Equirect;
            FloatTexture[] all = new FloatTexture[] { Cur, Fwd, Back, Out, Psi, Velocity, Omega, OmegaFwd, OmegaBack, OmegaOut, OmegaRel, OmegaLap, PsiAnalytic, PsiNext };
            for (int i = 0; i < all.Length; i++) all[i].RepeatX = repeat;
        }

        public void CommitTracer()
        {
            FloatTexture t = Cur; Cur = Out; Out = t;
        }

        public void CommitOmega()
        {
            FloatTexture t = Omega; Omega = OmegaOut; OmegaOut = t;
        }

        public void CommitPsi()
        {
            FloatTexture t = Psi; Psi = PsiNext; PsiNext = t;
        }
    }

    internal static class DomainMath
    {
        public const float Pi = 3.14159265358979f;

        public static V2 LonLatAtPos(DomainKind domain, V2 pixPos, int width, int height, float rhoMax)
        {
            if (domain == DomainKind.Equirect)
            {
                float lon = (pixPos.X / width) * 2f * Pi - Pi;
                float lat = 0.5f * Pi - (pixPos.Y / height) * Pi;
                return new V2(lon, lat);
            }
            V2 st = new V2((pixPos.X / width * 2f - 1f) * rhoMax,
                           (pixPos.Y / height * 2f - 1f) * rhoMax);
            float rho = Glsl.Length(st);
            float lonp = rho < 1e-6f ? 0f : MathF.Atan2(st.Y, st.X);
            float sign = domain == DomainKind.NorthPatch ? 1f : -1f;
            float latp = sign * (0.5f * Pi - rho);
            return new V2(lonp, latp);
        }

        public static V2 LonLatAt(DomainKind domain, int x, int y, int width, int height, float rhoMax)
        {
            return LonLatAtPos(domain, new V2(x + 0.5f, y + 0.5f), width, height, rhoMax);
        }

        public static V3 SpherePoint(V2 ll)
        {
            float cl = MathF.Cos(ll.Y);
            return new V3(cl * MathF.Cos(ll.X), MathF.Sin(ll.Y), cl * MathF.Sin(ll.X));
        }

        public static float LatProfileU(float lat)
        {
            return Glsl.Clamp((0.5f * Pi - lat) / Pi, 0f, 1f);
        }

        public static int WrapX(DomainKind domain, int x, int w)
        {
            if (domain == DomainKind.Equirect)
            {
                int r = x % w;
                return r < 0 ? r + w : r;
            }
            return x < 0 ? 0 : (x >= w ? w - 1 : x);
        }

        public static int ClampY(int y, int h)
        {
            return y < 0 ? 0 : (y >= h ? h - 1 : y);
        }

        public static V2 PatchStFromPix(V2 pixPos, int width, int height, float rhoMax)
        {
            return new V2((pixPos.X / width * 2f - 1f) * rhoMax,
                          (pixPos.Y / height * 2f - 1f) * rhoMax);
        }

        public static V2 PatchPixFromSt(V2 st, int width, int height, float rhoMax)
        {
            return new V2((st.X / rhoMax * 0.5f + 0.5f) * width,
                          (st.Y / rhoMax * 0.5f + 0.5f) * height);
        }

        public static V2 PatchVelocity(DomainKind domain, V2 st, V2 velEn)
        {
            float rho = Glsl.Length(st);
            if (rho < 1e-5f) return new V2(0f, 0f);
            V2 er = st / rho;
            V2 et = new V2(-er.Y, er.X);
            float metric = rho / MathF.Max(MathF.Sin(rho), 1e-5f);
            float sign = domain == DomainKind.NorthPatch ? 1f : -1f;
            return et * (velEn.X * metric) + er * (-sign * velEn.Y);
        }
    }
}
