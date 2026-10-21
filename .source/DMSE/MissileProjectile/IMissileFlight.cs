using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 發射特效（<see cref="CompEffecterLaunch"/>）讀取的飛行狀態。
    /// 舊的 Skyfaller 導彈與新的 <see cref="Projectile_MissileBase"/> 都實作，使尾焰／煙／點火公式不必知道載體型別
    /// （docs/Missile_Skyfaller轉Projectile_計畫.md §8.2）。
    /// </summary>
    public interface IMissileFlight : ILaunchTarget
    {
        /// <summary>畫面上的彈體位置（原 <c>Skyfaller.DrawPos</c>）：點火塵位置與「出地圖即停火」的判定。</summary>
        Vector3 VisualPos { get; }

        /// <summary>地面格（原 <c>Skyfaller.Position</c>）：是否該生成 fleck 的判定格。</summary>
        IntVec3 GroundCell { get; }

        /// <summary>升空段長度（原 <c>skyfaller.ticksToDiscard</c>），尾焰進度 evaluate 的分母。</summary>
        int AscentTicks { get; }

        /// <summary>尾焰噴射速度（原 <c>def.skyfaller.speedCurve.Evaluate(evaluate)</c>）。</summary>
        float EffectSpeed(float evaluate);

        /// <summary>
        /// 尾焰進度 evaluate（原 <c>ticksSinceSpawn / (ticksToDiscard + delay)</c>）。
        /// 新導彈在離場巡航段把它固定在交接當下，使火焰持續到飛出地圖。
        /// </summary>
        float EffectEvaluate(int ticksSinceSpawn, int ignitionDelayTicks);
    }

    /// <summary>
    /// 抵達落點的導彈（舊 <see cref="MissileIncoming"/> skyfaller 或新 <see cref="Projectile_MissileIncoming"/>）：
    /// BVR、警報與波次打擊回報只透過此介面讀寫，不需要知道載體型別。
    /// </summary>
    public interface IIncomingMissile
    {
        MissileConfig Config { get; set; }
        /// <summary>發射此導彈的陣營（來襲方）。</summary>
        Faction Attacker { get; set; }
        /// <summary>是否已交由 BVR 處理（避免重複登記攔截）。</summary>
        bool BvrHandled { get; set; }
        /// <summary>世界層已結算（抵達後只剩末端防禦）。</summary>
        bool WorldPhaseDone { get; set; }
        /// <summary>波次打擊的任務 id 與波次（-1 = 非波次打擊）。</summary>
        int StrikeMissionId { get; set; }
        int StrikeWave { get; set; }
    }

    /// <summary>地圖上飛行中的攔截彈（舊 <see cref="InterceptProjectile"/> 或攔截用途的 <see cref="Projectile_MissileLaunch"/>）。</summary>
    public interface IInterceptorShot
    {
        /// <summary>攔截目標 <see cref="BVRTarget.id"/>；-1 = 世界層攔截的純視覺升空彈。</summary>
        int TargetId { get; }

        /// <summary>升空結束時的命中率（可寫：測試用來強制命中/落空）。</summary>
        float HitChance { get; set; }
    }
}
