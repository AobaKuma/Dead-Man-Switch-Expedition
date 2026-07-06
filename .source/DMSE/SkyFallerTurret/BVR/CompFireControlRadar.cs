using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>階段二：火控雷達。提供火力通道（最大目標數）並決定中過程攔截命中率。</summary>
    public class CompProperties_FireControlRadar : CompProperties
    {
        /// <summary>最大目標數 = 同時可導引的火力通道數。</summary>
        public int maxTargets = 1;

        /// <summary>功率等級：與目標隱身值對衝；每超出 1 級隱身（stealthLevel - powerLevel）以 stealthMissFactor 遞減命中率，並延長鎖定時間。</summary>
        public int powerLevel = 1;

        /// <summary>導引精準時間（ticks）：可用導引時間達到此值時接近最大命中率。</summary>
        public float guidanceAccuracyTime = 600f;

        /// <summary>最大有效距離（以剩餘時間 ticks 近似）：剩餘時間大於此值時無法導引。</summary>
        public int maxRangeTicks = 6000;

        /// <summary>導引充分時的最大命中率。</summary>
        public float maxHitChance = 0.9f;

        /// <summary>每級隱身勝出（對衝後）對命中率的折扣係數。</summary>
        public float stealthMissFactor = 0.5f;

        /// <summary>基礎鎖定時間（ticks）：新目標計入火力通道後需鎖定此時間才發射；受隱身對衝與速度延長。</summary>
        public int lockOnTicks = 120;

        /// <summary>
        /// 是否不受反輻射導引頭（<see cref="GuidanceType_AntiRadiation"/>）的目標選擇影響。
        /// 與 <see cref="CompProperties_SearchRadar.immuneToAntiRadiationSeeker"/> 語意相同。
        /// </summary>
        public bool immuneToAntiRadiationSeeker = false;

        /// <summary>
        /// 是否支援為超視距（BVR）砲擊提供引導：<see cref="CompArtilleryStrike"/> 開火前
        /// 須先向同陣營、具備此旗標且尚有空閒通道的火控雷達佔用一個火力通道
        /// （與 <see cref="maxTargets"/> 共用同一份額度），開火後持續佔用直到引導結束才釋放。
        /// </summary>
        public bool supportsArtilleryGuidance = false;

        public CompProperties_FireControlRadar()
        {
            compClass = typeof(CompFireControlRadar);
        }
    }

    public class CompFireControlRadar : CompBVRDevice
    {
        public CompProperties_FireControlRadar Props => (CompProperties_FireControlRadar)props;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            MapComponent_BVRCombat m = Manager;
            if (m != null) { m.fireControlRadars.Add(this); }
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            MapComponent_BVRCombat m = map != null ? map.GetComponent<MapComponent_BVRCombat>() : null;
            if (m != null) { m.fireControlRadars.Remove(this); }
        }

        // 速度對命中率的影響參數。
        private const float SpeedRef = 15f;            // 此速度（含以下）無懲罰。
        private const float SpeedPenaltyPerUnit = 0.012f;
        private const float MinSpeedFactor = 0.2f;

        /// <summary>依導引時間、目標速度與隱身對衝計算單發攔截彈的命中率。</summary>
        public float ComputeHitChance(BVRTarget target, int guideTimeLeft)
        {
            float guide = Mathf.Clamp01(guideTimeLeft / Mathf.Max(1f, Props.guidanceAccuracyTime));
            float chance = Props.maxHitChance * guide;

            // 速度：越快越難命中。
            float speedFactor = Mathf.Clamp(1f - Mathf.Max(0f, target.speed - SpeedRef) * SpeedPenaltyPerUnit, MinSpeedFactor, 1f);
            chance *= speedFactor;

            // 隱身對衝：powerLevel 抵銷 stealthLevel，每超出 1 級以 stealthMissFactor 遞減。
            int stealthGap = Mathf.Max(0, target.stealthLevel - Props.powerLevel);
            if (stealthGap > 0)
            {
                chance *= Mathf.Pow(Props.stealthMissFactor, stealthGap);
            }

            return Mathf.Clamp01(chance);
        }

        /// <summary>新目標計入火力通道後所需的鎖定時間（ticks）：基礎時間受隱身對衝與速度延長。</summary>
        public int LockOnTicksFor(BVRTarget target)
        {
            int stealthGap = Mathf.Max(0, target.stealthLevel - Props.powerLevel);
            float mult = 1f + 0.5f * stealthGap + Mathf.Max(0f, target.speed - SpeedRef) / 60f;
            return Mathf.Max(1, Mathf.RoundToInt(Props.lockOnTicks * mult));
        }

        public override string CompInspectStringExtra()
        {
            string baseStr = "DMSE.BVR.FireControl".Translate(Props.maxTargets,
                Active ? "DMSE.BVR.Online".Translate() : "DMSE.BVR.Offline".Translate());

            if (Props.supportsArtilleryGuidance)
            {
                baseStr += "\n" + "DMSE.BVR.ArtilleryChannels".Translate(FreeArtilleryChannels, Props.maxTargets);
            }

            return baseStr;
        }

        // ====================================================================
        //  超視距砲擊引導：火力通道佔用（供 CompArtilleryStrike 使用）
        //
        //  通道額度 = Props.maxTargets（與 BVR 飛彈中過程攔截共用同一份）。
        //  BVR 系統在 MapComponent_BVRCombat.MidcourseDefense() 計算全域容量時
        //  會透過 OccupiedArtilleryChannels 扣除已預留給砲擊引導的通道，
        //  確保兩者不會超額使用同一份額度。
        // ====================================================================

        private int reservedArtilleryChannels;

        /// <summary>
        /// 目前已預留給砲擊引導的火力通道數。
        /// BVR 系統在計算中過程攔截容量時會讀取此值，從 maxTargets 中扣除。
        /// </summary>
        public int OccupiedArtilleryChannels => reservedArtilleryChannels;

        /// <summary>
        /// 目前可用於砲擊引導的空閒火力通道數。
        /// = maxTargets − 已預留砲擊通道 − 當前 BVR 波次已佔用通道（全域估算）。
        /// 若 supportsArtilleryGuidance = false 或裝置離線，固定為 0。
        /// </summary>
        public int FreeArtilleryChannels
        {
            get
            {
                if (!Active || !Props.supportsArtilleryGuidance) { return 0; }

                // BVR 中過程攔截佔用的通道是全域計算的，無法精確歸屬到單一雷達。
                // 以「全域已佔用數 × 本雷達份額」做保守估算，避免重複授權。
                int bvrGlobalEngaged = 0;
                Map map = parent.MapHeld;
                if (map != null)
                {
                    MapComponent_BVRCombat mgr = map.GetComponent<MapComponent_BVRCombat>();
                    if (mgr != null)
                    {
                        // 本雷達在全域容量中所占的比例（以防禦方為單位），
                        // 用來估算其應承擔的 BVR 佔用數。
                        int totalCapacity = 0;
                        foreach (CompFireControlRadar fc in mgr.fireControlRadars)
                        {
                            if (fc.Active && fc.parent.Faction == parent.Faction)
                            {
                                totalCapacity += fc.Props.maxTargets;
                            }
                        }
                        if (totalCapacity > 0)
                        {
                            int globalEngaged = mgr.CountEngaged();
                            // 以整數四捨五入分攤（保守：Ceil 使估算偏高，減少雙重授權風險）
                            bvrGlobalEngaged = Mathf.CeilToInt(
                                (float)globalEngaged * Props.maxTargets / totalCapacity);
                        }
                    }
                }

                return Mathf.Max(0, Props.maxTargets - reservedArtilleryChannels - bvrGlobalEngaged);
            }
        }

        /// <summary>
        /// 佔用一個火力通道以引導超視距砲擊。
        /// 呼叫前應先確認 <see cref="FreeArtilleryChannels"/> > 0
        /// （由 <see cref="CompArtilleryStrike.FindAvailableRadar"/> 保證）。
        /// 不回傳結果：若需要原子性「確認後佔用」，請改用 <see cref="TryOccupyArtilleryChannel"/>。
        /// </summary>
        public void OccupyArtilleryChannel()
        {
            reservedArtilleryChannels = Mathf.Min(reservedArtilleryChannels + 1, Props.maxTargets);
        }

        /// <summary>
        /// 原子性確認後佔用：先確認 Active、supportsArtilleryGuidance、有空閒通道，
        /// 若都滿足則立即佔用並回傳 true，否則回傳 false（無副作用）。
        /// 適合在沒有預先呼叫 FindAvailableRadar 的情境中使用。
        /// </summary>
        public bool TryOccupyArtilleryChannel()
        {
            if (!Active || !Props.supportsArtilleryGuidance) { return false; }
            if (FreeArtilleryChannels <= 0) { return false; }
            OccupyArtilleryChannel();
            return true;
        }

        /// <summary>
        /// 釋放一個先前佔用的砲擊引導火力通道。
        /// 安全防呆：不會扣至負數，多餘呼叫無副作用。
        /// </summary>
        public void ReleaseArtilleryChannel()
        {
            reservedArtilleryChannels = Mathf.Max(0, reservedArtilleryChannels - 1);
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref reservedArtilleryChannels, "reservedArtilleryChannels", 0);
        }
    }
}
