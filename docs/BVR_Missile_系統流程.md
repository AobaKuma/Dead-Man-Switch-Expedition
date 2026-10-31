# DMSE 遠程作戰系統 — 流程與實作總覽

最後全面校對 2026-10-18；之後各子系統變更以連結文件為準。`1.6/` 已凍結，不在範圍。原始完整版（含逐輪審查紀錄、19 項手動測試清單）在 `docs/_archive/`。

三套系統：

- **A. 地圖層超視距攔截（BVR）**：偵測並攔截來襲空投與導彈。世界層的偵測／中過程攔截見 [BVR 世界層](BVR_世界層偵測攔截重構.md)；本節的完整地圖內窗口仍用於空投、舊波次與直接生成的後備導彈。
- **B. 導彈裝配與發射**：玩家自組導彈、小人裝配、打擊地圖外目標；NPC 陣地共用同一條導彈鏈。波次打擊見[波次打擊](BVR_波次打擊規劃.md)；載體見 [Skyfaller→Projectile](Missile_Skyfaller轉Projectile_計畫.md)；裝備名冊見[三代裝備](三代裝備體系_實作計畫.md)。
- **C. 超視距火砲**：世界選靶持續砲擊，`DMSE_Turret_Empero`。

全部以 ThingComp 組合到建築／物品上（單一建築複合功能）。SAM 用裝配系統產出的實體導彈，戰鬥部影響攔截；進攻導彈可掛反輻射導引頭打雷達；火砲與 BVR 共用火控通道；所有發射平台共用 `CompMissileLauncher` 基類與 `CompBVRDevice` 可用性判定。

---

## A. 地圖層 BVR

### A-1. 角色

| 角色 | Comp | 關鍵參數 |
|---|---|---|
| 搜索雷達 | `CompSearchRadar` | `searchDistance`、`powerLevel`（平方）、`antiStealthLevel`、`ticksPerDistance`、`worldDetectionRange`、`immuneToAntiRadiationSeeker` |
| 火控雷達 | `CompFireControlRadar` | `maxTargets`（通道）、`powerLevel`、`guidanceAccuracyTime`、`maxRangeTicks`、`maxHitChance`、`stealthMissFactor`、`lockOnTicks`、`supportsArtilleryGuidance`、`supportsStrikeGuidance` |
| SAM | `CompMissileLauncher_Interceptor`（Props 名 `CompProperties_MissileLauncher`） | `reloadCooldownTicks`、`interceptorTravelTicks`；彈藥 = 容器內實體導彈 |
| CIWS | `CompTerminalCIWS` | `terminalWindowTicks`、`cooldownTicks`、`shotsPerBurst`、`ticksBetweenShots`、`interceptChance`（**整輪一次**）、`debrisChance`、`fuelPerShot`、瞄準視覺參數 |

`CompBVRDevice.Active`：已生成、未損壞、開關開、通電、未擊暈、**有 `CompMannable` 時必須有人操作**。四類裝置在 `PostSpawnSetup`／`PostDeSpawn` 自行註冊。SAM 屬發射平台家族，`HasAmmo` 為 Active 的一部分；其他平台與火砲只借用可用性判定，不註冊。

現行雷達與防空砲數值見[三代裝備](三代裝備體系_實作計畫.md)與 `1.7/Defs`。攔截器只取 `Interceptor`／`Dual` 彈（`MissileRoleExtension.CanIntercept`），命中率再乘 `interceptHitFactor`。單兵防空具消耗背包刺針攔截本地圖波次，不占通道。

UI 配件（不參與判定）：`CompTurretFacingRenderer`（追最緊急目標；`drawLoadedAmmo`）、`MapComponent_InterceptEffects`（攔截特效，見[攔截殘骸特效](攔截殘骸特效_實作與技術美術計畫.md)）、`WeatherEvent_MissileFlash`、`Alerts_BVRStatus`（雷達與通道分項）、`Alerts_Pods`（防禦方波次）、`Alerts_PlayerMissiles`（攻擊方，左右鍵跳轉）、`CompEffecterColdLaunch`／`HotLaunch`（經 `IMissileFlight`，見[發射特效](導彈發射特效_重製計畫.md)）。

