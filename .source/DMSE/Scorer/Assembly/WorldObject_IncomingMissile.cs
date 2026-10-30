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
    /// 抵達時依導引頭自動選擇落點（<see cref="GuidanceType.ArrivalCell"/>，找不到目標且裝有盤旋飛控時改為盤旋等待）並生成 <see cref="MissileIncoming"/>（不開啟玩家選靶）。
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
                ?? DefDatabase<ThingDef>.GetNamedSilentFail(IncomingMissileUtility.DefaultIncomingDef);

            if (def != null)
            {
                // 盤旋飛控：導引頭找不到目標時不直接回退慣性落點，而是在目標上空盤旋等待。
                IntVec3 cell = GuidanceType.ArrivalCell(config, map, base.Faction, out bool loiter);
                // 從世界航跡最後一段的方向飛入地圖。
                Thing thing = MissileProjectileMaker.SpawnIncoming(def, cell, map, ArrivalHeading);
                if (thing is IIncomingMissile incoming)
                {
                    incoming.Config = config != null ? config.Clone() : null;
                    incoming.WorldPhaseDone = true;
                    incoming.Attacker = base.Faction;
                    // 命中偏移套在落點上（每枚各自落在不同格），BVR 以偏移後的落點登記
                    if (thing is Projectile_MissileIncoming p)
                    {
                        p.ApplyLandingScatter();
                        cell = p.Position;
                    }
                    // 同步交給 BVR（不依賴首-tick 時序）；盤旋飛控改註冊為盤旋波次（失敗則照常交給一般波次）。
                    bool loitered = loiter && MissileBVRUtility.TryRegisterLoitering(thing, cell, incoming.Config);
                    if (!loitered) { MissileBVRUtility.TryRegister(thing, cell); }
                    if (Prefs.DevMode)
                    {
                        Log.Message($"[DMSE BVR] 來襲導彈抵達落點 {cell}（攻擊方={(base.Faction != null ? base.Faction.Name : "null")}），嘗試交給 BVR。"
                            + (loitered ? " 盤旋飛控啟動：導引頭無目標，改為盤旋等待。" : ""));
                    }
                }

                if (map.IsPlayerHome && !State.playerNotified)
                {
                    // 一小時內的後續來襲改用訊息，不再每發一封信件並暫停遊戲。
                    MissileAlertThrottle.Notify("Incoming",
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
