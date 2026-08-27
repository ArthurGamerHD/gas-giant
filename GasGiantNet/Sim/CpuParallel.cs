using System;
using System.Threading.Tasks;

namespace GasGiantNet.Sim
{
    internal static class CpuParallel
    {
        public static void ForRows(int height, int threads, Action<int> body)
        {
            if (threads == 1)
            {
                for (int y = 0; y < height; y++) body(y);
                return;
            }
            ParallelOptions options = new ParallelOptions();
            if (threads > 0) options.MaxDegreeOfParallelism = threads;
            Parallel.For(0, height, options, body);
        }
    }
}
