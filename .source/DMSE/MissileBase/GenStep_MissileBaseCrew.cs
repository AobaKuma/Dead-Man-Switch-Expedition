using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace DMSE
{
    /// <summary>
    /// 導彈陣地 Pawn 生成步驟。<br/>
    /// 依 <see cref="SitePartParams.threatPoints"/>（或 XML 固定值 <see cref="crewCount"/>）
    /// 生成敵方 Pawn，分為兩組：<br/>
    /// ‧ <b>炮手</b>（<see cref="gunnerFraction"/>）：
    ///   <see cref="LordJob_ManTurrets"/> → 自動占據最近 Mannable 炮塔。<br/>
    /// ‧ <b>陣地班</b>（其餘）：
    ///   <see cref="LordJob_MissileCrewDuty"/> → 防守陣地、後台自動搬運導彈至發射架。<br/>
    /// <br/>
    /// 生成順序應在 FFF 結構（order 400）之後，以便讀取 <c>RectOfInterest</c> 作為落點中心。
    /// </summary>
    public class GenStep_MissileBaseCrew : GenStep
    {
        // ──────────────── XML 欄位 ────────────────

        /// <summary>
        /// 固定生成人數。-1（預設）= 依 <see cref="pointsPerPawn"/> 從
        /// <see cref="SitePartParams.threatPoints"/> 換算，並限制在 [<see cref="minCrew"/>, <see cref="maxCrew"/>]。
        /// </summary>
        public int crewCount = -1;

        /// <summary>依 threatPoints 換算時，每名 Pawn 對應的點數。預設 100。</summary>
        public float pointsPerPawn = 100f;

        /// <summary>依 threatPoints 換算時，生成人數下限。預設 4。</summary>
        public int minCrew = 4;

        /// <summary>依 threatPoints 換算時，生成人數上限。預設 16。</summary>
        public int maxCrew = 16;

        /// <summary>
        /// 炮手佔總人數的比例（0–1）。炮手使用 <see cref="LordJob_ManTurrets"/>，
        /// 其餘皆為陣地班（<see cref="LordJob_MissileCrewDuty"/>）。<br/>
        /// 若地圖上不存在任何 <see cref="CompMannable"/> 建築，此比例強制為 0（全員陣地班）。
        /// </summary>
        public float gunnerFraction = 0.25f;

        /// <summary>落點隨機搜索半徑（格）。預設 12。</summary>
        public float spawnRadius = 12f;

        // ──────────────── GenStep 介面 ────────────────

        public override int SeedPart => 0x1D2C3B4A;

        public override void Generate(Map map, GenStepParams parms)
        {
            Faction faction = map.ParentFaction;
            if (faction == null || faction.IsPlayer)
            {
                if (Prefs.DevMode)
                    Log.Warning("[DMSE MissileBaseCrew] 無敵方 ParentFaction，跳過 Pawn 生成。");
                return;
            }

            int total = ResolveCrewCount(parms);
            if (total <= 0) return;

            IntVec3 center = GetStructureCenter(map);

            // 生成並落地所有 Pawn（尚未分配 Lord）
            List<Pawn> spawned = SpawnPawns(faction, map, center, total, parms);
            if (spawned.NullOrEmpty()) return;

            // 判定地圖是否有可操作炮塔；若無則全員陣地班
            bool hasTurrets = HasMannableTurrets(map, faction);
            int gunnerCount = hasTurrets
                ? Mathf.Clamp(Mathf.RoundToInt(spawned.Count * gunnerFraction), 1, spawned.Count - 1)
                : 0;

            List<Pawn> gunners = spawned.Take(gunnerCount).ToList();
            List<Pawn> crew    = spawned.Skip(gunnerCount).ToList();

            if (gunners.Any())
                CreateGunnerLord(gunners, faction, map);

            if (crew.Any())
                CreateCrewLord(crew, faction, map, center);

            if (Prefs.DevMode)
                Log.Message($"[DMSE MissileBaseCrew] 生成 {spawned.Count} Pawn：炮手 {gunnerCount}、陣地班 {crew.Count}。");
        }

        // ──────────────── 人數決定 ────────────────

        private int ResolveCrewCount(GenStepParams parms)
        {
            if (crewCount >= 0) return crewCount;
            float points = parms.sitePart?.parms?.threatPoints ?? (minCrew * pointsPerPawn);
            return Mathf.Clamp(Mathf.RoundToInt(points / pointsPerPawn), minCrew, maxCrew);
        }

        // ──────────────── 結構中心 ────────────────

        private static IntVec3 GetStructureCenter(Map map)
        {
            if (MapGenerator.TryGetVar<CellRect>("RectOfInterest", out CellRect rect))
                return rect.CenterCell;
            return map.Center;
        }

        // ──────────────── Mannable 炮塔偵測 ────────────────

        private static bool HasMannableTurrets(Map map, Faction faction)
        {
            foreach (Building b in map.listerBuildings.allBuildingsNonColonist)
            {
                if (b.Faction != faction || b.Destroyed) continue;
                if (b.TryGetComp<CompMannable>() != null) return true;
            }
            return false;
        }

        // ──────────────── Pawn 生成與落地 ────────────────

        private List<Pawn> SpawnPawns(Faction faction, Map map, IntVec3 center, int count, GenStepParams parms)
        {
            List<Pawn> result = new List<Pawn>(count);

            // 優先透過 PawnGroupMakerUtility 生成（帶裝備、符合 Faction pawnGroupMakers）
            if (!faction.def.pawnGroupMakers.NullOrEmpty())
            {
                PawnGroupMakerParms groupParms = BuildGroupMakerParms(faction, map, count, parms);
                List<Pawn> generated = PawnGroupMakerUtility.GeneratePawns(groupParms, warnOnZeroResults: false).ToList();
                if (!generated.NullOrEmpty())
                {
                    foreach (Pawn p in generated)
                        TrySpawnPawn(p, center, map, result);
                    return result;
                }
            }

            // 回退：逐一以 PawnGenerator 生成基礎士兵
            for (int i = 0; i < count; i++)
            {
                PawnKindDef kind = faction.RandomPawnKind();
                if (kind == null) break;
                Pawn p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                    kind, faction,
                    context: PawnGenerationContext.NonPlayer,
                    tile: map.Tile,
                    mustBeCapableOfViolence: true,
                    inhabitant: true));
                TrySpawnPawn(p, center, map, result);
            }
            return result;
        }

        private PawnGroupMakerParms BuildGroupMakerParms(Faction faction, Map map, int count, GenStepParams parms)
        {
            float points = parms.sitePart?.parms?.threatPoints ?? (count * pointsPerPawn);

            PawnGroupMakerParms dummy = new PawnGroupMakerParms
            {
                faction = faction,
                groupKind = PawnGroupKindDefOf.Settlement
            };
            float minPts = faction.def.MinPointsToGeneratePawnGroup(PawnGroupKindDefOf.Settlement, dummy);

            return new PawnGroupMakerParms
            {
                groupKind  = PawnGroupKindDefOf.Settlement,
                tile       = map.Tile,
                faction    = faction,
                inhabitants = true,
                generateFightersOnly = true,
                points     = Mathf.Max(points, minPts),
            };
        }

        private void TrySpawnPawn(Pawn p, IntVec3 center, Map map, List<Pawn> result)
        {
            IntVec3 cell = FindSpawnCell(center, map);
            if (!cell.IsValid)
            {
                p.Destroy();
                return;
            }
            GenSpawn.Spawn(p, cell, map);
            result.Add(p);
        }

        private IntVec3 FindSpawnCell(IntVec3 center, Map map)
        {
            // 嘗試在標準半徑內找可站立格（不在霧中優先）
            if (CellFinder.TryFindRandomCellNear(center, map, (int)spawnRadius,
                c => c.Standable(map) && !c.Fogged(map), out IntVec3 cell))
                return cell;

            // 擴大範圍，允許霧中格
            if (CellFinder.TryFindRandomCellNear(center, map, (int)(spawnRadius * 2),
                c => c.Standable(map), out cell))
                return cell;

            return IntVec3.Invalid;
        }

        // ──────────────── Lord 分配 ────────────────

        private static void CreateGunnerLord(List<Pawn> gunners, Faction faction, Map map)
        {
            // LordJob_ManTurrets：LordToil_ManClosestTurrets 讓每個 Pawn 尋找最近的可操作炮塔
            Lord lord = LordMaker.MakeNewLord(faction, new LordJob_ManTurrets(), map);
            foreach (Pawn p in gunners)
                lord.AddPawn(p);
        }

        private static void CreateCrewLord(List<Pawn> crew, Faction faction, Map map, IntVec3 stagingPoint)
        {
            // LordJob_MissileCrewDuty：防守 + 食物空投；DutyDef.Defend 允許後台搬運工作
            Lord lord = LordMaker.MakeNewLord(faction, new LordJob_MissileCrewDuty(stagingPoint), map);
            foreach (Pawn p in crew)
                lord.AddPawn(p);
        }
    }
}
