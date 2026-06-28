using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 導彈陣地的 SitePartWorker。<br/>
    /// ‧ 提供到達信函說明（<see cref="GetArrivedLetterPart"/>）。<br/>
    /// ‧ 地圖移除事件通知（<see cref="Notify_SiteMapAboutToBeRemoved"/>）：<br/>
    ///   若敵方火控雷達仍存活，啟動再進入封鎖計時，模擬敵方重新整備防線。
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
        /// 地圖移除前：<br/>
        /// 1. 重設陣地轉移計時（模擬陣地趁機重新部署）。<br/>
        /// 2. 若敵方火控雷達仍存活（玩家未能摧毀指揮中樞），啟動再進入封鎖計時。<br/>
        ///    封鎖期間，玩家車隊無法從地面進入陣地。
        /// </summary>
        public override void Notify_SiteMapAboutToBeRemoved(SitePart sitePart)
        {
            base.Notify_SiteMapAboutToBeRemoved(sitePart);

            WorldObjectComp_MissileBase woc = sitePart.site?.GetComponent<WorldObjectComp_MissileBase>();
            if (woc == null) return;

            // 1. 重設轉移計時
            woc.ResetRelocationTimer();

            // 2. 地圖仍可查詢（移除前）— 檢查敵方火控雷達是否仍存活
            Map map = sitePart.site.Map;
            if (map != null && HasActiveFireControlRadar(map, sitePart.site.Faction))
            {
                woc.StartReentryCooldown();

                if (Prefs.DevMode)
                    Log.Message("[DMSE MissileBase] 玩家撤退，敵方雷達仍存活：再進入封鎖已啟動。");
            }
        }

        /// <summary>
        /// 地圖上是否存在至少一個屬於 <paramref name="enemy"/> 陣營且目前活躍中的
        /// <see cref="CompFireControlRadar"/>。
        /// </summary>
        private static bool HasActiveFireControlRadar(Map map, Faction enemy)
        {
            if (enemy == null) return false;

            foreach (Building b in map.listerBuildings.allBuildingsNonColonist)
            {
                if (b.Faction != enemy || b.Destroyed) continue;
                CompFireControlRadar fcr = b.TryGetComp<CompFireControlRadar>();
                if (fcr != null && fcr.Active) return true;
            }
            return false;
        }
    }
}