### A-2. 資料模型

`MapComponent_BVRCombat`（每地圖）：

- `BVRWave`：`tickToImpact`、`targets`、`defenderFaction`、`lord`、`signal`、`terminalOnly`。
- `BVRTarget`：`ThingOwner<Thing>`（存檔鍵 `skyfaller`）收起空投艙或 `IIncomingMissile`（`Payload`／`Missile`／`Skyfaller`）；`position`、`stealthLevel`、`speed`、`lockUntil`、`midcourseEngagedUntil`、唯一 `id`。`ContainedPawns` 回傳艙內全部 pawn。
- 四個裝置註冊表；`Channels`（`BVRChannelLedger`）。
- `BVRTargetProps`（掛在空投艙或落地導彈 def）：`distanceLevel`、`stealthLevel`、`speed`、`altitude`；無擴充時 1／0／15／1000。
- `BVRTuning`：`PowerWindowExponent=2`、`AntiStealthBonusPerLevel=0.5`、`StealthDetectFactor=0.4`、`MergeWindowTicks=600`、`WindowPenaltyPerExtraTarget=60`。

### A-3. 目標進入

1. **敵對空投**：`Patch_MakeDropPodAt`（postfix `SkyfallerMaker.SpawnSkyfaller(ThingDef, Thing, IntVec3, Map)`）→ `BVRDetection.GetDropFaction`（`PawnsIn` 列出全部 pawn，取第一個有陣營者）→ `ResolveDefender` → `DeSpawn` + `RegisterIncoming`。舊 `InterceptProjectile` 豁免。
2. **來襲導彈**：生成端同步登記。`MissileProjectileMaker.SpawnIncoming` 生成 `Projectile_MissileIncoming`、設好 config／attacker 後立即 `MissileBVRUtility.TryRegister`；首 tick 自註冊為後備（`bvrHandled`／`checkedBvr` 防重複）。世界層漏網者走 `RegisterTerminal`。`Patch_SpawnMissileIncoming` 已廢棄留空。
3. **測試**：Debug「Enemy missile salvo」`RegisterSalvo` 直接生成波次。

### A-4. 預警窗口與波次合併

```
window = Σ(防禦方運作中搜索雷達貢獻) × distanceLevel × AltitudeFactor ÷ targetCount
單雷達 = searchDistance × ticksPerDistance × max(1, powerLevel)² × 隱身係數
隱身係數 = antiStealth < stealth → 0.4；否則 1 + 0.5 × (antiStealth − stealth)
AltitudeFactor = clamp(√(altitude / altitudeRefMeters), min, max)
```

無運作中搜索雷達 → -1，照常落地。同防禦方、抵達相差 ≤600 ticks 的目標合併，取最早抵達，每多一個目標 −60（下限 now+1）。多雷達貢獻相加。高度只決定反應時間，速度只影響火控。

### A-5. 時間線（每 tick，由 Waves 尾端往前）

