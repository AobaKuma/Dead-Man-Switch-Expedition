using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
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

    /// <summary>
    /// 單兵防空導彈發射具（docs/三代裝備體系_實作計畫.md §10 C10）。
    /// 與 DMS 拋棄式發射器同為腰帶層 Apparel；彈藥用原版 <see cref="CompApparelReloadable"/> 裝填（ammoDef = 輕型防空導彈，一發一枚）。
    /// </summary>
    public class CompProperties_ManPortableSAM : CompProperties_ApparelReloadable
    {
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

        /// <summary>
        /// 除 <see cref="CompProperties_ApparelReloadable.ammoDef"/> 外也能裝填的彈藥（如軍刀巡飛彈）。
        /// 原版自動裝填只找 ammoDef；這些彈藥經 <see cref="FloatMenuOptionProvider_ManPortableSAMReload"/> 手動裝入。
        /// </summary>
        public List<ThingDef> extraAmmoDefs = new List<ThingDef>();

        public CompProperties_ManPortableSAM()
        {
            compClass = typeof(CompManPortableSAM);
        }
    }

    /// <summary>
    /// 可對 BVR 目標發射攔截彈的單兵發射具。繼承原版 <see cref="CompApparelReloadable"/>：
    /// 對地射擊（原版 Verb_LaunchProjectile）與防空 Gizmo 共用同一組裝填彈數，裝填走原版「裝填」工作。
    ///
    /// 裝填時原版會銷毀彈藥物品，所以由 <see cref="Patch_ManPortableSAMReload"/> 先記下該枚導彈的裝配設定（彈體、戰鬥部），
    /// 發射時取最後裝入的一枚；沒有紀錄的彈數（出廠滿彈、開發者裝填）用彈藥的預設構型。
    ///
    /// 只接戰本地圖 <see cref="MapComponent_BVRCombat"/> 的波次（地圖內空投與已進入末端的世界導彈），
    /// 防禦方須是裝備者的陣營；不占火控通道，但需要雷達網提供的航跡。
    /// </summary>
    public class CompManPortableSAM : CompApparelReloadable
    {
        private int cooldownUntil;

        /// <summary>已裝入導彈的裝配設定，後進先出；數量不超過剩餘彈數。</summary>
        private List<MissileConfig> loadedConfigs = new List<MissileConfig>();

        private MissileConfig reloadingConfig;
        private int chargesBeforeReload;

        public new CompProperties_ManPortableSAM Props => (CompProperties_ManPortableSAM)props;

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

        /// <summary>裝備者身上的單兵發射具；沒有為 null。</summary>
        public static CompManPortableSAM WornBy(Pawn pawn)
        {
            List<Apparel> worn = pawn?.apparel?.WornApparel;
            if (worn == null) { return null; }
            for (int i = 0; i < worn.Count; i++)
            {
                CompManPortableSAM comp = worn[i].GetComp<CompManPortableSAM>();
                if (comp != null) { return comp; }
            }
            return null;
        }

        /// <summary>下一發的裝配設定（副本）：最後裝入的一枚，否則彈藥的預設構型。</summary>
        public MissileConfig NextConfig
        {
            get
            {
                if (loadedConfigs.Count > 0) { return loadedConfigs[loadedConfigs.Count - 1].Clone(); }
                MissileBodyDef body = BodyOfDef(Props.ammoDef);
                return body != null ? MissileConfig.NewWithDefaults(body) : null;
            }
        }

        /// <summary>下一發的彈藥物品：最後裝入那枚的彈體屬於哪個 <see cref="CompProperties_ManPortableSAM.extraAmmoDefs"/>，否則 ammoDef。</summary>
        public ThingDef NextAmmoDef
        {
            get
            {
                if (loadedConfigs.Count > 0 && !Props.extraAmmoDefs.NullOrEmpty())
                {
                    MissileBodyDef body = loadedConfigs[loadedConfigs.Count - 1].body;
                    for (int i = 0; i < Props.extraAmmoDefs.Count; i++)
                    {
                        if (BodyOfDef(Props.extraAmmoDefs[i]) == body) { return Props.extraAmmoDefs[i]; }
                    }
                }
                return Props.ammoDef;
            }
        }

        public bool CanLoad(ThingDef def) => def != null && (def == Props.ammoDef || Props.extraAmmoDefs.Contains(def));

        private static MissileBodyDef BodyOfDef(ThingDef def)
            => def?.GetCompProperties<CompProperties_MissileConfig>()?.defaultBody
               ?? def?.GetModExtension<MissileItemExtension>()?.body;

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
            MissileConfig cfg = NextConfig;
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
            if (pawn == null || !pawn.Spawned || pawn.Downed || pawn != Wearer) { return "DMSE.ManPortableSAM.NotReady".Translate(); }
            if (!CanBeUsed(out string reason)) { return reason.NullOrEmpty() ? "DMSE.ManPortableSAM.NotReady".Translate().ToString() : reason; }
            ThingDef next = NextAmmoDef;
            if (MissileRoleExtension.RoleOf(next) == MissileRole.Strike)
            {
                return "DMSE.ManPortableSAM.CannotIntercept".Translate(next.label);
            }
            if (OnCooldown)
            {
                return "DMSE.MissileLauncher.LaunchCooldown".Translate(
                    (cooldownUntil - Find.TickManager.TicksGame).ToStringTicksToPeriod());
            }
            return null;
        }

        public override IEnumerable<Gizmo> CompGetWornGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetWornGizmosExtra())
            {
                // 每種彈藥各有一個對地射擊動詞，只顯示下一發彈藥的那個。
                if (g is Command_VerbTarget cmd && cmd.verb is Verb_LaunchManPortableMissile v && !v.MatchesLoadedAmmo) { continue; }
                yield return g;
            }
            Pawn pawn = Wearer;
            if (pawn == null || !pawn.IsColonistPlayerControlled) { yield break; }

            Command_Action command = new Command_Action
            {
                defaultLabel = "DMSE.ManPortableSAM.Fire".Translate(),
                defaultDesc = "DMSE.ManPortableSAM.FireDesc".Translate(LabelRemaining),
                icon = Icon,
                action = () => OpenTargetMenu(pawn)
            };
            string reason = CannotFireReason(pawn);
            if (reason == null && pawn.WorkTagIsDisabled(WorkTags.Violent))
            {
                reason = "IsIncapableOfViolenceLower".Translate(pawn.LabelShort, pawn).CapitalizeFirst() + ".";
            }
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
        /// 對指定目標發射：消耗一發裝填彈、生成攔截彈（命中與否在它離開地圖時擲骰）、起算冷卻。
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
            // 升空外觀與尾焰隨裝填導彈的彈體；沒有 launchProjectile 時退回 interceptorSkyfaller。
            ThingDef launchDef = NextConfig?.body?.launchProjectile ?? Props.interceptorSkyfaller;
            // 升空後轉向來襲方位；經發射排程錯開生成。
            float? heading = MissileProjectileMaker.IncomingBearing(target, pawn.Position);
            MapComponent_MissileLaunchQueue.Launch(pawn.Map, new PendingMissileLaunch
            {
                def = launchDef,
                cell = pawn.Position,
                rotation = pawn.Rotation,
                launcher = pawn,
                interceptor = true,
                targetId = target.id,
                hitChance = chance,
                hasHeading = heading != null,
                heading = heading ?? 0f,
            });
            target.midcourseEngagedUntil = now + Props.interceptorTravelTicks;
            target.lockUntil = -1;

            UsedOnce();
            cooldownUntil = now + Props.cooldownTicks;
            return true;
        }

        /// <summary>每發（對地射擊或防空）消耗一發：彈出最後裝入的設定。</summary>
        public override void UsedOnce()
        {
            base.UsedOnce();
            if (loadedConfigs.Count > 0) { loadedConfigs.RemoveAt(loadedConfigs.Count - 1); }
            while (loadedConfigs.Count > RemainingCharges) { loadedConfigs.RemoveAt(0); }
        }

        internal void BeginReload(Thing ammo)
        {
            reloadingConfig = MissileItemExtension.ConfigFor(ammo);
            chargesBeforeReload = RemainingCharges;
        }

        internal void EndReload()
        {
            if (reloadingConfig != null)
            {
                for (int i = chargesBeforeReload; i < RemainingCharges; i++)
                {
                    loadedConfigs.Add(reloadingConfig.Clone());
                }
            }
            while (loadedConfigs.Count > RemainingCharges) { loadedConfigs.RemoveAt(0); }
            reloadingConfig = null;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref cooldownUntil, "manPortableSamCooldownUntil", 0);
            Scribe_Collections.Look(ref loadedConfigs, "manPortableSamLoadedConfigs", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && loadedConfigs == null)
            {
                loadedConfigs = new List<MissileConfig>();
            }
        }
    }

    /// <summary>原版裝填會銷毀彈藥物品；先記下它的裝配設定，裝填完成後按實際增加的彈數存入發射具。</summary>
    [HarmonyPatch(typeof(CompApparelReloadable), nameof(CompApparelReloadable.ReloadFrom))]
    public static class Patch_ManPortableSAMReload
    {
        public static void Prefix(CompApparelReloadable __instance, Thing ammo)
        {
            (__instance as CompManPortableSAM)?.BeginReload(ammo);
        }

        public static void Postfix(CompApparelReloadable __instance)
        {
            (__instance as CompManPortableSAM)?.EndReload();
        }
    }

    /// <summary>原地瞄準 <see cref="CompProperties_ManPortableSAM.aimTicks"/> 後發射；目標 id 放在 job.count。</summary>
    public class JobDriver_FireManPortableSAM : JobDriver
    {
        private CompManPortableSAM Launcher => CompManPortableSAM.WornBy(pawn);

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
                CompManPortableSAM launcher = Launcher;
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

    /// <summary>單兵發射具的對地射擊動詞屬性：<see cref="ammoDef"/> 為此動詞發射的彈藥；空 = 任何彈藥都可用。</summary>
    public class VerbProperties_ManPortableMissile : VerbProperties
    {
        public ThingDef ammoDef;
    }

    /// <summary>
    /// 單兵發射具的對地射擊：只在下一發彈藥是 <see cref="VerbProperties_ManPortableMissile.ammoDef"/> 時可用，
    /// 讓防空導彈與巡飛彈各用自己的投射物與射程；不符的動詞 Gizmo 由 <see cref="CompManPortableSAM"/> 隱藏。
    /// </summary>
    public class Verb_LaunchManPortableMissile : Verb_LaunchProjectile
    {
        public bool MatchesLoadedAmmo
        {
            get
            {
                ThingDef ammo = (verbProps as VerbProperties_ManPortableMissile)?.ammoDef;
                return ammo == null || !(DirectOwner is CompManPortableSAM launcher) || launcher.NextAmmoDef == ammo;
            }
        }

        public override bool Available() => base.Available() && MatchesLoadedAmmo;
    }

    /// <summary>原版「裝填」選單只認 ammoDef；為 <see cref="CompProperties_ManPortableSAM.extraAmmoDefs"/> 補上右鍵裝填選項。</summary>
    public class FloatMenuOptionProvider_ManPortableSAMReload : FloatMenuOptionProvider
    {
        protected override bool Drafted => true;

        protected override bool Undrafted => true;

        protected override bool Multiselect => false;

        public override IEnumerable<FloatMenuOption> GetOptionsFor(Thing clickedThing, FloatMenuContext context)
        {
            Pawn pawn = context.FirstSelectedPawn;
            CompManPortableSAM launcher = CompManPortableSAM.WornBy(pawn);
            if (launcher == null || clickedThing.def == launcher.Props.ammoDef || !launcher.CanLoad(clickedThing.def)) { yield break; }

            string text = "Reload".Translate(launcher.parent.Named("GEAR"), clickedThing.def.Named("AMMO")) + " (" + launcher.LabelRemaining + ")";
            if (!pawn.CanReach(clickedThing, PathEndMode.ClosestTouch, Danger.Deadly))
            {
                yield return new FloatMenuOption(text + ": " + "NoPath".Translate().CapitalizeFirst(), null);
                yield break;
            }
            if (!launcher.NeedsReload(allowForcedReload: true))
            {
                yield return new FloatMenuOption(text + ": " + "ReloadFull".Translate(), null);
                yield break;
            }
            if (clickedThing.stackCount < launcher.MinAmmoNeeded(allowForcedReload: true) || !pawn.CanReserve(clickedThing))
            {
                yield return new FloatMenuOption(text + ": " + "ReloadNotEnough".Translate(), null);
                yield break;
            }
            List<Thing> chosenAmmo = new List<Thing> { clickedThing };
            yield return FloatMenuUtility.DecoratePrioritizedTask(new FloatMenuOption(text, () =>
            {
                pawn.jobs.TryTakeOrderedJob(JobGiver_Reload.MakeReloadJob(launcher, chosenAmmo), JobTag.Misc);
            }), pawn, clickedThing);
        }
    }
}
