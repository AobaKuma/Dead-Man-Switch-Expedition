using RimWorld;
using System.Collections.Generic;
using Verse;

namespace DMSE.SkyFallerTurret
{
    public class MapComponent_InterceptSkyfaller : MapComponent
    {
        public MapComponent_InterceptSkyfaller(Map map) : base(map)
        {
        }
        public List<DroppodData> Pods 
        {
            get 
            {
                if (pods == null) 
                {
                    this.pods = new List<DroppodData>();
                }
                return this.pods;
            }
        }
        public override void MapComponentTick()
        {
            base.MapComponentTick();
            for (int i = Pods.Count - 1; i >= 0; i--)
            {
                var pod = Pods[i];
                if (Find.TickManager.TicksGame >= pod.tickToSpawn)
                { 
                    GenSpawn.Spawn(pod.pod, pod.position, this.map);
                    Pods.RemoveAt(i);
                }
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref this.pods,"pods",LookMode.Deep);
        }

        List<DroppodData> pods = new List<DroppodData>();


        public HashSet<SkyfallerTurretComp> turrets = new HashSet<SkyfallerTurretComp>();
    }
    public class DroppodData : IExposable
    {
        public DroppodData() 
        {
        
        }
        public DroppodData(int tick,Skyfaller pod,IntVec3 pos)
        {
            this.tickToSpawn = tick;
            this.pod = pod;
            this.position = pos;
        }
        public void ExposeData()
        {
            Scribe_Values.Look(ref this.tickToSpawn,"tickToSpawn");
            Scribe_Values.Look(ref this.position, "position");
            Scribe_Deep.Look(ref this.pod, "pod");
        }

        public int tickToSpawn;
        public Skyfaller pod;
        public IntVec3 position; 
    }
}
