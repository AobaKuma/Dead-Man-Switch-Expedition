# DMSE 遠程作戰系統 — 應用流程與實作說明

> 最後校對：2026-07-06（依 `.source/DMSE` 現行程式碼全面覆核）

本文整理三套同屬「超視距作戰」的系統：

- **A. 超視距攔截系統（BVR）** — 偵測並攔截敵方來襲空投（drop pod）**與來襲導彈**（`MissileIncoming`）。
- **B. 導彈自定義裝配系統** — 玩家自組導彈、由小人裝配、發射打擊地圖外目標。
- **C. 超視距火砲系統（Artillery）** — 曲射砲擊：砲彈經世界航跡抵達後，以 `Projectile` 從地圖邊緣飛入落點。

皆以 **ThingComp** 組合到建築/物品上，達成「單一建築複合功能」。系統間已有實質交互：SAM 發射器以裝配系統產出的實體攔截彈為彈藥，戰鬥部（`WarheadEffect`）直接影響攔截判定；進攻方導彈可掛反輻射導引頭反制防禦方雷達；火砲共用 `CompBVRDevice` 的可用性判定。

---

## A. 超視距攔截系統（BVR）

### A-1. 角色：建築 × ThingComp

| 設計圖角色 | ThingComp | 關鍵參數 |
|---|---|---|
| 搜索雷達 | `CompSearchRadar` | `searchDistance`、`powerLevel`（**超線性**，指數 `PowerWindowExponent=2`）、`antiStealthLevel`、`ticksPerDistance`、`immuneToAntiRadiationSeeker` |
| 火控雷達 | `CompFireControlRadar` | `maxTargets`（火力通道）、`powerLevel`（隱身對衝）、`guidanceAccuracyTime`、`maxRangeTicks`、`maxHitChance`、`stealthMissFactor`、`lockOnTicks`、`immuneToAntiRadiationSeeker` |
| 導彈發射裝置（SAM） | `CompMissileLauncher` | `interceptorSkyfaller`、`reloadCooldownTicks`、`interceptorTravelTicks`；**彈藥為實體導彈 Thing**（見 A-5） |
| 末端攔截砲塔（CIWS） | `CompTerminalCIWS` | `terminalWindowTicks`、`cooldownTicks`、`shotsPerBurst`、`ticksBetweenShots`、`interceptChance`（**整輪連射只判定一次**）、`debrisChance`、`fuelPerShot`、瞄準視覺（`aimLeadCells`/`aimSweepCells`/`aimHeightFar`/`aimHeightNear`）、`interceptSound` |

所有裝置繼承 `CompBVRDevice`，統一 `Active` 判定（已生成／未損壞（Breakdownable）／開關開啟（Flickable）／通電（PowerTrader）／未被擊暈（Stunnable）），並於 `PostSpawnSetup`/`PostDeSpawn` 自行向地圖中央管理器註冊。`CompMissileLauncher` 額外要求**有彈**才算 Active。

**純視覺/UI 配件**（不參與判定）：

- `CompTurretFacingRenderer` — 通用砲塔頭渲染：讀取本陣營最緊急目標平滑轉向，無目標時閒置擺動；可同時掛於雷達與發射架。
- `MapComponent_InterceptEffects` — 攔截成功時在目標上方**地圖外高空**繪製閃光 + 音效（中過程與末端參數不同）。
- `WeatherEvent_MissileFlash` — 導彈落地時的全地圖爆炸閃光（強度∝爆炸半徑）。
- `Alerts_BVRStatus` — 常駐警報：玩家持有任一 BVR 裝置即顯示雷達清單、火力通道占用（`CountEngaged()/總通道`）。
- `Alerts_Pods` — 防禦方為玩家的來襲波次警報。
- `Alerts_PlayerMissiles` — 玩家為**攻擊方**、導彈被敵方 BVR 追蹤時顯示；左右鍵循環跳轉相機至各彈落點。

### A-2. 中央管理器與資料模型

`MapComponent_BVRCombat`（每張地圖一個）持有：

