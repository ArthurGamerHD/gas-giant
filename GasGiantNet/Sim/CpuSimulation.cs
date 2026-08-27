using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using GasGiantNet.Config;
using GasGiantNet.MathCore;

namespace GasGiantNet.Sim
{
    internal sealed class CpuSimulation
    {
        public const float RhoMax = 34.0f * Glsl.PI / 180.0f;
        public const float ExchangePatchLo = 63.0f * Glsl.PI / 180.0f;
        public const float ExchangePatchHi = 65.0f * Glsl.PI / 180.0f;
        public const float ExchangeEqLo = 65.0f * Glsl.PI / 180.0f;
        public const float ExchangeEqHi = 67.0f * Glsl.PI / 180.0f;
        private const double VortexSpeedMargin = 0.45;

        public readonly ParamTree Params;
        public readonly BandLayout Bands;
        public LatProfiles Profiles;
        public readonly VortexRegistry Vortices;
        public readonly EventSchedule Events;
        public readonly SimStaticUniforms Static;
        public readonly SimDomain Equirect;
        public readonly SimDomain North;
        public readonly SimDomain South;
        public readonly SimDomain[] Domains;
        public readonly LatLut ProfileDyn;
        public readonly LatLut ProfileStamp;
        public readonly LatLut ProfileOmega;
        public readonly double StepScale;
        public readonly float FestoonLat;
        public readonly float RibbonLat;
        public readonly float HeroFestoonLat;
        public readonly bool Festoon2;
        public readonly bool HeroEmergence;
        public readonly bool CastLevers;
        public readonly float HeroFlowRenorm;
        public FloatTexture ExternalOmega;
        public float ExternalOmegaGain;
        public double Dt;
        public int StepIndex;

        private readonly int _threads;
        private readonly float _relaxK;
        private readonly float _replenish;
        private readonly float _beltReplenish;

        public static CpuSimulation Build(ParamTree p, int threads)
        {
            BandLayout bands = Sim.Bands.Generate(p.Int("seed"), p);
            double? heroLat = p.Int("storms.hero_count") > 0 && p.Has("storms.hero_latitude") ? (double?)p.Double("storms.hero_latitude") : null;
            LatProfiles profiles = Sim.Profiles.Build(p.Int("seed"), bands, p, heroLat, p.Double("storms.hero_radius"));
            double dt = ComputeDt(p.Int("sim.resolution"), p.Double("sim.dt_scale"), profiles.MaxSpeed);
            double scale = ResolutionScaling.ScaleFactor(p);
            VortexRegistry vortices = Sim.Vortices.Generate(p.Int("seed"), bands, profiles, p, dt, p.Int("sim.dev_steps"), scale);
            EventSchedule events = EventSchedule.Generate(p.Int("seed"), p, bands, profiles, dt);
            return new CpuSimulation(p,bands,profiles,vortices,events,threads,dt,scale);
        }

        private CpuSimulation(ParamTree p,BandLayout bands,LatProfiles profiles,VortexRegistry vortices,EventSchedule events,int threads,double dt,double scale)
        {
            Params=p; Bands=bands; Profiles=profiles; Vortices=vortices; Events=events; _threads=threads; Dt=dt; StepScale=scale;
            Static=SimStaticUniforms.Build(p.Int("seed"),p.Int("storms.hero_shape_seed"));
            ProfileDyn=new LatLut(profiles.DynLut(),profiles.Lat.Length,4);
            ProfileStamp=new LatLut(profiles.StampLut(),profiles.Lat.Length,4);
            ProfileOmega=new LatLut(profiles.OmegaLut(),profiles.Lat.Length,4);
            double fest,rib; Sim.Profiles.SelectWaveLatitudes(bands,profiles,out fest,out rib);
            FestoonLat=(float)fest; RibbonLat=(float)rib;
            double? heroFest=null;
            if(p.Double("waves.festoon_hero_strength")>0.0)
            {
                List<Vortex> heroes=vortices.Heroes();
                if(heroes.Count>0)heroFest=Sim.Profiles.SelectHeroFestoonLatitude(bands,heroes[0].Lat,fest);
            }
            Festoon2=heroFest.HasValue;
            HeroFestoonLat=(float)(heroFest.HasValue?heroFest.Value:0.0);
            HeroEmergence=HeroEmergenceActive(p,vortices);
            CastLevers=CastLeversActive(p);
            HeroFlowRenorm=HeroFlowRenormCpu.Compute(p,vortices);

            int w=p.Int("sim.resolution");
            int patch=PatchResolution(w);
            Equirect=new SimDomain(DomainKind.Equirect,w,w/2,RhoMax);
            North=new SimDomain(DomainKind.NorthPatch,patch,patch,RhoMax);
            South=new SimDomain(DomainKind.SouthPatch,patch,patch,RhoMax);
            Domains=new SimDomain[]{Equirect,North,South};

            _relaxK=(float)ResolutionScaling.ScaleDecayFraction(1.0/Math.Max(p.Double("turbulence.relax_tau"),1.0),scale);
            _replenish=(float)ResolutionScaling.ScaleDecayFraction(p.Double("turbulence.replenish_rate"),scale);
            _beltReplenish=(float)ResolutionScaling.ScaleDecayFraction(p.Double("turbulence.belt_replenish"),scale);
        }

