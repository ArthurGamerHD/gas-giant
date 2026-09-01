using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using GasGiantNet.Config;
using GasGiantNet.MathCore;
using GasGiantNet.Random;
using GasGiantNet.Sim;

namespace GasGiantNet.Render
{
    internal sealed class DerivedMaps
    {
        public FloatTexture Color;
        public FloatTexture Height;
        public FloatTexture Emission;
    }

    internal static class DeriveCpu
    {
        private const float BlendLo=64.0f*Glsl.PI/180.0f;
        private const float BlendHi=67.0f*Glsl.PI/180.0f;

        public static DerivedMaps Derive(CpuSimulation sim,int width,int height,FloatTexture detail,FloatTexture mask,int threads)
        {
            return DeriveTile(sim,0,0,width,height,width,height,detail,mask,threads);
        }

        public static DerivedMaps DeriveTile(CpuSimulation sim,int originX,int originY,int width,int height,int fullWidth,int fullHeight,FloatTexture detail,FloatTexture mask,int threads)
        {
            ParamTree p=sim.Params;
            AppearanceLuts luts=PaletteLuts.Bake(p);
            DerivedMaps outv=new DerivedMaps();
            outv.Color=new FloatTexture(width,height,4);
            outv.Height=new FloatTexture(width,height,1);
            bool emissionOn=EmissionEnabled(p);
            outv.Emission=emissionOn?new FloatTexture(width,height,4):null;

            List<double[]> laneList=Profiles.SelectLanes(p.Int("seed"),sim.Bands,p.Double("bands.lane_density"));
            int laneCount=Math.Min(16,laneList.Count);
            V2[] lanes=new V2[laneCount];
            for(int i=0;i<laneCount;i++)lanes[i]=new V2((float)laneList[i][0],(float)laneList[i][1]);

            float detailIntensity=detail!=null?p.Float("detail.intensity"):0.0f;
            float detailGain=0.35f;
            float haze=p.Float("appearance.haze_amount");
            V3 hazeColor=Read3(p,"appearance.haze_color");
            float contrastParam=p.Float("appearance.contrast");
            float saturation=p.Float("appearance.saturation");
            float gamma=p.Float("appearance.gamma");
            V3 polarTint=Read3(p,"appearance.polar_tint_color");
            float polarTintStrength=p.Float("appearance.polar_tint_strength");
            float polarTintStart=p.Float("appearance.polar_tint_start_lat")*Glsl.PI/180.0f;
            float polarCanvas=p.Float("appearance.polar_canvas_value");
            float bandTintStrength=p.Float("appearance.band_tint_strength");
            float detailChroma=p.Float("appearance.detail_chroma");
            float chromaScale=p.Float("appearance.chroma_scale");
            float chromaVariance=p.Float("appearance.chroma_variance");
            float hueVariance=p.Float("appearance.hue_variance");
            float chromaAging=p.Float("appearance.chroma_aging");
            bool chromaOn=chromaScale!=1.0f||chromaVariance>0.0f||hueVariance>0.0f||chromaAging>0.0f;
            V3 chromaOffset=Draw3(RandomGenerator.Subseed(p.Int("seed"),"chroma-variance"));
            V3 hueOffset=Draw3(RandomGenerator.Subseed(p.Int("seed"),"hue-variance"));

            bool maskOn=mask!=null&&(p.Float("mask.band_fade")>0.0f||p.Float("mask.emission_gain")>0.0f||p.Float("mask.detail_gain")>0.0f);
            float maskBandFade=p.Float("mask.band_fade"),maskEmissionGain=p.Float("mask.emission_gain"),maskDetailGain=p.Float("mask.detail_gain");
            V3 warpOffset=sim.Static.WarpOffset;
            float warpAmount=p.Float("bands.warp_amount"),warpFreq=p.Float("bands.warp_freq");

            EmissionContext ec=emissionOn?BuildEmissionContext(p):null;

            // Equirectangular geometry is separable: longitude depends only on
            // X and latitude only on Y. Cache all expensive trig once per tile.
            float[] uvX=new float[width];
            float[] lonTable=new float[width];
            float[] sinLon=new float[width];
            float[] cosLon=new float[width];
            for(int x=0;x<width;x++)
            {
                float u=(x+originX+0.5f)/fullWidth;
                float lon=u*2.0f*Glsl.PI-Glsl.PI;
                uvX[x]=u;lonTable[x]=lon;
                sinLon[x]=MathF.Sin(lon);cosLon[x]=MathF.Cos(lon);
            }

            float[] uvY=new float[height];
            float[] latTable=new float[height];
            float[] sinLat=new float[height];
            float[] cosLat=new float[height];
            for(int y=0;y<height;y++)
            {
                float v=(y+originY+0.5f)/fullHeight;
                float lat=0.5f*Glsl.PI-v*Glsl.PI;
                uvY[y]=v;latTable[y]=lat;
                sinLat[y]=MathF.Sin(lat);cosLat[y]=MathF.Cos(lat);
            }

            CpuParallel.ForRows(height,threads,delegate(int py)
            {
                for(int px=0;px<width;px++)
                {
                    V2 uv=new V2(uvX[px],uvY[py]);
                    V4 t=sim.Equirect.Cur.SampleLinear(uv);
                    float lat=latTable[py];
                    V3 sp=new V3(cosLat[py]*cosLon[px],
                                 sinLat[py],
                                 cosLat[py]*sinLon[px]);
                    float polarW=Glsl.SmoothStep(BlendLo,BlendHi,MathF.Abs(lat));
                    if(polarW>0.0f)
                    {
                        float lon=uv.X*2.0f*Glsl.PI-Glsl.PI;
                        float rho=0.5f*Glsl.PI-MathF.Abs(lat);
                        V2 st=new V2(rho*MathF.Cos(lon),rho*MathF.Sin(lon));
                        V2 puv=st/sim.Equirect.RhoMax*0.5f+new V2(0.5f,0.5f);
                        V4 tp=(lat>=0.0f?sim.North.Cur:sim.South.Cur).SampleLinear(puv);
                        t=Glsl.Mix(t,tp,polarW);
                    }

                    V3 col=luts.Palette.SampleLinear(new V2(Glsl.Clamp(t.X,0.0f,1.0f),1.0f-uv.Y)).XYZ;
                    V3 maskBandCol=col;
                    float maskValue=maskOn?mask.SampleLinear1(uv):1.0f;

                    V3 stormTint=luts.Storm.SampleLinear(new V2(Glsl.Clamp(t.W*0.5f+0.5f,0.0f,1.0f),0.5f)).XYZ;
                    col=Glsl.Mix(col,stormTint,Glsl.Clamp(MathF.Abs(t.W),0.0f,1.0f));

                    if(polarTintStrength>0.0f)
                    {
                        float pw=Glsl.SmoothStep(polarTintStart,polarTintStart+0.30f,MathF.Abs(lat));
                        float gap=1.0f-Glsl.SmoothStep(0.32f,0.72f,t.Y);
                        float lum=Luma(col);
                        V3 polar=polarTint*(0.30f+1.45f*lum);
                        col=Glsl.Mix(col,polar,polarTintStrength*pw*gap);
                    }

                    col*=1.0f+detailGain*(t.Z-0.5f);
                    float dsyn=0.5f;
                    if(detailIntensity>0.0f)
                    {
                        dsyn=detail.SampleLinear1(new V2((px+0.5f)/width,(py+0.5f)/height));
                        col*=1.0f+detailIntensity*0.55f*(dsyn-0.5f);
                        if(detailChroma>0.0f)
                        {
                            float ex=Glsl.Clamp((dsyn-0.5f)*2.0f,-1.0f,1.0f);
                            float ss=detailChroma*(ex>0.0f?1.0f:0.3f)*ex;
                            V3 lab=Oklab.SrgbToOklab(col);
                            lab.Y-=ss*0.013f; lab.Z-=ss*0.045f;
                            col=Oklab.OklabToSrgb(lab);
                        }
                    }
                    if(maskOn)col*=Glsl.Mix(1.0f,maskValue,maskDetailGain);

                    if(polarCanvas>0.0f)
                    {
                        float cpw=Glsl.SmoothStep(polarTintStart,polarTintStart+0.30f,MathF.Abs(lat));
                        float plum=Luma(col);
                        float lowmask=1.0f-Glsl.SmoothStep(0.50f,0.82f,plum);
                        V3 teal=polarTint*(0.10f+0.50f*plum);
                        col=Glsl.Mix(col,teal,polarCanvas*cpw*lowmask);
                    }

                    float laneDim=0.0f;
                    if(laneCount>0)
                    {
                        float warp=Noise3D.Fbm(sp*warpFreq+warpOffset,3,2.0f,0.5f)*warpAmount;
                        float wl=lat+warp;
                        float laneW=MathF.Max(0.0035f,1.5f*Glsl.PI/fullHeight);
                        for(int i=0;i<laneCount;i++){float dl=(wl-lanes[i].X)/laneW;laneDim+=lanes[i].Y*MathF.Exp(-dl*dl);}
                        col*=1.0f-Glsl.Clamp(laneDim,0.0f,0.5f);
                    }
                    if(maskOn)col=Glsl.Mix(col,maskBandCol,maskValue*maskBandFade);

                    col=Glsl.Mix(col,hazeColor,haze);
                    float contrast=Glsl.Mix(MathF.Max(contrastParam,0.0f),1.0f,haze*0.5f);
                    col=(col-new V3(0.5f,0.5f,0.5f))*contrast+new V3(0.5f,0.5f,0.5f);
                    float luma=Luma(col);
                    col=Glsl.Mix(new V3(luma,luma,luma),col,saturation*(1.0f-haze*0.4f));

                    if(chromaOn)
                    {
                        col=Glsl.Clamp(col,0.0f,1.0f);
                        float cscale=chromaScale;
                        if(chromaVariance>0.0f)
                        {
                            float drift=Noise3D.Fbm(sp*new V3(0.9f,4.0f,0.9f)+chromaOffset,3,2.0f,0.5f);
                            cscale*=1.0f+chromaVariance*drift;
                        }
                        V3 lab=Oklab.SrgbToOklab(col);
                        lab.Y*=cscale;lab.Z*=cscale;
                        if(chromaAging>0.0f)
                        {
                            float L=Glsl.Clamp(lab.X,0.0f,1.0f);
                            float dark=Glsl.SmoothStep(0.28f,0.70f,1.0f-L);
                            float age=Glsl.Clamp(0.5f-t.Z,-0.5f,0.5f);
                            float poleTaper=1.0f-Glsl.SmoothStep(0.87f,1.20f,MathF.Abs(lat));
                            float chromo=dark*poleTaper*(0.65f+0.7f*Glsl.SmoothStep(-0.25f,0.30f,age));
                            V2 warm=Glsl.Normalize(new V2(0.6f,0.8f));
                            lab.Y+=chromaAging*chromo*0.28f*warm.X;lab.Z+=chromaAging*chromo*0.28f*warm.Y;
                            float scl=Glsl.Clamp(1.0f+chromaAging*chromo*0.55f,0.5f,1.8f);lab.Y*=scl;lab.Z*=scl;
                        }
                        if(hueVariance>0.0f)
                        {
                            float theta=hueVariance*Noise3D.Fbm(sp*new V3(0.9f,4.0f,0.9f)+hueOffset,3,2.0f,0.5f);
                            float cs=MathF.Cos(theta),sn=MathF.Sin(theta);float a=lab.Y,b=lab.Z;lab.Y=a*cs-b*sn;lab.Z=a*sn+b*cs;
                        }
                        col=Oklab.OklabToSrgb(lab);
                    }

                    if(bandTintStrength>0.0f)
                    {
                        V3 tintColor=luts.BandTint.SampleLinear(new V2(Glsl.Clamp(1.0f-uv.Y,0.0f,1.0f),0.5f)).XYZ;
                        col=Glsl.Mix(col,tintColor,bandTintStrength);
                    }
                    col=PowGamma(Glsl.Clamp(col,0.0f,1.0f),1.0f/MathF.Max(gamma,1e-3f));

                    float outHeight=Glsl.Clamp(t.Y+0.15f*detailGain*(t.Z-0.5f)+0.1f*detailIntensity*(dsyn-0.5f),0.0f,1.0f);
                    outHeight=Glsl.Mix(outHeight,0.5f,haze*0.6f);
                    outv.Color.Set4(px,py,new V4(col.X,col.Y,col.Z,1.0f));
                    outv.Height.Set(px,py,0,outHeight);

                    if(emissionOn)
                    {
                        V4 em=EmissionPixel(sim,p,ec,uv,lat,sp,t,dsyn,detailIntensity,warpOffset,warpFreq,warpAmount,haze);
                        if(maskOn)
                        {
                            float emMask=Glsl.Mix(1.0f,maskValue,maskEmissionGain);
                            em.X*=emMask;em.Y*=emMask;em.Z*=emMask;em.W*=emMask;
                        }
                        outv.Emission.Set4(px,py,em);
                    }
                }
            });
            return outv;
        }

