using RimWorld;
using System.Collections.Generic;
using System.Linq;
using Verse;
using Verse.AI.Group;

namespace DMSE
{
    /// <summary>一個目標波次：在同一預測落地 tick 抵達的一組目標。</summary>
    public class BVRWave : IExposable
    {
        public int tickToImpact;
        public List<BVRTarget> targets = new List<BVRTarget>();
        public Lord lord;
        public List<string> signal = new List<string>();

        /// <summary>負責攔截此波次的防禦方陣營（擁有雷達/發射器/CIWS 的一方）。</summary>
        public Faction defenderFaction;

        public BVRWave() { }

        public BVRWave(int tick, List<BVRTarget> targets)
        {
            this.tickToImpact = tick;
            this.targets = targets;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref tickToImpact, "tickToImpact");
            Scribe_References.Look(ref lord, "lord");
            Scribe_References.Look(ref defenderFaction, "defenderFaction");
            Scribe_Collections.Look(ref targets, "targets", LookMode.Deep);
            Scribe_Collections.Look(ref signal, "signal", LookMode.Value);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (targets == null) { targets = new List<BVRTarget>(); }
                if (signal == null) { signal = new List<string>(); }
            }
        }
    }

    /// <summary>單一被攔截目標：持有被臨時收起的 incoming skyfaller，並記錄攔截狀態。</summary>
    public class BVRTarget : IExposable, IThingHolder
    {
        public int id;
        public ThingOwner<Skyfaller> skyfaller;
        public IntVec3 position;
        public Map map;

        // 目標屬性（由 def 的 BVRTargetProps 解析）。
        public int stealthLevel;
        public float speed = 15f;

        // 中過程狀態：在此 tick 之前已有攔截彈在飛，避免重複指派（占用一個火力通道）。
        public int midcourseEngagedUntil = -1;

        // 火控鎖定：>= 0 表示已占用一個火力通道（> now 鎖定中，<= now 鎖定完成待發）。-1 = 未鎖定（含已發射）。
        public int lockUntil = -1;

        public BVRTarget() { }

        public BVRTarget(Skyfaller faller, IntVec3 pos, Map map, int id)
        {
            this.id = id;
            this.map = map;
            this.position = pos;
            this.skyfaller = new ThingOwner<Skyfaller>(this);
            this.skyfaller.TryAdd(faller);

            BVRTargetProps tp = (faller != null && faller.def != null
                ? faller.def.GetModExtension<BVRTargetProps>() : null) ?? BVRTargetProps.Default;
            this.stealthLevel = tp.stealthLevel;
            this.speed = tp.speed;
        }

        public Skyfaller Skyfaller => skyfaller != null ? skyfaller.InnerListForReading.FirstOrDefault() : null;

        /// <summary>取得目標內部的 pawn（直接持有或包在 ActiveTransporter 內）。</summary>
        public Pawn ContainedPawn
        {
            get
            {
                Skyfaller f = Skyfaller;
                if (f == null || !f.innerContainer.Any) { return null; }
                Thing inner = f.innerContainer.First();
                if (inner is Pawn p) { return p; }
                if (inner is ActiveTransporter transporter
                    && transporter.Contents != null
                    && transporter.Contents.SingleContainedThing is Pawn p2)
                {
                    return p2;
                }
                return null;
            }
        }

        /// <summary>未被攔截：讓內部 skyfaller 真正落地。</summary>
        public void Resolve(BVRWave wave)
        {
            Skyfaller f = Skyfaller;
            if (f == null || map == null) { return; }

            if (skyfaller.TryDrop(f, position, map, ThingPlaceMode.Direct, out Thing dropped))
            {
                if (wave != null && wave.lord != null
                    && dropped is Skyfaller sf && sf.innerContainer.Any
                    && sf.innerContainer.First() is Pawn pawn
                    && !wave.lord.ownedPawns.Contains(pawn))
                {
                    wave.lord.AddPawn(pawn);
                }
            }
        }

        /// <summary>末端攔截成功且擲骰通過時呼叫：殺死內部 pawn 並生成殘骸空投（碎片）。</summary>
        public void SpawnDebris(Map map)
        {
            Pawn pawn = ContainedPawn;
            if (pawn == null)
            {
                // 無 pawn 的目標（例如導彈）：無屍體殘骸，直接乾淨清除。
                Discard();
                return;
            }

            pawn.Kill(new DamageInfo(DamageDefOf.Blunt, 50f));
            Corpse corpse = pawn.Corpse ?? pawn.MakeCorpse(null, null);
            if (corpse == null) { return; }

            if (corpse.holdingOwner != null)
            {
                corpse.holdingOwner.Remove(corpse);
            }

            ActiveTransporterInfo info = new ActiveTransporterInfo();
            if (info.innerContainer.TryAdd(corpse))
            {
                DropPodUtility.MakeDropPodAt(position, map, info);
            }
        }

        /// <summary>清掉持有的 skyfaller（攔截成功、不落地、不留殘骸）。</summary>
        public void Discard()
        {
            if (skyfaller != null)
            {
                skyfaller.ClearAndDestroyContents();
            }
        }

        public IThingHolder ParentHolder => map;
        public void GetChildHolders(List<IThingHolder> outChildren)
            => ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, GetDirectlyHeldThings());
        public ThingOwner GetDirectlyHeldThings() => skyfaller;

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_References.Look(ref map, "map");
            Scribe_Values.Look(ref position, "position");
            Scribe_Deep.Look(ref skyfaller, "skyfaller", this);
            Scribe_Values.Look(ref stealthLevel, "stealthLevel", 0);
            Scribe_Values.Look(ref speed, "speed", 15f);
            Scribe_Values.Look(ref midcourseEngagedUntil, "midcourseEngagedUntil", -1);
            Scribe_Values.Look(ref lockUntil, "lockUntil", -1);
        }
    }
}