        public void Initialize()
        {
            VortexStampContext stampCtx=MakeVortexStampContext();
            bool vorticity=string.Equals(Params.String("solver.type"),"vorticity",StringComparison.OrdinalIgnoreCase);
            for(int i=0;i<Domains.Length;i++)
            {
                SimDomain d=Domains[i];
                if(vorticity) VorticitySolverCpu.Initialize(this,d,_threads);
                TracerKernels.Init(d,MakeTracerContext(d,stampCtx,0.0f),_threads);
            }
            Exchange();
        }

        public void Develop(int steps)
        {
            for(int i=0;i<steps;i++)Step();
        }

        public void Step()
        {
            List<OutflowImpulse> impulses=Events!=null?Events.Apply(StepIndex,Vortices):new List<OutflowImpulse>();
            Vortices.Drift(Profiles,Dt);
            if(Params.Double("storms.merge_rate")>0.0)Sim.Vortices.ResolveMergers(Vortices,Profiles,Params);
            float turbTime=(float)(StepIndex*Params.Double("turbulence.evolution_rate")/StepScale);
            VortexStampContext stampCtx=MakeVortexStampContext();

            for(int i=0;i<Domains.Length;i++)
            {
                SimDomain d=Domains[i];
                FlowContext fc=MakeFlowContext(d,stampCtx,turbTime,impulses);
                ProducePsi(d,fc,turbTime);
                FlowKernels.BuildVelocity(d,fc,_threads);
                TracerKernels.StepMacCormack(d,MakeTracerContext(d,stampCtx,turbTime),(float)Dt,_threads);
            }
            Exchange();
            StepIndex++;
        }

        private void ProducePsi(SimDomain d,FlowContext fc,float turbTime)
        {
            string solver=Params.String("solver.type");
            if(string.Equals(solver,"vorticity",StringComparison.OrdinalIgnoreCase))
            {
                // The full vorticity path is supplied by VorticitySolverCpu and
                // writes d.Psi after advancing absolute q and solving Poisson.
                VorticitySolverCpu.ProducePsi(this,d,fc,turbTime,_threads);
                return;
            }
            FlowKernels.BuildPsi(d,fc,_threads);
        }

        private VortexStampContext MakeVortexStampContext()
        {
            return new VortexStampContext(Params,Static,Vortices,HeroEmergence,CastLevers);
        }

        private FlowContext MakeFlowContext(SimDomain d,VortexStampContext stamp,float turbTime,List<OutflowImpulse> impulses)
        {
            FlowContext c=new FlowContext();
            c.Params=Params; c.ProfileDyn=ProfileDyn; c.ProfileStamp=ProfileStamp; c.Static=Static; c.Vortices=Vortices;
            c.FestoonLat=FestoonLat; c.RibbonLat=RibbonLat; c.TurbTime=turbTime; c.HeroEmergence=HeroEmergence; c.CastLevers=CastLevers;
            c.CastLeverData=CastLevers?Vortices.PackCastLeversSsbo(Params):null;
            c.Outbreaks=impulses==null?null:impulses.ToArray();
            ApplyPoly(d,c);
            return c;
        }