- `List<BVRWave> Waves` — 波次：`tickToImpact`、`targets`、`defenderFaction`（負責攔截的防禦方）、`lord`、`signal`。
- `BVRTarget` — 以 `ThingOwner<Skyfaller>` 收起 incoming skyfaller；`position`（預測落點）、`stealthLevel`、`speed`（來自 def 的 `BVRTargetProps`）、`lockUntil`（火控鎖定計時）、`midcourseEngagedUntil`（攔截彈在飛計時）、唯一 `id`（`nextTargetId` 遞增）。
- 四個裝置註冊表（搜索／火控／發射器／CIWS，`HashSet`）。
- `BVRTargetProps`（DefModExtension，掛在目標 skyfaller def）：`distanceLevel`（越高越早被偵測）、`stealthLevel`、`speed`（越快越難攔截）。無擴充時用 `Default`。
- `BVRTuning` — 集中平衡常數：`PowerWindowExponent=2`、`AntiStealthBonusPerLevel=0.5`、`StealthDetectFactor=0.4`、`MergeWindowTicks=600`、`WindowPenaltyPerExtraTarget=60`。

### A-3. 目標進入系統的三條路徑

1. **敵對空投**：`Patch_MakeDropPodAt`（postfix on `SkyfallerMaker.SpawnSkyfaller`）→ `BVRDetection.GetDropFaction`（取內容物 pawn 陣營，含 `ActiveTransporter` 內）→ `ResolveDefender` 找防禦方 → 有則 `DeSpawn` + `RegisterIncoming`。攔截彈自身（`InterceptProjectile`）豁免。
2. **來襲導彈**：`MissileIncoming` **首次 Tick 時自行註冊**（`MissileBVRUtility.TryRegister`）——因為 spawn-time patch 執行時呼叫端尚未設好 `attacker`/`config`，`Patch_SpawnMissileIncoming` 已**廢棄留空**。`bvrHandled` 旗標避免重投時重複登記。導彈的世界航跡由 `WorldObject_IncomingMissile` 承載（任何陣營可發射，抵達時依導引頭自動選格生成 `MissileIncoming`，不開玩家選靶）。
3. **測試**：`RegisterSalvo`（Debug）直接生成含 N 枚導彈的單一波次，繞過世界航跡。

### A-4. 預警窗口與波次合併

```
window = Σ(各運作中防禦方搜索雷達的貢獻) × distanceLevel ÷ targetCount
單雷達貢獻 = searchDistance × ticksPerDistance × powerLevel^2 × 隱身係數
隱身係數  = antiStealthLevel <  stealthLevel → 0.4（StealthDetectFactor）
            antiStealthLevel >= stealthLevel → 1 + 0.5 × (antiStealth − stealth)
```

回傳 -1（防禦方無運作中搜索雷達）則不攔截、照常落地。`tickToImpact = now + window`。

**波次合併**：同防禦方、預測抵達相差 ≤ 600 ticks（`MergeWindowTicks`）的目標併入同一波次；合併後取最早抵達 tick，且每多一個目標再縮短 60 ticks（`WindowPenaltyPerExtraTarget`，下限 now+1）。

### A-5. 線性時間線（偵測 → 落地）

