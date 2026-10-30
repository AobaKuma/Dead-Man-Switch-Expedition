using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 建築用的本地圖砲塔武器：讓非 <see cref="Building_TurretGun"/> 的平台（例如以 <see cref="Building_MissileRack"/>
    /// 為本體的彈砲一體野戰防空砲）也能像原版砲塔一樣自動搜尋並射擊本地圖上的敵人。
    ///
    /// 以 vanilla <see cref="CompTurretGun"/>（機械族背負砲塔用）為基礎，補上建築需要的三件事：
    ///   1) 可運作判定沿用 <see cref="CompBVRDevice.IsOperational"/>（斷電／關閉／損壞／被擊暈時不開火）；
    ///   2) 砲塔頭朝向：鎖定目標後每 tick 對 <see cref="CompTurretFacingRenderer"/> 下達瞄準指令，
    ///      並且在砲塔頭轉到目標方位（<see cref="CompProperties_BuildingTurretGun.aimToleranceDeg"/> 內）
    ///      且穩定瞄準 warmupTime 之後才開火，確保射擊方向與畫面朝向一致；
    ///   3) 彈藥：由武器 verb 的 consumeFuelPerShot 直接扣建築的 <see cref="CompRefuelable"/>（vanilla Verb 內建行為）。
    /// </summary>
    public class CompProperties_BuildingTurretGun : CompProperties_TurretGun
    {
        /// <summary>砲塔頭與目標方位的夾角在此度數內才視為已對準、可以開火。</summary>
        public float aimToleranceDeg = 8f;

        /// <summary>鎖定目標後最多等待轉向的 ticks；逾時放棄目標重新搜尋（避免卡在轉不到的目標上）。</summary>
        public int maxAimTicks = 300;

        public CompProperties_BuildingTurretGun()
        {
            compClass = typeof(CompBuildingTurretGun);
        }
    }

    public class CompBuildingTurretGun : CompTurretGun
    {
        public new CompProperties_BuildingTurretGun Props => (CompProperties_BuildingTurretGun)props;

        // 進入預熱後仍需穩定瞄準的 ticks（-1 = 尚未開始本次預熱）；以及本次預熱已等待的 ticks。
        private int aimSettleTicksLeft = -1;
        private int aimWaitTicks;

        private CompTurretFacingRenderer facingInt;
        private CompTurretFacingRenderer Facing => facingInt ?? (facingInt = parent.GetComp<CompTurretFacingRenderer>());

        public LocalTargetInfo CurrentTarget => currentTarget;

        public bool Active => CompBVRDevice.IsOperational(parent);

        /// <summary>砲塔頭是否已對準目前目標（沒有砲塔頭渲染 Comp 時一律視為已對準）。</summary>
        public bool AimedAtTarget
        {
            get
            {
                if (!currentTarget.IsValid) { return false; }
                CompTurretFacingRenderer facing = Facing;
                return facing == null || facing.IsFacing(currentTarget.CenterVector3, Props.aimToleranceDeg);
            }
        }

        private CompArtilleryStrike artilleryInt;
        private bool artilleryLooked;

        /// <summary>同一根砲管的超視距火砲（<see cref="CompArtilleryStrike"/>）正在持續射擊時，本地圖武器暫停。</summary>
        private bool BarrelBusy
        {
            get
            {
                if (!artilleryLooked) { artilleryInt = parent.GetComp<CompArtilleryStrike>(); artilleryLooked = true; }
                return artilleryInt != null && artilleryInt.IsSustainedFiring;
            }
        }

        public override void CompTick()
        {
            if (!Active || BarrelBusy)
            {
                ClearTarget();
                return;
            }

            if (currentTarget.IsValid && AttackVerb.state != VerbState.Bursting)
            {
                if (currentTarget.HasThing && (currentTarget.ThingDestroyed || !currentTarget.Thing.Spawned || currentTarget.Thing.MapHeld != parent.Map))
                {
                    ClearTarget();
                }
            }

            if (currentTarget.IsValid)
            {
                Facing?.ForceFaceTarget(currentTarget.CenterVector3);
            }

            // 預熱期間：砲塔頭未對準前、或對準後的穩定瞄準時間未滿前，把 vanilla 的預熱計數撐住不歸零。
            if (burstWarmupTicksLeft > 0 && currentTarget.IsValid)
            {
                if (aimSettleTicksLeft < 0)
                {
                    aimSettleTicksLeft = AttackVerb.verbProps.warmupTime.SecondsToTicks();
                    aimWaitTicks = 0;
                }
                if (!AimedAtTarget)
                {
                    if (++aimWaitTicks > Props.maxAimTicks)
                    {
                        ClearTarget();
                    }
                    else
                    {
                        burstWarmupTicksLeft++;
                    }
                }
                else if (aimSettleTicksLeft > 0)
                {
                    aimSettleTicksLeft--;
                    burstWarmupTicksLeft++;
                }
            }
            else
            {
                aimSettleTicksLeft = -1;
            }

            base.CompTick();
        }

        private void ClearTarget()
        {
            currentTarget = LocalTargetInfo.Invalid;
            burstWarmupTicksLeft = 0;
            aimSettleTicksLeft = -1;
        }

        public override string CompInspectStringExtra()
        {
            if (!currentTarget.IsValid || !parent.Spawned) { return null; }
            return "DMSE.TurretGun.Engaging".Translate(currentTarget.Thing?.LabelShort ?? currentTarget.Cell.ToString());
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref aimSettleTicksLeft, "aimSettleTicksLeft", -1);
            Scribe_Values.Look(ref aimWaitTicks, "aimWaitTicks", 0);
        }
    }
}
