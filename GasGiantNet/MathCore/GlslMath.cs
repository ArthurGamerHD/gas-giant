using System;

namespace GasGiantNet.MathCore
{
    internal struct V2
    {
        public float X, Y;
        public V2(float x, float y) { X = x; Y = y; }
        public static V2 operator +(V2 a, V2 b) { return new V2(a.X + b.X, a.Y + b.Y); }
        public static V2 operator -(V2 a, V2 b) { return new V2(a.X - b.X, a.Y - b.Y); }
        public static V2 operator -(V2 a) { return new V2(-a.X, -a.Y); }
        public static V2 operator *(V2 a, float b) { return new V2(a.X * b, a.Y * b); }
        public static V2 operator *(float b, V2 a) { return new V2(a.X * b, a.Y * b); }
        public static V2 operator *(V2 a, V2 b) { return new V2(a.X * b.X, a.Y * b.Y); }
        public static V2 operator /(V2 a, float b) { return new V2(a.X / b, a.Y / b); }
        public static V2 operator /(V2 a, V2 b) { return new V2(a.X / b.X, a.Y / b.Y); }
    }

    internal struct V3
    {
        public float X, Y, Z;
        public V3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public V3 YXZ { get { return new V3(Y, X, Z); } }
        public V3 YZX { get { return new V3(Y, Z, X); } }
        public V3 ZXY { get { return new V3(Z, X, Y); } }
        public V3 ZYX { get { return new V3(Z, Y, X); } }
        public static V3 operator +(V3 a, V3 b) { return new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
        public static V3 operator -(V3 a, V3 b) { return new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
        public static V3 operator -(V3 a) { return new V3(-a.X, -a.Y, -a.Z); }
        public static V3 operator *(V3 a, float b) { return new V3(a.X * b, a.Y * b, a.Z * b); }
        public static V3 operator *(float b, V3 a) { return new V3(a.X * b, a.Y * b, a.Z * b); }
        public static V3 operator *(V3 a, V3 b) { return new V3(a.X * b.X, a.Y * b.Y, a.Z * b.Z); }
        public static V3 operator /(V3 a, float b) { return new V3(a.X / b, a.Y / b, a.Z / b); }
        public static V3 operator /(V3 a, V3 b) { return new V3(a.X / b.X, a.Y / b.Y, a.Z / b.Z); }
    }

    internal struct V4
    {
        public float X, Y, Z, W;
        public V4(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
        public V3 XYZ { get { return new V3(X, Y, Z); } }
        public static V4 operator +(V4 a, V4 b) { return new V4(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.W + b.W); }
        public static V4 operator -(V4 a, V4 b) { return new V4(a.X - b.X, a.Y - b.Y, a.Z - b.Z, a.W - b.W); }
        public static V4 operator -(V4 a) { return new V4(-a.X, -a.Y, -a.Z, -a.W); }
        public static V4 operator *(V4 a, float b) { return new V4(a.X * b, a.Y * b, a.Z * b, a.W * b); }
        public static V4 operator *(float b, V4 a) { return a * b; }
        public static V4 operator *(V4 a, V4 b) { return new V4(a.X * b.X, a.Y * b.Y, a.Z * b.Z, a.W * b.W); }
        public static V4 operator /(V4 a, float b) { return new V4(a.X / b, a.Y / b, a.Z / b, a.W / b); }
        public float this[int i]
        {
            get { return i == 0 ? X : (i == 1 ? Y : (i == 2 ? Z : W)); }
            set { if (i == 0) X = value; else if (i == 1) Y = value; else if (i == 2) Z = value; else W = value; }
        }
    }

    internal static class Glsl
    {
        public const float PI = 3.14159265358979f;
        public const float TAU = 6.28318530717958f;

        public static float Clamp(float x, float a, float b) { return x < a ? a : (x > b ? b : x); }
        public static int Clamp(int x, int a, int b) { return x < a ? a : (x > b ? b : x); }
        public static V2 Clamp(V2 x, float a, float b) { return new V2(Clamp(x.X, a, b), Clamp(x.Y, a, b)); }
        public static V3 Clamp(V3 x, float a, float b) { return new V3(Clamp(x.X, a, b), Clamp(x.Y, a, b), Clamp(x.Z, a, b)); }
        public static V4 Clamp(V4 x, float a, float b) { return new V4(Clamp(x.X, a, b), Clamp(x.Y, a, b), Clamp(x.Z, a, b), Clamp(x.W, a, b)); }
        public static float Mix(float a, float b, float t) { return a + (b - a) * t; }
        public static V2 Mix(V2 a, V2 b, float t) { return a + (b - a) * t; }
        public static V3 Mix(V3 a, V3 b, float t) { return a + (b - a) * t; }
        public static V4 Mix(V4 a, V4 b, float t) { return a + (b - a) * t; }
        public static V4 Mix(V4 a, V4 b, V4 t) { return new V4(Mix(a.X,b.X,t.X), Mix(a.Y,b.Y,t.Y), Mix(a.Z,b.Z,t.Z), Mix(a.W,b.W,t.W)); }
        public static float Fract(float x) { return x - MathF.Floor(x); }
        public static V2 Fract(V2 x) { return new V2(Fract(x.X), Fract(x.Y)); }
        public static float SmoothStep(float a, float b, float x)
        {
            if (a == b) return x < a ? 0f : 1f;
            float t = Clamp((x - a) / (b - a), 0.0f, 1.0f);
            return t * t * (3.0f - 2.0f * t);
        }
        public static float Step(float edge, float x) { return x < edge ? 0.0f : 1.0f; }
        public static float Sign(float x) { return x > 0f ? 1f : (x < 0f ? -1f : 0f); }
        public static float Dot(V2 a, V2 b) { return a.X * b.X + a.Y * b.Y; }
        public static float Dot(V3 a, V3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
        public static float Length(V2 a) { return MathF.Sqrt(a.X * a.X + a.Y * a.Y); }
        public static float Length(V3 a) { return MathF.Sqrt(Dot(a, a)); }
        public static V2 Normalize(V2 a)
        {
            float d = Length(a);
            return d > 0.0f ? a / d : new V2(0, 0);
        }
        public static V3 Normalize(V3 a)
        {
            float d = Length(a);
            return d > 0.0f ? a / d : new V3(0, 0, 0);
        }
        public static V3 Cross(V3 a, V3 b)
        {
            return new V3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        }
        public static float Min(float a, float b) { return a < b ? a : b; }
        public static float Max(float a, float b) { return a > b ? a : b; }
        public static V4 Min(V4 a, V4 b) { return new V4(Min(a.X,b.X),Min(a.Y,b.Y),Min(a.Z,b.Z),Min(a.W,b.W)); }
        public static V4 Max(V4 a, V4 b) { return new V4(Max(a.X,b.X),Max(a.Y,b.Y),Max(a.Z,b.Z),Max(a.W,b.W)); }
        public static int WrapX(int x, int w)
        {
            int r = x % w;
            return r < 0 ? r + w : r;
        }
        public static float WrappedLonDelta(float a, float b)
        {
            return MathF.Atan2(MathF.Sin(a - b), MathF.Cos(a - b));
        }
        public static V3 SpherePoint(float lon, float lat)
        {
            float cl = MathF.Cos(lat);
            return new V3(cl * MathF.Cos(lon), MathF.Sin(lat), cl * MathF.Sin(lon));
        }
        public static float Mod(float x, float y)
        {
            return x - y * MathF.Floor(x / y);
        }
        public static int Mod(int x, int y)
        {
            int r = x % y;
            return r < 0 ? r + y : r;
        }
        public static float Pow(float x, float y) { return MathF.Pow(x, y); }
        public static float Exp(float x) { return MathF.Exp(x); }
        public static float Acos(float x) { return MathF.Acos(x); }
        public static float Asin(float x) { return MathF.Asin(x); }
        public static float Atan(float y, float x) { return MathF.Atan2(y, x); }
        public static float Sin(float x) { return MathF.Sin(x); }
        public static float Cos(float x) { return MathF.Cos(x); }
        public static float Abs(float x) { return MathF.Abs(x); }
        public static float Floor(float x) { return MathF.Floor(x); }
    }
}
