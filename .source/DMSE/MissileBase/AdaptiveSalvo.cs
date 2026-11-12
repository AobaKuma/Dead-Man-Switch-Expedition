using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 敵方對目標地圖防空能力的評估：C = 運作中的火控通道數，L = 有彈可射的攔截發射平台數，
    /// E = min(C, L) 為一次最多能同時接戰的枚數（只有通道或只有發射器都不算防空）。
    /// </summary>
    public readonly struct AirDefenseAssessment
    {
        public readonly int channels;
        public readonly int launchers;

        public AirDefenseAssessment(int channels, int launchers)
        {
            this.channels = Math.Max(0, channels);
            this.launchers = Math.Max(0, launchers);
        }

        /// <summary>瓶頸值：通道與發射平台取小。</summary>
        public int Effective => Math.Min(channels, launchers);

        public static AirDefenseAssessment None => default;

        /// <summary>評估一個打擊目標；沒有地圖（運輸船、車隊）時視為無防空。防禦方取目標陣營，沒有則為玩家。</summary>
        public static AirDefenseAssessment Of(MapParent target)
        {
            if (target == null || !target.HasMap) { return None; }
            return Of(target.Map, target.Faction ?? Faction.OfPlayer);
        }

        public static AirDefenseAssessment Of(Map map, Faction defender)
        {
            MapComponent_BVRCombat bvr = map?.GetComponent<MapComponent_BVRCombat>();
            if (bvr == null || defender == null) { return None; }
            int launchers = 0;
            foreach (CompMissileLauncher_Interceptor l in bvr.launchers)
            {
                if (l.parent.Faction == defender && l.Props.interceptorSkyfaller != null && l.Active) { launchers++; }
            }
            return new AirDefenseAssessment(bvr.Channels.Capacity(defender), launchers);
        }

        public override string ToString() => $"C={channels} L={launchers} E={Effective}";
    }

    /// <summary>
    /// 依目標防空能力調整世界層齊射（<see cref="CompProperties_MissileBase.adaptiveSalvo"/>）：<br/>
    /// ‧ 枚數：<c>clamp(基礎 + round(alpha · E^exponent · 難度), 基礎, maxPerSalvo)</c>，次線性成長，防空投資才划算。<br/>
    /// ‧ 組成：E 達到 <see cref="AdaptiveSalvoTier.minEffective"/> 的梯次，每枚依 <see cref="AdaptiveSalvoTier.chance"/>
    ///   換成該梯次的彈體／部件（誘餌、巡航彈…），從最高梯次往下擲；都沒中就用陣地的基本設定。
    /// </summary>
    public class AdaptiveSalvoProps
    {
        /// <summary>防空每增加一單位 E^exponent 時多射的枚數。</summary>
        public float alpha = 0.8f;

        /// <summary>小於 1 時次線性成長。</summary>
        public float exponent = 0.7f;

        /// <summary>一次齊射的上限（與陣地等級綁定）；小於基礎枚數時以基礎枚數為準。</summary>
        public int maxPerSalvo = 6;

        /// <summary>是否乘上說書人難度的威脅倍率（threatScale）。</summary>
        public bool scaleWithThreat = true;

        /// <summary>依 E 解鎖的彈種梯次。</summary>
        public List<AdaptiveSalvoTier> tiers;

        /// <summary>難度倍率：威脅倍率限制在 0.25–3，避免極端設定讓枚數失控。</summary>
        public float DifficultyFactor
            => scaleWithThreat ? Mathf.Clamp(Find.Storyteller?.difficulty?.threatScale ?? 1f, 0.25f, 3f) : 1f;

        /// <summary>給定基礎枚數與目標的 E，回傳本次齊射枚數。</summary>
        public int Count(int baseCount, int effective, float difficulty)
        {
            baseCount = Math.Max(1, baseCount);
            if (effective <= 0) { return baseCount; }
            int extra = Mathf.RoundToInt(alpha * Mathf.Pow(effective, exponent) * Mathf.Max(0f, difficulty));
            return Mathf.Clamp(baseCount + extra, baseCount, Math.Max(baseCount, maxPerSalvo));
        }

        /// <summary>為一枚導彈挑選梯次：從 E 已達標的最高梯次往下，各擲一次 <see cref="AdaptiveSalvoTier.chance"/>；都沒中回傳 null。</summary>
        public AdaptiveSalvoTier PickTier(int effective, Func<float, bool> chance)
        {
            if (tiers.NullOrEmpty()) { return null; }
            foreach (AdaptiveSalvoTier tier in tiers.Where(t => t != null && effective >= t.minEffective).OrderByDescending(t => t.minEffective))
            {
                if (chance(tier.chance)) { return tier; }
            }
            return null;
        }

        public IEnumerable<string> ConfigErrors(string owner)
        {
            if (exponent <= 0f) { yield return $"{owner}: adaptiveSalvo.exponent must be positive."; }
            if (tiers == null) { yield break; }
            foreach (AdaptiveSalvoTier tier in tiers)
            {
                if (tier == null) { continue; }
                if (tier.missileBody == null && tier.partOptions.NullOrEmpty())
                {
                    yield return $"{owner}: adaptiveSalvo tier at E>={tier.minEffective} changes neither missileBody nor partOptions.";
                }
                if (tier.partOptions != null && tier.partOptions.Any(o => o?.parts.NullOrEmpty() != false))
                {
                    yield return $"{owner}: adaptiveSalvo tier at E>={tier.minEffective} has a partOptions entry with no parts.";
                }
            }
        }
    }

    /// <summary>一個依防空能力解鎖的彈種梯次。</summary>
    public class AdaptiveSalvoTier
    {
        /// <summary>目標 E 至少要達到多少才會出現。</summary>
        public int minEffective = 1;

        /// <summary>每枚導彈改用此梯次的機率（混編比例）。</summary>
        public float chance = 1f;

        /// <summary>換用的彈體；null = 沿用陣地彈體。</summary>
        public MissileBodyDef missileBody;

        /// <summary>換用的部件選項；null = 沿用陣地的 partOptions（不相容的部件自動略過）。</summary>
        public List<MissileBasePartOption> partOptions;
    }
}