```
每 tick：MapComponent_BVRCombat.MapComponentTick → 依 timeLeft 分階段
   │
   ├─ timeLeft > 末端窗口  →  中過程（MidcourseDefense）
   │     • 火力通道容量 = Σ 防禦方運作中火控雷達.maxTargets（共用池，限 timeLeft ≤ maxRangeTicks）
   │     • 占用數 CountEngaged = 全部波次中 lockUntil ≥ 0 或攔截彈在飛的目標數
   │     ① 鎖定：未鎖定新目標，若占用 < 容量 → 選最佳火控雷達開始鎖定
   │           lockUntil = now + lockOnTicks × (1 + 0.5×隱身差 + max(0, speed−15)/60)
   │     ② 發射：鎖定完成（lockUntil ≤ now）→ 最佳火控雷達 + 最閒置的就緒發射器
   │           命中率 = maxHitChance × clamp01(可導引時間 / guidanceAccuracyTime)
   │                    × 速度係數（>15 每單位 −1.2%，下限 0.2）
   │                    × stealthMissFactor^(stealthLevel − powerLevel)₊
   │                    + 攔截彈戰鬥部加成（連續桿 InterceptBonus = N×0.05，clamp01）
   │           CompMissileLauncher.FireInterceptor → 生成 InterceptProjectile（skyfaller）
   │                    消耗一枚實體攔截彈（從 Building_MissileRack 容器取出銷毀）、進冷卻
   │           空爆戰鬥部：額外 floor(N/5) 次獨立擲骰攔截同波次其他目標（不耗彈、不占通道）
   │           midcourseEngagedUntil = now + interceptorTravelTicks（通道續占）、lockUntil = −1
   │     ③ 結算：InterceptProjectile.LeaveMap → 釋放通道（midcourseEngagedUntil = −1）→ 擲 hitChance
   │           成功 → 地圖外攔截閃光 + RemoveTarget（乾淨消滅，【不留碎片】）
   │           落空 → 目標存活，下一 tick 重新排隊鎖定
   │
   ├─ timeLeft ≤ 末端窗口  →  末端（TerminalDefense）
   │     • 末端窗口 = 防禦方所有 CIWS 中最大的 terminalWindowTicks（預設 1800 ≈ 30 秒）
   │     • 每具 CIWS 獨立節奏：冷卻完＋有彈料 → 對波次首目標開始一輪連射
   │           整輪鎖定同一目標；每發間隔 ticksBetweenShots、耗 fuelPerShot（CompRefuelable，
   │           例如裝甲砲管耐久；無 Refuelable 則無限）；目標消失或斷料 → 中止本輪、不判定
   │           視覺：砲塔轉向天空格（提前量 + 線性掃射 + 依迫近度下移瞄準高度）、槍口閃光、曳光彈
   │     • 連射全部打完後【整輪只擲一次】interceptChance：
   │           命中 → 末端攔截閃光；再擲 debrisChance：
   │                 過 → SpawnDebris（殺死內部 pawn、生成屍體殘骸空投）【碎片只在此產生】
   │                      ※ 無 pawn 的目標（如導彈）永遠乾淨清除，無碎片
   │                 未過 → 乾淨消滅
   │     • 判定後進入 cooldownTicks
   │
   └─ timeLeft ≤ 0  →  ResolveWave：未被攔截的目標真正落地（TryDrop incoming skyfaller），
         落地 pawn 加入 wave.lord，發送 wave.signal
```

### A-6. SAM 彈藥：實體攔截彈

發射器 parent 為 `Building_MissileRack`（`IThingHolder` 容器建築），攔截彈以**實體導彈 Thing** 逐枚存放（與 Scorer 的 `CompMissileRailLauncher` 同機制，取代舊 CompRefuelable 抽象燃料）：

- 裝填：`WorkGiver_LoadMissileLauncher` + `JobDriver_LoadMissileLauncher`（小人搬彈上架），`MissileAutoFillUtility` 輔助；`CompMissileRackRenderer` 依建築朝向渲染在架彈體。
- 發射時取出容器首枚並銷毀；`GetLoadedMissileConfig()` 讀取該彈的 `CompMissileConfig.config`，讓**裝配系統的戰鬥部直接影響攔截**（連續桿→命中率加成；空爆→額外攔截擲骰，皆以酬載容量 N 縮放）。

### A-7. 重點設計

- **碎片規則**：中過程攔截＝完全消滅、不落地、不留殘骸；末端攔截命中後依 `debrisChance` 生成殘骸，且僅限含 pawn 的目標。
- **火力通道**：共用池（Σ maxTargets）。通道在「鎖定階段」（`lockUntil ≥ 0`）與「攔截彈在飛」（`midcourseEngagedUntil > now`）期間均占用，結算後才釋放；存讀檔安全（in-flight 的 `InterceptProjectile` 以 `targetId`(int) 回連，不持久化引用）。
- **隱身雙層對衝**：搜索段對衝 `antiStealthLevel`（影響預警窗口）；火控段對衝 `powerLevel`（影響命中率與鎖定時間）。目標 `speed` 只影響火控段。
- **陣營相對防禦**（非玩家專屬）：以 `ResolveDefender` 找「擁有運作中搜索雷達、且與來襲方敵對」的陣營作防禦方（玩家優先），整波次只由該陣營裝置參與。玩家空投/導彈突擊敵方基地時**會被敵方防空攔截**（`Alerts_PlayerMissiles` 追蹤己方被攔截彈藥）；自家空投不被自家雷達攔截。
- **電子對抗閉環**：進攻導彈可裝反輻射導引頭（`GuidanceType_AntiRadiation`）——自動鎖定地圖上運作中、功率最高的雷達建築；雷達可設 `immuneToAntiRadiationSeeker = true`（如純被動聲學預警裝置）豁免。攻防雙系統由此互為反制。
- **存檔**：波次/目標經 `ExposeData` 深度持久化；CIWS 連射狀態（`burstShotsLeft`/`burstTargetId` 等）與發射器冷卻亦持久化。