        private sealed class EmissionContext
        {
            public V3 ThermalColor; public float ThermalStrength,Threshold,Hdr;
            public V3 LightningColor; public float LightningStrength,Density; public V3 LightningOffset;
            public float AuroraStrength,Radius,Width; public V3 PoleN,PoleS,AuroraOffset;
        }

        private static EmissionContext BuildEmissionContext(ParamTree p)
        {
            EmissionContext c=new EmissionContext();
            c.ThermalColor=Read3(p,"emission.thermal_color");c.ThermalStrength=p.Float("emission.thermal_strength");c.Threshold=p.Float("emission.thermal_threshold");c.Hdr=p.Float("emission.thermal_hdr");
            c.LightningColor=Read3(p,"emission.lightning_color");c.LightningStrength=p.Float("emission.lightning_strength");c.Density=p.Float("emission.lightning_density");
            c.LightningOffset=Draw3(RandomGenerator.Subseed(p.Int("seed"),"emission-lightning"));
            c.AuroraStrength=p.Float("emission.aurora_strength");c.Radius=p.Float("emission.aurora_radius")*Glsl.PI/180.0f;c.Width=p.Float("emission.aurora_width")*Glsl.PI/180.0f;
            RandomGenerator au=RandomGenerator.Subseed(p.Int("seed"),"emission-aurora");
            c.AuroraOffset=Draw3(au);float tilt=p.Float("emission.aurora_pole_offset")*Glsl.PI/180.0f;float lonN=(float)au.Uniform(-Math.PI,Math.PI),lonS=(float)au.Uniform(-Math.PI,Math.PI);float st=MathF.Sin(tilt),ct=MathF.Cos(tilt);
            c.PoleN=new V3(st*MathF.Cos(lonN),ct,st*MathF.Sin(lonN));c.PoleS=new V3(st*MathF.Cos(lonS),-ct,st*MathF.Sin(lonS));
            return c;
        }

