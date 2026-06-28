using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 通用「來襲導彈」世界地圖物件。以巡航導彈的 WorldObject 旅行/繪製邏輯為基礎，
    /// 但抵達時自動選擇落點並生成 <see cref="MissileIncoming"/>（不開啟玩家選靶）。
    /// 任何陣營皆可發射；攜帶攻擊方陣營與裝配設定，作為日後 BVR 攔截的航跡/識別來源。
    /// </summary>
    public class WorldObject_IncomingMissile : WorldObject
    {
        public MissileConfig config;
        public ThingDef incomingSkyfaller;
        public PlanetTile destinationTile = PlanetTile.Invalid;

        private PlanetTile initialTile = PlanetTile.Invalid;
        private float traveledPct;
        private bool arrived;

        private Vector3 Start => Find.WorldGrid.GetTileCenter(initialTile);
        private Vector3 End => Find.WorldGrid.GetTileCenter(destinationTile);

        public override Vector3 DrawPos => Vector3.Slerp(Start, End, traveledPct);

        public override bool ExpandingIconFlipHorizontal
            => GenWorldUI.WorldToUIPosition(Start).x > GenWorldUI.WorldToUIPosition(End).x;

        public override Color ExpandingIconColor => base.Faction != null ? base.Faction.Color : Color.white;

        public override float ExpandingIconRotation
        {
            get
            {
                if (!def.rotateGraphicWhenTraveling) { return base.ExpandingIconRotation; }
                Vector2 a = GenWorldUI.WorldToUIPosition(Start);
                Vector2 b = GenWorldUI.WorldToUIPosition(End);
                float num = Mathf.Atan2(b.y - a.y, b.x - a.x) * 57.29578f;
                if (num > 180f) { num -= 180f; }
                return num + 90f;
            }
        }

        private float TraveledPctStepPerTick
        {
            get
            {
                Vector3 start = Start;
                Vector3 end = End;
                if (start == end) { return 1f; }
                float num = GenMath.SphericalDistance(start.normalized, end.normalized);
                if (num == 0f) { return 1f; }
                float speedFactor = config != null ? config.WorldSpeedFactor : 1f;
                return (0.00025f * speedFactor) / num;
            }
        }

        public override void PostAdd()
        {
            base.PostAdd();
            initialTile = base.Tile;
        }

        protected override void TickInterval(int delta)
        {
            base.TickInterval(delta);
            traveledPct += TraveledPctStepPerTick * delta;
            if (traveledPct >= 1f)
            {
                traveledPct = 1f;
                Arrive();
            }
        }

        private void Arrive()
        {
            if (arrived) { return; }
            arrived = true;

            Map map = GetOrGenerateMapUtility.GetOrGenerateMap(destinationTile, WorldObjectDefOf.Camp);
            ThingDef def = incomingSkyfaller
                ?? (config != null && config.body != null ? config.body.incomingSkyfaller : null)
                ?? DefDatabase<ThingDef>.GetNamedSilentFail("DMSE_Incoming_CruiseMissile");

            if (map != null && def != null)
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
            Scribe_Values.Look(ref destinationTile, "destinationTile", PlanetTile.Invalid);
            Scribe_Values.Look(ref initialTile, "initialTile", PlanetTile.Invalid);
            Scribe_Values.Look(ref traveledPct, "traveledPct", 0f);
            Scribe_Values.Look(ref arrived, "arrived", false);
            Scribe_Defs.Look(ref incomingSkyfaller, "incomingSkyfaller");
            Scribe_Deep.Look(ref config, "config");
        }
    }
}
