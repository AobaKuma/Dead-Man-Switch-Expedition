using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 交戰領域：空中（導彈、空投艙、攔截彈，DMSE 的預設）或水下（例如其他 mod 的魚雷、潛航突擊艇）。
    /// 航跡的領域來自彈體（<see cref="MissileBodyDef.domain"/>），沒有彈體時來自來襲物的 <see cref="BVRTargetProps.domain"/>。
    /// 能接戰的一方各自宣告打哪些領域：火控雷達與 CIWS 用 <c>domains</c> 清單（空 = 只有空中），攔截彈看彈體的領域，
    /// 其他世界層效果器實作 <see cref="IDomainAwareEffector"/>（沒實作的只打空中）。
    /// </summary>
    public enum CombatDomain : byte
    {
        Air,
        Underwater,
    }

    /// <summary>世界層效果器宣告自己能打的領域；沒實作的效果器只打 <see cref="CombatDomain.Air"/>。</summary>
    public interface IDomainAwareEffector
    {
        bool Engages(CombatDomain domain);
    }

    public static class CombatDomainUtility
    {
        /// <summary>有彈體看彈體，否則看來襲物的目標屬性。</summary>
        public static CombatDomain Of(MissileConfig config, BVRTargetProps props)
        {
            if (config?.body != null) { return config.body.domain; }
            return props?.domain ?? CombatDomain.Air;
        }

        public static CombatDomain Of(IBVRTrack track) => track == null ? CombatDomain.Air : Of(track.Config, track.TargetProps);

        /// <summary>地圖層被收起的來襲物：導彈看裝配，空投艙看 def 上的 <see cref="BVRTargetProps"/>。</summary>
        public static CombatDomain Of(Thing payload)
        {
            if (payload == null) { return CombatDomain.Air; }
            MissileConfig config = (payload as IIncomingMissile)?.Config;
            BVRTargetProps props = payload.def?.GetModExtension<BVRTargetProps>();
            if (config?.body != null) { return config.body.domain; }
            if (props != null) { return props.domain; }
            // 空投艙的 skyfaller 沒有屬性時，看艙裡的東西（例如其他 mod 的突擊艇建築）。
            if (payload is Skyfaller skyfaller && skyfaller.innerContainer.Count > 0)
            {
                BVRTargetProps inner = skyfaller.innerContainer[0].def?.GetModExtension<BVRTargetProps>();
                if (inner != null) { return inner.domain; }
            }
            return CombatDomain.Air;
        }

        /// <summary>空清單 = 只有空中（DMSE 既有內容不必設定）。</summary>
        public static bool Covers(List<CombatDomain> domains, CombatDomain domain)
            => domains.NullOrEmpty() ? domain == CombatDomain.Air : domains.Contains(domain);

        public static bool Engages(Fortified.IAirEffector effector, CombatDomain domain)
            => effector is IDomainAwareEffector aware ? aware.Engages(domain) : domain == CombatDomain.Air;
    }
}
