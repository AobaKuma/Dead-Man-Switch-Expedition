using Verse;

namespace DMSE.SkyFallerTurret
{
    public class SkyfallerTurretCompProperties : CompProperties
    {
        public SkyfallerTurretCompProperties() 
        {
            this.compClass = typeof(SkyfallerTurretComp);
        }
        public ThingDef projectile;
        public int cooldown = 600;
        public int countLimit = 3;
    }

    public class SkyfallerTurretComp : ThingComp
    {
        public SkyfallerTurretCompProperties Props => (SkyfallerTurretCompProperties)this.props;
        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            var list = map.GetComponent<MapComponent_InterceptSkyfaller>().turrets;
            if (list.Contains(this)) 
            {
                list.Remove(this);
            }
        }
        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            var list = this.parent.Map.GetComponent<MapComponent_InterceptSkyfaller>().turrets;
            if (!list.Contains(this))
            {
                if (Prefs.DevMode) 
                {
                    Log.Message("添加Turrets:" + this.parent.Label);
                }
                list.Add(this);
            }
        }
        public override void CompTick()
        {
            base.CompTick();
            if (this.count < this.Props.countLimit && Find.TickManager.TicksGame >= this.cooldown) 
            {
                this.cooldown = Find.TickManager.TicksGame + this.Props.cooldown;
                this.count++;
            }
        }
        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref this.cooldown, "cooldown");
            Scribe_Values.Look(ref this.count, "count");
        }


        public int cooldown;
        public int count;
    }
}