        private static V4 EmissionPixel(CpuSimulation sim,ParamTree p,EmissionContext c,V2 uv,float lat,V3 sp,V4 t,float dsyn,float detailIntensity,V3 warpOffset,float warpFreq,float warpAmount,float haze)
        {
            V3 emis=new V3(0,0,0);
            if(c.ThermalStrength>0.0f)
            {
                float ewarp=Noise3D.Fbm(sp*warpFreq+warpOffset,3,2.0f,0.5f)*warpAmount;
                float su=Glsl.Clamp((0.5f*Glsl.PI-(lat+ewarp))/Glsl.PI,0.0f,1.0f);
                float stampT1=sim.ProfileStamp.Sample(su).Y;float anomaly=MathF.Max(stampT1-t.Y,0.0f);
                float deck=0.05f*Glsl.SmoothStep(0.05f,0.15f,anomaly);float hot=c.Hdr*Glsl.SmoothStep(c.Threshold,c.Threshold+0.14f,anomaly);
                emis+=c.ThermalColor*(c.ThermalStrength*(deck+hot)*(1.0f-haze));
            }
            if(c.LightningStrength>0.0f)
            {
                float belt=sim.ProfileDyn.Sample(Glsl.Clamp((0.5f*Glsl.PI-lat)/Glsl.PI,0.0f,1.0f)).W;
                float turb=detailIntensity>0.0f?Glsl.Clamp(MathF.Abs(dsyn-0.5f)*2.0f+0.4f,0.0f,1.0f):0.7f;
                float act=Glsl.Clamp(belt*turb+Glsl.SmoothStep(0.96f,1.22f,MathF.Abs(lat)),0.0f,1.0f);
                act*=1.0f-Glsl.SmoothStep(0.5f,0.7f,t.W);act+=0.2f*MathF.Min(MathF.Max(-t.W,0.0f),0.25f);
                float cluster=Glsl.SmoothStep(0.15f,0.65f,Noise3D.SNoise(sp*14.0f+c.LightningOffset));float gate=c.Density*act*cluster;
                if(gate>1e-3f){float s=Noise3D.SNoise(sp*100.0f+c.LightningOffset.ZXY*1.7f);float thi=Glsl.Mix(0.88f,0.70f,gate);float core=25.0f*Glsl.SmoothStep(thi,thi+0.03f,s);float halo=1.5f*Glsl.SmoothStep(thi-0.14f,thi,s);emis+=c.LightningColor*(c.LightningStrength*(core+halo)*act);}
            }
            float aurora=0.0f;
            if(c.AuroraStrength>0.0f)
            {
                float wob=Noise3D.Fbm(sp*3.0f+c.AuroraOffset,3,2.0f,0.5f);float along=0.55f+0.45f*Noise3D.Fbm(sp*8.0f+c.AuroraOffset.YZX,2,2.0f,0.5f);
                V3[] poles=new V3[]{c.PoleN,c.PoleS};for(int i=0;i<2;i++){float rho=MathF.Acos(Glsl.Clamp(Glsl.Dot(sp,poles[i]),-1.0f,1.0f));float r0=c.Radius*(1.0f+0.22f*wob);float dr=(rho-r0)/c.Width;aurora+=c.AuroraStrength*along*MathF.Exp(-dr*dr);}
            }
            return new V4(emis.X,emis.Y,emis.Z,aurora);
        }

        private static bool EmissionEnabled(ParamTree p){return p.Float("emission.thermal_strength")>0.0f||p.Float("emission.lightning_strength")>0.0f||p.Float("emission.aurora_strength")>0.0f;}
        private static V3 Read3(ParamTree p,string path){float[] a=p.FloatArray(path);return new V3(a[0],a[1],a[2]);}
        private static V3 Draw3(RandomGenerator r){return new V3((float)r.Uniform(-100.0,100.0),(float)r.Uniform(-100.0,100.0),(float)r.Uniform(-100.0,100.0));}
        private static float Luma(V3 c){return c.X*0.2126f+c.Y*0.7152f+c.Z*0.0722f;}
        private static V3 PowGamma(V3 c,float e){return new V3(MathF.Pow(c.X,e),MathF.Pow(c.Y,e),MathF.Pow(c.Z,e));}
    }
}
