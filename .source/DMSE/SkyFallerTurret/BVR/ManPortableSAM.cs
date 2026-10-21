using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DMSE
{
    [DefOf]
    public static class DMSE_ManPortableSAMDefOf
    {
        public static JobDef DMSE_FireManPortableSAM;

        static DMSE_ManPortableSAMDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(DMSE_ManPortableSAMDefOf));
        }
    }

    /// <summary>單兵防空導彈發射具（docs/三代裝備體系_實作計畫.md §10 C10）。</summary>
    public class CompProperties_ManPortableSAM : CompProperties
    {
        /// <summary>彈藥：裝備者背包裡的這種導彈，每發消耗一枚（對地射擊與防空共用）。</summary>
        public ThingDef ammoDef;

        /// <summary>防空發射時生成的攔截彈 skyfaller（thingClass 應為 DMSE.InterceptProjectile）。</summary>
        public ThingDef interceptorSkyfaller;

        /// <summary>原地瞄準鎖定時間（ticks）。</summary>
        public int aimTicks = 90;

        /// <summary>兩次防空發射之間的冷卻（ticks）。單兵發射不占火控通道，以冷卻限制射速。</summary>
        public int cooldownTicks = 300;

        /// <summary>攔截彈飛行時間（ticks）：期間目標標記為已接戰，其他攔截器不重複射擊。</summary>
        public int interceptorTravelTicks = 180;

        /// <summary>目標預計落點與裝備者的最大距離（格）。</summary>
        public float maxRangeCells = 40f;

        /// <summary>只接戰剩餘時間不超過此值的目標（ticks）。</summary>
        public int maxEtaTicks = 2400;

        /// <summary>導引頭參數：沿用火控雷達的命中率公式（隱身對衝、速度懲罰、導引時間）。</summary>
        public CompProperties_FireControlRadar seeker;

        public string uiIconPath;

        public CompProperties_ManPortableSAM()
        {
            compClass = typeof(CompEquippable_ManPortableSAM);
        }
    }

    /// <summary>
    /// 可對 BVR 目標發射攔截彈的單兵武器。取代武器的 <see cref="CompEquippable"/>（XML 需 comps Inherit="False"），
    /// 原版 <see cref="Pawn_EquipmentTracker.GetGizmos"/> 會為主武器呼叫 <see cref="CompGetEquippedGizmosExtra"/>。
    ///
    /// 只接戰本地圖 <see cref="MapComponent_BVRCombat"/> 的波次（地圖內空投與已進入末端的世界導彈），
    /// 防禦方須是裝備者的陣營；不占火控通道，但需要雷達網提供的航跡。
    /// </summary>
    public class CompEquippable_ManPortableSAM : CompEquippable
    {
        private int cooldownUntil;

        public CompProperties_ManPortableSAM Props => (CompProperties_ManPortableSAM)props;

        public Pawn Wielder => (parent.ParentHolder as Pawn_EquipmentTracker)?.pawn;

        public bool OnCooldown => Find.TickManager.TicksGame < cooldownUntil;

        private Texture2D iconInt;
        private Texture2D Icon
        {
            get
            {
                if (iconInt == null)
                {
                    iconInt = !Props.uiIconPath.NullOrEmpty()
                        ? ContentFinder<Texture2D>.Get(Props.uiIconPath, false)
                        : parent.def.uiIcon;
                }
                return iconInt;
            }
        }

        /// <summary>背包裡的第一枚彈藥；沒有為 null。</summary>
        public static Thing AmmoIn(Pawn pawn, ThingDef ammoDef)
        {
            if (pawn?.inventory == null || ammoDef == null) { return null; }
            ThingOwner<Thing> inv = pawn.inventory.innerContainer;
            for (int i = 0; i < inv.Count; i++)
            {
                if (inv[i].def == ammoDef) { return inv[i]; }
            }
            return null;
        }

        public int AmmoCount(Pawn pawn)
        {
            if (pawn?.inventory == null || Props.ammoDef == null) { return 0; }
            return pawn.inventory.innerContainer.Where(t => t.def == Props.ammoDef).Sum(t => t.stackCount);
        }

        /// <summary>可接戰的目標：本陣營防禦、未被攔截彈占用、在射程與時間內。</summary>
        public IEnumerable<BVRTarget> EngageableTargets(Pawn pawn)
        {
            MapComponent_BVRCombat combat = pawn?.Map?.GetComponent<MapComponent_BVRCombat>();
            if (combat == null) { yield break; }
            int now = Find.TickManager.TicksGame;
            foreach (BVRWave wave in combat.Waves)
            {
                if (wave.defenderFaction != pawn.Faction) { continue; }
                int eta = wave.tickToImpact - now;
                if (eta <= 0 || eta > Props.maxEtaTicks) { continue; }
                foreach (BVRTarget target in wave.targets)
                {
                    if (target.midcourseEngagedUntil > now) { continue; }
                    if (target.position.DistanceTo(pawn.Position) > Props.maxRangeCells) { continue; }
                    yield return target;
                }
            }
        }

        private static BVRWave WaveOf(MapComponent_BVRCombat combat, BVRTarget target)
            => combat?.Waves.FirstOrDefault(w => w.targets.Contains(target));

        /// <summary>命中率：導引頭公式 × 彈體攔截係數 × 射手射擊精度，再加戰鬥部攔截加成。</summary>
        public float HitChance(Pawn pawn, BVRTarget target, int eta)
        {
            if (Props.seeker == null) { return 0f; }
            float chance = CompFireControlRadar.ComputeHitChance(Props.seeker, target, eta);
            MissileConfig cfg = MissileItemExtension.ConfigFor(AmmoIn(pawn, Props.ammoDef));
            chance *= cfg?.InterceptHitFactor ?? 1f;
            chance *= pawn.GetStatValue(StatDefOf.ShootingAccuracyPawn);
            MissilePartDef warhead = cfg?.PartFor(MissilePartCategory.Warhead);
            if (warhead?.warheadEffect != null)
            {
                chance += warhead.warheadEffect.InterceptBonus(cfg.PayloadCapacity);
            }
            return Mathf.Clamp01(chance);
        }

        /// <summary>發射前的共同檢查；回傳 null 表示可以發射，否則為停用理由。</summary>
        public string CannotFireReason(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || pawn.Downed) { return "DMSE.ManPortableSAM.NotReady".Translate(); }
            if (AmmoIn(pawn, Props.ammoDef) == null)
            {
                return "DMSE.ManPortableSAM.NoAmmo".Translate(Props.ammoDef?.label ?? "?");
            }
            if (OnCooldown)
            {
                return "DMSE.MissileLauncher.LaunchCooldown".Translate(
                    (cooldownUntil - Find.TickManager.TicksGame).ToStringTicksToPeriod());
            }
            return null;
        }

        public override IEnumerable<Gizmo> CompGetEquippedGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetEquippedGizmosExtra())
            {
                yield return g;
            }
            Pawn pawn = Wielder;
            if (pawn == null || !pawn.IsColonistPlayerControlled) { yield break; }

            Command_Action command = new Command_Action
            {
                defaultLabel = "DMSE.ManPortableSAM.Fire".Translate(),
                defaultDesc = "DMSE.ManPortableSAM.FireDesc".Translate(AmmoCount(pawn)),
                icon = Icon,
                action = () => OpenTargetMenu(pawn)
            };
            string reason = CannotFireReason(pawn);
            if (reason == null && !EngageableTargets(pawn).Any())
            {
                reason = "DMSE.ManPortableSAM.NoTargets".Translate();
            }
            if (reason != null) { command.Disable(reason); }
            yield return command;
        }

        private void OpenTargetMenu(Pawn pawn)
        {
            MapComponent_BVRCombat combat = pawn.Map?.GetComponent<MapComponent_BVRCombat>();
            int now = Find.TickManager.TicksGame;
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            foreach (BVRTarget target in EngageableTargets(pawn).ToList())
            {
                BVRWave wave = WaveOf(combat, target);
                if (wave == null) { continue; }
                int eta = wave.tickToImpact - now;
                string label = "DMSE.ManPortableSAM.TargetOption".Translate(
                    target.Payload?.LabelShortCap ?? "?",
                    eta.ToStringTicksToPeriod(),
                    HitChance(pawn, target, eta).ToStringPercent());
                int id = target.id;
                options.Add(new FloatMenuOption(label, () =>
                {
                    Job job = JobMaker.MakeJob(DMSE_ManPortableSAMDefOf.DMSE_FireManPortableSAM, pawn);
                    job.count = id;
                    pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                }));
            }
            if (options.Count > 0) { Find.WindowStack.Add(new FloatMenu(options)); }
        }

        /// <summary>
        /// 對指定目標發射：消耗一枚背包彈藥、生成攔截彈（命中與否在它離開地圖時擲骰）、起算冷卻。
        /// 失敗時回傳 false 與理由（目標已消失、已被其他攔截彈占用、彈藥用完…）。
        /// </summary>
        public bool TryFire(Pawn pawn, int targetId, out string failReason)
        {
            failReason = CannotFireReason(pawn);
            if (failReason != null) { return false; }
            MapComponent_BVRCombat combat = pawn.Map.GetComponent<MapComponent_BVRCombat>();
            BVRTarget target = combat?.GetTarget(targetId);
            BVRWave wave = WaveOf(combat, target);
            int now = Find.TickManager.TicksGame;
            if (target == null || wave == null || wave.tickToImpact <= now || target.midcourseEngagedUntil > now)
            {
                failReason = "DMSE.ManPortableSAM.TargetLost".Translate();
                return false;
            }
            if (Props.interceptorSkyfaller == null)
            {
                failReason = "DMSE.BVR.DeviceInactive".Translate();
                return false;
            }

            float chance = HitChance(pawn, target, wave.tickToImpact - now);
            Thing ammo = AmmoIn(pawn, Props.ammoDef);
            // 升空外觀與尾焰隨彈藥的彈體；沒有 launchProjectile 時退回 interceptorSkyfaller。
            ThingDef launchDef = MissileItemExtension.BodyOf(ammo)?.launchProjectile ?? Props.interceptorSkyfaller;
            MissileProjectileMaker.LaunchInterceptor(launchDef, pawn.Position, pawn.Map, pawn.Rotation, pawn, target.id, chance);
            target.midcourseEngagedUntil = now + Props.interceptorTravelTicks;
            target.lockUntil = -1;

            if (ammo.stackCount > 1)
            {
                ammo.SplitOff(1).Destroy(DestroyMode.Vanish);
            }
            else
            {
                pawn.inventory.innerContainer.Remove(ammo);
                ammo.Destroy(DestroyMode.Vanish);
            }
            cooldownUntil = now + Props.cooldownTicks;
            return true;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref cooldownUntil, "manPortableSamCooldownUntil", 0);
        }
    }

    /// <summary>原地瞄準 <see cref="CompProperties_ManPortableSAM.aimTicks"/> 後發射；目標 id 放在 job.count。</summary>
    public class JobDriver_FireManPortableSAM : JobDriver
    {
        private CompEquippable_ManPortableSAM Launcher => pawn.equipment?.Primary?.GetComp<CompEquippable_ManPortableSAM>();

        private BVRTarget Target => pawn.Map?.GetComponent<MapComponent_BVRCombat>()?.GetTarget(job.count);

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOn(() => Launcher == null || Target == null);

            Toil aim = Toils_General.Wait(Launcher?.Props.aimTicks ?? 90);
            aim.WithProgressBarToilDelay(TargetIndex.A);
            aim.handlingFacing = true;
            aim.tickAction = () =>
            {
                BVRTarget target = Target;
                if (target != null) { pawn.rotationTracker.FaceCell(target.position); }
            };
            yield return aim;

            Toil fire = ToilMaker.MakeToil("DMSE_FireManPortableSAM");
            fire.initAction = () =>
            {
                CompEquippable_ManPortableSAM launcher = Launcher;
                if (launcher == null) { return; }
                if (!launcher.TryFire(pawn, job.count, out string reason) && pawn.Faction == Faction.OfPlayer)
                {
                    Messages.Message(reason, pawn, MessageTypeDefOf.RejectInput, false);
                }
            };
            fire.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return fire;
        }
    }

    /// <summary>
    /// 單兵發射具的對地射擊：只有背包裡有彈藥（刺針）時可用，每發消耗一枚。
    /// 彈藥種類取自武器上的 <see cref="CompEquippable_ManPortableSAM"/>。
    /// </summary>
    public class Verb_ShootStinger : Verb_Shoot
    {
        private ThingDef AmmoDef => EquipmentSource?.GetComp<CompEquippable_ManPortableSAM>()?.Props.ammoDef;

        public override bool Available()
        {
            return base.Available() && CompEquippable_ManPortableSAM.AmmoIn(CasterPawn, AmmoDef) != null;
        }

        protected override bool TryCastShot()
        {
            Thing ammo = CompEquippable_ManPortableSAM.AmmoIn(CasterPawn, AmmoDef);
            if (ammo == null) { return false; }
            bool fired = base.TryCastShot();
            if (fired)
            {
                if (ammo.stackCount > 1)
                {
                    ammo.SplitOff(1).Destroy(DestroyMode.Vanish);
                }
                else
                {
                    CasterPawn.inventory.innerContainer.Remove(ammo);
                    ammo.Destroy(DestroyMode.Vanish);
                }
            }
            return fired;
        }
    }
}
