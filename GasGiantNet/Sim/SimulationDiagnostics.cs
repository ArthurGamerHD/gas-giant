using System;

namespace GasGiantNet.Sim
{
    internal static class SimulationDiagnostics
    {
        public static void ThrowIfNonFinite(CpuSimulation sim, int step)
        {
            CheckDomain(sim.Equirect, "equirect", step);
            CheckDomain(sim.North, "north", step);
            CheckDomain(sim.South, "south", step);
        }



        public static string Summary(CpuSimulation sim)
        {
            return TextureSummary(sim.Equirect.Cur,"eq.cur")+"; "+TextureSummary(sim.Equirect.Psi,"eq.psi")+"; "+TextureSummary(sim.Equirect.Velocity,"eq.vel")+"; "+TextureSummary(sim.North.Cur,"n.cur")+"; "+TextureSummary(sim.North.Psi,"n.psi")+"; "+TextureSummary(sim.North.Velocity,"n.vel")+"; "+TextureSummary(sim.South.Cur,"s.cur")+"; "+TextureSummary(sim.South.Psi,"s.psi")+"; "+TextureSummary(sim.South.Velocity,"s.vel");
        }

        private static string TextureSummary(FloatTexture t,string name)
        {
            string result=name+"{";
            for(int c=0;c<t.Channels;c++)
            {
                float mn=float.PositiveInfinity,mx=float.NegativeInfinity;int bad=0;double sum=0.0;int count=0;
                for(int pix=0;pix<t.Width*t.Height;pix++)
                {
                    float v=t.Data[pix*t.Channels+c];
                    if(float.IsNaN(v)||float.IsInfinity(v)){bad++;continue;}
                    if(v<mn)mn=v;if(v>mx)mx=v;sum+=v;count++;
                }
                if(c>0)result+=",";
                result+="c"+c+"=["+mn.ToString("0.###E+0")+","+mx.ToString("0.###E+0")+",avg="+(count>0?(sum/count).ToString("0.###E+0"):"n/a")+",bad="+bad+"]";
            }
            return result+"}";
        }
        private static void CheckDomain(SimDomain d, string name, int step)
        {
            Check(d.Cur, name + ".tracers", step);
            Check(d.Psi, name + ".psi", step);
            Check(d.Velocity, name + ".velocity", step);
            if (string.Equals(name, "", StringComparison.Ordinal)) return;
        }

        private static void Check(FloatTexture t, string name, int step)
        {
            float[] a=t.Data;
            for(int i=0;i<a.Length;i++)
            {
                float v=a[i];
                if(float.IsNaN(v)||float.IsInfinity(v))
                {
                    int pixel=i/t.Channels;
                    int ch=i%t.Channels;
                    int x=pixel%t.Width;
                    int y=pixel/t.Width;
                    throw new InvalidOperationException("non-finite simulation value at step "+step+": "+name+"["+x+","+y+","+ch+"]="+v);
                }
            }
        }
    }
}
