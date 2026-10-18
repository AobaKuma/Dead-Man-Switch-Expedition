using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 通用「來襲導彈」世界地圖物件。飛行、存檔與終端更新見 <see cref="MissileWorldObject"/>；
    /// 抵達時自動選擇落點並生成 <see cref="MissileIncoming"/>（不開啟玩家選靶）。
    /// 任何陣營皆可發射；攜帶攻擊方陣營與裝配設定，作為 BVR 攔截的航跡/識別來源。
    /// 目標已離開（且終端更新追不上）或不存在時落空，不生成地圖。
    /// </summary>
    public class WorldObject_IncomingMissile : MissileWorldObject
    {
        public ThingDef incomingSkyfaller;

        public override Color ExpandingIconColor => base.Faction != null ? base.Faction.Color : Color.white;

        protected override bool AllowGenerateTargetMap => false;

        protected override void OnArrived(Map map)
        {
            if (map == null)
            {
                if (targetParent != null && targetParent.Faction == Faction.OfPlayer)
                {
                    Messages.Message("DMSE.Missile.IncomingMissed".Translate(base.Faction != null ? base.Faction.Name : "?"),
                        new GlobalTargetInfo(destinationTile), MessageTypeDefOf.PositiveEvent, historical: false);
                }
                Destroy();
                return;
            }

            ThingDef def = incomingSkyfaller
                ?? (config != null && config.body != null ? config.body.incomingSkyfaller : null)
                ?? DefDatabase<ThingDef>.GetNamedSilentFail("DMSE_Incoming_CruiseMissile");

            if (def != null)
            {
                IntVec3 cell = PickTargetCell(map);
                Skyfaller faller = SkyfallerMaker.SpawnSkyfaller(def, cell, map);
                if (faller is MissileIncoming incoming)
                {
                    incoming.config = config != null ? config.Clone() : null;
                    incoming.attacker = base.Faction;
                    if (Prefs.DevMode)
                    {
                        Log.Message($"[DMSE BVR] 來襲導彈抵達落點 {cell}（攻擊方={(base.Faction != null ? base.Faction.Name : "null")}），嘗試交給 BVR。");
                    }
                    // 同步交給 BVR（不依賴首-tick 時序）。
                    MissileBVRUtility.TryRegister(incoming, cell);
                }

                if (map.IsPlayerHome)
                {
                    Find.LetterStack.ReceiveLetter(
                        "DMSE.Missile.IncomingLetter".Translate(),
                        "DMSE.Missile.IncomingLetterDesc".Translate(base.Faction != null ? base.Faction.Name : "?"),
                        LetterDefOf.ThreatBig,
                        new TargetInfo(cell, map));
                }
            }

            Destroy();
        }

        /// <summary>
        /// 選擇落點格：依導引頭類別委派，找不到有效格時退化為慣性邏輯。
        /// </summary>
        private IntVec3 PickTargetCell(Map map)
        {
            float N = config?.PayloadCapacity ?? 5f;
            MissilePartDef guidancePart = config?.PartFor(MissilePartCategory.Guidance);
            GuidanceType guidance = guidancePart?.guidanceType;

            if (guidance != null)
            {
                IntVec3 result = guidance.PickTarget(N, map, base.Faction);
                if (result.IsValid && result.InBounds(map)) { return result; }
            }

            // 後備慣性邏輯
            return GuidanceType.InertialTarget(map, base.Faction);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Defs.Look(ref incomingSkyfaller, "incomingSkyfaller");
        }
    }
}