```
timeLeft > 0（非 terminalOnly）→ SAM 中過程（MidcourseDefense），2026-10-27 起一路到落地、末端內與 CIWS 並行
  • 趕得上才接戰：攔截彈結算時間 = InterceptFlightTicks（模擬發射 Projectile 升空；否則 interceptorTravelTicks）
  • 容量 = Σ 防禦方運作中火控 max(0, maxTargets − 借給火砲)，只計 timeLeft ≤ maxRangeTicks 的雷達
  • 占用 = 鎖定中或攔截彈在飛的目標數
  ① 鎖定：占用 < 容量 且防禦方有可用攔截彈（無彈不鎖、打光即放）
        lockUntil = now + lockOnTicks × (1 + 0.5×隱身差 + max(0, speed−15)/60)
  ② 發射：最佳火控 + 冷卻最早結束的就緒 SAM（只是冷卻中則保持鎖定）
        hit = maxHitChance × clamp01(timeLeft / guidanceAccuracyTime)
              × 速度係數（>15 每單位 −1.2%，下限 0.2）× stealthMissFactor^(stealth − power)₊
              × interceptHitFactor × 箔條折減 + 戰鬥部加成（連續桿 N×0.05），clamp01
        FireInterceptor：發射 Projectile（攔截模式）、消耗首枚未保留彈、進冷卻
        空爆：額外 floor(N/5) 次擲骰攔截同波次其他目標（先收集，迴圈後移除）
  ③ 結算：攔截彈出圖 → 釋放通道 → 擲骰；成功 RemoveTarget＋攔截特效；落空則下一 tick 重排
timeLeft ≤ 末端窗口（或 terminalOnly）→ 另加 CIWS（TerminalDefense）
  • 每具 CIWS 每 tick 只處理一次；新連射選 tickToImpact 最早的末端波次 targets[0]；連射中以 burstTargetId 跨波次找目標
  • 每發耗 fuelPerShot；目標消失或斷料則中止不判定
  • 整輪打完擲一次 interceptChance；命中後擲 debrisChance → SpawnDebris（殺死艙內 pawn、屍體合併成一個殘骸空投；無 pawn 則乾淨清除）
timeLeft ≤ 0 → ResolveWave：未攔截者落地（空投 TryDrop；導彈依保存的進入方位從邊緣飛入），發 signal；wave.lord 只收編 skyfaller 直接持有的 pawn
```

terminalOnly 目標只交給 CIWS。CIWS 與 SAM 可能打同一目標，先擊毀者移除目標。

### A-6. SAM 彈藥

發射器 parent 是 `Building_MissileRack`（或實作 `IMissileRack` 的 `CompMissileRackStorage`），容量 `MissileRackExtension.capacity`，能收什麼由 storage filter 決定。裝填：地面搬運（`IHaulDestination`）；彈架 → 平台（`WorkGiver_LoadMissileLauncher`，儲存架內 unspawned 導彈原版看不到）；操作員自行補彈（`CompMannableMissileLauncher` 改派 `DMSE_ManMissileLauncher`，取彈裝滿才操作）；敵方陣地 `MissileAutoFillUtility.TryAutoFill`（讀檔跳過）。輕型發射箱同時掛 Rail 與 Interceptor，共用彈艙。

### A-7. 重點設計

- **陣營相對防禦**：`ResolveDefender` 找擁有運作中搜索雷達、與來襲方敵對的陣營（玩家優先）；玩家打敵方基地也會被攔截。
- **隱身雙層對衝**：搜索段對 `antiStealthLevel`（窗口），火控段對 `powerLevel`（命中與鎖定）。
- **電子對抗**：反輻射導引打開機中 `powerLevel` 最高的敵對雷達；`immuneToAntiRadiationSeeker` 豁免（聲學雷達）。箔條 `jamStrength`／`chaffCharges`：世界層斷鎖（`chaffBreakChance × jam`，不論成敗扣一發）、地圖層命中 × `(1 − chaffMapHitPenalty × jam)`。後續見 [ECM 計畫](ECM與ECCM_實作計畫.md)。
- **VGE 近防**（`1.7/GravshipExpanded/PointDefence`）：`CompPointDefence` 與 `CompTerminalCIWS` 並存、共用砲管，判定互不相通。
- **存檔**：波次、目標、CIWS 連射狀態、冷卻皆持久化。

---

## B. 導彈裝配與發射

### B-1. Def 架構