        private TracerKernelContext MakeTracerContext(SimDomain d,VortexStampContext stamp,float turbTime)
        {
            TracerKernelContext c=new TracerKernelContext();
            c.Params=Params; c.Bands=Bands; c.ProfileDyn=ProfileDyn; c.ProfileStamp=ProfileStamp; c.Static=Static; c.VortexStamp=stamp;
            c.FestoonLat=FestoonLat;c.RibbonLat=RibbonLat;c.HeroFestoonLat=HeroFestoonLat;c.Festoon2=Festoon2;
            c.TurbTime=turbTime;c.RelaxK=_relaxK;c.Replenish=_replenish;c.BeltReplenish=_beltReplenish;c.BeltScale=Params.Float("turbulence.belt_replenish_scale");
            if(d.Kind!=DomainKind.Equirect)
            {
                bool north=d.Kind==DomainKind.NorthPatch;
                string prefix=north?"poles.north.":"poles.south.";
                bool enabled=Params.String(prefix+"style")=="polygon_jet"&&Params.Double(prefix+"strength")>0.0;
                c.PolyAmp=enabled?(float)(0.016*Params.Double(prefix+"strength")):0.0f;
                c.PolyK=Params.Float(prefix+"polygon_sides");c.PolyRho=0.21f;c.PolyEps=0.12f;c.PolyPhase=Static.PolyPhase;c.PolyWidth=0.03f;
            }
            return c;
        }

        private void ApplyPoly(SimDomain d,FlowContext c)
        {
            if(d.Kind==DomainKind.Equirect)return;
            string prefix=d.Kind==DomainKind.NorthPatch?"poles.north.":"poles.south.";
            bool enabled=Params.String(prefix+"style")=="polygon_jet"&&Params.Double(prefix+"strength")>0.0;
            c.PolyAmp=enabled?(float)(0.016*Params.Double(prefix+"strength")):0.0f;
            c.PolyK=Params.Float(prefix+"polygon_sides");c.PolyRho=0.21f;c.PolyEps=0.12f;c.PolyWidth=0.03f;
        }

        private void Exchange()
        {
            DomainExchangeCpu.EquirectToPatch(North,Equirect.Cur,ExchangePatchLo,ExchangePatchHi,_threads);
            DomainExchangeCpu.EquirectToPatch(South,Equirect.Cur,ExchangePatchLo,ExchangePatchHi,_threads);
            DomainExchangeCpu.PatchToEquirect(Equirect,North,South,RhoMax,ExchangeEqLo,ExchangeEqHi,_threads);
        }

        public static double ComputeDt(int resolution,double dtScale,double profilesMaxSpeed)
        {
            double cell=2.0*Math.PI/resolution;
            double maxSpeed=Math.Max(profilesMaxSpeed+VortexSpeedMargin,0.3);
            return dtScale*1.2*cell/maxSpeed;
        }

        public static int PatchResolution(int eqWidth)
        {
            int n=(int)Math.Round(eqWidth*(double)RhoMax/Math.PI/16.0,MidpointRounding.ToEven)*16;
            return Math.Max(n,64);
        }

        private static bool HeroEmergenceActive(ParamTree p,VortexRegistry registry)
        {
            List<Vortex> h=registry.Heroes();
            for(int i=0;i<h.Count;i++) if(Sim.Vortices.EffectiveCastLever(p,h[i].CastRef,"emergence")>0.0)return true;
            return false;
        }

        private static bool CastLeversActive(ParamTree p)
        {
            if(!p.Has("storms.cast"))return false;
            JsonArray cast=p.Array("storms.cast");
            string[] fields=new string[]{"rim_contrast","rim_tint","rim_warp","mottle","tint_var","wake_detail","solid_core","emergence","shape","taper"};
            for(int i=0;i<cast.Count;i++)
            {
                JsonObject o=cast[i] as JsonObject;
                if(o==null)continue;
                JsonNode kindNode=o["kind"];
                if(kindNode==null||kindNode.GetValue<string>()!="hero")continue;
                for(int j=0;j<fields.Length;j++) if(o[fields[j]]!=null)return true;
            }
            return false;
        }
    }
}
