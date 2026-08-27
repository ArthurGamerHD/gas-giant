using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using GasGiantNet.Config;
using GasGiantNet.Export;
using GasGiantNet.Random;

namespace GasGiantNet
{
    internal static class Program
    {
        private sealed class Options
        {
            public string Preset="jupiter_like";
            public int? Seed;
            public int? Resolution;
            public int? DevSteps;
            public int? SimulationResolution;
            public string Name;
            public string Out="out";
            public int Threads=0;
            public int? Compression;
            public int? CheckpointStep;
            public bool SelfTest;
            public bool Help;
        }

        private static int Main(string[] args)
        {
            try
            {
                Options o=Parse(args);if(o.Help){PrintHelp();return 0;}
                RandomSelfTest.Run();
                if(o.SelfTest){Console.WriteLine("System.Random distribution self-test passed.");return 0;}
                string baseDir=AppContext.BaseDirectory;
                ParamTree p;
                if(File.Exists(o.Preset))p=ParamTree.LoadPresetFile(o.Preset,Path.Combine(baseDir,"PresetsResolved","_defaults.json"));
                else p=ParamTree.LoadResolvedPreset(o.Preset,baseDir);
                if(o.Seed.HasValue)p.SetInt("seed",o.Seed.Value);
                if(o.Resolution.HasValue)p.SetInt("export.width",o.Resolution.Value);
                if(o.DevSteps.HasValue)p.SetInt("sim.dev_steps",o.DevSteps.Value);
                if(o.SimulationResolution.HasValue)p.SetInt("sim.resolution",o.SimulationResolution.Value);
                if(o.Name!=null)p.SetString("name",o.Name);
                if(o.Compression.HasValue)p.SetInt("export.png_compression",o.Compression.Value);
                Stopwatch sw=Stopwatch.StartNew();
                HeadlessExporter.Run(p,o.Out,o.Threads,o.CheckpointStep,delegate(string msg){Console.WriteLine(msg);});
                sw.Stop();
                Console.WriteLine("exported {0}x{1} map set to {2} in {3:0.0}s",p.Int("export.width"),p.Int("export.width")/2,o.Out,sw.Elapsed.TotalSeconds);
                return 0;
            }
            catch(Exception ex){Console.Error.WriteLine("error: "+ex.Message);return 2;}
        }

        private static Options Parse(string[] args)
        {
            Options o=new Options();
            for(int i=0;i<args.Length;i++)
            {
                string a=args[i];
                if(a=="-h"||a=="--help")o.Help=true;
                else if(a=="--self-test")o.SelfTest=true;
                else if(a=="--preset")o.Preset=Need(args,ref i,a);
                else if(a=="--seed")o.Seed=Int(Need(args,ref i,a),a);
                else if(a=="--res"||a=="--width")o.Resolution=Int(Need(args,ref i,a),a);
                else if(a=="--dev-steps")o.DevSteps=Int(Need(args,ref i,a),a);
                else if(a=="--sim-res"||a=="--sim-resolution")o.SimulationResolution=Int(Need(args,ref i,a),a);
                else if(a=="--name")o.Name=Need(args,ref i,a);
                else if(a=="--out")o.Out=Need(args,ref i,a);
                else if(a=="--threads")o.Threads=Int(Need(args,ref i,a),a);
                else if(a=="--compression")o.Compression=Int(Need(args,ref i,a),a);
                else if(a=="--checkpoint-step")o.CheckpointStep=Int(Need(args,ref i,a),a);
                else throw new ArgumentException("unknown argument: "+a);
            }
            if(o.Seed.HasValue&&(o.Seed.Value<0))throw new ArgumentException("--seed must be >= 0");
            if(o.Resolution.HasValue&&(o.Resolution.Value<512||o.Resolution.Value>32768))throw new ArgumentException("--res must be 512..32768");
            if(o.DevSteps.HasValue&&(o.DevSteps.Value<0||o.DevSteps.Value>3000))throw new ArgumentException("--dev-steps must be 0..3000");
            if(o.SimulationResolution.HasValue&&(o.SimulationResolution.Value<64||o.SimulationResolution.Value>8192))throw new ArgumentException("--sim-res must be 64..8192");
            if(o.Threads<0)throw new ArgumentException("--threads must be >= 0");
            if(o.Compression.HasValue&&(o.Compression.Value<0||o.Compression.Value>9))throw new ArgumentException("--compression must be 0..9");
            if(o.CheckpointStep.HasValue&&o.CheckpointStep.Value<=0)throw new ArgumentException("--checkpoint-step must be > 0");
            return o;
        }

        private static string Need(string[] a,ref int i,string flag){if(i+1>=a.Length)throw new ArgumentException(flag+" requires a value");return a[++i];}
        private static int Int(string s,string flag){int v;if(!int.TryParse(s,NumberStyles.Integer,CultureInfo.InvariantCulture,out v))throw new ArgumentException(flag+" requires an integer");return v;}
        private static void PrintHelp()
        {
            Console.WriteLine("gasgiant-cpu-parity - CPU port of Gas Giant Studio headless generation");
            Console.WriteLine("Usage: gasgiant-cpu-parity [--preset NAME|FILE] [--seed N] [--res N] [--sim-res N] [--dev-steps N] [--out DIR] [--threads N]");
            Console.WriteLine("  --preset NAME|FILE   resolved factory preset or preset JSON (default jupiter_like)");
            Console.WriteLine("  --seed N             override master seed");
            Console.WriteLine("  --res N              export width, 512..32768 (height = width/2)");
            Console.WriteLine("  --dev-steps N        override development steps, 0..3000");
            Console.WriteLine("  --sim-res N          override simulation width, 64..8192 (height = width/2)");
            Console.WriteLine("  --name NAME          override manifest planet name");
            Console.WriteLine("  --out DIR            output directory (default out)");
            Console.WriteLine("  --threads N          max CPU workers; 0 = runtime default");
            Console.WriteLine("  --compression 0..9   PNG compression request");
            Console.WriteLine("  --checkpoint-step N  emit checkpoint color PNGs every N development steps");
            Console.WriteLine("  --self-test          test deterministic System.Random streams and distributions");
        }
    }
}