- `MissileBodyDef`：基礎數值（爆炸半徑、傷害、散布、`baseWorldSpeedFactor`、`payloadCapacity` N）、燃料／比衝、`assembleWorkAmount`、`refundFraction`、`sizeClass`（Loitering／Light／Medium／Ballistic）、`launchMode`（Tilt／Vertical／Both）、`customizeLocation`（None／Item／StorageBuilding／Launcher）、`slots`、`incomingSkyfaller`（現為落地 Projectile def）、`launchProjectile`、`builtInGuidance`、`builtInTerminalGuidance`、`defaultParts`、`terminalUpdates`、`interceptHitFactor`、`researchPrerequisites`。
- `MissilePartDef`（Warhead／Guidance／Propulsion／Payload）：數值修正、`signatureFactor`、`jamStrength`、`chaffCharges`、`compatibleBodies`、`researchPrerequisites`、`costList`；行為子類：
  - `warheadEffect`：ContinuousRod（攔截 +N×5%）、ExtendedFuel（射程 +N×2）、Airburst、TungstenPenetrator（坍塌厚岩頂）、Incendiary、EMP、AnimalBerserk、BerserkPulse。
  - `guidanceType`：見 B-7。
  - `payloadEffect`：None、HighExplosive、Cluster、Defoliant、FireFoam、Antiparticle、ToxicGas、AirburstTungsten（射程 ×0.75）。
- 平台 `MissileLauncherExtension`（`sizeClass`、`extraSizeClasses`、`launchMode`、`capacity`、`isStorage`）。實際能裝什麼看 storage filter；`check_missile_content.py` 第 8 條比對。只有 `defaultBody.customizeLocation == Launcher` 的建築（塔式、戰略井）開放換彈體（同 sizeClass、未研究者鎖定）；物品與儲存架彈體唯讀。

### B-2. 設定與射程

- `MissileConfig`（彈體 + 每槽最多一部件）：`Range = round((Fuel × SpecificImpulse × rangePerFuelImpulse + baseRange + Σ rangeOffset + 戰鬥部 RangeOffset(N)) × 酬載 RangeFactor(N))`，0 = 不限。例：100 × 3 × 0.1 = 30 格。
- `CompMissileConfig`：`config`（已套用）、`pending`（目標）、`delivered`（已搬料）、`assemblyConfirmed`；`NeedsAssembly = config ≠ pending`。
- **預設零件**：只有物品型態彈體有；`EnsureInit` 在建立時播種一次（`defaultsSeeded` 讀檔預設 true，不會白送或長回來）。材料併入物品 `costList`。彈道井／架建好是空的，裝配時依零件收費；發射後清空 parts、保留 pending。
- 距離一律 `MissileWorldUtility.RangeDistance`（地表等效格，跨圖層 +10）；不用原版 `ApproxDistanceInTiles(PlanetTile, PlanetTile)`（跨圖層讀錯圖層、可能越界）。

### B-3. 裝配

```
ITab「裝配」→ 寫入 pending、清除 assemblyConfirmed →「確認裝配」（未確認不派工）
WorkGiver_AssembleMissile（地上導彈＋架內待裝配者，後者以儲存架為工作目標）
  缺料 → JobDriver_DeliverMissileResource；備齊 → JobDriver_AssembleMissile → ApplyAssembly
  （消耗、部件按 refundFraction 退還、config = pending）
彈道：WorkGiver_AssembleBallisticSilo → JobDriver_AssembleBallisticSilo → MarkLoaded
```

`MissileAssemblyUtility.MissileDefs` 只收 Item 類別，建築上的 `CompMissileConfig` 不會被當物品。舊 Scorer 平台由 `Patch_Refuel_CarryConfig` 同步設定。被打擊任務保留的導彈不派工。導彈組裝台見[組裝台計畫](導彈組裝台與裝配方案_實作計畫.md)。

### B-4. 發射

```
CompBVRDevice
 └ CompMissileLauncher（冷卻、HasAmmo、Active）
    ├ CompMissileLauncher_Interceptor   SAM
    └ CompMissileLauncher_WorldTargeting（Gizmo、世界選靶、TryLaunchRound）
       ├ _Rail       容器實體導彈；MissileItemExtension → 平台 missileBody
       ├ _Ballistic  isAssembled
       └ _Scorer     CompRefuelable（1.7 無 Def）
```

