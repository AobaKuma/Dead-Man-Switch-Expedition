using System.Collections.Generic;
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
    ///   3) 彈藥：由武器 verb 的 consumeFuelPerShot 直接扣建築的 <see cref="CompRefuelable"/>（vanilla Verb 內建行為）；
    ///   4) 玩家指令（同原版砲塔）：停火、強制目標／停止強制攻擊。強制目標與自動索敵都受 verb 的
    ///      minRange（近距離射程限制）與 range 限制，選取時畫出最小（紅）／最大射程圈。
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

        // ---- 玩家指令 ----
        private bool holdFire;
        private LocalTargetInfo forcedTarget = LocalTargetInfo.Invalid;

        public bool HoldFire => holdFire;
        public LocalTargetInfo ForcedTarget => forcedTarget;

        private float MinRange => AttackVerb.verbProps.minRange;
        private float MaxRange => AttackVerb.verbProps.range;

        /// <summary>目標是否在射程帶內（不小於 minRange、不大於 range）；不在時給出原因。</summary>
        public bool InRange(LocalTargetInfo target, out string reason)
        {
            reason = null;
            if (!target.IsValid) { return false; }
            float dist = target.Cell.DistanceTo(parent.Position);
            if (dist < MinRange)
            {
                reason = "DMSE.TurretGun.TooClose".Translate(MinRange.ToString("0.#"));
                return false;
            }
            if (dist > MaxRange)
            {
                reason = "DMSE.TurretGun.OutOfRange".Translate(MaxRange.ToString("0.#"));
                return false;
            }
            return true;
        }

        /// <summary>設定強制目標；不在射程帶內則拒絕並回傳 false。Gizmo 與測試共用。</summary>
        public bool TrySetForcedTarget(LocalTargetInfo target, out string reason)
        {
            if (target.HasThing && target.Thing == parent) { reason = null; return false; }
            if (!InRange(target, out reason)) { return false; }
            forcedTarget = target;
            holdFire = false;
            // 正在預熱別的目標（尚未射擊）就立刻改打強制目標
            if (AttackVerb.state != VerbState.Bursting) { ClearTarget(); }
            return true;
        }

        public void ResetForcedTarget()
        {
            forcedTarget = LocalTargetInfo.Invalid;
            if (AttackVerb.state != VerbState.Bursting) { ClearTarget(); }
        }

        /// <summary>停火：同原版砲塔，同時取消強制目標。</summary>
        public void SetHoldFire(bool value)
        {
            holdFire = value;
            if (value)
            {
                forcedTarget = LocalTargetInfo.Invalid;
                ClearTarget();
            }
        }

        private bool ForcedTargetStillValid
        {
            get
            {
                if (!forcedTarget.IsValid) { return false; }
                if (forcedTarget.HasThing)
                {
                    Thing t = forcedTarget.Thing;
                    if (t.Destroyed || !t.Spawned || t.MapHeld != parent.Map) { return false; }
                    if (t is Pawn p && (p.Dead || p.Downed)) { return false; }
                }
                return InRange(forcedTarget, out _);
            }
        }

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
            if (!Active || BarrelBusy || holdFire)
            {
                ClearTarget();
                return;
            }

            // 強制目標：失效（摧毀／倒地／離開地圖／移出射程帶）就解除；有效時冷卻一結束就搶在 vanilla 索敵前接手
            if (forcedTarget.IsValid && !ForcedTargetStillValid)
            {
                forcedTarget = LocalTargetInfo.Invalid;
            }
            if (forcedTarget.IsValid)
            {
                if (AttackVerb.state != VerbState.Bursting && burstWarmupTicksLeft <= 0 && burstCooldownTicksLeft <= 0)
                {
                    currentTarget = forcedTarget;
                    burstWarmupTicksLeft = 1;
                }
            }
            // 自動索敵（vanilla 只看 verb 射程）挑到近距離射程限制內的目標就放掉
            else if (currentTarget.IsValid && AttackVerb.state != VerbState.Bursting && !InRange(currentTarget, out _))
            {
                ClearTarget();
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

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra())
            {
                yield return g;
            }
            if (parent.Faction != Faction.OfPlayer) { yield break; }

            Command_Action force = new Command_Action
            {
                defaultLabel = "CommandSetForceAttackTarget".Translate(),
                defaultDesc  = "CommandSetForceAttackTargetDesc".Translate() + "\n\n"
                    + "DMSE.TurretGun.RangeBand".Translate(MinRange.ToString("0.#"), MaxRange.ToString("0.#")),
                icon         = ContentFinder<Texture2D>.Get("UI/Commands/Attack"),
                hotKey       = KeyBindingDefOf.Misc4,
                action       = BeginForceTargeting,
            };
            if (!Active) { force.Disable("DMSE.BVR.DeviceInactive".Translate()); }
            else if (BarrelBusy) { force.Disable("DMSE.TurretGun.BarrelBusy".Translate()); }
            yield return force;

            if (forcedTarget.IsValid)
            {
                yield return new Command_Action
                {
                    defaultLabel = "CommandStopForceAttack".Translate(),
                    defaultDesc  = "CommandStopForceAttackDesc".Translate(),
                    icon         = ContentFinder<Texture2D>.Get("UI/Commands/Halt"),
                    hotKey       = KeyBindingDefOf.Misc5,
                    action       = ResetForcedTarget,
                };
            }

            yield return new Command_Toggle
            {
                defaultLabel = "CommandHoldFire".Translate(),
                defaultDesc  = "CommandHoldFireDesc".Translate(),
                icon         = ContentFinder<Texture2D>.Get("UI/Commands/HoldFire"),
                hotKey       = KeyBindingDefOf.Misc6,
                isActive     = () => holdFire,
                toggleAction = () => SetHoldFire(!holdFire),
            };
        }

        private void BeginForceTargeting()
        {
            TargetingParameters parms = new TargetingParameters
            {
                canTargetLocations = true,
                canTargetPawns     = true,
                canTargetBuildings = true,
                canTargetItems     = true,
            };
            Find.Targeter.BeginTargeting(parms, target =>
            {
                if (!TrySetForcedTarget(target, out string reason) && !reason.NullOrEmpty())
                {
                    Messages.Message(reason, MessageTypeDefOf.RejectInput, historical: false);
                }
            },
            highlightAction: null,
            targetValidator: t => InRange(t, out _),
            caster: null,
            actionWhenFinished: null,
            mouseAttachment: ContentFinder<Texture2D>.Get("UI/Commands/Attack"),
            playSoundOnAction: true,
            onGuiAction: t =>
            {
                if (t.IsValid && !InRange(t, out string why)) { Widgets.MouseAttachedLabel(why); }
            });
        }

        public override void PostDrawExtraSelectionOverlays()
        {
            base.PostDrawExtraSelectionOverlays();
            if (MinRange > 0f) { GenDraw.DrawRadiusRing(parent.Position, MinRange, Color.red); }
            if (MaxRange < GenRadial.MaxRadialPatternRadius) { GenDraw.DrawRadiusRing(parent.Position, MaxRange); }
            if (forcedTarget.IsValid)
            {
                GenDraw.DrawLineBetween(parent.TrueCenter(), forcedTarget.CenterVector3, SimpleColor.Red);
                GenDraw.DrawTargetHighlight(forcedTarget);
            }
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
            Scribe_Values.Look(ref holdFire, "holdFire", false);
            Scribe_TargetInfo.Look(ref forcedTarget, "forcedTarget");
        }
    }
}
