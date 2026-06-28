using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DMSE
{
    // ══════════════════════════════════════════════════════════════════════════
    // 填充規則（XML 資料類）
    //
    // 每條規則宣告「當敵方 Faction 的 TechLevel 在 [minTechLevel, maxTechLevel] 範圍內，
    // 從 candidates 隨機選取彈型並填充 fillCount 發」。
    //
    // 規則列表採「第一條匹配即生效」語意（First-Match Wins）：
    //   ‧ 在 XML 中將最嚴格的條件放前面（如 Archotech），最寬鬆的放後面（如 Industrial）。
    //   ‧ 不滿足任何規則時什麼都不填充。
    //
    // 未來擴充點：
    //   1. 在此類新增欄位（如 requiredFactionTag、customFilterClass）。
    //   2. 在 MissileAutoFillUtility.MatchesRule() 增加對應判定。
    //   3. 子類化此類（在 XML 以 <li Class="DMSE.MyCustomFillRule">）即可覆寫評估邏輯。
    // ══════════════════════════════════════════════════════════════════════════

    public class MissileAutoFillRule
    {
        // ──────────────── TechLevel 條件 ────────────────

        /// <summary>
        /// Faction TechLevel 下限（含）。<br/>
        /// 預設 <see cref="TechLevel.Undefined"/>（= 0），不限。
        /// </summary>
        public TechLevel minTechLevel = TechLevel.Undefined;

        /// <summary>
        /// Faction TechLevel 上限（含）。<br/>
        /// 預設 <see cref="TechLevel.Archotech"/>，不限。
        /// </summary>
        public TechLevel maxTechLevel = TechLevel.Archotech;

        // ──────────────── 候選彈型 ────────────────

        /// <summary>
        /// 可選填的彈型列表，每個槽位從中均勻隨機選一個。<br/>
        /// 留空（或不填）時，自動回退到建築的 <c>fixedStorageSettings.filter</c> 中所有允許的 ThingDef。
        /// </summary>
        public List<ThingDef> candidates = new List<ThingDef>();

        /// <summary>
        /// 若 <see cref="candidates"/> 非空，是否額外用 <c>fixedStorageSettings.filter</c> 過濾候選清單，
        /// 排除建築本身不接受的彈型。預設 true（安全守衛）。
        /// </summary>
        public bool mustMatchStorageFilter = true;

        // ──────────────── 填充數量 ────────────────

        /// <summary>
        /// 填充發數。<br/>
        /// ‧ -1（預設）= 填滿至建築容量上限。<br/>
        /// ‧ ≥ 0 = 填充固定數量（不超過容量上限）。
        /// </summary>
        public int fillCount = -1;

        // ──────────────── 擴充點：自定義額外條件 ────────────────

        /// <summary>
        /// 在此覆寫以增加任意額外的匹配條件，例如 Faction def tag、生物群系、
        /// WorldObject comp、季節等。<br/>
        /// 基礎實作永遠回傳 <c>true</c>（不增加限制）。
        /// </summary>
        public virtual bool ExtraConditionSatisfied(Faction faction, Map map) => true;
    }


    // ══════════════════════════════════════════════════════════════════════════
    // ThingDef 掛載擴充（XML DefModExtension）
    //
    // 範例 XML：
    //
    //   <modExtensions>
    //     <li Class="DMSE.MissileRackAutoFillExtension">
    //       <enemyFactionOnly>true</enemyFactionOnly>
    //       <fillRules>
    //         <!-- 規則 1：Ultra 以上 → 同時備有巡弋與攔截 -->
    //         <li>
    //           <minTechLevel>Ultra</minTechLevel>
    //           <candidates>
    //             <li>DMSE_CruiseMissile</li>
    //             <li>DMSE_InterceptorMissile</li>
    //           </candidates>
    //         </li>
    //         <!-- 規則 2：Industrial 以上（第一條未命中時） → 只有巡弋 -->
    //         <li>
    //           <minTechLevel>Industrial</minTechLevel>
    //           <candidates>
    //             <li>DMSE_CruiseMissile</li>
    //           </candidates>
    //         </li>
    //         <!-- TechLevel 低於 Industrial 的派系不符合任何規則 → 不填充 -->
    //       </fillRules>
    //     </li>
    //   </modExtensions>
    // ══════════════════════════════════════════════════════════════════════════

    public class MissileRackAutoFillExtension : DefModExtension
    {
        /// <summary>
        /// 僅在建築由非玩家派系擁有時才觸發填充。<br/>
        /// 預設 <c>true</c>；改為 <c>false</c> 可讓玩家自建的建築在初次生成時也自動填充（用於場景/劇本）。
        /// </summary>
        public bool enemyFactionOnly = true;

        /// <summary>
        /// 填充規則列表，按順序評估，第一條匹配的規則生效（First-Match Wins）。<br/>
        /// 子類化 <see cref="MissileAutoFillRule"/> 並在 XML 以 <c>Class="DMSE.MyRule"</c> 宣告可提供自定義評估邏輯。
        /// </summary>
        public List<MissileAutoFillRule> fillRules = new List<MissileAutoFillRule>();
    }


    // ══════════════════════════════════════════════════════════════════════════
    // 填充工具類
    //
    // 由 Building_MissileRack.SpawnSetup 在 respawningAfterLoad == false 時呼叫。
    // 完全無狀態；所有輸入透過參數傳入，方便測試與未來重用。
    // ══════════════════════════════════════════════════════════════════════════

    public static class MissileAutoFillUtility
    {
        /// <summary>
        /// 評估並填充 <paramref name="rack"/> 的內部容器。<br/>
        /// 以下情況靜默返回，不做任何事：<br/>
        /// ‧ <paramref name="respawningAfterLoad"/> == true（載入存檔，容器已被反序列化）。<br/>
        /// ‧ ThingDef 上未掛 <see cref="MissileRackAutoFillExtension"/>。<br/>
        /// ‧ 擴充設定 <c>enemyFactionOnly=true</c> 而建築屬於玩家。<br/>
        /// ‧ 沒有規則匹配當前 Faction 的 TechLevel。<br/>
        /// ‧ 解析出的可選彈型列表為空。
        /// </summary>
        public static void TryAutoFill(Building_MissileRack rack, bool respawningAfterLoad)
        {
            if (respawningAfterLoad) return;

            MissileRackAutoFillExtension ext = rack.def.GetModExtension<MissileRackAutoFillExtension>();
            if (ext == null) return;

            Faction faction = rack.Faction;
            if (ext.enemyFactionOnly && (faction == null || faction.IsPlayer)) return;

            Map map = rack.Map; // 此時已 Spawned，Map 不為 null

            MissileAutoFillRule rule = FindFirstMatchingRule(ext.fillRules, faction, map);
            if (rule == null) return;

            List<ThingDef> pool = ResolvePool(rule, rack);
            if (pool.NullOrEmpty()) return;

            int slots = rule.fillCount < 0
                ? rack.MaxStored
                : Math.Min(rule.fillCount, rack.MaxStored);

            for (int i = 0; i < slots; i++)
            {
                ThingDef chosen = pool.RandomElement();
                Thing missile = ThingMaker.MakeThing(chosen);
                if (!rack.GetDirectlyHeldThings().TryAdd(missile))
                {
                    missile.Destroy();
                    break; // 容器已滿（理論上不會到這裡，安全守衛）
                }
            }

            if (Prefs.DevMode)
                Log.Message($"[DMSE AutoFill] {rack.def.defName} @ {rack.Position} 填充 {slots} 發"
                    + $"（faction={faction?.Name ?? "null"}, techLevel={faction?.def?.techLevel.ToString() ?? "?"}）");
        }

        // ──────────────── 規則匹配 ────────────────

        /// <summary>
        /// 從規則列表找出第一條滿足 <paramref name="faction"/> 及地圖條件的規則（First-Match Wins）。<br/>
        /// 回傳 <c>null</c> 表示無規則匹配（不填充）。
        /// </summary>
        public static MissileAutoFillRule FindFirstMatchingRule(
            List<MissileAutoFillRule> rules, Faction faction, Map map)
        {
            if (rules.NullOrEmpty()) return null;

            TechLevel tech = faction?.def?.techLevel ?? TechLevel.Undefined;

            foreach (MissileAutoFillRule rule in rules)
            {
                if (!MatchesRule(rule, tech, faction, map)) continue;
                return rule;
            }
            return null;
        }

        /// <summary>
        /// 判定單條規則是否匹配。可被外部呼叫以做診斷或預覽。
        /// </summary>
        public static bool MatchesRule(MissileAutoFillRule rule, TechLevel tech, Faction faction, Map map)
        {
            // TechLevel 範圍
            if (tech < rule.minTechLevel) return false;
            if (tech > rule.maxTechLevel) return false;

            // 子類自定義額外條件
            if (!rule.ExtraConditionSatisfied(faction, map)) return false;

            return true;
        }

        // ──────────────── 候選池解析 ────────────────

        /// <summary>
        /// 從規則的 <see cref="MissileAutoFillRule.candidates"/> 解析可選彈型池。<br/>
        /// ‧ candidates 非空 → 以 candidates 為基礎，視 <see cref="MissileAutoFillRule.mustMatchStorageFilter"/> 過濾。<br/>
        /// ‧ candidates 為空  → 回退到建築 <c>fixedStorageSettings.filter</c> 允許的所有 ThingDef。
        /// </summary>
        public static List<ThingDef> ResolvePool(MissileAutoFillRule rule, Building_MissileRack rack)
        {
            ThingFilter storageFilter = rack.def.building?.fixedStorageSettings?.filter;

            if (!rule.candidates.NullOrEmpty())
            {
                if (!rule.mustMatchStorageFilter)
                    return rule.candidates;

                // 過濾掉建築本身不接受的彈型
                List<ThingDef> filtered = new List<ThingDef>(rule.candidates.Count);
                foreach (ThingDef d in rule.candidates)
                {
                    if (storageFilter == null || storageFilter.Allows(d))
                        filtered.Add(d);
                }
                return filtered;
            }

            // 回退：使用 fixedStorageSettings 中所有允許的 ThingDef
            if (storageFilter == null) return null;

            List<ThingDef> fallback = new List<ThingDef>();
            foreach (ThingDef d in storageFilter.AllowedThingDefs)
                fallback.Add(d);

            return fallback;
        }
    }
}