世界航跡基底 `MissileWorldObject`（玩家 `ScorerProjectile_WorldObject`、敵方 `WorldObject_IncomingMissile`）：運動見世界層文件；`targetParent`、終端更新、落空；抵達時 `ResolveArrivalMap`。

- **玩家**：選靶（`CanTargetTile`、`CanEngage`：射程、跨圖層需導引能力；游標顯示距離與「將會落空」）→ `TryLaunchRound` → 發射 Projectile 升空轉向飛出邊緣 → 世界航跡 → `OnArrived`（`AllowGenerateTargetMap = true`；非敵對陣營轉敵對；`GameComponent_MissileEngage` 保圖 2 天）→ 導引決定落點（TV 暫停讓玩家選；其餘自動）→ `SpawnIncoming`（進入方位 = 最後一段）→ 敵方 BVR。
- **敵方**：`IncomingMissileUtility.Launch` → `OnArrived`（`AllowGenerateTargetMap = false`）→ `PickCell`（TV 退化慣性）→ 落地彈 → BVR；落點為玩家家園時發威脅信件。
- **NPC 陣地**：見 [NPC 導彈站點](敵對NPC導彈發射站點_生成與彈藥選型計畫書.md) §3；地圖層以 `CompMissileLauncher_WorldTargeting` 辨識發射器，全滅時陣地摧毀。
- **落點結算** `MissileImpactUtility.Detonate`：散布偏移 → 主爆炸 → `warheadEffect.Apply` → `payloadEffect.Apply` → 閃光。無 config 時 5 格 Bomb 後備。歸因於發射建築（`MissileImpactUtility.Instigator`）。高角護盾見 Projectile 文件。

### B-5. 跨圖層與終端更新（逃逸區）

- `HasTerminalGuidance`：導引槽部件 `ProvidesTerminalGuidance`（慣性 = false）或彈體 `builtInTerminalGuidance`。`builtInGuidance` 只影響選靶。
- 跨圖層打擊需導引能力且射程含 +10；火砲一律不可跨圖層。
- 終端更新：每 60 ticks（`TracksMobileTargets` 時 20）檢查目標 tile；次數 = 彈體 `terminalUpdates` + 導引加成（反艦 +2），且已飛＋新航段 ≤ 射程。用完或燃料不足就沿最後位置飛，目標不在即落空——**逃逸區**。
- 移動目標登錄：`WorldComponent_BVRNetwork` 每 60 ticks 取樣所有 `MapParent` tile（保留 900 ticks），涵蓋轉移艦、搬遷陣地、其他 mod。飛行中的 `Gravship` 無地圖，不列為目標。

### B-6. 導引頭（`GuidanceType.PickCell`）

抵達目標地圖後依 `TargetingGuidance`（導引部件，否則 `builtInGuidance`）選落點；找不到退化為慣性。「敵對」= 有陣營、非攻擊方、與攻擊方敵對。

| 導引 | 類別 | 鎖定 | 後備 |
|---|---|---|---|
| 慣性 | `Inertial` | 隨機敵對建築 → 敵對 Pawn → 地圖中心 | — |
| 反輻射 | `AntiRadiation` | 開機（`ActiveForSeeker`）敵對雷達中 `powerLevel` 最高，同級比耗電 | 電磁 → 慣性 |
| 熱訊號 | `HeatSource`（舊名 `Infrared`） | 最熱室內房間高出室外 15°C → 該房間；否則最大 Pawn | 慣性 |
| 電磁訊號 | `ElectromagneticSignal`（舊名 `HighEnergy`） | 耗電最大的敵對建築 | 慣性 |
| 電視 | `TV` | 玩家：暫停手選；敵方：慣性 | — |
| 雷射 | `Laser` | 攻擊方最近放置、仍有效的雷射標點 | 慣性 |
| 火控 | `FireControl` | 慣性（較小散布；主要語意是攔截彈半主動） | — |
| 無線電指令 | `RadioCommand` | 萊茵女兒內建；占外部通道 | — |
| 主動雷達 | `ActiveRadar` | 耐久最高的敵對建築 | 慣性 |
| 影像 | `Imaging` | 露天存活敵對 Pawn（戰力、體型、ID 排序） | 慣性 |
| 反艦 | `AntiShip` | 船圖上耐久最高的敵對建築 → 最大 Pawn；+2 終端更新、20 ticks 檢查 | 慣性 |

