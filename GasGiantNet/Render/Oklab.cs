using System;
using GasGiantNet.MathCore;

namespace GasGiantNet.Render
{
    internal static class Oklab
    {
        public static V3 SrgbToOklab(V3 c)
        {
            c = Glsl.Clamp(c, 0.0f, 1.0f);
            V3 lin = new V3(SrgbToLinear(c.X), SrgbToLinear(c.Y), SrgbToLinear(c.Z));
            float l = 0.4122214708f * lin.X + 0.5363325363f * lin.Y + 0.0514459929f * lin.Z;
            float m = 0.2119034982f * lin.X + 0.6806995451f * lin.Y + 0.1073969566f * lin.Z;
            float s = 0.0883024619f * lin.X + 0.2817188376f * lin.Y + 0.6299787005f * lin.Z;
            l = CbrtNonnegative(l); m = CbrtNonnegative(m); s = CbrtNonnegative(s);
            return new V3(
                0.2104542553f*l + 0.7936177850f*m - 0.0040720468f*s,
                1.9779984951f*l - 2.4285922050f*m + 0.4505937099f*s,
                0.0259040371f*l + 0.7827717662f*m - 0.8086757660f*s);
        }

        public static V3 OklabToSrgb(V3 lab)
        {
            float l = lab.X + 0.3963377774f*lab.Y + 0.2158037573f*lab.Z;
            float m = lab.X - 0.1055613458f*lab.Y - 0.0638541728f*lab.Z;
            float s = lab.X - 0.0894841775f*lab.Y - 1.2914855480f*lab.Z;
            l=l*l*l; m=m*m*m; s=s*s*s;
            V3 lin = new V3(
                4.0767416621f*l - 3.3077115913f*m + 0.2309699292f*s,
               -1.2684380046f*l + 2.6097574011f*m - 0.3413193965f*s,
               -0.0041960863f*l - 0.7034186147f*m + 1.7076147010f*s);
            return new V3(LinearToSrgb(lin.X), LinearToSrgb(lin.Y), LinearToSrgb(lin.Z));
        }

        // Palette baking uses NumPy float64 matrices and numerical inverse. Keep a
        // double path so the baked LUT matches that side more closely than the
        // shader float32 conversion used by CHROMA_FX.
        public static void SrgbToOklabDouble(double r,double g,double b,out double L,out double A,out double B)
        {
            r=SrgbToLinearDouble(Clamp01(r)); g=SrgbToLinearDouble(Clamp01(g)); b=SrgbToLinearDouble(Clamp01(b));
            double l=0.4122214708*r+0.5363325363*g+0.0514459929*b;
            double m=0.2119034982*r+0.6806995451*g+0.1073969566*b;
            double s=0.0883024619*r+0.2817188376*g+0.6299787005*b;
            l=Math.Cbrt(l); m=Math.Cbrt(m); s=Math.Cbrt(s);
            L=0.2104542553*l+0.7936177850*m-0.0040720468*s;
            A=1.9779984951*l-2.4285922050*m+0.4505937099*s;
            B=0.0259040371*l+0.7827717662*m-0.8086757660*s;
        }

        public static void OklabToSrgbDouble(double L,double A,double B,out double r,out double g,out double b)
        {
            // Exact float64 inverses of gradient.py's two matrices (the Python
            // implementation calls np.linalg.inv rather than the rounded GLSL
            // published inverse constants).
            double l=0.9999999984505197*L+0.3963377921737678*A+0.2158037580607588*B;
            double m=1.0000000088817607*L-0.1055613423236563*A-0.0638541747717059*B;
            double s=1.0000000546724108*L-0.0894841820949657*A-1.2914855378640917*B;
            l=l*l*l; m=m*m*m; s=s*s*s;
            double rl=4.076741661347994*l-3.3077115904081933*m+0.2309699287294279*s;
            double gl=-1.2684380040921763*l+2.6097574006633715*m-0.3413193963102196*s;
            double bl=-0.0041960865418371*l-0.7034186144594495*m+1.7076147009309446*s;
            r=Clamp01(LinearToSrgbDouble(Math.Max(rl,0.0)));
            g=Clamp01(LinearToSrgbDouble(Math.Max(gl,0.0)));
            b=Clamp01(LinearToSrgbDouble(Math.Max(bl,0.0)));
        }

        private static float SrgbToLinear(float c){return c<=0.04045f?c/12.92f:MathF.Pow((c+0.055f)/1.055f,2.4f);}
        private static float LinearToSrgb(float c){c=MathF.Max(c,0.0f);float v=c<=0.0031308f?c*12.92f:1.055f*MathF.Pow(c,1.0f/2.4f)-0.055f;return Glsl.Clamp(v,0.0f,1.0f);}
        private static float CbrtNonnegative(float x){return MathF.Pow(MathF.Max(x,0.0f),1.0f/3.0f);}
        private static double SrgbToLinearDouble(double c){return c<=0.04045?c/12.92:Math.Pow((c+0.055)/1.055,2.4);}
        private static double LinearToSrgbDouble(double c){return c<=0.0031308?c*12.92:1.055*Math.Pow(c,1.0/2.4)-0.055;}
        private static double Clamp01(double x){return x<0?0:(x>1?1:x);}
    }
}
