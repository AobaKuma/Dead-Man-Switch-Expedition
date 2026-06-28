# DMSE 遠程作戰系統 — 應用流程與實作說明

本文整理兩套相互獨立但同屬「超視距作戰」的系統：

- **A. 超視距攔截系統（BVR）** — 偵測並攔截敵方來襲空投（drop pod / 空降）。
- **B. 導彈自定義裝配系統** — 玩家自組導彈、由小人裝配、發射打擊地圖外目標。

兩者皆以 **ThingComp** 組合到建築/物品上，達成「單一建築複合功能」。

---

## A. 超視距攔截系統（BVR）

### A-1. 角色：建築 × ThingComp

| 設計圖角色 | ThingComp | 掛載建築（範例） | 關鍵參數 |
|---|---|---|---|
| 搜索雷達 | `CompSearchRadar` | 相控陣雷達 `DMSE_PhaseArrayRadar` | `searchDistance`、`powerLevel`、`antiStealthLevel`、`ticksPerDistance` |
| 火控雷達 | `CompFireControlRadar` | 相控陣雷達（同棟，複合） | `maxTargets`（火力通道）、`guidanceAccuracyTime`、`maxRangeTicks`、`maxHitChance` |
| 導彈發射裝置 | `CompMissileLauncher` | SAM 發射器 `DMSE_Building_SAMLauncher` | `interceptorSkyfaller`、`reloadCooldownTicks`、`interceptorTravelTicks` |
| 末端攔截砲塔 | `CompTerminalCIWS` | Vulcan WingBack 等砲塔 | `terminalWindowTicks`、`shotsPerBurst`、`interceptChance`、`debrisChance` |

所有裝置繼承 `CompBVRDevice`，統一 `Active` 判定（通電／開關／未損壞／未被擊暈），並於 `PostSpawnSetup`/`PostDeSpawn` 自行向地圖中央管理器註冊。

### A-2. 中央管理器與資料模型

`MapComponent_BVRCombat`（每張地圖一個）持有：

- `List<BVRWave> Waves` — 每個波次含 `tickToImpact` 與一組 `BVRTarget`。
- `BVRTarget` — 持有被臨時收起的 incoming skyfaller、預測落點、隱身/距離等級、`midcourseEngagedUntil`（占用火力通道的計時）、唯一 `id`。
- 四個裝置註冊表（搜索／火控／發射器／CIWS）。

### A-3. 線性時間線（偵測 → 落地）

```
敵方空投生成
   │  Patch_MakeDropPodAt（postfix on SkyfallerMaker.SpawnSkyfaller）
   │   ├─ 非敵對 / 無運作中搜索雷達 → 照常落地（不介入）
   │   └─ 敵對 + 有搜索雷達 → DeSpawn，交給管理器
   ▼
RegisterIncoming：以搜索雷達算出「預警窗口」→ tickToImpact = now + 窗口
   │   窗口 = Σ(searchDistance × ticksPerDistance × powerLevel) × 目標距離等級 ÷ 目標數
   │   （反隱身 < 目標隱身值時，該雷達貢獻 ×0.4）
   ▼
每 tick：MapComponent_BVRCombat.MapComponentTick → 依 timeLeft 分階段
   │
   ├─ timeLeft > 末端窗口  →  中過程（MidcourseDefense）
   │     • 火力通道容量 = Σ 運作中火控雷達.maxTargets（共用池，限 timeLeft ≤ maxRangeTicks）
   │     • 對未占用通道的目標：選最佳火控雷達 + 一具就緒發射器
   │     • 命中率 = maxHitChance × clamp(可導引時間 / guidanceAccuracyTime)，反隱身不足再打折
   │     • CompMissileLauncher.FireInterceptor → 生成 InterceptProjectile（skyfaller），耗一枚彈、進冷卻
   │     • 攔截彈升空後火力通道繼續占用（midcourseEngagedUntil > now），直到結算
   │     • InterceptProjectile.LeaveMap：擲 hitChance
   │           成功 → RemoveTarget（乾淨消滅，【不留碎片】）
   │           落空 → 目標仍存活，lockUntil = -1 / midcourseEngagedUntil = -1，下一 tick 重新開始鎖定
   │           無論成敗 → midcourseEngagedUntil = -1，火力通道正式釋放
   │
   ├─ timeLeft ≤ 末端窗口  →  末端（TerminalDefense）
   │     • 末端窗口 = 所有 CIWS 中最大的 terminalWindowTicks（約 30 秒）
   │     • 每具 CIWS 冷卻就緒時對目標連射 shotsPerBurst 發（視覺彈）
   │     • 每發擲 interceptChance：擊落成功後再擲 debrisChance
   │           → 命中 + 過 debrisChance：SpawnDebris（殺死內部 pawn、生成屍體殘骸空投）【碎片只在此產生】
   │           → 命中但未過：乾淨消滅
   │
   └─ timeLeft ≤ 0  →  ResolveWave：未被攔截的目標真正落地（TryDrop incoming skyfaller），送出 signal
```

### A-4. 重點設計

