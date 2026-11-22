using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary><see cref="Building_LongRangeGun"/> 的砲身瞄準參數（掛在建築 def 的 modExtensions）。</summary>
    public class ModExtension_TurretTraverse : DefModExtension
    {
        /// <summary>砲身與目標方位的夾角在此度數內，原版預熱才開始倒數。</summary>
        public float aimToleranceDeg = 4f;

        /// <summary>一次預熱最多等砲身轉多久（ticks）；超過就放掉目標，原版下次索敵再挑。</summary>
        public int maxAimTicks = 450;
    }

    /// <summary>
    /// 超視距火砲（皇帝長程砲）：原版 <see cref="Building_TurretGun"/> 負責本地圖射擊（索敵、強制目標、停火、
    /// 射程環、AI 威脅判定），<see cref="CompArtilleryStrike"/> 負責世界砲擊，兩者共用同一根砲管：
    ///   - 砲身由 <see cref="CompTurretFacingRenderer"/> 畫（10x4 長方形；原版 TurretTop 只能畫正方形，這裡改畫透明），
    ///     依 rotationSpeedDegPerTick 慢慢轉；砲身轉到目標方位（<see cref="ModExtension_TurretTraverse.aimToleranceDeg"/>）之前，
    ///     原版的預熱計數不往下扣，所以砲彈一定沿砲口方向出膛。
    ///   - 原版只讓有人操作的砲塔指定目標；這門砲全自動，玩家的就能指定。
    ///   - 世界砲擊持續射擊期間不接戰本地圖目標、也不能指定目標（原版 Tick 會解除既有的強制目標），
    ///     砲身由 CompArtilleryStrike 指向世界方位。
    /// 沒有 CompTurretFacingRenderer 時退回原版砲塔頭與瞬間轉向。
    /// </summary>
    public class Building_LongRangeGun : Building_TurretGun
    {
        private static readonly ModExtension_TurretTraverse DefaultTraverse = new ModExtension_TurretTraverse();

        private CompArtilleryStrike artilleryInt;
        private CompTurretFacingRenderer facingInt;
        private ModExtension_TurretTraverse traverseInt;
        private bool compsCached;

        /// <summary>這次預熱已經等砲身轉了多久（ticks）。</summary>
        private int aimWaitTicks;

        private void CacheComps()
        {
            if (compsCached) { return; }
            artilleryInt = GetComp<CompArtilleryStrike>();
            facingInt = GetComp<CompTurretFacingRenderer>();
            traverseInt = def.GetModExtension<ModExtension_TurretTraverse>() ?? DefaultTraverse;
            compsCached = true;
        }

        private CompTurretFacingRenderer Facing
        {
            get { CacheComps(); return facingInt; }
        }

        private ModExtension_TurretTraverse Traverse
        {
            get { CacheComps(); return traverseInt; }
        }

        /// <summary>世界砲擊正在持續射擊：砲管被占用，本地圖武器暫停。</summary>
        public bool WorldFiring
        {
            get { CacheComps(); return artilleryInt != null && artilleryInt.IsSustainedFiring; }
        }

        /// <summary>砲身改由 CompTurretFacingRenderer 繪製，原版 TurretTop 畫透明材質。</summary>
        public override Material TurretTopMaterial => Facing != null ? BaseContent.ClearMat : base.TurretTopMaterial;

        protected override bool CanSetForcedTarget => Faction == Faction.OfPlayer && !WorldFiring;

        public override LocalTargetInfo TryFindNewTarget()
            => WorldFiring ? LocalTargetInfo.Invalid : base.TryFindNewTarget();

        protected override void Tick()
        {
            // 世界砲擊接手砲管：放掉本地目標（已在射擊的這一發不打斷）。
            if (WorldFiring && currentTargetInt.IsValid && AttackVerb.state != VerbState.Bursting)
            {
                currentTargetInt = LocalTargetInfo.Invalid;
                burstWarmupTicksLeft = 0;
            }
            HoldWarmupUntilAimed();

            base.Tick();

            CompTurretFacingRenderer facing = Facing;
            if (facing == null) { return; }
            if (!WorldFiring && currentTargetInt.IsValid)
            {
                facing.ForceFaceTarget(currentTargetInt.CenterVector3);
            }
            // 原版 TurretTop 不畫，但後坐力方向與其他讀 Top.CurRotation 的程式仍以它為準，同步成實際砲身方位。
            top.CurRotation = facing.CurRotation;
        }

        /// <summary>
        /// 原版每 tick 在 <see cref="Building_TurretGun.Tick"/> 扣一次預熱；砲身還沒轉到目標方位時先加回一次，
        /// 讓預熱從對準那一刻才開始算（同舊版 CompBuildingTurretGun）。
        /// </summary>
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
                // 目標移動太快、砲身追不上：放掉，原版下次索敵（每 15 tick）再挑；強制目標會重新接上。
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

            // 世界砲擊中原版不顯示「強制攻擊」（CanSetForcedTarget 為 false）：補一個停用的按鈕說明原因。
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
