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
- 地圖層波次常數在 `BVRWorldTuningDef`（`DMSE_BVRWorld.xml`）：`searchPowerWindowExponent=2`、`mapAntiStealthWindowBonusPerLevel=0.5`、`mapStealthWindowFactor=0.4`、`waveMergeTicks=600`、`windowPenaltyPerExtraTarget=60`、`loiterDiveTicks=600`。

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

數量一律以「枚」計：飛彈 def 都是 `stackLimit=1`，但堆疊模組放寬上限後同一個 Thing 可能是整疊、容器 `TryAdd` 也會合併。所以 `StoredCount` 是 `TotalStackCount`，彈架 → 平台只搬 `MissileRackUtil.LoadCount`（最多填滿剩餘空位），三條發射路徑（Interceptor、Rail、NPC 陣地）都用 `MissileRackUtil.ConsumeOne` 每發扣一枚（BVRTests `MissileStacks`）。

### A-7. 重點設計

- **陣營相對防禦**：`ResolveDefender` 找擁有運作中搜索雷達、與來襲方敵對的陣營（玩家優先）；玩家打敵方基地也會被攔截。
- **隱身雙層對衝**：搜索段對 `antiStealthLevel`（窗口），火控段對 `powerLevel`（命中與鎖定）。
- **電子對抗**：反輻射導引打開機中 `powerLevel` 最高的敵對雷達；`immuneToAntiRadiationSeeker` 豁免（聲學雷達）。箔條 `jamStrength`／`chaffCharges`：世界層斷鎖（`chaffBreakChance × jam`，不論成敗扣一發）、地圖層命中 × `(1 − chaffMapHitPenalty × jam)`。地面干擾機、功率段位與戰情終端見[電子戰建築](電子戰建築_實作計畫.md)；ECCM 等後續見 [ECM 計畫](ECM與ECCM_實作計畫.md)。
- **VGE 近防**（`1.7/GravshipExpanded/PointDefence`）：`CompPointDefence` 與 `CompTerminalCIWS` 並存、共用砲管，判定互不相通。
- **存檔**：波次、目標、CIWS 連射狀態、冷卻皆持久化。

---

## B. 導彈裝配與發射

### B-1. Def 架構

- `MissileBodyDef`：基礎數值（爆炸半徑、傷害、散布、`baseWorldSpeedFactor`、`payloadCapacity` N）、燃料／比衝、`assembleWorkAmount`、`refundFraction`、`sizeClass`（Loitering／Light／Medium／Ballistic）、`launchMode`（Tilt／Vertical／Both）、`customizeLocation`（None／Item／StorageBuilding／Launcher）、`slots`、`incomingSkyfaller`（現為落地 Projectile def）、`launchProjectile`、`builtInGuidance`、`builtInTerminalGuidance`、`defaultParts`、`terminalUpdates`、`interceptHitFactor`、`researchPrerequisites`。
- `MissilePartDef`（Warhead／Guidance／Propulsion／Payload）：數值修正、`signatureFactor`、`jamStrength`、`chaffCharges`、`compatibleBodies`、`researchPrerequisites`、`costList`；行為子類：
  - `warheadEffect`：ContinuousRod（攔截 +N×5%）、ExtendedFuel（射程 +N×2）、Airburst、TungstenPenetrator（坍塌厚岩頂）、Incendiary、EMP、AnimalBerserk、BerserkPulse。
  - `guidanceType`：見 B-6。
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

### B-7. 飛彈行為框架（`.source/DMSE/MissileFramework/`）

預定併入 Fortified，所以 `MissileFrameworkTypes.cs` 的 namespace 已是 `Fortified`，**不得引用 DMSE 型別**；搬進 `Fortified.dll` 後，XML 與存檔的 `Class="Fortified.X"` 都不必改。部件槽與尺寸 enum 不動；`GuidanceType`、`WarheadEffect`、`PayloadEffect` 暫留 DMSE。