---

## B. 導彈自定義裝配系統

> 進攻側的四階平台分級（巡飛彈/巡航/彈道/戰略）與發射器相容性見 `docs/導彈四階分級架構.md`，此處只記與裝配/發射核心相關者。

### B-1. Def 架構

- `MissileBodyDef`（彈體）：基礎參數 + 裝配槽位（`slots`）+ 對應落點 skyfaller。
  - 數值：`baseExplosionRadius`、`baseDamageDef`、`baseDamageAmount`、`baseScatter`、`baseWorldSpeedFactor`、`baseRange`
  - 燃料/推進：`baseFuel`、`baseSpecificImpulse`、`rangePerFuelImpulse`
  - 裝配：`assembleWorkAmount`、`refundFraction`
- `MissilePartDef`（部件）：屬於某 `MissilePartCategory`（`Warhead`／`Guidance`／`Propulsion`／`Payload`），提供數值修正與成本。
  - 數值修正：`explosionRadiusOffset`、`damageDefOverride`、`damageAmountOffset`、`scatterOffset`、`worldSpeedFactorOffset`、`specificImpulseOffset`、`fuelOffset`、`rangeOffset`
  - **行為類**（XML `Class=` 指定子類）：`warheadEffect`（`WarheadEffect_*`：連續桿/空爆/鎢芯/燃燒等，含 BVR 攔截加成介面 `InterceptBonus`/`ExtraInterceptRolls`）、`payloadEffect`（`PayloadEffect_*`）、`guidanceType`（`GuidanceType_*`：慣性/高能反應/熱源/反輻射/電視，決定敵方發射時的自動落點）
  - 限制：`compatibleBodies`、`researchPrerequisites`、`costList`

### B-2. 設定資料模型

- `MissileConfig`（彈體 + 每槽位最多一部件）提供有效數值：`ExplosionRadius`／`DamageDef`／`DamageAmount`／`Scatter`／`WorldSpeedFactor`／`PayloadCapacity`(N)
  - `Fuel = baseFuel + Σ fuelOffset`；`SpecificImpulse = baseSpecificImpulse + Σ specificImpulseOffset`
  - **`Range ≈ Fuel × SpecificImpulse × rangePerFuelImpulse + baseRange + Σ rangeOffset`**（0 = 不限）
- `CompMissileConfig`（掛在導彈物品；發射器也掛一份作「已裝填設定」）：`config`（已套用）、`pending`（ITab 目標設定）、`delivered`（已搬未耗資源）、`NeedsAssembly = config ≠ pending`

### B-3. 裝配流程（小人搬運）

```
玩家選取導彈物品 → ITab「裝配」分頁
   • 選彈體、各槽位部件（FloatMenu 顯示成本、研究未解鎖灰顯、不相容隱藏）
   • 編輯寫入 pending；面板即時顯示有效數值與所需資源
   • pending 改回與 config 相同 → RefundDeliveredIfIdle 退回已搬料
   ▼
WorkGiver_AssembleMissile（Crafting 工種；只掃 item 類別）
   • NeedsAssembly 且缺料 → JobDriver_DeliverMissileResource（反覆補發直到備齊）
   • 備齊 → JobDriver_AssembleMissile：施工 assembleWorkAmount → ApplyAssembly
        消耗需求資源；移除部件按 refundFraction 部分退還；config = pending
```

### B-4. 發射流程（打擊地圖外）

發射平台三型（詳見四階分級文件）：

- `CompScorer` — CompRefuelable 裝填（容量 1），`Patch_Refuel_CarryConfig` 把導彈 config 複製到發射器（同步 pending、清 delivered）。
- `CompMissileRailLauncher` — 發射軌：彈藥為容器中實體導彈（`Building_MissileRack`），發射時取實體。
- `CompBallisticLauncher` — 彈道發射井。

共同流程：世界選靶（受 `Range` 強制限制）→ 離場 skyfaller + `ScorerProjectile_WorldObject`／`WorldObject_IncomingMissile`（攜 config、旅速 × `WorldSpeedFactor`）→ 抵達：玩家發射開當地選落點、敵方發射由 `guidanceType` 自動選格 → 生成 `MissileIncoming` →（先過目標地圖 BVR 攔截，見 A-3）→ `Impact` 動態結算：Scatter 偏移 → 主彈體爆炸 → `warheadEffect.Apply` → `payloadEffect.Apply`（或舊式 payload 數值欄位後備）→ 爆炸閃光（`WeatherEvent_MissileFlash`）。