- **電網啟動中的建築**：導彈打未生成地圖的據點時建築都還沒開機（原版每 30–200 ticks 開一棟）。`GuidanceType.PowerStarting` 把符合原版開機條件的建築視為運作中；反輻射用 `ActiveForSeeker`，電磁以額定耗電計。熱訊號不受影響（房間還沒升溫）。
- **雷射標點**（`Scorer/Guidance/LaserDesignator.cs`）：`DMSE_LaserDesignatorMark`（Ethereal、不可攻擊，預設 5000 ticks 失效）；`DMSE_Gun_LaserDesignator` 射出無傷害標定彈（`weaponTags Inherit="False"`、`ai_IsWeapon false`，AI 不會攜帶）；`CompProperties_AbilityLaserDesignate` 可掛任何 AbilityDef（未隨附）。

---

## C. 超視距火砲（`.source/DMSE/Artillery/`）

| 角色 | 類別 | 職責 |
|---|---|---|
| 核心 | `CompArtilleryStrike`（繼承 `CompBVRDevice`，不註冊） | 世界選靶、持續射擊狀態機、彈藥／砲管、砲管轉向、選配借火控通道 |
| 離場 | `Projectile_ArtilleryDeparture` | 弧線飛到目標方位的出口邊緣格，`Impact` 建立世界航跡 |
| 世界航跡 | `WorldObject_ArtilleryStrike` | Slerp（`speed/10 × 0.00025 / 距離`）；`Arrived` 解析地圖（落空／生成據點地圖）、排程齊射 |
| 排程 | `MapComponent_ArtilleryStrikes`＋`ArtilleryShellRequest` | `fireTick`、`targetCell`、`entryEdgeCell`、`projectileDef`、`attackerFaction`、`scatterRadius`、`guided` |
| 工具 | `ArtilleryStrikeUtility` | `WorldDirectionAngle`、`EdgeCellFromAngle`、`CalcEntryEdgeCell`、`QueueSalvo`、`FindGuidedTarget`、`SpawnShell` |

Props：`departureProjectileDef`、`worldObjectDef`（必填）、`projectileDef`、`cooldownTicks`、`warmupTicks`、`scatterRadius`、`salvoCount`、`salvoIntervalTicks`、`requiresFireControlChannel`（無 Def 使用）、`fireControlHoldTicks`、`guidedScatterRadius`（<0 不支援導引）、`maxRangeTiles`、`refuelableIsAmmo`。Empero：冷卻 1800、瞄準 300、散布 14／導引 3、射程 20、砲彈 12；另有本地圖減裝藥曲射（共用砲彈、世界射擊期間停火）、hold fire／強制目標。

流程：Gizmo 選靶（同圖層、非本地圖、射程內）→ 持續射擊（`!Active` 暫停、無彈停止、等通道）→ 瞄準 warmup（砲管轉向世界方位）→ `FireSingleShot`（耗彈、出口格 = 世界方位、`targetCell` = 目標地圖中心、開火瞬間搶到通道則 guided）→ 離場 → 世界航跡 → `QueueSalvo`（進入邊 = 從發射地一側）→ `SpawnShell`（guided 每發重挑 `FindGuidedTarget`：砲塔 → 用電設備 → 人員；散布；`launcher = null`）→ 原版爆炸。