| 型別 | 職責 |
|---|---|
| `IMissileLoadout` | 裝配的抽象（`MissileConfig` 實作）：`PayloadCapacity`、`PenetratesThickRoof`、`BehaviorProps` |
| `IMissileTrack` | 世界層航跡的抽象（`MissileWorldObject` 實作）：`RouteProgress`、`TicksToArrival`、`PositionAt`、`NotifyManoeuvre` 等 |
| `AirTrackSignature`（見 B-8） | 世界層偵測看到的特徵：`distanceLevel`、`stealthLevel`、`speed`、`altitude` |
| `MissileSeekContext` | 導引頭選目標的輸入；`GuidanceType.PickTarget` 改收它。`ClaimKey` 非 null 才分散目標並登記鎖定 |
| `MissileImpactContext` | 引爆的輸入；戰鬥部與載荷新增 `Apply(MissileImpactContext)`，預設轉給舊的 `Apply(N, center, map, attacker)` |
| `MissileBehaviorProps` | XML 設定（共用、無狀態），掛在 `MissileBodyDef.behaviors` 或 `MissilePartDef.behaviors`；可直接覆寫 `ModifySeekResult`、`PostDetonate` |
| `MissileBehavior` | 單條航跡上的實例（可存檔），`behaviorClass` 有值才建立；生命週期到航跡進入目標地圖為止 |
| `MissileBehaviorSet` | 一條航跡的行為集合：分派、彙總、存檔。順序固定為彈體在前、部件依裝配順序，存檔依序對應 |

Hook 時機：

| Hook | 時機 |
|---|---|
| `PostLaunch` | 航跡加入世界時一次，讀檔不再呼叫 |
| `FlightTick` | 飛行推進後；可銷毀航跡，之後的行為不再執行 |
| `SpeedFactorAt`（配 `HasSpeedProfile`） | 速度倍率，必須是航線進度的純函式，預測未來位置也會呼叫；多個行為相乘，下限 0.01 |
| `ModifySignature` | 每次讀 `TargetProps` 時 |
| `PostTerminalUpdate`、`OnIntercepted`、`OnJammedOut` | 終端更新後；航跡被攔截或被干擾、銷毀前 |
| `PreArrive` | 抵達時；回傳 true 表示已自行結算，不走抽象節點與一般抵達流程 |
| `ModifySeekResult`（Props） | 導引頭選定後；設成 `IntVec3.Invalid` 表示放棄 |
| `PostDetonate`（Props） | 戰鬥部與載荷之後；在厚岩層表面引爆時不呼叫 |

參考實作（目前沒有出貨的 Def 使用，驗收以程式掛上）：

- `Fortified.MissileBehaviorProps_FlightProfile`：`phases` 依 `untilProgress` 分段，每段可改 `speedFactor`、`altitude`（負值不改）、`signatureFactor`、`stealthOffset`；`evasiveEntry` 在進入該段時計一次機動。
- `DMSE.MissileBehaviorProps_ReleaseChildren`（`Scorer/Assembly/MissileBehavior_ReleaseChildren.cs`）：`atProgress` 釋放 `count` 枚子彈，`childBody`／`childParts` 指定內容，`retireParent` 銷毀母彈。依賴裝配系統，所以跟 `MissileBodyDef` 一起遷移。`SpawnChild` 上限 3 代，超過回傳 null。

與既有系統的接點：

