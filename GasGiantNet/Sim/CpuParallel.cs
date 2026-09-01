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

            if (height <= 0) return;

            // Use static contiguous ranges instead of scheduling one TPL work
            // item for every row. This reduces scheduling overhead and keeps
            // each worker on contiguous memory for better cache locality.
            int workerCount = threads > 0 ? threads : Environment.ProcessorCount;
            if (workerCount < 1) workerCount = 1;
            if (workerCount > height) workerCount = height;

            int rowsPerWorker = (height + workerCount - 1) / workerCount;

            ParallelOptions options = new ParallelOptions();
            options.MaxDegreeOfParallelism = workerCount;

            Parallel.For(0, workerCount, options, delegate(int worker)
            {
                int y0 = worker * rowsPerWorker;
                int y1 = Math.Min(y0 + rowsPerWorker, height);
                for (int y = y0; y < y1; y++)
                    body(y);
            });
        }
    }
}