- 與 BVR 共用火控通道：`FreeArtilleryChannels = maxTargets − 借給火砲 − 按比例分攤的 BVR 占用`；`MidcourseDefense` 反向扣除。
- 砲彈是 Projectile，**BVR 不攔截**。不可跨圖層。
- 存檔：冷卻、`targetTile`（`PlanetTile` 完整 tileId,layerId）、`channelReleaseTick`、借用雷達、在途齊射。
- 已知：傷害歸因 `launcher: null`（`attackerFaction` 未參與）；`DMSE_Turret_Empero` 是 `Building` 不是 `Building_Turret`，`FindGuidedTarget` 不會把它當 tier 0。見[反砲兵提案](反砲兵雷達_實作計畫.md)。

---

## D. 檔案地圖

- **BVR 核心** `.source/DMSE/SkyFallerTurret/BVR/`：`CompBVRDevice`、`MapComponent_BVRCombat`、`BVRWave`、`BVRTargetProps`、`CompSearchRadar`、`CompFireControlRadar`、`CompMissileLauncher`（Interceptor）、`CompMannableMissileLauncher`、`JobDriver_ManMissileLauncher`、`CompTerminalCIWS`、`CompTurretFacingRenderer`、`MapComponent_InterceptEffects`、`InterceptDebris/`、`World/`。
- **BVR 周邊** `.source/DMSE/SkyFallerTurret/`：`InterceptProjectile`、`Patch_MakeDropPodAt`、`Alerts_*`、`Patch_QuestPart_Bossgroup`、`ManPortableSAM`。只剩 superseded 註解的空檔：`CompProperties_InterceptRadar`、`CompSkyfallerTurret`、`EarlyWarningUtility`、`InterceptSkyfallerUtility`、`MapComponent_InterceptSkyfaller`。
- **導彈** `.source/DMSE/Scorer/`：`Assembly/`（Defs、Config、Effects、Guidance、ITab、WorkGiver／JobDriver、`MissileBVRUtility`、`MissileIncoming` 舊存檔轉換）、`Strike/`、`Ballistic/`、`Guidance/`、`MissileWorldObject`、`MissileWorldUtility`、`ScorerProjectile_WorldObject`、`GameComponent_MissileEngage`、`Patch_ShouldBeRemoved`。
- **載體** `.source/DMSE/MissileProjectile/`；**特效** `.source/DMSE/MissileFx/`；**平台容器** `.source/DMSE/`（`CompMissileLauncher`、`Building_MissileRack`、`CompMissileRackRenderer`）。
- **NPC 陣地** `.source/DMSE/MissileBase/`；**火砲** `.source/DMSE/Artillery/`；**VGE** `.source/GravshipExpanded/PointDefenceCompat`。
- **Defs** `1.7/Defs/ThingDefs_Buildings/`、`ThingDefs_Items/`、`Missile/`、`WorldObjects_*.xml`、`DMSE_BVRWorld.xml`、`Effects/`。

## E. 未解問題與手動測試

- 火砲傷害歸因未生效（見 C）。
- `ImpactCraterUtility`（撞擊飛行）仍用 `ApproxDistanceInTiles(PlanetTile, PlanetTile)`，跨圖層錯誤。
- 原版事實：新接上電網的耗電建築逐一開啟（30–200 ticks），`DebugSettings.unlimitedPower` 只對已在電網內的有效；非世界畫面時世界物件每 15 ticks 更新。
- 自動驗收未涵蓋、仍需手動：裝配 UI、發射 UI、地圖層空爆一彈多殺、導彈陣地摧毀信件、跨圖層拒絕／允許、逃逸區用完次數後落空、對空白 tile 發射落空、雷射標點完整流程、舊存檔。驗收與單元測試見 `.source/DMSE.BVRAcceptance/README.md`、`.source/DMSE.BVRTests/README.md`。