- **變速航跡**：`IAirTrack.VariableSpeed` 為 true 時，ETA 與位置預測以 30 ticks 為步長積分，`BVRInterceptSolver.TrySolve` 改走 `TrySolveGeneral`。
- **機動**：`RouteRevision = terminalUpdatesUsed + manoeuvres`，飛向舊會合點的在飛攔截彈判定目標脫離。
- **`TargetProps`**：`SignatureFactor` 與行為都沒有改變特徵時回傳 Def 自己的 props；否則用 `BVRTargetProps.WithSignature` 複製，未改的欄位（如 `debrisProfile`）一律保留。以前誘餌部件的放大會遺失 `altitude` 與 `debrisProfile`。
- **導引特性**：`HomesOnEmissions`、`UsesGroundChannel`、`ExtraGroundChannels`、`BuiltInLabel` 取代原本的型別判斷。電視導引的零件資訊卡因此開始顯示通道占用，這是有意的變更。
- **存檔**：`manoeuvres`、`childDepth` 與 `behaviors` 節點；節點不存在時（舊存檔、新增部件）維持預設值。

測試：`DMSE.BVRTests/Tests_MissileBehaviors.cs`（資料與公式）、`DMSE.BVRAcceptance/Tests_MissileBehaviors.cs` 的 MB1–MB4（遊戲內 hook、子彈、存讀檔）。尚未驗證：從 XML 載入 `behaviors`。`MissilePartDef` 目前沒有 `ConfigErrors` 覆寫，部件上的行為設定錯誤不會在載入時回報（彈體會）。

---

### B-8. 防空事件、修正鏈與目標抽象（`.source/DMSE/MissileFramework/AirDefenseFramework.cs`）

與 B-7 相同，namespace 是 `Fortified`，不引用 DMSE 型別。DMSE 這邊的接線在 `World/AirDefenseHooks.cs`，沒有修正或處理者時不配置物件。

**修正鏈** `AirDefensePipeline`：兩層共用。

| 數值 | 世界層 | 地圖層 |
|---|---|---|
| `Detection` | `BVRNode.EquipmentDetectionRange`（地表格） | `ComputeWarningWindow`（ticks） |
| `LockTicks` | 開始鎖定時 | 開始鎖定時 |
| `HitChance` | 據點攔截、封鎖攔截 | 中過程、肩射、CIWS |

- 順序：全域 `AirDefenseModifier`（依 `order`）→ 目標裝配 `ModifyDefenseAsTarget` → 攔截彈裝配 `ModifyDefenseAsInterceptor` → 航跡 `IAirTrack.ModifyDefense`（只有世界層有航跡）。
- 結果會限制範圍：命中率 0–1、鎖定至少 1 tick、偵測不小於 0。修正丟例外或回傳 NaN 時只跳過它，並記錄一次錯誤。
- 修正會被介面與排序呼叫，不可有副作用。
- `Detection` 修正也影響涵蓋範圍顯示（此時 `Track` 為 null，只有 `Signature`）；雷達自身的 `EffectiveWorldRange` 面板數值不經過修正鏈。

**事件** `AirCombatEvents.Raised`：單一事件流，依 `Kind` 分辨，`Layer` 標示來源層。

| Kind | 時機 |
|---|---|
| `TrackDetected` | 世界層每陣營第一次看到（中立陣營也算）；地圖層在目標進入非末端波次時 |
| `LockStarted`、`InterceptorLaunched` | 開始鎖定；攔截彈發射 |
| `InterceptResolved` | `Outcome` = `Hit`、`Miss`、`Evaded`、`Safed`；空爆波及的擲骰 `Secondary` = true |
| `TrackArrived` | 飛彈航跡抵達，或地圖波次落地。外部航跡的抵達由它自己處理，不會發出 |
| `TrackJammed` | 導引被干擾機打掉；`Removed` = 直接落在地圖外 |

處理者丟例外時只跳過它。事件當下目標可能已被移除。

**目標抽象** `IAirTrack`：不綁飛彈的航跡。`IBVRTrack` 是它加上網路內部資料（`TargetProps`、`Config`、`State`）。

