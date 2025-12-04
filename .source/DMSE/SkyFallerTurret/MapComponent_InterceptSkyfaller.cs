using RimWorld;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;
using Verse.AI.Group;

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
        public FieldInfo BurstCooldownTicksLeft
        {
            get
            {
                if (burstCooldownTicksLeft == null)
                {
                    this.burstCooldownTicksLeft = 
                        typeof(Building_TurretGun).GetField("burstCooldownTicksLeft",BindingFlags.NonPublic | BindingFlags.Instance);
                }
                return this.burstCooldownTicksLeft;
            }
        }
        public FieldInfo CurrentTargetInt
        {
            get
            {
                if (currentTargetInt == null)
                {
                    this.currentTargetInt =
                        typeof(Building_TurretGun).GetField("currentTargetInt", BindingFlags.NonPublic | BindingFlags.Instance);
                }
                return this.currentTargetInt;
            }
        }
        public MethodInfo ResetCurrentTarget
        {
            get
            {
                if (resetCurrentTarget == null)
                {
                    this.resetCurrentTarget =
                        typeof(Building_TurretGun).GetMethod("ResetCurrentTarget", BindingFlags.NonPublic | BindingFlags.Instance);
                }
                return this.resetCurrentTarget;
            }
        }
        public override void MapComponentTick()
        {
            base.MapComponentTick();
            for (int i = Pods.Count - 1; i >= 0; i--)
            {
                var pod = Pods[i];
                int time = pod.tickToSpawn - Find.TickManager.TicksGame;
                foreach (var c in this.turrets)
                { 
                    if (time <= c.Props.lastInterceptTick && c.cooldownLast <= 0 && c.parent is Building_TurretGun turret
                        && BurstCooldownTicksLeft.GetValue(turret) is int cooldown && cooldown <= 0) 
                    { 
                        ResetCurrentTarget.Invoke(turret, null);
                        int count = c.Props.countLimitForLast;
                        while (count > 0 && pod.pods.Any()) 
                        {
                            var p = pod.pods.First(); 
                            LocalTargetInfo target = new LocalTargetInfo(p.position);
                            Projectile pro = (Projectile)GenSpawn.Spawn(c.Props.projectile_Last,c.parent.Position,c.parent.Map, WipeMode.Vanish);
                            pro.Launch(c.parent, target, target,ProjectileHitFlags.All);
                            CurrentTargetInt.SetValue(turret,target);
                            count--;
                            if (count <= 0) 
                            {
                                break;
                            }
                            if (Rand.Chance(c.Props.interceptChance))
                            { 
                                p.Intercept(this.map);
                                pod.pods.Remove(p);
                                if (!pod.pods.Any())
                                {
                                    Pods.Remove(pod);
                                }
                            }
                        }
                        BurstCooldownTicksLeft.SetValue(turret,turret.AttackVerb.verbProps.defaultCooldownTime.SecondsToTicks());
                        c.cooldownLast = time;
                    }
                }
                if (Find.TickManager.TicksGame >= pod.tickToSpawn)
                {
                    pod.pods.ForEach(p => 
                    {
                        p.pod.TryDrop(p.pod.InnerListForReading.First(),p.position,p.map,ThingPlaceMode.Direct,
                            out var p2);
                        if (pod.lord != null && p2.innerContainer.First() is Pawn pawn 
                        && !pod.lord.ownedPawns.Contains(pawn)) 
                        {
                            pod.lord.AddPawn(pawn);
                        }
                    });
                    pod.signal.ForEach(p =>
                    { 
                        Find.SignalManager.SendSignal(new Signal(p, false));
                    });
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

        FieldInfo burstCooldownTicksLeft;
        FieldInfo currentTargetInt;
        MethodInfo resetCurrentTarget;
        public HashSet<SkyfallerTurretComp> turrets = new HashSet<SkyfallerTurretComp>();
    }
    public class DroppodData : IExposable
    {
        public DroppodData() 
        {
        
        }
        public DroppodData(int tick,List<PodData> datas)
        {
            this.tickToSpawn = tick;
            this.pods = datas;
        }
        public void ExposeData()
        {
            Scribe_Values.Look(ref this.tickToSpawn,"tickToSpawn");
            Scribe_References.Look(ref this.lord,"lord");
            Scribe_Collections.Look(ref this.pods, "pods",LookMode.Deep);
            Scribe_Collections.Look(ref this.signal, "signal", LookMode.Value);
        }

        public int tickToSpawn;
        public List<PodData> pods = new List<PodData>();
        public Lord lord;
        public List<string> signal = new List<string>();
    }

    public class PodData : IExposable,IThingHolder
    {
        public PodData()
        {

        }
        public PodData(Skyfaller pod, IntVec3 pos,Map map)
        {
            this.map = map;
            this.pod = new ThingOwner<Skyfaller>(this);
            this.pod.TryAdd(pod);
            this.position = pos;
        }
        public IThingHolder ParentHolder => this.map;
        public void Intercept(Map map)
        {
            Pawn pawn = null;
            if (this.pod.InnerListForReading.First().innerContainer.First() is ActiveTransporter transporter &&
                transporter.Contents.SingleContainedThing is Pawn pawn2)
            {
                pawn = pawn2;
            }
            if (this.pod.InnerListForReading.First().innerContainer.First() is Pawn p)
            {
                pawn = p;
            }
            if (pawn != null)
            {
                pawn.Kill(new DamageInfo(DamageDefOf.Blunt, 50f));
                var info = new ActiveTransporterInfo();
                Corpse corpse = null;
                if (pawn.Corpse != null)
                {
                    corpse = pawn.Corpse;
                }
                if (corpse == null)
                {
                    corpse = pawn.MakeCorpse(null, null);
                }
                if (corpse == null)
                {
                    if (Prefs.DevMode)
                    {
                        Log.Message("尸体生成失败");
                    }
                }
                else
                {
                    if (Prefs.DevMode)
                    {
                        Log.Message("尸体" + corpse);
                    }
                }
                if (corpse.holdingOwner != null)
                {
                    corpse.holdingOwner.Remove(corpse);
                }
                if (info.innerContainer.TryAdd(corpse))
                {
                    if (Prefs.DevMode)
                    {
                        Log.Message("尸体进入残骸成功");
                    }
                }
                else if (Prefs.DevMode)
                {
                    Log.Message("尸体进入残骸失败");
                }
                DropPodUtility.MakeDropPodAt(this.position, map, info); 
                if (Prefs.DevMode)
                {
                    Log.Message("生成残骸空投" + this.position);
                }
            }
        }
        public void ExposeData()
        {
            Scribe_References.Look(ref this.map,"map");
            Scribe_Values.Look(ref this.position, "position");
            Scribe_Deep.Look(ref this.pod, "pod");
        }

        public void GetChildHolders(List<IThingHolder> outChildren)
        {
            ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, this.GetDirectlyHeldThings());
        }

        public ThingOwner GetDirectlyHeldThings()
        {
            return pod;
        }

        public ThingOwner<Skyfaller> pod;
        public IntVec3 position;
        public Map map;
    }
} 