- **碎片規則**：中過程攔截＝完全消滅、不落地、不留殘骸；末端攔截命中後依 `debrisChance` 機率生成殘骸。
- **火力通道**：以「共用池」計算（Σ maxTargets）。通道在「鎖定階段」（`lockUntil ≥ 0`）與「攔截彈在飛」（`midcourseEngagedUntil > now`）期間均持續占用——攔截彈命中或落空結算後（`InterceptProjectile.LeaveMap` 將 `midcourseEngagedUntil = -1`）才釋放，存讀檔安全（不需持久化 in-flight 引用）。
- **存檔**：波次與目標經 `ExposeData` 持久化；in-flight 的 `InterceptProjectile` 以 `targetId`（int）回連目標，避免引用非 `ILoadReferenceable` 物件。
- **陣營相對防禦**（非玩家專屬）：偵測與攔截以「防禦方」為基準。空投生成時由 `ResolveDefender` 找出「擁有運作中搜索雷達、且與空投內容物敵對」的陣營作為防禦方（玩家優先），整個波次（`BVRWave.defenderFaction`）只由該陣營的雷達/發射器/CIWS 參與。因此：
  - 敵方空降到玩家基地 → 玩家防空攔截（原行為）。
  - 玩家空投突擊敵方基地（敵方擁有這些建築）→ **敵方防空攔截玩家的空投**。
  - 玩家自己的空投落到自家地圖 → 不被自家雷達攔截（非敵對）。
  - 玩家進攻時，攔截提示（`Alerts_Pods`）只對玩家防禦的波次顯示，敵方防空不會誤報。

---

## B. 導彈自定義裝配系統

### B-1. Def 架構

- `MissileBodyDef`（彈體）：基礎參數 + 開放的裝配槽位（`slots`）+ 對應落點 skyfaller。
  - 數值：`baseExplosionRadius`、`baseDamageDef`、`baseDamageAmount`、`baseScatter`、`baseWorldSpeedFactor`、`baseRange`
  - 燃料/推進：`baseFuel`、`baseSpecificImpulse`、`rangePerFuelImpulse`
  - 裝配：`assembleWorkAmount`、`refundFraction`
- `MissilePartDef`（部件）：屬於某 `MissilePartCategory`，提供數值修正與成本。
  - 類別：`Warhead`／`Guidance`／`Propulsion`／`Payload`
  - 修正：`explosionRadiusOffset`、`damageDefOverride`、`damageAmountOffset`、`scatterOffset`、`worldSpeedFactorOffset`、`specificImpulseOffset`、`fuelOffset`、`rangeOffset`、酬載 `payload*`
  - 限制：`compatibleBodies`、`researchPrerequisites`、`costList`

### B-2. 設定資料模型

- `MissileConfig`（彈體 + 每槽位最多一部件）提供「有效數值」計算：
  - `ExplosionRadius`／`DamageDef`／`DamageAmount`／`Scatter`／`WorldSpeedFactor`
  - `Fuel = baseFuel + Σ fuelOffset`
  - `SpecificImpulse = baseSpecificImpulse + Σ specificImpulseOffset`
  - **`Range ≈ Fuel × SpecificImpulse × rangePerFuelImpulse + baseRange + Σ rangeOffset`**（0 = 不限）
- `CompMissileConfig`（掛在導彈物品；發射器也掛一份作「已裝填設定」）：
  - `config` = 已套用（影響發射效果）
  - `pending` = 玩家在 ITab 設定的目標
  - `delivered` = 已搬運到此導彈、尚未消耗的資源
  - `NeedsAssembly` = config 與 pending 不同

### B-3. 裝配流程（小人搬運）

```
玩家選取導彈物品 → ITab「裝配」分頁
   • 選彈體、各槽位部件（FloatMenu 顯示成本、研究未解鎖灰顯、不相容隱藏）
   • 編輯寫入 pending；面板即時顯示有效數值（含燃料/比衝/射程）與所需資源
   • 編輯後若 pending 回到與 config 相同 → RefundDeliveredIfIdle 退回已搬料
   ▼
WorkGiver_AssembleMissile（Crafting 工種，scanThings；只掃 item 類別）
   • NeedsAssembly 且尚缺資源 → 發 JobDriver_DeliverMissileResource
   │     找最近可預約資源 → 搬到導彈 → 存入 delivered（destroy 搬運物）
   │     不足一趟則 WorkGiver 反覆補發，直到備齊
   • 資源備齊 → 發 JobDriver_AssembleMissile
         於導彈處施工 assembleWorkAmount → ApplyAssembly：
            消耗需求資源；【部分退還】移除部件（refundFraction）＋多搬餘料；config = pending
```

成本 = 新增部件（pending\config）的 `costList` 總和；退費 = 移除部件（config\pending）成本 × `refundFraction`。基礎件（如標準馬達、慣性導引）無 `costList` → 免料、僅需工時。

### B-4. 發射流程（打擊地圖外）

