using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using GasGiantNet.Config;
using GasGiantNet.Render;
using GasGiantNet.Sim;

namespace GasGiantNet.Export
{
    internal static class HeadlessExporter
    {
        public const int Tile=1024;

        public static void Run(ParamTree p,string outDir,int threads,int? checkpointStep,Action<string> progress)
        {
            if(progress==null)progress=delegate(string s){};
            string projection=p.String("export.projection");
            if(!string.Equals(projection,"equirect",StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("WIP: cube export is not wired yet; equirect generation is the current parity path");
            progress("building simulation state");
            CpuSimulation sim=CpuSimulation.Build(p,threads);
            sim.Initialize();
            int devSteps=p.Int("sim.dev_steps");
            if(checkpointStep.HasValue)Directory.CreateDirectory(Path.Combine(outDir,"checkpoints"));
            for(int i=0;i<devSteps;i++)
            {
                long tick0=System.Diagnostics.Stopwatch.GetTimestamp();
                sim.Step();
                int step=i+1;
                if(Environment.GetEnvironmentVariable("GASGIANT_DIAG_STEPS")=="1")
                {
                    double elapsed=(System.Diagnostics.Stopwatch.GetTimestamp()-tick0)/(double)System.Diagnostics.Stopwatch.Frequency;
                    progress("stepdiag "+step+" vortices="+sim.Vortices.Vortices.Count+" dt="+sim.Dt.ToString("G6",System.Globalization.CultureInfo.InvariantCulture)+" elapsed="+elapsed.ToString("0.000",System.Globalization.CultureInfo.InvariantCulture)+"s");
                }
                bool isCheckpoint=checkpointStep.HasValue&&(step%checkpointStep.Value==0||step==devSteps);
                if((step)%25==0||step==devSteps)progress("developing "+step+"/"+devSteps);
                if(isCheckpoint)
                {
                    SimulationDiagnostics.ThrowIfNonFinite(sim,step);
                    progress("checkpoint state "+step+": "+SimulationDiagnostics.Summary(sim));
                    if(Environment.GetEnvironmentVariable("GASGIANT_DIAG_ONLY")=="1")return;
                    ExportCheckpointColor(sim,outDir,step,threads,progress);
                }
            }

            ExportDeveloped(sim,outDir,threads,progress);
        }


        public static void ExportCheckpointColor(CpuSimulation sim,string outDir,int step,int threads,Action<string> progress)
        {
            if(progress==null)progress=delegate(string s){};
            ParamTree p=sim.Params;
            int w=p.Int("export.width"),h=w/2;
            progress("checkpoint state "+step+": "+TextureStats(sim.Equirect.Cur));
            ushort[] color=new ushort[checked(w*h*3)];

            FloatTexture mask=null;
            if(p.Has("mask.file"))
            {
                string maskPath=p.NullableString("mask.file");
                if(!string.IsNullOrEmpty(maskPath))
                {
                    if(!File.Exists(maskPath))throw new FileNotFoundException("mask file not found",maskPath);
                    mask=PngImageReader.ReadMask(maskPath);
                }
            }

            List<int[]> tiles=EnumerateTiles(w,h);
            bool useDetail=p.Float("detail.intensity")>0.0f;
            for(int ti=0;ti<tiles.Count;ti++)
            {
                int x0=tiles[ti][0],y0=tiles[ti][1];
                int tw=Math.Min(Tile,w-x0),th=Math.Min(Tile,h-y0);
                FloatTexture detail=useDetail?DetailSynthCpu.Synthesize(sim,x0,y0,tw,th,w,h,true,threads):null;
                if(detail!=null)ThrowIfNonFinite(detail,"checkpoint detail",step);
                DerivedMaps d=DeriveCpu.DeriveTile(sim,x0,y0,tw,th,w,h,detail,mask,threads);
                ThrowIfNonFinite(d.Color,"checkpoint color",step);
                ScatterColor(d,color,w,x0,y0,tw,th);
            }

            int compression=p.Int("export.png_compression");
            string dir=Path.Combine(outDir,"checkpoints");
            Directory.CreateDirectory(dir);
            string file=Path.Combine(dir,string.Format("step_{0:D6}.png",step));
            progress("encoding checkpoint "+step+" -> "+Path.GetFileName(file));
            Png16Writer.WriteRgb(file,w,h,color,compression);
        }

        public static void ExportDeveloped(CpuSimulation sim,string outDir,int threads,Action<string> progress)
        {
            if(progress==null)progress=delegate(string s){};
            ParamTree p=sim.Params;
            int w=p.Int("export.width"),h=w/2;
            if(w<512||w>32768)throw new ArgumentException("export.width must be in 512..32768");
            Directory.CreateDirectory(outDir);

            ushort[] color=new ushort[checked(w*h*3)];
            float[] height=new float[checked(w*h)];
            bool emissionOn=EmissionEnabled(p);
            bool flowOn=p.Bool("export.flow_map");
            bool ringsOn=p.Bool("rings.enabled");
            float[] emission=emissionOn?new float[checked(w*h*4)]:null;
            float[] flow=flowOn?new float[checked(w*h*4)]:null;

            FloatTexture mask=null;
            if(p.Has("mask.file"))
            {
                string maskPath=p.NullableString("mask.file");
                if(!string.IsNullOrEmpty(maskPath))
                {
                    if(!File.Exists(maskPath))throw new FileNotFoundException("mask file not found",maskPath);
                    mask=PngImageReader.ReadMask(maskPath);
                }
            }

            List<int[]> tiles=EnumerateTiles(w,h);
            bool useDetail=p.Float("detail.intensity")>0.0f;
            for(int ti=0;ti<tiles.Count;ti++)
            {
                int x0=tiles[ti][0],y0=tiles[ti][1];
                int tw=Math.Min(Tile,w-x0),th=Math.Min(Tile,h-y0);
                FloatTexture detail=useDetail?DetailSynthCpu.Synthesize(sim,x0,y0,tw,th,w,h,true,threads):null;
                DerivedMaps d=DeriveCpu.DeriveTile(sim,x0,y0,tw,th,w,h,detail,mask,threads);
                Scatter(d,color,height,emission,w,x0,y0,tw,th);
                if(flowOn)
                {
                    FloatTexture ft=FlowResampleCpu.Resample(sim,x0,y0,tw,th,w,h,threads);
                    ScatterRgba(ft,flow,w,x0,y0,tw,th);
                }
                progress("tile "+(ti+1)+"/"+tiles.Count);
            }

            int compression=p.Int("export.png_compression");
            progress("encoding color.png");
            Png16Writer.WriteRgb(Path.Combine(outDir,"color.png"),w,h,color,compression);
            progress("encoding height.exr");
            OpenExrWriter.WriteGray32(Path.Combine(outDir,"height.exr"),w,h,height);
            if(emissionOn)
            {
                progress("encoding emission.exr");
                if(p.Bool("export.emission_half"))OpenExrWriter.WriteRgba16(Path.Combine(outDir,"emission.exr"),w,h,emission);
                else OpenExrWriter.WriteRgba32(Path.Combine(outDir,"emission.exr"),w,h,emission);
            }
            if(flowOn)
            {
                progress("encoding flow.exr");
                OpenExrWriter.WriteRgba32(Path.Combine(outDir,"flow.exr"),w,h,flow);
            }
            if(ringsOn)
            {
                progress("encoding rings.exr");
                FloatTexture rings=RingsCpu.Build(p);OpenExrWriter.WriteRgba32(Path.Combine(outDir,"rings.exr"),rings);
            }
            WriteManifest(outDir,p,w,h,emissionOn,flowOn,ringsOn);
            progress("done");
        }

        private static void ThrowIfNonFinite(FloatTexture t,string label,int step)
        {
            for(int i=0;i<t.Data.Length;i++)
            {
                float v=t.Data[i];
                if(float.IsNaN(v)||float.IsInfinity(v))
                    throw new InvalidOperationException(label+" became non-finite at development step "+step+" (element "+i+")");
            }
        }


        private static string TextureStats(FloatTexture t)
        {
            if(t==null)return "null";
            float min=float.PositiveInfinity,max=float.NegativeInfinity;long finite=0,nan=0,inf=0;double sum=0.0;
            for(int i=0;i<t.Data.Length;i++)
            {
                float v=t.Data[i];
                if(float.IsNaN(v)){nan++;continue;}
                if(float.IsInfinity(v)){inf++;continue;}
                finite++;if(v<min)min=v;if(v>max)max=v;sum+=v;
            }
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,"finite={0} nan={1} inf={2} min={3:G6} max={4:G6} mean={5:G6}",finite,nan,inf,min,max,finite>0?sum/finite:0.0);
        }

        private static List<int[]> EnumerateTiles(int w,int h)
        {
            List<int[]> result=new List<int[]>();for(int y=0;y<h;y+=Tile)for(int x=0;x<w;x+=Tile)result.Add(new int[]{x,y});return result;
        }

        private static void ScatterColor(DerivedMaps d,ushort[] color,int fullW,int x0,int y0,int tw,int th)
        {
            for(int y=0;y<th;y++)for(int x=0;x<tw;x++)
            {
                int si=(y*tw+x)*4,di=((y+y0)*fullW+(x+x0));
                color[di*3]=Png16Writer.Quantize(d.Color.Data[si]);
                color[di*3+1]=Png16Writer.Quantize(d.Color.Data[si+1]);
                color[di*3+2]=Png16Writer.Quantize(d.Color.Data[si+2]);
            }
        }

        private static void Scatter(DerivedMaps d,ushort[] color,float[] height,float[] emission,int fullW,int x0,int y0,int tw,int th)
        {
            for(int y=0;y<th;y++)for(int x=0;x<tw;x++)
            {
                int si=(y*tw+x)*4,di=((y+y0)*fullW+(x+x0));
                color[di*3]=Png16Writer.Quantize(d.Color.Data[si]);
                color[di*3+1]=Png16Writer.Quantize(d.Color.Data[si+1]);
                color[di*3+2]=Png16Writer.Quantize(d.Color.Data[si+2]);
                height[di]=d.Height.Data[y*tw+x];
                if(emission!=null&&d.Emission!=null)
                {
                    int ei=di*4;emission[ei]=d.Emission.Data[si];emission[ei+1]=d.Emission.Data[si+1];emission[ei+2]=d.Emission.Data[si+2];emission[ei+3]=d.Emission.Data[si+3];
                }
            }
        }

        private static void ScatterRgba(FloatTexture src,float[] dst,int fullW,int x0,int y0,int tw,int th)
        {
            for(int y=0;y<th;y++)for(int x=0;x<tw;x++)
            {
                int si=(y*tw+x)*4,di=((y+y0)*fullW+(x+x0))*4;
                dst[di]=src.Data[si];dst[di+1]=src.Data[si+1];dst[di+2]=src.Data[si+2];dst[di+3]=src.Data[si+3];
            }
        }

        private static bool EmissionEnabled(ParamTree p)
        {
            return p.Float("emission.thermal_strength")>0.0f||p.Float("emission.lightning_strength")>0.0f||p.Float("emission.aurora_strength")>0.0f;
        }

        private static void WriteManifest(string outDir,ParamTree p,int w,int h,bool emission,bool flow,bool rings)
        {
            JsonObject maps=new JsonObject();
            maps["color"]=MapEntry("color.png","png16","srgb",3);
            maps["height"]=MapEntry("height.exr","exr32f","non-color",1);
            if(emission)
            {
                JsonObject e=MapEntry("emission.exr",p.Bool("export.emission_half")?"exr16f":"exr32f","non-color",4);
                e["aurora_color"]=FloatArrayNode(p.FloatArray("emission.aurora_color"));maps["emission"]=e;
            }
            if(flow)
            {
                JsonObject f=MapEntry("flow.exr","exr32f","non-color",4);f["convention"]="rg_east_north_texel_per_step";maps["flow"]=f;
            }
            if(rings)
            {
                JsonObject r=MapEntry("rings.exr","exr32f","non-color",4);r["convention"]="radial_inner_to_outer_alpha_coverage";maps["rings"]=r;
            }

            JsonObject physical=new JsonObject();physical["radius_km"]=p.Double("physical.radius_km");physical["height_scale"]=p.Double("physical.height_scale");physical["height_midlevel"]=p.Double("physical.height_midlevel");
            if(rings){physical["ring_inner_km"]=p.Double("physical.ring_inner_km");physical["ring_outer_km"]=p.Double("physical.ring_outer_km");}

            JsonObject preset=new JsonObject();preset["preset_format"]=2;preset["app_version"]="0.1.0";preset["name"]=p.String("name");preset["params"]=JsonNode.Parse(p.ToJson());
            JsonObject manifest=new JsonObject();manifest["schema_version"]=1;
            JsonObject generator=new JsonObject();generator["name"]="gasgiant";generator["version"]="0.1.0";manifest["generator"]=generator;
            manifest["name"]=p.String("name");manifest["seed"]=p.Int("seed");manifest["projection"]="equirectangular";
            JsonArray resolution=new JsonArray();resolution.Add(w);resolution.Add(h);manifest["resolution"]=resolution;manifest["physical"]=physical;manifest["maps"]=maps;manifest["preset"]=preset;
            JsonObject atmo=new JsonObject();JsonArray rim=new JsonArray();rim.Add(0.55);rim.Add(0.65);rim.Add(1.0);atmo["rim_color"]=rim;atmo["rim_strength"]=0.4;manifest["atmosphere_hint"]=atmo;
            File.WriteAllText(Path.Combine(outDir,"mapset.json"),manifest.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
        }

        private static JsonObject MapEntry(string file,string format,string colorspace,int channels)
        {JsonObject o=new JsonObject();o["file"]=file;o["format"]=format;o["colorspace"]=colorspace;o["channels"]=channels;return o;}
        private static JsonArray FloatArrayNode(float[] v){JsonArray a=new JsonArray();for(int i=0;i<v.Length;i++)a.Add(v[i]);return a;}
    }
}