- 世界物件實作 `IAirTrack` 就會自動成為航跡，不必註冊。
- 無法修改的世界物件用 `AirTrackProvider` 提供包裝。來源不再回傳的物件，下次掃描時狀態就被丟棄。
- 外部航跡由 `BVRExternalTrack` 包裝，狀態存在 `WorldComponent_BVRNetwork.externalStates`（依世界物件 ID，會存檔）。
- `Notify_Intercepted` 由航跡決定後果：可以銷毀自己，也可以只受傷繼續飛。
- 飛彈專屬流程只走 `MissileTracks`：導引干擾、玩家飛彈警示、終端更新。
- `WorldObject_DropPodTrack` 不再繼承飛彈，直接實作 `IBVRTrack`；存檔鍵沿用，舊存檔可讀。

**武器抽象** `IAirEffector`：世界層節點只透過它使用武器（`BVRNode.Effectors`）。

- 實作者：`CompMissileLauncher_Interceptor`、`BVRDefenseEffector`（抽象據點庫存），以及向 `MapComponent_AirEffectors` 註冊的第三方武器。陣營看 `Source.Faction`；該地圖要有同陣營的防空節點（搜索或火控雷達）才會被使用。
- `UsesFireControl` 為 true：走鎖定、火力通道、火控射程與高度限制，由 `ReadyEffector` 選最閒置的一具。封鎖攔截也走這條。
- `UsesFireControl` 為 false：點防禦式。不鎖定、不佔通道，不受火控高度限制與在飛攔截彈影響；只要節點陣營本次掃描看得到且政策允許，就詢問 `CanFire`、`CanEngage`。範圍與高度限制由武器自己在 `CanEngage` 判斷。不參與封鎖攔截。
- `PrepareShot` 回傳 `AirEffectorShot`：`AngularSpeed` 為 0 表示瞬間命中，當場結算；否則產生攔截物。`ShotDef` 可換外觀，類別必須是 `WorldObject_Interceptor`。
- 只涵蓋世界層。地圖層中過程、肩射與 CIWS 仍直接使用各自的 Comp，所以第三方武器打不到只存在於地圖波次的目標（空投）。

**陣營準則** `MissileDoctrineDef`（`.source/DMSE/MissileBase/MissileDoctrineDef.cs`、`1.7/Defs/DMSE_MissileDoctrines.xml`）：

- 陣營用 `FactionDef` 上的 `MissileDoctrineExtension` 指定；沒有時依 `defaultMinTech`–`defaultMaxTech` 選預設。出貨三套：Industrial、Spacer、Ultra 以上；Industrial 以下沒有準則。
- `settlementDefense`：沒有地圖的一般據點的合成防空（探測半徑、反隱身、通道、庫存、補充、冷卻、火控建築、攔截彈）。null = 沒有合成防空。出貨值與先前寫在程式裡的相同。
- `threatAssessment`：NPC 地圖節點是否先打雷達截面最大的目標；出貨 Spacer 以上開啟，與先前相同。
- `targetWorker`（`Fortified.StrikeTargetWorker`）：導彈陣地與發射站選目標。省略 = `StrikeTargetWorker_Players`，只打玩家地圖。`StrikeTargetWorker_Hostile` 會打敵對陣營的據點，包含 NPC，可用 `includePlayer`、`includeNpc`、`requireMap` 調整；設在準則上就有 NPC 互打。射程、跨圖層與自身據點由呼叫端過濾。
- 交戰規則（警戒階段政策）不在準則內，仍由 `AirDefenseAlertPolicy` 決定。

**宣告方式**：`AirDefenseExtensionDef`（`modifiers`、`listeners`、`trackProviders`），或用程式呼叫 `AirDefensePipeline.Add`、`AirCombatEvents.Raised`、`AirDefenseExtensions.AddProvider`。不需要寫 C# 的數值調整用 `AirDefenseModifier_Factor`。目前沒有出貨的 Def 使用。

已知限制：

