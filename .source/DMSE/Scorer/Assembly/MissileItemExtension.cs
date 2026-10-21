using System.Collections.Generic;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 掛在「固定構型」導彈物品（沒有 <see cref="CompMissileConfig"/>，例如 V1、白蟻、萊茵女兒）上，宣告它的彈體。
    /// 同一條發射軌可裝不同型號，所以發射設定不能只靠平台的 <see cref="CompProperties_MissileRailLauncher.missileBody"/>。
    /// </summary>
    public class MissileItemExtension : DefModExtension
    {
        public MissileBodyDef body;

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors())
            {
                yield return e;
            }
            if (body == null)
            {
                yield return "MissileItemExtension.body 不可為空。";
            }
        }

        /// <summary>物品宣告的彈體：<see cref="CompMissileConfig"/> 的已套用彈體，其次是本擴充；都沒有為 null。</summary>
        public static MissileBodyDef BodyOf(Thing missile)
        {
            if (missile == null) { return null; }
            CompMissileConfig cfg = missile.TryGetComp<CompMissileConfig>();
            if (cfg?.config?.body != null) { return cfg.config.body; }
            return missile.def.GetModExtension<MissileItemExtension>()?.body;
        }

        /// <summary>
        /// 物品的發射設定（副本）：有效的 <see cref="CompMissileConfig"/> 優先，其次以本擴充的彈體帶預設零件建立；
        /// 都沒有回傳 null（由呼叫端退回平台預設）。
        /// </summary>
        public static MissileConfig ConfigFor(Thing missile)
        {
            if (missile == null) { return null; }
            CompMissileConfig cfg = missile.TryGetComp<CompMissileConfig>();
            if (cfg != null && cfg.config != null && cfg.config.Valid)
            {
                return cfg.config.Clone();
            }
            MissileBodyDef body = missile.def.GetModExtension<MissileItemExtension>()?.body;
            return body != null ? MissileConfig.NewWithDefaults(body) : null;
        }
    }
}