```
導彈物品搬入 Scorer（CompRefuelable，容量 1）
   │  Patch_Refuel_CarryConfig（prefix on CompRefuelable.Refuel(List<Thing>)）
   │     把被裝填導彈的 config 複製到發射器的 CompMissileConfig（同步 pending、清 delivered）
   ▼
CompScorer 發射 Gizmo → 世界地圖選靶
   • 讀已裝填 Range：超出射程的目標被拒絕並提示；滑鼠標籤顯示「距離 / 射程」
   ▼
Launch：生成 ScorerProjectile（離場 skyfaller）+ ScorerProjectile_WorldObject（攜帶 config 副本）
   • 世界旅行速度 × config.WorldSpeedFactor
   ▼
Arrived（抵達目標 tile）：玩家在當地地圖選落點
   • 生成 MissileIncoming（incoming skyfaller），把 config 交給它
   ▼
MissileIncoming.Impact（依設定動態結算，取代固定爆炸欄位）
   • 命中偏移：依 Scatter 取附近落點
   • 主彈頭：DoExplosion(半徑=ExplosionRadius, 型別=DamageDef, 量=DamageAmount)
   • 特殊酬載：次級爆炸（payloadDamageDef/半徑）或落點生成物
```

### B-5. 射程模型（燃料 × 比衝）

射程由「彈體燃料量」與「推進比衝（效率）」相乘決定。範例：彈體 100 燃料 × 比衝 3 × 0.1 = **30 格**；換增程馬達（比衝 +2 → 5）→ **50 格**。在 ITab 與發射選靶皆即時可見並強制生效。

---

## C. 兩系統的關係

- BVR 是**防禦**（攔敵方來襲空投）；導彈裝配/Scorer 是**進攻**（打地圖外）。兩者資料與流程獨立。
- `CompMissileLauncher`（BVR 的 SAM 發射器）與 `CompMissileConfig`（裝配）是不同 Comp，互不影響。
- 發射器建築同時掛 `CompMissileConfig` 只為「接收已裝填設定」，**不會**被裝配 WorkGiver 當成可裝配物（已限定 item 類別 + 同步 pending）。

---

## D. 檔案清單

**BVR（`.source/DMSE/SkyFallerTurret/`）**
`BVR/CompBVRDevice`、`BVR/MapComponent_BVRCombat`、`BVR/BVRWave`(含 BVRTarget)、`BVR/BVRTargetProps`、`BVR/CompSearchRadar`、`BVR/CompFireControlRadar`、`BVR/CompMissileLauncher`、`BVR/CompTerminalCIWS`、`InterceptProjectile`、`Patch_MakeDropPodAt`(含 BVRDetection)、`Alerts_Pods`、`Patch_QuestPart_Bossgroup`

**導彈裝配（`.source/DMSE/Scorer/Assembly/`）**
`MissileDefs`(列舉+Body+Part)、`MissileConfig`、`CompMissileConfig`、`ITab_MissileAssembly`、`MissileIncoming`、`Patch_Refuel_CarryConfig`、`MissileAssemblyUtility`(+JobDefOf)、`WorkGiver_AssembleMissile`、`JobDriver_DeliverMissileResource`、`JobDriver_AssembleMissile`；既有 `CompScorer`、`ScorerProjectile_WorldObject`（已改）

**Defs**
`ThingDefs_Buildings/DMSE_Misc.xml`（搜索+火控雷達）、`DMSE_Security.xml`（CIWS 砲塔）、`DMSE_Security_Missile.xml`（Scorer、SAM 發射器、攔截彈、落點導彈）、`ThingDefs_Items/DMSE_Manufactured.xml`（導彈物品 + ITab）、`Missile/DMSE_MissileAssembly.xml`（彈體+部件）、`Missile/DMSE_MissileAssembly_Jobs.xml`（JobDef×2 + WorkGiverDef）

---

## E. 本次審查發現與修正

**Bug：發射器被誤判為可裝配物。** 裝配 WorkGiver 原本掃描所有掛 `CompMissileConfig` 的 ThingDef（含 Scorer 建築）；裝填導彈後 patch 只更新建築的 `config` 而未更新 `pending`，使建築 `NeedsAssembly = true`，小人會試圖「裝配發射器」。
**修正（雙保險）**：(1) `MissileAssemblyUtility.MissileDefs` 只取 `ThingCategory.Item`；(2) `Patch_Refuel_CarryConfig` 同步設定 `config` 與 `pending` 並清空 `delivered`。

---

## F. 測試檢查清單

1. **編譯**：Visual Studio 開 `DMSE.csproj` 建置（沙箱無工具鏈）。
2. **BVR**：建搜索雷達→敵方空投出現預警窗口→火控分配火力通道→SAM 中過程攔截（無碎片）→末端 CIWS（命中後機率碎片）→未攔截者落地。無雷達時照常落地。
3. **裝配**：ITB 改部件→紅字顯示缺料→小人搬料→施工→config 生效；更換/移除部件部分退還；資源不足無法指派。
4. **發射**：裝填帶設定→世界選靶受射程限制→落點依彈頭/酬載/精度結算；增程件提升射程與速度。
5. **存讀檔**：BVR 波次/目標、導彈 config/pending/delivered、發射器已裝填設定皆正確還原。
6. **回歸**：發射器不再被當成裝配目標。