- 外部航跡的詳細資料不會進玩家介面；是否在世界地圖隱藏由物件自己決定，可用 `TrackDetected` 得知被偵測。
- `TicksToArrival` 超過火控 `maxRangeTicks` 時不會接戰，長航程的外部航跡要等進入距離。
- 封鎖攔截只處理 `BlockadeHitUtility.InFlight` 列出的載具，外部航跡不會被封鎖圈攔截。
- 導引干擾只影響飛彈；外部航跡不受地面干擾機影響。
- 地圖層沒有航跡，`MissileBehavior` 實例的狀態到不了地圖層；要兩層都生效請覆寫 Props 上的 `ModifyDefenseAs*`。

### B-9. 攔截高度與高度航跡

**攔截高度**：`CompProperties_FireControlRadar.minInterceptAltitude`（0 = 不限）、`maxInterceptAltitude`（負值 = 不限）。

| 系統 | 範圍 | 打不到的出貨飛彈 |
|---|---|---|
| 早期火控 `DMSE_EarlyFireControlRadar`、野戰防空 `DMSE_Building_FieldAirDefense` | 100 m – 20 km | 掠海（50–60 m）；彈道（30 km 以上） |
| 火控雷達 `DMSE_FireControlRadar` | 50 m – 40 km | 高空彈道（50 km、100 km） |
| 相位陣列 `DMSE_PhaseArrayRadar` | 30 m – 150 km | 無 |
| 肩射 SAM | 30 m – 5 km | 彈道 |

抽象據點沿用對應雷達：導彈陣地是相位陣列，野戰發射站是火控雷達，一般據點看陣營準則（出貨預設：Industrial 早期火控，Spacer 以上相位陣列）。

- 範圍外不鎖定也不發射；`BVRNode.CanReachAltitude` 為 false 的節點不佔接戰權，由其他節點接手。
- 接近上下限時命中率下降：離界線不到 `altitudeEdgeRatio`（1.5 倍）開始，到界線時乘 `altitudeEdgeHitFactor`（0.6）。寫在 `ComputeHitChance`，所有呼叫端都會套用。
- 世界層鎖定時看目前高度，發射時看會合點高度（`IAirTrack.AltitudeAt`）；會合點在範圍外就放掉鎖定。
- 末端 CIWS 不受限：目標最後一定會俯衝。
- 封鎖攔截的載具沒有高度資料，以 `BVRTarget` 預設的 1000 m 計算。
- 只有防空受影響；偵測仍只看 `AltitudeFactor`。
- 改變高度的行為要覆寫 `MissileBehavior.ModifyAltitudeAt`，否則預測不到（`ModifySignature` 只影響目前高度）。
- 打不到時航跡狀態顯示 `DMSE.BVR.World.AltitudeDenied`；地圖層目前沒有對應顯示。

**高度航跡**（`BVRWorldDrawer.DrawAltitudeTracks`）：純顯示。只畫玩家發射或已被玩家偵測的航跡，畫剩餘路線：

- 抬升量依 `AirTrackDisplay.Lift`（對數縮放，在 `Fortified`）；虛線是地面投影，兩者距離就是高度。世界相機由上往下看，所以偏離畫面中心時最明顯。
- 飛彈、空投幽靈與攔截彈的 `DrawPos` 畫在目前高度，一條垂直線連回地面位置；擊毀閃光也在該高度。判定一律用貼地的 `PositionAt`，不要讀這些物件的 `DrawPos`。外部航跡要自己用 `AirTrackDisplay.CurrentLift` 抬高。
- 顏色分段：低空（青，500 m 以下）、中空（綠，5 km 以下）、高空（黃，20 km 以下）、彈道（洋紅）。線寬也隨高度增加。
- 頭尾的爬升與俯衝只是線條落回地面，模擬裡沒有這段高度變化。
- 攔截彈的點線會爬到會合點高度；檢視面板顯示目前高度，火控雷達面板顯示攔截高度。

## C. 超視距火砲（`.source/DMSE/Artillery/`）