### B-5. 射程模型（燃料 × 比衝）

射程由「彈體燃料量 × 推進比衝」決定。範例：100 燃料 × 比衝 3 × 0.1 = **30 格**；換增程馬達（比衝 +2）→ **50 格**。ITab 與發射選靶皆即時可見並強制生效。

---

## C. 超視距火砲系統（Artillery，`.source/DMSE/Artillery/`）

> 後續擴展規劃（齊射指揮器/反砲兵雷達/ABM 攔截砲/目標指示器/干擾機）見 `docs/artillery-expansion-plan.md`；**該五項目前均未實作**，本節只記已存在的程式碼。

### C-1. 架構角色

| 角色 | 類別 | 職責 |
|---|---|---|
| 火砲建築核心 | `CompArtilleryStrike`（**繼承 `CompBVRDevice`**） | Gizmo 選靶、冷卻、建立世界航跡；共用電力/損壞/開關的 `Active` 判定，但**不向 BVR 註冊表登記** |
| 世界航跡 | `WorldObject_ArtilleryStrike` | 世界地圖 Slerp 飛行；抵達時確保目標地圖載入（`GetOrGenerateMap`）、排程齊射、自毀 |
| 地圖排程器 | `MapComponent_ArtilleryStrikes` | 持有 `List<ArtilleryShellRequest>`（Deep 序列化），每 tick 輪詢 `fireTick` 到時者生成砲彈 |
| 排程資料 | `ArtilleryShellRequest` | `fireTick`、`targetCell`、`entryEdgeCell`、`projectileDef`、`attackerFaction`、`scatterRadius` |
| 靜態工具 | `ArtilleryStrikeUtility` | `CalcEntryEdgeCell`（邊緣格計算）、`QueueSalvo`（齊射排程）、`SpawnShell`（生成 Projectile） |

`CompProperties_ArtilleryStrike` 關鍵參數：`departureSkyfaller`（重用 `ScorerProjectile`）、`worldObjectDef`、`projectileDef`（需繼承 `Projectile_Explosive`）、`cooldownTicks`（預設 2400）、`scatterRadius`、`salvoCount`、`salvoIntervalTicks`、`minRange`（近距離禁射）。

### C-2. 線性時間線（選靶 → 落彈）

```
玩家點擊 Gizmo → 本地地圖選靶（Targeter）
   │   ValidateTarget：格內界 + 距離 ≥ minRange
   ▼
CompArtilleryStrike.Launch
   │   • 生成離場 ScorerProjectile skyfaller（朝目標側 4 向離場）
   │   • 建立 WorldObject_ArtilleryStrike（targetCell/彈藥 def/齊射參數/攻擊方）
   │     destinationTile = 自身地圖 tile（現行為同地圖曲射；資料結構已支援跨 tile）
   │   • 進入 cooldownTicks
   ▼
WorldObject_ArtilleryStrike：世界 Slerp 飛行
   │   旅速 ∝ projectileDef.projectile.speed
   ▼
Arrived：GetOrGenerateMap → ArtilleryStrikeUtility.QueueSalvo → 鏡頭跳轉 → WorldObject 自毀
   │   • CalcEntryEdgeCell：目標格相對地圖中心偏移，x/z 較大者決定進入邊
   │     （E/W 或 N/S），取邊緣上與目標投影最接近的格 —— 呼應「從目標側落下」
   │   • 依 salvoIntervalTicks 錯開，逐發推入 MapComponent_ArtilleryStrikes 佇列
   ▼
每 tick：MapComponent_ArtilleryStrikes 輪詢 → 到時 SpawnShell
   │   • 散佈：targetCell + Rand.InsideUnitCircle × scatterRadius（生成時定格）
   │   • 生成 Projectile 從邊緣格 Launch 至散佈後落點
   │     hitFlags = IntendedTarget | NonTargetPawns，attackerFaction 歸因
   ▼
Projectile_Explosive 命中結算（原版彈道/爆炸邏輯）
```

### C-3. 重點設計

