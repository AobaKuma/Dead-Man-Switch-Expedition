using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace DMSE
{
    [DefOf]
    public static class DMSE_MissileCrewDutyDefOf
    {
        /// <summary>操作陣地上需人操作的設備；飢餓時先去吃，吃完回崗（見 DMSE_MissileCrewDuties.xml）。</summary>
        public static DutyDef DMSE_ManMissileStations;

        static DMSE_MissileCrewDutyDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(DMSE_MissileCrewDutyDefOf));
    }

    /// <summary>
    /// 找最近一座本派系、需人操作（<see cref="CompMannable"/>）且還沒人操作的設備：砲塔、雷達、發射架都算。<br/>
    /// 原版 <see cref="JobGiver_ManTurrets"/> 的驗證會把每個候選強制轉成 <see cref="Building_TurretGun"/>，
    /// 所以 NPC 從不操作雷達或導彈發射架。這裡依設備類型給對應工作：攔截發射架用 <c>DMSE_ManMissileLauncher</c>，
    /// 其餘（砲塔、雷達）用原版 <see cref="JobDefOf.ManTurret"/>（其 JobDriver 對非砲塔建築也適用）。<br/>
    /// 工作每 <see cref="expiryInterval"/> tick 到期重新評估，讓思考樹中排在前面的進食節點能讓守軍下崗。
    /// </summary>
    public class JobGiver_ManMissileStation : ThinkNode_JobGiver
    {
        public float maxDistFromPoint = 60f;
        public int expiryInterval = 600;

        public override ThinkNode DeepCopy(bool resolve = true)
        {
            JobGiver_ManMissileStation copy = (JobGiver_ManMissileStation)base.DeepCopy(resolve);
            copy.maxDistFromPoint = maxDistFromPoint;
            copy.expiryInterval = expiryInterval;
            return copy;
        }

        protected override Job TryGiveJob(Pawn pawn)
        {
            if (pawn.Faction == null || pawn.Map == null) return null;
            IntVec3 root = pawn.mindState.duty?.focus.Cell ?? pawn.Position;
            if (!root.IsValid) root = pawn.Position;

            Building best = null;
            float bestDist = float.MaxValue;
            foreach (Building b in pawn.Map.listerBuildings.allBuildingsNonColonist)
            {
                if (b.Faction != pawn.Faction || b.Destroyed || !b.def.hasInteractionCell) continue;
                CompMannable mannable = b.GetComp<CompMannable>();
                if (mannable == null) continue;
                if (mannable.MannedNow && mannable.ManningPawn != pawn) continue;
                if (b.Position.DistanceTo(root) > maxDistFromPoint) continue;
                if (b is Building_TurretGun turret && !TurretUsable(pawn, turret)) continue;
                float d = b.Position.DistanceToSquared(pawn.Position);
                if (d >= bestDist) continue;
                if (!pawn.CanReserveAndReach(b, PathEndMode.InteractionCell, Danger.Deadly)) continue;
                best = b;
                bestDist = d;
            }
            if (best == null) return null;

            JobDef jobDef = best.TryGetComp<CompMissileLauncher_Interceptor>() != null
                ? DMSE_MissileJobDefOf.DMSE_ManMissileLauncher
                : JobDefOf.ManTurret;
            Job job = JobMaker.MakeJob(jobDef, best);
            job.expiryInterval = expiryInterval;
            job.checkOverrideOnExpire = true;
            return job;
        }

        /// <summary>
        /// 需要砲彈（迫擊砲類、尚未裝填）或燃料（彈藥以燃料表示）而附近沒有時不去操作。
        /// 原版驗證不分砲塔都要求地上有砲彈，等於 NPC 只會操作迫擊砲；這裡只對真的要裝填的砲塔要求砲彈。
        /// </summary>
        private static bool TurretUsable(Pawn pawn, Building_TurretGun turret)
        {
            CompChangeableProjectile shells = turret.gun?.TryGetComp<CompChangeableProjectile>();
            if (shells != null && !shells.Loaded && JobDriver_ManTurret.FindAmmoForTurret(pawn, turret) == null) return false;
            CompRefuelable fuel = turret.TryGetComp<CompRefuelable>();
            return fuel == null || fuel.HasFuel || JobDriver_ManTurret.FindFuelForTurret(pawn, turret) != null;
        }
    }

    /// <summary>給每位炮手 <see cref="DMSE_MissileCrewDutyDefOf.DMSE_ManMissileStations"/> 職責，以陣地中心為據點。</summary>
    public class LordToil_ManMissileStations : LordToil
    {
        private readonly IntVec3 center;

        public LordToil_ManMissileStations(IntVec3 center) { this.center = center; }

        public override bool ForceHighStoryDanger => false;

        public override void UpdateAllDuties()
        {
            foreach (Pawn p in lord.ownedPawns)
                p.mindState.duty = new PawnDuty(DMSE_MissileCrewDutyDefOf.DMSE_ManMissileStations, center);
        }
    }

    /// <summary>NPC 導彈陣地炮手：操作砲塔、雷達與發射架；飢餓時下崗進食後回崗。</summary>
    public class LordJob_MissileStationCrew : LordJob
    {
        public IntVec3 center;

        public LordJob_MissileStationCrew() { }

        public LordJob_MissileStationCrew(IntVec3 center) { this.center = center; }

        public override StateGraph CreateGraph()
        {
            return new StateGraph { StartingToil = new LordToil_ManMissileStations(center) };
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref center, "center");
        }
    }
}