| 角色 | 類別 | 職責 |
|---|---|---|
| 核心 | `CompArtilleryStrike`（繼承 `CompBVRDevice`，不註冊） | 世界選靶、持續射擊狀態機、彈藥／砲管、砲管轉向、選配借火控通道 |
| 離場 | `Projectile_ArtilleryDeparture` | 弧線飛到目標方位的出口邊緣格，`Impact` 建立世界航跡 |
| 世界航跡 | `WorldObject_ArtilleryStrike` | Slerp（`speed/10 × 0.00025 / 距離`）；`Arrived` 解析地圖（目標地圖必須已存在，未載入即落空，不生成地圖）、排程齊射 |
| 排程 | `MapComponent_ArtilleryStrikes`＋`ArtilleryShellRequest` | `fireTick`、`targetCell`、`entryEdgeCell`、`projectileDef`、`attackerFaction`、`scatterRadius`、`guided`、`fireForEffect`；各地圖各陣營的效力射落點（`ArtilleryImpactRegistration`） |
| 工具 | `ArtilleryStrikeUtility` | `WorldDirectionAngle`（原版 `PlanetLayer.GetHeadingFromTo`，與世界地圖畫面上的方位一致）、`EdgeCellFromAngle`、`CalcEntryEdgeCell`、`QueueSalvo`、`FindGuidedTarget`、`SpawnShell`（校射彈登錄落點） |

Props：`departureProjectileDef`、`worldObjectDef`（必填）、`projectileDef`（可留空：可換彈藥的砲由砲手載入的彈種決定）、`cooldownTicks`、`warmupTicks`、`scatterRadius`、`salvoCount`、`salvoIntervalTicks`、`requiresFireControlChannel`（無 Def 使用）、`fireControlHoldTicks`、`guidedScatterRadius`（<0 不支援導引）、`maxRangeTiles`、`refuelableIsAmmo`。Empero：冷卻 1800、瞄準 300、散布 14／導引 3、射程 20、砲彈 12；另有本地圖減裝藥曲射（共用砲彈、世界射擊期間停火）、hold fire／強制目標。

流程：Gizmo 選靶（同圖層、非本地圖、射程內、**目標地圖已載入（spawned）**、**效力射需該地圖已有校射落點**）→ 持續射擊（`!Active` 暫停、無彈停止、等通道、效力射無落點待命）→ 瞄準 warmup（砲管轉向世界方位）→ `FireSingleShot`（耗彈、出口格 = 世界方位、校射＝`targetCell` 地圖中心／效力射＝已登錄落點且每發收束 25%；效力射不找導引，校射開火瞬間搶到通道則 guided）→ 離場 → 世界航跡 → `QueueSalvo`（進入邊 = 從發射地一側）→ `SpawnShell`（guided 每發重挑 `FindGuidedTarget`：砲塔 → 用電設備 → 人員；散布；校射彈把實際落點登錄到地圖（每陣營一筆，同陣營共用）；`launcher = null`）→ 原版爆炸。

- 重型迫擊砲 `DMSE_Turret_HeavyMortar`：原版 `Building_TurretGun` ＋ 掛在 gun 上的 `CompChangeableProjectile`。`CompArtilleryStrike` 從建築或 gun 找可換彈藥 comp，開火時用砲手載入彈種的 `projectileWhenLoaded` 當 BVR 投射物，並以 `Notify_ProjectileLaunched` 扣彈；沒彈時任務暫停等砲手補彈（`DMSE.Artillery.WaitingForShell`），沒砲手時 `Active` 為 false（選靶回 `NoCrew`／`NoLoadedShell`）。世界砲擊中 `Building_HeavyMortar.TryFindNewTarget` 不接本地目標，Tick 結束後把 `TurretTop.CurRotation` 拉回世界方位。
- 與 BVR 共用火控通道：`FreeArtilleryChannels = maxTargets − 借給火砲 − 按比例分攤的 BVR 占用`；`MidcourseDefense` 反向扣除。
- 砲彈是 Projectile，**BVR 不攔截**。不可跨圖層。
- 存檔：冷卻、`targetTile`（`PlanetTile` 完整 tileId,layerId）、`channelReleaseTick`、借用雷達、在途齊射、射擊模式（`artilleryFireForEffect`）與效力射收束發數、各地圖各陣營的落點。
- 已知：傷害歸因 `launcher: null`（`attackerFaction` 未參與）；`DMSE_Turret_Empero` 是 `Building` 不是 `Building_Turret`，`FindGuidedTarget` 不會把它當 tier 0。見[反砲兵提案](反砲兵雷達_實作計畫.md)。