- **落彈是 `Projectile` 而非 Skyfaller**：與導彈（`MissileIncoming` skyfaller 直落）本質不同——砲彈從邊緣格水平飛入，走原版投射物邏輯，因此**現行 BVR 系統不會攔截砲彈**（BVR 只攔 skyfaller）。擴展規劃中的 `CompABMInterceptor` 即為此缺口而設。
- **與 Scorer 導彈鏈的差異**：抵達時不開玩家選靶（直接用 `targetCell`）；重用 `ScorerProjectile` 作離場視覺。
- **存檔安全**：在途齊射由 `ArtilleryShellRequest` 全值序列化；世界航跡的 `PlanetTile` 以 int 分別序列化（同 `ScorerProjectile_WorldObject` 做法）。

---

## D. 系統間的關係

- BVR 是**防禦**、裝配/發射與火砲是**進攻**，但已非完全獨立：
  1. SAM 發射器的彈藥是裝配系統產出的實體攔截彈，戰鬥部效果（連續桿/空爆）直接參與攔截判定（A-5/A-6）。
  2. 進攻導彈落地前先過目標地圖的 BVR（`MissileIncoming.Tick` → `MissileBVRUtility`），玩家攻擊敵方雷達基地時己方導彈也會被攔截。
  3. 反輻射導引頭 vs 雷達 `immuneToAntiRadiationSeeker` 構成電子對抗閉環。
  4. 火砲的 `CompArtilleryStrike` 繼承 `CompBVRDevice` 共用可用性判定，但不進 BVR 註冊表、不參與攔截判定；砲彈（`Projectile`）現階段**無法被 BVR 攔截**（見 C-3，缺口留給規劃中的 ABM 攔截砲）。
- `CompMissileLauncher`（BVR SAM）與 `CompMissileConfig`（裝配）仍是不同 Comp；發射器建築掛 `CompMissileConfig` 只為接收已裝填設定，不會被裝配 WorkGiver 當成可裝配物（限定 item 類別 + patch 同步 pending）。

---

## E. 檔案清單

**BVR 核心（`.source/DMSE/SkyFallerTurret/BVR/`）**
`CompBVRDevice`、`MapComponent_BVRCombat`（含 `BVRTuning`）、`BVRWave`（含 `BVRTarget`）、`BVRTargetProps`、`CompSearchRadar`、`CompFireControlRadar`、`CompMissileLauncher`、`CompTerminalCIWS`、`CompTurretFacingRenderer`、`MapComponent_InterceptEffects`、`WeatherEvent_MissileFlash`

**BVR 周邊（`.source/DMSE/SkyFallerTurret/`）**
`InterceptProjectile`、`Patch_MakeDropPodAt`（含 `BVRDetection`）、`Alerts_BVRStatus`、`Alerts_Pods`、`Alerts_PlayerMissiles`、`Patch_QuestPart_Bossgroup`

**BVR 銜接（`.source/DMSE/Scorer/Assembly/`）**
`MissileBVRUtility`、`MissileIncoming`（`attacker`/`bvrHandled`/首 tick 自註冊）、`WorldObject_IncomingMissile`、`IncomingMissileUtility`、`Patch_SpawnMissileIncoming`（**已廢棄留空**，被首 tick 自註冊取代）

**導彈裝配/發射（`.source/DMSE/Scorer/`）**
`Assembly/MissileDefs`、`Assembly/MissileConfig`、`Assembly/CompMissileConfig`、`Assembly/WarheadEffect`、`Assembly/PayloadEffect`、`Assembly/GuidanceType`、`Assembly/MissileLauncherExtension`、`Assembly/ITab_MissileAssembly`、`Assembly/ITab_MissileInventory`、`Assembly/MissileAssemblyUtility`、`Assembly/WorkGiver_AssembleMissile`、`Assembly/JobDriver_DeliverMissileResource`、`Assembly/JobDriver_AssembleMissile`、`Assembly/WorkGiver_LoadMissileLauncher`、`Assembly/JobDriver_LoadMissileLauncher`、`Assembly/Patch_Refuel_CarryConfig`、`CompScorer`、`CompMissileRailLauncher`、`Ballistic/CompBallisticLauncher`、`ScorerProjectile`、`ScorerProjectile_WorldObject`、`GameComponent_MissileEngage`；容器建築 `Building_MissileRack` + `CompMissileRackRenderer`（`.source/DMSE/`）

**超視距火砲（`.source/DMSE/Artillery/`）**
`CompArtilleryStrike`、`WorldObject_ArtilleryStrike`、`MapComponent_ArtilleryStrikes`（含 `ArtilleryShellRequest`）、`ArtilleryStrikeUtility`

