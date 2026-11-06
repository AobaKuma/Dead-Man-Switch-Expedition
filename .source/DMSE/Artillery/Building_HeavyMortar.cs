using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 重型迫擊砲：原版 <see cref="Building_TurretGun"/>（砲手裝填、可換砲彈）＋世界砲擊
    /// （<see cref="CompArtilleryStrike"/>）。
    ///
    /// 原版砲塔邏輯與世界砲擊共用同一根砲管、同一發載入的砲彈，因此：
    ///   - 世界砲擊進行中不自動接戰本地圖目標（<see cref="TryFindNewTarget"/> 回無效），
    ///     避免本地砲塔把砲手剛裝好的砲彈打掉；手動強制目標仍照常。
    ///   - 原版 <see cref="Building_TurretGun.Tick"/> 先呼叫 base.Tick()（comp 在這裡 tick，
    ///     世界瞄準方位也在此寫入），之後才跑砲塔自己的 TurretTopTick；所以每次 Tick 結束後
    ///     再把砲管拉回世界瞄準方位。
    /// 沒有世界目標時完全交給原版砲塔邏輯。
    /// </summary>
    public class Building_HeavyMortar : Building_TurretGun
    {
        /// <summary>世界砲擊進行中不自動接戰本地目標（手動強制目標仍可）。</summary>
        public override LocalTargetInfo TryFindNewTarget()
        {
            CompArtilleryStrike art = GetComp<CompArtilleryStrike>();
            if (art != null && art.IsSustainedFiring) { return LocalTargetInfo.Invalid; }
            return base.TryFindNewTarget();
        }

        protected override void Tick()
        {
            base.Tick();

            CompArtilleryStrike art = GetComp<CompArtilleryStrike>();
            if (art == null || !art.IsSustainedFiring) { return; }

            float aim = art.AimAngle;
            if (top != null && !float.IsNaN(aim)) { top.CurRotation = aim; }
        }
    }
}
