using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace DMSE
{
    // ──────────────────────────────────────────────────────────────────────────
    // LordToil：防守導彈陣地（允許後台工作：搬運導彈）
    // ──────────────────────────────────────────────────────────────────────────

    public class LordToil_MissileCrewDefend : LordToil
    {
        private IntVec3 anchor;
        private float radius;

        public LordToil_MissileCrewDefend(IntVec3 anchor, float radius = 40f)
        {
            this.anchor = anchor;
            this.radius = radius;
        }

        public override bool ForceHighStoryDanger => false;

        public override void UpdateAllDuties()
        {
            foreach (Pawn p in lord.ownedPawns)
                p.mindState.duty = new PawnDuty(DutyDefOf.Defend, anchor, radius);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // LordJob：導彈陣地守備任務
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 導彈陣地敵方 Pawn 的 LordJob。<br/>
    /// ‧ 平時：防守陣地、允許搬運（填充導彈發射架）。<br/>
    /// ‧ 受攻擊：切換至全力進攻。<br/>
    /// ‧ 每 <see cref="FoodCheckInterval"/> tick 檢查食物庫存；不足時空投生存食品包。
    /// </summary>
    public class LordJob_MissileCrewDuty : LordJob
    {
        public IntVec3 stagingPoint;

        private const int FoodCheckInterval = 5000;   // ~3.5 遊戲小時
        private const int MinFoodPerPawn = 5;          // 每個 pawn 維持最低儲量
        private int lastFoodCheckTick = -1;

        public LordJob_MissileCrewDuty() { }

        public LordJob_MissileCrewDuty(IntVec3 stagingPoint)
        {
            this.stagingPoint = stagingPoint;
        }

        // ──────────────── LordJob 圖 ────────────────

        public override StateGraph CreateGraph()
        {
            StateGraph graph = new StateGraph();

            // ① 防守狀態（預設）
            LordToil_MissileCrewDefend defend =
                new LordToil_MissileCrewDefend(stagingPoint, 40f);
            graph.StartingToil = defend;

            // ② 進攻狀態（1.6 中 LordToil_AssaultColony 不接受 IntVec3 參數）
            LordToil_AssaultColony assault = new LordToil_AssaultColony();
            graph.AddToil(assault);

            // ③ 防守 → 進攻：任一己方 Pawn 被傷害（>=50% 機率觸發）
            Transition toAssault = new Transition(defend, assault);
            toAssault.AddTrigger(new Trigger_PawnHarmed(0.5f, false, null));
            toAssault.AddPostAction(new TransitionAction_WakeAll());
            graph.AddTransition(toAssault);

            // ④ 進攻 → 防守：2500 tick 無傷害（自動重置）
            Transition toDefend = new Transition(assault, defend);
            toDefend.AddTrigger(new Trigger_TicksPassedWithoutHarm(2500));
            graph.AddTransition(toDefend);

            return graph;
        }

        // ──────────────── 食物空投 ────────────────

        public override void LordJobTick()
        {
            base.LordJobTick();

            int now = Find.TickManager.TicksGame;
            if (lastFoodCheckTick >= 0 && now - lastFoodCheckTick < FoodCheckInterval)
                return;

            lastFoodCheckTick = now;
            CheckAndAirdropFood();
        }

        private void CheckAndAirdropFood()
        {
            if (lord?.Map == null || lord.ownedPawns.NullOrEmpty()) return;
            Map map = lord.Map;
            Faction faction = lord.faction;

            // 計算地圖上屬於敵方（或非歸屬）的可食用物品數量
            int foodCount = 0;
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree))
            {
                if (t.Faction != null && t.Faction != faction) continue;
                if (!t.def.IsNutritionGivingIngestible) continue;
                foodCount += t.stackCount;
            }

            int needed = lord.ownedPawns.Count * MinFoodPerPawn;
            if (foodCount >= needed) return;

            AirdropFood(map, needed - foodCount);
        }

        private void AirdropFood(Map map, int shortfall)
        {
            int dropAmount = Mathf.Max(lord.ownedPawns.Count * MinFoodPerPawn, shortfall);
            dropAmount = Mathf.Min(dropAmount, ThingDefOf.MealSurvivalPack.stackLimit);

            IntVec3 dropCell = CellFinder.RandomClosewalkCellNear(stagingPoint, map, 10);
            if (!dropCell.IsValid || !dropCell.InBounds(map)) return;

            Thing food = ThingMaker.MakeThing(ThingDefOf.MealSurvivalPack);
            food.stackCount = dropAmount;

            DropPodUtility.DropThingsNear(dropCell, map, new List<Thing> { food },
                openDelay: 110, canInstaDropDuringInit: true, forbid: false);

            if (Prefs.DevMode)
                Log.Message($"[DMSE MissileBase] 食物空投 ×{dropAmount} 至 {dropCell}");
        }

        // ──────────────── 存讀 ────────────────

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref stagingPoint, "stagingPoint");
            Scribe_Values.Look(ref lastFoodCheckTick, "lastFoodCheckTick", -1);
        }
    }
}