**Defs**
`ThingDefs_Buildings/DMSE_Misc.xml`（雷達）、`DMSE_Security.xml`（CIWS）、`DMSE_Security_Missile.xml`（發射器/攔截彈/落點導彈）、`DMSE_LauncherBases.xml`（發射平台建造限制）、`DMSE_ArtillerySystem.xml`（火砲建築）、`ThingDefs_Items/DMSE_Manufactured.xml`（導彈物品 + ITab）、`Missile/DMSE_MissileAssembly.xml`（彈體+部件）、`Missile/DMSE_MissileAssembly_Jobs.xml`（JobDef + WorkGiverDef）

---

## F. 審查發現與修正記錄

### 2026-07-06 第二輪：已修正

1. **【已修正】空爆額外攔截在枚舉中修改集合。** 原 `ApplyExtraInterceptRolls` 在 `MidcourseDefense` 的 `foreach (target in wave.targets)` 進行中直接 `RemoveTarget`（修改 `wave.targets`、必要時連 `Waves` 一起移除），會拋 `InvalidOperationException`。修正：命中的目標 id 先收集到 `airburstKills`（`ref List<int>`，跨多次空爆去重），待 foreach 結束後於 `MidcourseDefense` 尾端統一 `Discard()` + 從 `wave.targets` 移除；**刻意不移除空波次本身**——空波次交由 `MapComponentTick` 迴圈尾端既有的 `targets.Count == 0` 檢查處理，索引 `i` 因此始終有效（一併化解原審查第 2 點的索引錯位疑慮）。
2. **【已修正】註解漂移**：`BVRTuning.WindowPenaltyPerExtraTarget` 的 XML 註解已改為實際語意「波次合併時每多一目標縮短的 ticks」。
3. 修改僅涉及 `MapComponent_BVRCombat.cs`；尚未編譯驗證（沙箱無工具鏈），請以 Visual Studio 建置確認。

### 2026-07-06 第一輪：文件漂移（已更新）

舊版文件的預警窗口公式（功率線性、無反隱身加成）、SAM 彈藥（CompRefuelable 抽象燃料）、CIWS 判定（每發擲骰）、缺少鎖定階段/速度係數/戰鬥部攔截加成/導彈進入路徑等，均已按現行程式碼改寫。

（歷史紀錄）早前修正過「發射器被誤判為可裝配物」：`MissileAssemblyUtility` 限定 item 類別 + `Patch_Refuel_CarryConfig` 同步 `pending`，現行程式碼仍保有此雙保險。

---

## G. 測試檢查清單

1. **編譯**：Visual Studio 開 `DMSE.csproj` 建置（沙箱無工具鏈）。
2. **BVR 空投**：建搜索雷達→敵方空投出現預警窗口→火控鎖定（觀察通道占用警報）→SAM 消耗實體攔截彈、中過程攔截（地圖外閃光、無碎片）→末端 CIWS 連射（整輪一次判定；命中後機率碎片、耗砲管耐久）→未攔截者落地入 lord。無雷達時照常落地。
3. **BVR 導彈**：敵方（或 Debug `RegisterSalvo`）發射導彈→首 tick 進入 BVR→隱身/速度高的彈體窗口短、命中率低；**空爆回歸**：以 `RegisterSalvo` 生成多彈波次、裝空爆攔截彈齊射，確認一彈多殺且不再拋例外、空波次正常清除。
4. **攻防互換**：玩家對有雷達的敵方基地發射導彈/空投→被敵方防空攔截，`Alerts_PlayerMissiles` 正確追蹤；反輻射導引頭優先打雷達、豁免建築不被選中。
5. **裝配**：ITab 改部件→缺料紅字→小人搬料→施工→config 生效；更換/移除部件部分退還。
6. **發射**：三型平台裝填→世界選靶受射程限制→落點依彈頭/酬載/導引結算；增程件提升射程。
7. **火砲**：Gizmo 選靶（minRange 內拒絕）→世界航跡→齊射依 salvoIntervalTicks 錯開、從目標側邊緣飛入→散佈半徑生效、傷害歸因攻擊方；砲彈不觸發 BVR 攔截；在途齊射存讀檔正確還原。
8. **存讀檔**：BVR 波次/目標/CIWS 連射狀態、導彈 config/pending/delivered、發射器冷卻與已裝填設定、火砲冷卻/在途齊射皆正確還原。
