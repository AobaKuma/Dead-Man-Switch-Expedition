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
    /// 抵達時依導引頭自動選擇落點（<see cref="GuidanceType.PickCell"/>）並生成 <see cref="MissileIncoming"/>（不開啟玩家選靶）。
    /// 任何陣營皆可發射；攜帶攻擊方陣營與裝配設定，作為 BVR 攔截的航跡/識別來源。
    /// 目標已離開（且終端更新追不上）或不存在時落空，不生成地圖。
    /// </summary>
    public class WorldObject_IncomingMissile : MissileWorldObject
    {
        public ThingDef incomingSkyfaller;

        public override ThingDef IncomingDef => incomingSkyfaller ?? base.IncomingDef;

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
                IntVec3 cell = GuidanceType.PickCell(config, map, base.Faction);
                // 從世界航跡最後一段的方向飛入地圖。
                Thing thing = MissileProjectileMaker.SpawnIncoming(def, cell, map, ArrivalHeading);
                if (thing is IIncomingMissile incoming)
                {
                    incoming.Config = config != null ? config.Clone() : null;
                    incoming.WorldPhaseDone = true;
                    incoming.Attacker = base.Faction;
                    if (Prefs.DevMode)
                    {
                        Log.Message($"[DMSE BVR] 來襲導彈抵達落點 {cell}（攻擊方={(base.Faction != null ? base.Faction.Name : "null")}），嘗試交給 BVR。");
                    }
                    // 同步交給 BVR（不依賴首-tick 時序）。
                    MissileBVRUtility.TryRegister(thing, cell);
                }

                if (map.IsPlayerHome && !State.playerNotified)
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

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Defs.Look(ref incomingSkyfaller, "incomingSkyfaller");
        }
    }
}