---

## D. 檔案地圖

- **BVR 核心** `.source/DMSE/SkyFallerTurret/BVR/`：`CompBVRDevice`、`MapComponent_BVRCombat`、`BVRWave`、`BVRTargetProps`、`CompSearchRadar`、`CompFireControlRadar`、`CompMissileLauncher`（Interceptor）、`CompMannableMissileLauncher`、`JobDriver_ManMissileLauncher`、`CompTerminalCIWS`、`CompTurretFacingRenderer`、`MapComponent_InterceptEffects`、`InterceptDebris/`、`World/`。
- **BVR 周邊** `.source/DMSE/SkyFallerTurret/`：`InterceptProjectile`、`Patch_MakeDropPodAt`、`Alerts_*`、`Patch_QuestPart_Bossgroup`、`ManPortableSAM`。只剩 superseded 註解的空檔：`CompProperties_InterceptRadar`、`CompSkyfallerTurret`、`EarlyWarningUtility`、`InterceptSkyfallerUtility`、`MapComponent_InterceptSkyfaller`。
- **導彈** `.source/DMSE/Scorer/`：`Assembly/`（Defs、Config、Effects、Guidance、ITab、WorkGiver／JobDriver、`MissileBVRUtility`、`MissileIncoming` 舊存檔轉換）、`Strike/`、`Ballistic/`、`Guidance/`、`MissileWorldObject`、`MissileWorldUtility`、`ScorerProjectile_WorldObject`、`GameComponent_MissileEngage`、`Patch_ShouldBeRemoved`。
- **載體** `.source/DMSE/MissileProjectile/`；**特效** `.source/DMSE/MissileFx/`；**平台容器** `.source/DMSE/`（`CompMissileLauncher`、`Building_MissileRack`、`CompMissileRackRenderer`）。
- **飛彈行為框架** `.source/DMSE/MissileFramework/`（namespace `Fortified`，見 B-7）。
- **NPC 陣地** `.source/DMSE/MissileBase/`；**火砲** `.source/DMSE/Artillery/`；**VGE** `.source/GravshipExpanded/PointDefenceCompat`。
- **Defs** `1.7/Defs/ThingDefs_Buildings/`、`ThingDefs_Items/`、`Missile/`、`WorldObjects_*.xml`、`DMSE_BVRWorld.xml`、`Effects/`。

## E. 未解問題與手動測試

- 火砲傷害歸因未生效（見 C）。
- `ImpactCraterUtility`（撞擊飛行）仍用 `ApproxDistanceInTiles(PlanetTile, PlanetTile)`，跨圖層錯誤。
- 原版事實：新接上電網的耗電建築逐一開啟（30–200 ticks），`DebugSettings.unlimitedPower` 只對已在電網內的有效；非世界畫面時世界物件每 15 ticks 更新。
- 自動驗收未涵蓋、仍需手動：裝配 UI、發射 UI、地圖層空爆一彈多殺、導彈陣地摧毀信件、跨圖層拒絕／允許、逃逸區用完次數後落空、對空白 tile 發射落空、雷射標點完整流程、舊存檔。驗收與單元測試見 `.source/DMSE.BVRAcceptance/README.md`、`.source/DMSE.BVRTests/README.md`。
