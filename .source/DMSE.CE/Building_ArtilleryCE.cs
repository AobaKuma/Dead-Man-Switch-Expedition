using System.Collections.Generic;
using CombatExtended;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE.CE
{
    /// <summary>
    /// CE 版 DMSE 世界砲擊火砲的共同基底：CE 砲塔（<see cref="Building_TurretGunCE"/>）＋DMSE 的世界砲擊
    /// （<see cref="CompArtilleryStrike"/>，砲彈取自 CE 彈匣，見 <see cref="CECombatCompat"/>）。
    ///   - 世界砲擊持續射擊中不接戰本地目標、也不能指定本地目標（砲管只有一根，同原版的 DMSE 類別）。
    ///   - CE 自己的世界砲擊按鈕（<see cref="Command_ArtilleryTarget"/>）拿掉：世界射擊只走 DMSE 那一套
    ///     （雷達導引、校射／效力射、世界航跡），兩套同時存在會各自扣彈、各自轉砲管。
    /// </summary>
    public abstract class Building_WorldArtilleryCE : Building_TurretGunCE
    {
        private CompArtilleryStrike artilleryInt;
        private bool artilleryCached;

        protected CompArtilleryStrike Artillery
        {
            get
            {
                if (!artilleryCached) { artilleryInt = GetComp<CompArtilleryStrike>(); artilleryCached = true; }
                return artilleryInt;
            }
        }

        /// <summary>世界砲擊正在持續射擊：砲管被占用，本地圖武器暫停。</summary>
        public bool WorldFiring => Artillery != null && Artillery.IsSustainedFiring;

        public override LocalTargetInfo TryFindNewTarget()
            => WorldFiring ? LocalTargetInfo.Invalid : base.TryFindNewTarget();

        public override void Tick()
        {
            // 世界砲擊接手砲管：放掉本地目標（已在射擊的這一發不打斷）。
            if (WorldFiring && currentTargetInt.IsValid && AttackVerb?.state != VerbState.Bursting)
            {
                currentTargetInt = LocalTargetInfo.Invalid;
                burstWarmupTicksLeft = 0;
            }
            base.Tick();
            if (WorldFiring && top != null)
            {
                // CE 的 TurretTopTick 沒目標時會閒置擺動：拉回世界瞄準方位
                float aim = Artillery.AimAngle;
                if (!float.IsNaN(aim)) { top.CurRotation = aim; }
            }
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos())
            {
                if (gizmo is Command_ArtilleryTarget) { continue; }
                yield return gizmo;
            }
        }
    }

    /// <summary>CE 版重型迫擊砲（取代 <see cref="Building_HeavyMortar"/>）：砲手操作、120 mm 迫擊砲彈匣。</summary>
    public class Building_HeavyMortarCE : Building_WorldArtilleryCE
    {
    }

    /// <summary>
    /// CE 版皇帝長程砲（取代 <see cref="Building_LongRangeGun"/>）：全自動、155 mm 彈匣（DMSE 自動裝填架或 CE 自動裝彈機補彈）。
    /// 砲身同原版的 DMSE 類別由 <see cref="CompTurretFacingRenderer"/> 畫 10x4 長方形；CE 的 TurretTop 改畫透明，
    /// 砲身轉到目標方位（<see cref="ModExtension_TurretTraverse"/>）之前撐住 CE 的預熱，砲彈才會沿砲口方向出膛。
    /// 原版只讓有人操作的砲塔指定目標；這門砲全自動，玩家的就能指定。
    /// </summary>
    public class Building_LongRangeGunCE : Building_WorldArtilleryCE
    {
        private static readonly ModExtension_TurretTraverse DefaultTraverse = new ModExtension_TurretTraverse();

        private CompTurretFacingRenderer facingInt;
        private ModExtension_TurretTraverse traverseInt;
        private bool compsCached;
        private int aimWaitTicks;

        private void CacheComps()
        {
            if (compsCached) { return; }
            facingInt = GetComp<CompTurretFacingRenderer>();
            traverseInt = def.GetModExtension<ModExtension_TurretTraverse>() ?? DefaultTraverse;
            compsCached = true;
        }

        private CompTurretFacingRenderer Facing { get { CacheComps(); return facingInt; } }

        private ModExtension_TurretTraverse Traverse { get { CacheComps(); return traverseInt; } }

        public override Material TurretTopMaterial => Facing != null ? BaseContent.ClearMat : base.TurretTopMaterial;

        protected override bool CanSetForcedTarget => Faction == Faction.OfPlayer && !WorldFiring;

        public override void Tick()
        {
            HoldWarmupUntilAimed();
            base.Tick();

            CompTurretFacingRenderer facing = Facing;
            if (facing == null || top == null) { return; }
            if (!WorldFiring && currentTargetInt.IsValid)
            {
                facing.ForceFaceTarget(currentTargetInt.CenterVector3);
            }
            // CE 的 TurretTop 不畫，但後坐力方向等仍讀 top.CurRotation：同步成實際砲身方位。
            top.CurRotation = facing.CurRotation;
        }

        /// <summary>CE 每 tick 扣一次預熱；砲身還沒轉到目標方位時先加回一次，讓預熱從對準那一刻才開始算。</summary>
        private void HoldWarmupUntilAimed()
        {
            CompTurretFacingRenderer facing = Facing;
            if (facing == null || burstWarmupTicksLeft <= 0 || !currentTargetInt.IsValid)
            {
                aimWaitTicks = 0;
                return;
            }
            if (facing.IsFacing(currentTargetInt.CenterVector3, Traverse.aimToleranceDeg)) { return; }

            if (++aimWaitTicks > Traverse.maxAimTicks)
            {
                // 目標移動太快、砲身追不上：放掉，下次索敵再挑；強制目標會重新接上。
                currentTargetInt = LocalTargetInfo.Invalid;
                burstWarmupTicksLeft = 0;
                aimWaitTicks = 0;
                return;
            }
            burstWarmupTicksLeft++;
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos())
            {
                yield return gizmo;
            }
            // 世界砲擊中不顯示「強制攻擊」（CanSetForcedTarget 為 false）：補一個停用的按鈕說明原因。
            if (Faction == Faction.OfPlayer && WorldFiring)
            {
                Command_Action force = new Command_Action
                {
                    defaultLabel = "CommandSetForceAttackTarget".Translate(),
                    defaultDesc = "CommandSetForceAttackTargetDesc".Translate(),
                    icon = ContentFinder<Texture2D>.Get("UI/Commands/Attack"),
                    hotKey = KeyBindingDefOf.Misc4,
                    action = delegate { },
                };
                force.Disable("DMSE.TurretGun.BarrelBusy".Translate());
                yield return force;
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref aimWaitTicks, "aimWaitTicks", 0);
        }
    }
}
