using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 導彈陣地的 SitePartWorker。<br/>
    /// ‧ 提供到達信函說明（<see cref="GetArrivedLetterPart"/>）。<br/>
    /// ‧ 地圖移除事件通知（<see cref="Notify_SiteMapAboutToBeRemoved"/>）：<br/>
    ///   陣地未摧毀時站點保留（<see cref="Patch_MissileBaseSiteKept"/>）；地面突擊後撤退且敵方火控雷達仍在，啟動再進入封鎖計時，模擬敵方重新整備防線。
    /// </summary>
    public class SitePartWorker_MissileBase : SitePartWorker
    {
        /// <summary>返回到達信函的正文說明。</summary>
        public override string GetArrivedLetterPart(Map map, out LetterDef preferredLetterDef,
            out LookTargets lookTargets)
        {
            preferredLetterDef = LetterDefOf.ThreatBig;
            lookTargets = new LookTargets(new GlobalTargetInfo(map.Center, map));
            return "DMSE.MissileBase.ArrivedLetter".Translate();
        }

        /// <summary>
        /// 地圖移除前（陣地已摧毀時站點隨後刪除，不處理）：<br/>
        /// 1. 重設陣地轉移與世界層開火計時（模擬陣地趁機重新部署）。<br/>
        /// 2. 若玩家曾地面突擊且敵方火控雷達仍在（未摧毀必要目標），啟動再進入封鎖計時。<br/>
        ///    封鎖期間，玩家車隊與運輸艙無法進入陣地；下次進入時陣地完整重建。
        /// </summary>
        public override void Notify_SiteMapAboutToBeRemoved(SitePart sitePart)
        {
            base.Notify_SiteMapAboutToBeRemoved(sitePart);

            Site site = sitePart.site;
            WorldObjectComp_MissileBase woc = site?.GetComponent<WorldObjectComp_MissileBase>();
            MapComponent_MissileBase comp = site?.Map?.GetComponent<MapComponent_MissileBase>();
            if (woc == null || comp == null || comp.BaseSiteDestroyed) return;

            // 1. 重設轉移與開火計時
            woc.Notify_SiteMapRemoved();

            // 2. 地圖仍可查詢（移除前）— 地面突擊後撤退且火控雷達尚存
            if (comp.PlayerVisited && comp.RemainingRadars.Any())
            {
                woc.StartReentryCooldown();
                Messages.Message("DMSE.MissileBase.ReentryBlockStarted".Translate(site.LabelCap, woc.ReentryBlockedFor.ToStringTicksToPeriod()),
                    site, MessageTypeDefOf.NegativeEvent);

                if (Prefs.DevMode)
                    Log.Message("[DMSE MissileBase] 玩家撤退，敵方火控雷達仍存活：再進入封鎖已啟動。");
            }
        }
    }
}
