# DMSE 遠程作戰系統 — 應用流程與實作說明

> 最後校對：2026-10-18（依 `.source/DMSE` 現行程式碼與 `1.7/Defs` 全面覆核；`1.6/` 已凍結為 Workshop 版，不在本文範圍）
> 同日併入 9c3f5c5：跨圖層打擊、終端更新（逃逸區）、落空規則與世界 tile 圖層修正（A-3、B-4～B-6、C、F、G）

> 同日世界層 BVR 更新：導彈正式航跡改為「世界偵測 → 世界 SAM 會合 → 抵達後 CIWS 末端」；本文 A 節的完整地圖內窗口仍適用空投、舊波次與直接生成的後備導彈。新的世界速度（150 ticks/格）、通道帳本、NPC 抽象防空、XML 與驗收狀態見 [BVR 世界層重構 §15](BVR_世界層偵測攔截重構.md#15-實作與驗證記錄2026-10-18)。

> 2026-10-19 波次打擊 P0：攔截彈（`MissileRoleExtension` = Interceptor）不再被對地發射軌當成輕型導彈射出；世界選靶平台改以「指定彈藥」發射（`TryLaunchRound`）；被打擊任務保留的導彈不會被單發、SAM 或裝配取用；世界防空改為依 ETA 先到先打。同日 P1：新增打擊指揮中心（`DMSE_StrikeCommandCenter`／`CompStrikeConsole`），可把在架進攻導彈分波發射，波次占用火控通道（帳本 Strike 分項）；沒有可用攔截彈的節點不再鎖定。P2：新增打擊規劃桌（`DMSE_StrikePlanningTable`，人工版），確認後由研究者計算彈道，依波次逐枚解出。P3：抵達時間軸與順序警示、組別展開指定單枚、執行中調整波次（增量計算）、打擊航跡分組警報與戰果彙總信件。P4：定時發射、目標移動 ≥ 5 格重算、誘餌酬載（`signatureFactor`）與威脅評估、NPC 陣地齊射。詳見 [波次打擊規劃 §15](BVR_波次打擊規劃.md#15-實作與驗證記錄2026-10-19)。

本文整理三套同屬「超視距作戰」的系統：

- **A. 超視距攔截系統（BVR）** — 偵測並攔截敵方來襲空投（drop pod）**與來襲導彈**（`MissileIncoming`）。
- **B. 導彈自定義裝配與發射系統** — 玩家自組導彈、由小人裝配、發射打擊地圖外目標；NPC 導彈陣地也走同一條導彈鏈。
- **C. 超視距火砲系統（Artillery）** — 世界選靶的持續砲擊：砲彈以 `Projectile` 離場 → 世界航跡 → 從目標地圖邊緣飛入。**1.7 目前沒有任何 Def 使用**，屬「僅程式碼」狀態。

皆以 **ThingComp** 組合到建築/物品上，達成「單一建築複合功能」。系統間的實質交互：SAM 以裝配系統產出的實體導彈為彈藥、戰鬥部（`WarheadEffect`）直接影響攔截判定；進攻導彈可掛反輻射導引頭反制雷達；火砲與 BVR 中過程攔截**共用火控雷達的火力通道**；所有發射平台共用 `CompMissileLauncher` 基類與 `CompBVRDevice` 的可用性判定。

---

## A. 超視距攔截系統（BVR）

### A-1. 角色：建築 × ThingComp

| 角色 | ThingComp（Props） | 關鍵參數 |
|---|---|---|
| 搜索雷達 | `CompSearchRadar`（`CompProperties_SearchRadar`） | `searchDistance`、`powerLevel`（**超線性**，指數 `PowerWindowExponent=2`）、`antiStealthLevel`、`ticksPerDistance`、`immuneToAntiRadiationSeeker` |
| 火控雷達 | `CompFireControlRadar`（`CompProperties_FireControlRadar`） | `maxTargets`（火力通道）、`powerLevel`（隱身對衝）、`guidanceAccuracyTime`、`maxRangeTicks`、`maxHitChance`、`stealthMissFactor`、`lockOnTicks`、`immuneToAntiRadiationSeeker`、`supportsArtilleryGuidance`（通道可借給火砲，見 C-3） |
| 導彈發射裝置（SAM） | `CompMissileLauncher_Interceptor`（Props 仍名 `CompProperties_MissileLauncher`） | `interceptorSkyfaller`、`reloadCooldownTicks`、`interceptorTravelTicks`；**彈藥為容器中的實體導彈**（見 A-6） |
| 末端攔截砲塔（CIWS） | `CompTerminalCIWS`（`CompProperties_TerminalCIWS`） | `terminalWindowTicks`、`cooldownTicks`、`shotsPerBurst`、`ticksBetweenShots`、`interceptChance`（**整輪連射只判定一次**）、`debrisChance`、`fuelPerShot`、瞄準視覺（`aimLeadCells`/`aimSweepCells`/`aimHeightFar`/`aimHeightNear`）、`interceptSound`、曳光彈 `projectile` |

所有裝置繼承 `CompBVRDevice`，統一 `Active` 判定：已生成且未銷毀／未損壞（Breakdownable）／開關開啟（Flickable）／通電（PowerTrader）／未被擊暈（Stunnable）／**掛有 `CompMannable` 時必須有人操作**（與 `CompTurretFacingRenderer` 的轉向判定一致，視覺停轉與實際停火同步）。搜索／火控／SAM／CIWS 四者於 `PostSpawnSetup`/`PostDeSpawn` 自行向地圖中央管理器註冊。

`CompMissileLauncher_Interceptor` 屬於發射平台家族（`.source/DMSE/CompMissileLauncher.cs`，見 B-4）：抽象基類 `CompMissileLauncher` 再疊加 `HasAmmo`，所以 **SAM 要有彈才算 Active**。家族中只有 Interceptor 會向 BVR 註冊；世界選靶發射平台與火砲雖然也繼承 `CompBVRDevice`，但只借用可用性判定。

**1.7 現行配置**（數值取自 `1.7/Defs`）：

| 建築 | 角色 | 重點數值 |
|---|---|---|
| `DMSE_AcousticEarlyWarningRadar` 早期預警雷達 | 搜索（無火控） | 距離 120、功率 1、反隱身 0、`ticksPerDistance` 10、**反輻射免疫** |
| `DMSE_PhaseArrayRadar` 相控陣雷達 | 搜索 + 火控 | 搜索 200/功率 2/反隱身 2/`tpd` 4；火控 2 通道、功率 2、`maxHitChance` 0.9、`lockOnTicks` 120、`maxRangeTicks` 6000 |
| `DMSE_EarlyFireControlRadar` 早期火控雷達 | 火控（**需人操作**） | 2 通道、功率 1、`maxHitChance` 0.75、`stealthMissFactor` 0.6、`lockOnTicks` 200、`maxRangeTicks` 4000、`supportsArtilleryGuidance` |
| `DMSE_Building_RotaryMissileLauncher` 旋轉發射架 | SAM（**需人操作**） | 容量 1、裝填冷卻 200、攔截彈飛行 180；收 `DMSE_InterceptorMissile`／`DMSE_Missile_Termite`；砲塔頭繪製在架導彈 |
| `DMSE_Building_LightMissileLauncher` 輕型導彈發射箱 | **SAM + 對地發射軌（共用彈艙）** | 容量 4；SAM 冷卻 240／飛行 300；對地冷卻 450 |
| `DMSE_Turret_Vulcan_WingBack` GAU-4 | CIWS | 窗口 1800、每輪 10 發、間隔 4、冷卻 20、`interceptChance` 0.25、`debrisChance` 0.3、每發耗 1 砲管（容量 600） |

**純視覺/UI 配件**（不參與判定）：

- `CompTurretFacingRenderer` — 通用砲塔頭渲染：讀取本陣營最緊急（`tickToImpact` 最早）的目標平滑轉向，無目標時閒置擺動；裝置不可運作時停止追蹤。可選 `drawLoadedAmmo` 讓在架導彈隨砲塔頭旋轉。
- `MapComponent_InterceptEffects` — 攔截成功時在目標上方**地圖外高空**繪製閃光 + 音效（中過程與末端參數不同）；材質於啟動時以 `[StaticConstructorOnStartup]` 在主執行緒建立。
- `WeatherEvent_MissileFlash` — 導彈落地時的全地圖爆炸閃光（強度∝爆炸半徑）。
- `Alerts_BVRStatus` — 常駐警報：玩家在當前地圖持有任一**搜索或火控雷達**即顯示雷達清單與火力通道占用（`CountEngaged()/Σ運作中火控 maxTargets`；`CountEngaged` 計入該地圖所有波次）。
- `Alerts_Pods` — 玩家為防禦方時，顯示最早抵達波次的中過程／末端剩餘時間與逐目標狀態（攔截彈在飛／鎖定中／未接戰），點擊跳到首目標。
- `Alerts_PlayerMissiles` — 玩家為**攻擊方**、導彈被敵方 BVR 追蹤時顯示；左鍵／右鍵正反向循環跳轉相機至各彈落點（可跨地圖）。
- `CompEffecterColdLaunch`／`CompEffecterHotLaunch` — 冷射／熱射排氣特效；`ScorerProjectile` 與 `InterceptProjectile` 皆實作 `ILaunchTarget` 提供繪製位置。

### A-2. 中央管理器與資料模型

`MapComponent_BVRCombat`（每張地圖一個）持有：

- `List<BVRWave> Waves` — 波次：`tickToImpact`、`targets`、`defenderFaction`（負責攔截的防禦方）、`lord`、`signal`。
- `BVRTarget` — 以 `ThingOwner<Skyfaller>` 收起 incoming skyfaller；`position`（預測落點）、`stealthLevel`、`speed`（來自 def 的 `BVRTargetProps`）、`lockUntil`（火控鎖定計時）、`midcourseEngagedUntil`（攔截彈在飛計時）、唯一 `id`（`nextTargetId` 遞增並存檔）。`ContainedPawns` 回傳艙內**所有** pawn。
- 四個裝置註冊表（搜索／火控／SAM／CIWS，`HashSet`）。
- `BVRTargetProps`（DefModExtension，掛在目標 skyfaller def）：`distanceLevel`（越高越早被偵測）、`stealthLevel`、`speed`（越快越難攔截）。無擴充時用 `Default`（1／0／15）。
- `BVRTuning` — 集中平衡常數：`PowerWindowExponent=2`、`AntiStealthBonusPerLevel=0.5`、`StealthDetectFactor=0.4`、`MergeWindowTicks=600`、`WindowPenaltyPerExtraTarget=60`。

1.7 目標屬性：

| 目標 def | `distanceLevel` | `stealthLevel` | `speed` |
|---|---|---|---|
| 空投艙等（無擴充，`Default`） | 1 | 0 | 15 |
| `DMSE_Incoming_Loiter` 巡飛彈 | 1 | 0 | 15 |
| `DMSE_Incoming_LightMissile` 輕型導彈 | 1.2 | 0 | 22 |
| `DMSE_Incoming_CruiseMissile` 巡航導彈 | 1.5 | 1 | 30 |
| `DMSE_Incoming_BallisticMissile` 彈道導彈 | 2 | 2 | 80 |

### A-3. 目標進入系統的路徑

1. **敵對空投**：`Patch_MakeDropPodAt`（postfix on `SkyfallerMaker.SpawnSkyfaller(ThingDef, Thing, IntVec3, Map)` 這個多載）→ `BVRDetection.GetDropFaction`：以 `PawnsIn` 列出內容物 pawn（`innerThing` 本身，或 `ActiveTransporter` 艙內的**全部** pawn；艙內可同時有補給等其他物品），取第一個有陣營者 → `ResolveDefender` 找防禦方 → 有則 `DeSpawn` + `RegisterIncoming`。攔截彈自身（`InterceptProjectile`）豁免。
2. **來襲導彈**：**生成端同步登記**。`WorldObject_IncomingMissile.Arrive`（敵方）與 `ScorerProjectile_WorldObject.SpawnIncoming`（玩家）在生成 `MissileIncoming`、設好 `config`/`attacker` 後立即呼叫 `MissileBVRUtility.TryRegister`。`MissileIncoming` 的首次 `Tick` 自註冊保留作後備（其他生成路徑，如 Debug 單發），`bvrHandled`/`checkedBvr` 旗標避免重複登記。`Patch_SpawnMissileIncoming` 已**廢棄留空**（spawn-time patch 執行時 `attacker`/`config` 尚未設定）。
3. **測試**：`RegisterSalvo`（Debug「Enemy missile salvo」）直接生成含 N 枚導彈的單一波次，繞過世界航跡；無防禦方雷達時改為直接落地。

**敵方導彈的來源**：

- **NPC 導彈陣地**（`DMSE_MissileBase` Site，`.source/DMSE/MissileBase/`）：
  - 地圖未生成：`WorldObjectComp_MissileBase` 每 1–2 天（`missileFireIntervalDays`）對這枚導彈打得到的玩家地圖（`MissileWorldUtility.CanEngage`：陣地射程 20 與導彈射程取小，跨圖層需導引能力）隨機選一個發射，設定為 `missileBody` 的空白 config（留空時取 `DefDatabase` 第一個 `MissileBodyDef`，取決於載入順序，建議在 XML 明確指定）；15–30 天後陣地轉移（站點消失）。
  - 地圖已生成：改由 `MapComponent_MissileBase` 每 500 ticks 推進。發射通道數為地圖上敵方運作中火控雷達的 `maxTargets` 總和，每個空閒通道從找到的第一個存有可發射導彈（帶 `CompMissileConfig`）的敵方 `Building_MissileRack` 取一枚導彈（用該彈的 config），先以同一套 `CanEngage` 篩出它打得到的玩家地圖，沒有目標就保留在架上不消耗；發射時把目標據點設為 `targetParent`（供終端更新追蹤），`WorldObject_IncomingMissile` 銷毀（抵達或落空）後釋放通道。玩家觀察中且敵方世界選靶發射器（`CompMissileLauncher_WorldTargeting`）全數被摧毀時，陣地消失。陣地結構（FFF `LaunchSite_A`）放置相控陣雷達、4 座 `DMSE_Building_Scorer`、2 座輕型導彈發射箱（含 SAM）與 WingBack CIWS，彈架由 `MissileAutoFillUtility` 依陣營科技等級自動填彈。輕型發射箱的彈藥同時是陣地 SAM 的攔截彈與對外打擊的彈源。
- **Debug**：`Enemy missile strike (incoming, world)` 走世界航跡；`single`／`volley x5` 直接生成於指定格。

### A-4. 預警窗口與波次合併

```
window = Σ(各運作中防禦方搜索雷達的貢獻) × distanceLevel ÷ targetCount
單雷達貢獻 = searchDistance × ticksPerDistance × max(1, powerLevel)^2 × 隱身係數
隱身係數  = antiStealthLevel <  stealthLevel → 0.4（StealthDetectFactor）
            antiStealthLevel >= stealthLevel → 1 + 0.5 × (antiStealth − stealth)
```

回傳 -1（防禦方無運作中搜索雷達）則不攔截、照常落地。`tickToImpact = now + window`。

**波次合併**：同防禦方、預測抵達相差 ≤ 600 ticks（`MergeWindowTicks`）的目標併入同一波次；合併後取最早抵達 tick，且每多一個目標再縮短 60 ticks（`WindowPenaltyPerExtraTarget`，下限 now+1）。

**1.7 單雷達、單目標的窗口試算**（ticks；60 ticks = 1 秒）：

| 目標 | 早期預警雷達（1200 基值） | 相控陣雷達（3200 基值） |
|---|---|---|
| 空投艙 | 1200 | 6400 |
| 輕型導彈 | 1440 | 7680 |
| 巡航導彈（隱身 1） | 720（反隱身不足 ×0.4） | 7200 |
| 彈道導彈（隱身 2） | 960（反隱身不足 ×0.4） | 6400 |

多座雷達的貢獻相加。**注意**：末端窗口（WingBack 為 1800）內一律走 CIWS；只有早期預警雷達時窗口都 ≤ 1800，若同時有 CIWS，目標一進系統就直接進入末端，**中過程 SAM 不會出手**。

### A-5. 線性時間線（偵測 → 落地）

```
每 tick：MapComponent_BVRCombat.MapComponentTick → 由 Waves 尾端往前，依 timeLeft 分階段
   │
   ├─ timeLeft > 末端窗口  →  中過程（MidcourseDefense）
   │     • 火力通道容量 = Σ 防禦方運作中火控雷達的 max(0, maxTargets − OccupiedArtilleryChannels)
   │                       （共用池；僅計 timeLeft ≤ maxRangeTicks 的雷達；已借給火砲的通道先扣除）
   │     • 占用數 CountEngaged = 全部波次中 lockUntil ≥ 0 或攔截彈在飛的目標數
   │     ① 鎖定：未鎖定的新目標，若占用 < 容量，且防禦方有可用攔截彈（任一 Active 的 SAM，冷卻中也算）
   │           → 選命中率最高的火控雷達開始鎖定；沒有可用攔截彈時不鎖定，已有的鎖定也釋放（2026-10-19）
   │           lockUntil = now + lockOnTicks × (1 + 0.5×隱身差 + max(0, speed−15)/60)
   │     ② 發射：鎖定完成（lockUntil ≤ now）→ 最佳火控雷達 + 冷卻最早結束的就緒 SAM
   │           就緒 = Active（含有彈、有人操作）且不在冷卻；SAM 只是冷卻中時目標保持鎖定、續占通道
   │           命中率 = maxHitChance × clamp01((timeLeft − 末端窗口) / guidanceAccuracyTime)
   │                    × 速度係數（>15 每單位 −1.2%，下限 0.2）
   │                    × stealthMissFactor^(stealthLevel − powerLevel)₊
   │                    + 攔截彈戰鬥部加成（連續桿 InterceptBonus = N×0.05），clamp01
   │           CompMissileLauncher_Interceptor.FireInterceptor → 生成 InterceptProjectile（skyfaller，
   │                    朝向同發射架）、消耗容器首枚實體導彈、進入 reloadCooldownTicks
   │           空爆戰鬥部：額外 floor(N/5) 次獨立擲骰攔截同波次其他目標（不耗彈、不占通道）；
   │                    命中者先收集，foreach 結束後才移除
   │           midcourseEngagedUntil = now + interceptorTravelTicks（通道續占）、lockUntil = −1
   │     ③ 結算：InterceptProjectile.LeaveMap → 釋放通道（midcourseEngagedUntil = −1）→ 擲 hitChance
   │           成功 → 地圖外攔截閃光 + RemoveTarget（乾淨消滅，【不留碎片】）
   │           落空 → 目標存活，下一 tick 重新排隊鎖定
   │
   ├─ timeLeft ≤ 末端窗口  →  末端（TerminalDefense）
   │     • 末端窗口 = 防禦方運作中 CIWS 的最大 terminalWindowTicks；沒有 CIWS 則為 0（全程中過程）
   │     • 每具 CIWS 獨立節奏、每 tick 只處理一次：冷卻完＋有彈料 → 對己方末端波次中
   │           tickToImpact 最早者的 targets[0] 開始一輪連射；連射中跨所有末端波次以 burstTargetId 找目標
   │           整輪鎖定同一目標；每發間隔 ticksBetweenShots、耗 fuelPerShot（CompRefuelable，
   │           例如裝甲砲管耐久；無 Refuelable 則無限）；目標消失或斷料 → 中止本輪、不判定
   │           視覺：砲塔轉向天空格（提前量 + 線性掃射 + 依迫近度下移瞄準高度）、槍口閃光、曳光彈
   │     • 連射全部打完後【整輪只擲一次】interceptChance：
   │           命中 → 末端攔截閃光；再擲 debrisChance：
   │                 過 → SpawnDebris：殺死艙內所有 pawn，屍體合併成一個殘骸空投落下，
   │                      其餘內容物隨艙體清除【碎片只在此產生】
   │                      ※ 無 pawn 的目標（如導彈）不產生殘骸，等同乾淨清除
   │                 未過 → 乾淨消滅
   │     • 判定（或中止）後進入 cooldownTicks
   │
   └─ timeLeft ≤ 0  →  ResolveWave：未被攔截的目標真正落地（TryDrop incoming skyfaller），
         發送 wave.signal；wave.lord（由 Patch_QuestPart_Bossgroup 為 Boss 群波次設定）
         只會收編 skyfaller 直接持有的 pawn，ActiveTransporter 艙內的 pawn 不經此路徑
```

**1.7 火控試算**（導引時間充足、無戰鬥部加成）：

| 目標 | 相控陣：命中率／鎖定 ticks | 早期火控：命中率／鎖定 ticks |
|---|---|---|
| 空投艙 | 0.90／120 | 0.75／200 |
| 輕型導彈 | 0.82／134 | 0.69／223 |
| 巡航導彈 | 0.74／150 | 0.62／250 |
| 彈道導彈 | 0.20／250 | 0.10／517 |

輕型導彈作攔截彈時 N=3，連續桿戰鬥部 +0.15。

### A-6. SAM 彈藥：實體導彈與裝填路徑

發射器 parent 為 `Building_MissileRack`（`IThingHolder` + `IHaulDestination` 容器建築），攔截彈以**實體導彈 Thing** 逐枚存放。容量來自 def 的 `MissileRackExtension.capacity`（發射平台用子類 `MissileLauncherExtension`，預設 1）；能收哪種彈由建築的 storage filter 決定。

裝填路徑：

- **地面搬運**：散落在地的導彈經原版 `IHaulDestination` 搬運系統直接放入。
- **彈架 → 發射平台**：`WorkGiver_LoadMissileLauncher` + `JobDriver_LoadMissileLauncher`。儲存架內的導彈是 unspawned，原版搬運看不到，由此補上；目標為掛 `CompMissileLauncher_Rail` 或 `_Interceptor` 的未滿平台，來源只取純儲存架。
- **操作員自行補彈**：旋轉發射架以 `CompMannableMissileLauncher` 取代原版 `CompMannable` 的右鍵選項，改派 `DMSE_ManMissileLauncher`（`JobDriver_ManMissileLauncher`）。操作員先到彈架取彈、容器對容器搬入，直到裝滿才開始操作；操作中彈艙出現空槽就中斷回去取彈。找不到來源但艙內仍有彈時照常操作，完全無彈才放棄指令。
- **敵方陣地**：`Building_MissileRack.SpawnSetup` 呼叫 `MissileAutoFillUtility.TryAutoFill`，依 `MissileRackAutoFillExtension` 規則與陣營 TechLevel 選彈填滿（讀檔時跳過）。

發射時取出容器首枚並銷毀；`GetLoadedMissileConfig()` 讀取該彈的 `CompMissileConfig.config`，讓**裝配系統的戰鬥部直接影響攔截**（連續桿 → 命中率加成；空爆 → 額外攔截擲骰，皆以酬載容量 N 縮放）。`CompMissileRackRenderer` 依建築朝向渲染在架彈體（`renderStoredBody=false` 時不畫）。

輕型導彈發射箱同時掛 `CompMissileLauncher_Rail` 與 `_Interceptor`，兩者讀同一個容器：同一批彈既可手動對地發射，也會在來襲時被 BVR 自動調用。

### A-7. 重點設計

- **碎片規則**：中過程攔截＝完全消滅、不落地、不留殘骸；末端攔截命中後依 `debrisChance` 生成殘骸，且僅限含 pawn 的目標；一個艙內多人時屍體合併成一個殘骸空投。
- **火力通道**：共用池（Σ maxTargets，扣除借給火砲的通道）。通道在「鎖定階段」（`lockUntil ≥ 0`）與「攔截彈在飛」（`midcourseEngagedUntil > now`）期間均占用，結算後才釋放；存讀檔安全（in-flight 的 `InterceptProjectile` 以 `targetId`(int) 回連，不持久化引用）。
- **隱身雙層對衝**：搜索段對衝 `antiStealthLevel`（影響預警窗口）；火控段對衝 `powerLevel`（影響命中率與鎖定時間）。目標 `speed` 只影響火控段。
- **陣營相對防禦**（非玩家專屬）：以 `ResolveDefender` 找「擁有運作中搜索雷達、且與來襲方敵對」的陣營作防禦方（玩家優先），整波次只由該陣營裝置參與。玩家空投/導彈突擊敵方基地時**會被敵方防空攔截**（`Alerts_PlayerMissiles` 追蹤己方被攔截彈藥）；自家空投不被自家雷達攔截。
- **人工操作裝置**：早期火控雷達與旋轉發射架掛 `CompMannable`，無人操作時 `Active=false`，不鎖定、不發射、砲塔頭不追蹤。
- **電子對抗閉環**：導彈可裝反輻射導引頭（`GuidanceType_AntiRadiation`），在落點地圖的**敵對建築**中挑開機、`powerLevel` 最高的搜索／火控雷達（玩家打敵方雷達、敵方打玩家雷達都適用）；找不到退化為電磁訊號，再退化為慣性（B-7）。雷達可設 `immuneToAntiRadiationSeeker = true` 豁免（1.7 的早期預警雷達即是）。
- **VGE 近防相容層**（`1.7/GravshipExpanded/PointDefence`，僅載入 VGE 時）：為 WingBack 等砲塔加上 VGE 的 `CompPointDefence`，與 `CompTerminalCIWS` 並存且共用砲管耐久；`PointDefenceCompat` 調整半徑加成、非玩家持有時改用 VGE 敵方命中公式並不攔空投。這一層與 BVR 的波次/通道完全不相通。
- **存檔**：波次/目標經 `ExposeData` 深度持久化；CIWS 連射狀態（`burstShotsLeft`/`nextShotTick`/`burstTargetId`）與各發射器冷卻亦持久化（各平台沿用各自的 Scribe 鍵以維持舊檔相容）。

---

## B. 導彈自定義裝配與發射系統

### B-1. Def 架構與四階分級

- `MissileBodyDef`（彈體）：基礎參數 + 裝配槽位（`slots`）+ 對應落點 `incomingSkyfaller`。
  - 數值：`baseExplosionRadius`、`baseDamageDef`、`baseDamageAmount`、`baseScatter`、`baseWorldSpeedFactor`、`baseRange`、`payloadCapacity`（N，預設 5）
  - 燃料/推進：`baseFuel`、`baseSpecificImpulse`、`rangePerFuelImpulse`
  - 裝配：`assembleWorkAmount`、`refundFraction`
  - 分級：`sizeClass`（`Loitering`／`Light`／`Medium`／`Ballistic`）、`launchMode`（`Tilt`／`Vertical`／`Both`）、`customizeLocation`（`None`／`Item`／`StorageBuilding`／`Launcher`）
- `MissilePartDef`（部件）：屬於某 `MissilePartCategory`（`Warhead`／`Guidance`／`Propulsion`／`Payload`），提供數值修正與成本。
  - 數值修正：`explosionRadiusOffset`、`damageDefOverride`、`damageAmountOffset`、`scatterOffset`、`worldSpeedFactorOffset`、`specificImpulseOffset`、`fuelOffset`、`rangeOffset`；舊式酬載欄位（`payloadExplosionRadius` 等）作後備
  - **行為類**（XML `Class=` 指定子類）：
    - `warheadEffect`（8 種）：`ContinuousRod`（攔截 +N×5%）、`ExtendedFuel`（射程 +N×2）、`Airburst`（攔截額外 floor(N/5) 擲骰；對地 N 半徑爆炸）、`TungstenPenetrator`（坍塌厚岩頂）、`Incendiary`、`EMP`、`AnimalBerserk`、`BerserkPulse`
    - `guidanceType`：`Inertial`、`AntiRadiation`、`HeatSource`（舊名 `Infrared`）、`ElectromagneticSignal`（舊名 `HighEnergy`）、`TV`、`Laser`、`FireControl`。決定導彈抵達後的落點，玩家與敵方發射都適用；只有 `TV` 在玩家發射時改為手動選點（見 B-7）
  - 彈體另有 `builtInGuidance`：沒有導引部件時用來選目標（1.7 巡飛彈為電視制導），不提供導引能力
    - `payloadEffect`（8 種）：`None`、`HighExplosive`、`Cluster`、`Defoliant`、`FireFoam`、`Antiparticle`、`ToxicGas`、`AirburstTungsten`（射程 ×0.75）
  - 限制：`compatibleBodies`、`researchPrerequisites`、`costList`

1.7 彈體：

| 彈體 | 分級／發射方式 | 自定義位置 | N | 燃料×比衝 | 槽位 |
|---|---|---|---|---|---|
| `DMSE_MissileBody_Loiter` 巡飛彈 | Loitering／Tilt | None | 5 | 60×2 | 無（內建電視制導） |
| `DMSE_MissileBody_Light` 輕型導彈 | Light／Both | Item | 3 | 70×2.5 | 彈頭/導引/酬載 |
| `DMSE_MissileBody_Cruise` 巡航導彈 | Medium／Both | Item | 5 | 100×3 | 彈頭/導引/推進/酬載 |
| `DMSE_MissileBody_Ballistic` 彈道導彈 | Ballistic／Vertical | Launcher | 8 | 150×3 | 彈頭/導引/酬載 |

平台側以 `MissileLauncherExtension`（`sizeClass`、`launchMode`、`capacity`、`isStorage`）標記。`MissileTaxonomy.PlatformAccepts` 與 `customizeLocation` 目前**只是資料標記、程式中沒有呼叫端**；實際能裝什麼彈由各建築的 storage filter 決定，ITab 是否鎖定彈體則看 `IsBodyLocked`（見 B-3）。垂直發射的建造限制在 `DMSE_LauncherBases.xml`（純 XML）。

### B-2. 設定資料模型

- `MissileConfig`（彈體 + 每槽位最多一部件）提供有效數值：`ExplosionRadius`／`DamageDef`／`DamageAmount`／`Scatter`／`WorldSpeedFactor`／`PayloadCapacity`(N)
  - `Fuel = baseFuel + Σ fuelOffset`；`SpecificImpulse = baseSpecificImpulse + Σ specificImpulseOffset`
  - **`Range = round((Fuel × SpecificImpulse × rangePerFuelImpulse + baseRange + Σ rangeOffset + 戰鬥部 RangeOffset(N)) × 酬載 RangeFactor(N))`**，下限 0（0 = 不限）
- `CompMissileConfig`（掛在導彈物品；彈道井與舊 Scorer 平台也掛一份）：`config`（已套用）、`pending`（ITab 目標設定）、`delivered`（已搬未耗資源）、`assemblyConfirmed`（玩家已按「確認裝配」）、`NeedsAssembly = config ≠ pending`

### B-3. 裝配流程（小人搬運）

```
玩家選取導彈物品，或導彈儲存架（編輯架內第一枚）→ ITab「裝配」分頁
   • 選彈體（選取物為建築，或物品 def 指定 defaultBody 時鎖定）、各槽位部件
     （FloatMenu 顯示成本、研究未解鎖灰顯、不相容隱藏）
   • 編輯寫入 pending，並清除 assemblyConfirmed；面板即時顯示有效數值、射程與所需資源
   • 按「確認裝配」→ assemblyConfirmed = true（未確認不派工）
   • 「還原為已裝配設定」或改回與 config 相同 → RefundDeliveredIfIdle 退回已搬料
   ▼
WorkGiver_AssembleMissile（掃描地面上的導彈物品 + 架內待裝配的導彈；後者以儲存架作工作目標）
   • NeedsAssembly 且已確認、缺料 → JobDriver_DeliverMissileResource（反覆補發直到備齊）
   • 備齊 → JobDriver_AssembleMissile：施工 assembleWorkAmount → ApplyAssembly
        消耗需求資源；移除部件按 refundFraction 部分退還、多搬的原樣退還；
        config = pending，assemblyConfirmed 清除
   ▼
彈道導彈（無物品型態）：WorkGiver_AssembleBallisticSilo 掃描發射井建築，
   重用 DMSE_DeliverMissileResource 搬料 → JobDriver_AssembleBallisticSilo
   → ApplyAssembly + CompMissileLauncher_Ballistic.MarkLoaded
```

`MissileAssemblyUtility.MissileDefs` 只收 `ThingCategory.Item`，掛著 `CompMissileConfig` 的建築不會被當成可裝配物品；舊 Scorer 平台另由 `Patch_Refuel_CarryConfig` 在裝填時同步 `config`/`pending`、清空 `delivered`。

### B-4. 發射流程（打擊地圖外）

發射平台家族（`.source/DMSE/CompMissileLauncher.cs`；檔名沿用舊名，類別已更名）：

```
CompBVRDevice
 └─ CompMissileLauncher（抽象：冷卻、HasAmmo、Active = base.Active && HasAmmo、檢視字串）
     ├─ CompMissileLauncher_Interceptor   SAM（BVR 調用，見 A-5/A-6）          SkyFallerTurret/BVR/CompMissileLauncher.cs
     └─ CompMissileLauncher_WorldTargeting（抽象：發射 Gizmo、世界選靶、Launch）
         ├─ CompMissileLauncher_Rail        容器實體導彈；導彈無設定時用 Props.missileBody    Scorer/CompMissileRailLauncher.cs
         ├─ CompMissileLauncher_Ballistic   isAssembled 旗標；發射後清空部件、保留 pending   Scorer/Ballistic/CompBallisticLauncher.cs
         └─ CompMissileLauncher_Scorer      CompRefuelable 滿燃料 = 一發（1.7 無 Def 使用）    Scorer/CompScorer.cs
```

1.7 世界選靶平台：`DMSE_Building_Scorer`（巡航，Rail）、`DMSE_Building_LightMissileLauncher`／`DMSE_Building_LightMissileVLS`（輕型，Rail）、`DMSE_Building_LoiteringLauncher`（巡飛彈，Rail）、`DMSE_Building_BallisticSilo`（Ballistic）。

兩種導彈世界航跡（玩家的 `ScorerProjectile_WorldObject`、敵方的 `WorldObject_IncomingMissile`）共用基底 `MissileWorldObject`（`.source/DMSE/Scorer/`）：Slerp 飛行（每 tick 0.00025 弧度 × `WorldSpeedFactor`）、鎖定目標 `targetParent`、終端更新與落空（B-6）、存檔。抵達時以 `MissileWorldUtility.ResolveArrivalMap` 解析目標地圖，再呼叫子類的 `OnArrived(map)`；`map == null` 即落空。

**玩家發射**：

```
發射 Gizmo（無彈／不可運作／冷卻中停用）→ 世界選靶
   • CanTargetTile：排除水域與已有非 MapParent 世界物件的 tile
   • MissileWorldUtility.CanEngage：射程以地表等效格計（跨圖層 +10 格），跨圖層需導引能力
   • 游標旁顯示「距離/射程」；該處沒有據點時提示「將會落空」
   ▼
Launch（發射前再驗一次 Active）
   • 生成離場 ScorerProjectile skyfaller（建築前方 LaunchForwardCells 格、沿建築朝向）
   • 建 ScorerProjectile_WorldObject（陣營 = 玩家、config 副本、落點 skyfaller def、
     targetParent = 點到的 MapParent 或該 tile 上的 MapParent）
   • ConsumeAmmo + 起算冷卻
   ▼
ScorerProjectile.LeaveMap → 世界物件加入世界 → 飛行（目標移動時依 B-6 改飛）
   ▼
OnArrived（AllowGenerateTargetMap = true：目標據點尚無地圖時會生成）
   • map == null → 「未命中」訊息、銷毀
   • 目標據點的陣營非敵對 → 轉敵對
   • GameComponent_MissileEngage 讓該地圖 2 天內不被移除（Patch_ShouldBeRemoved）
   • 依導引頭決定落點（B-7）：
       電視制導（config.PlayerChoosesTarget）→ 遊戲暫停、鏡頭切到目標地圖，玩家選落點格（取消 → 地圖中心）
       其餘導引頭與慣性 → GuidanceType.PickCell 自動鎖定，不暫停，發訊息「{導引}已鎖定目標」
   • SpawnIncoming：生成 MissileIncoming（config 副本、attacker = 玩家）→ 同步 TryRegister（敵方 BVR）
```

選靶游標與 ITab 裝配面板都會顯示落點方式（「{導引}：抵達後由你選擇落點」或「抵達後自動鎖定目標」）。

**敵方發射**（陣地、Debug）：`IncomingMissileUtility.Launch(…, target)` → `WorldObject_IncomingMissile` 飛行 → `OnArrived`（`AllowGenerateTargetMap = false`：只打既有地圖）：落空時若目標是玩家據點發訊息；命中則以 `GuidanceType.PickCell` 自動選格（敵方的電視制導沒有操作員，退化為慣性）→ 生成 `MissileIncoming` → 同步 `TryRegister`；落點為玩家家園時發威脅信件。

**落點結算**（`MissileIncoming.Impact`，先過目標地圖 BVR，見 A-3）：Scatter ≥ 1 時隨機偏移落點 → 主彈體爆炸 → `warheadEffect.Apply` → `payloadEffect.Apply`（或舊式酬載數值欄位後備）→ 爆炸閃光（`WeatherEvent_MissileFlash`）。無有效 config 時以 5 格 Bomb 後備爆炸。

### B-5. 射程模型（燃料 × 比衝）

射程由「彈體燃料量 × 推進比衝」決定，再加上部件與戰鬥部的固定加成、乘上酬載係數。範例：100 燃料 × 比衝 3 × 0.1 = **30 格**；換增程馬達（比衝 +2）→ **50 格**；N=5 的增程燃油戰鬥部再 **+10 格**；改掛空爆鎢芯酬載則整體 **×0.75**。ITab 與發射選靶皆即時可見並強制生效。

距離一律以**地表等效格**計算（`MissileWorldUtility.RangeDistance`：兩點球面夾角以地表格大小換算），跨圖層另加 `CrossLayerRangeCost = 10` 格。不使用原版 `WorldGrid.ApproxDistanceInTiles(PlanetTile, PlanetTile)`：它以第一個 tile 的圖層解讀第二個 tile 的編號，跨圖層時結果錯誤（軌道層讀地表編號還會越界報錯）。同圖層的地表距離與舊算法完全相同；軌道對軌道的距離會比舊算法大（軌道格較大，舊算法以軌道格計數）。

### B-6. 跨圖層打擊與終端更新（逃逸區）

- **導引能力** `MissileConfig.HasTerminalGuidance`：導引槽部件的 `ProvidesTerminalGuidance`。有 `guidanceType` 時看 `GuidanceType.TerminalGuidance`（慣性制導 = false，其餘 = true）；沒有 `guidanceType` 的部件看 XML 欄位 `terminalGuidance`（1.7 的導引部件都已指定 `guidanceType`，巡航的慣性導引例外，視為無導引能力）。未裝導引部件，或彈體沒有導引槽（巡飛彈），都算無導引能力；彈體的 `builtInGuidance`（B-7）只影響選目標，不提供導引能力。
- **跨圖層打擊**（地表 ↔ 軌道）：必須具導引能力，且射程涵蓋含跨圖層成本的距離。玩家選靶、敵方陣地選目標都套用；火砲無導引，一律不可跨圖層。
- **終端更新**：世界航跡鎖定 `targetParent`，每 60 tick 檢查一次；目標換了 tile 時，具導引能力的導彈可從當下位置改飛新位置。次數上限 = 彈體 `terminalUpdates`（輕型 1、巡航 3、彈道 2），且「已飛航程 + 新航段」不得超過射程。次數用完或燃料不足就沿最後位置飛行，抵達時目標已不在即落空——這就是**逃逸區**：目標在導彈用完更新次數後再移動，或移出剩餘射程，就能躲掉。
- 世界航跡的檢視字串顯示剩餘更新次數；ITab 裝配面板顯示導引能力與更新次數。
- **落空**：抵達時目標不在，或打擊的 tile 上沒有地圖也沒有據點 → 銷毀並發訊息。玩家選靶時，若該處沒有目標，游標旁會提示「將會落空」。

### B-7. 導引頭與目標選擇

導彈抵達目標地圖後，`GuidanceType.PickCell(config, map, attacker)` 依 `MissileConfig.TargetingGuidance`（導引部件的 `guidanceType`，沒有時用彈體的 `builtInGuidance`）選落點；導引頭找不到目標時退化為慣性。玩家與敵方發射都套用同一套規則，唯一例外是電視制導：玩家發射時改為手動選點。

「敵對」= 有陣營、不是攻擊方、且與攻擊方敵對（`GuidanceType.IsHostileTarget`）；建築同時檢查玩家與非玩家建築，所以玩家打敵方據點、敵方打玩家都適用。

| 導引頭 | 類別 | 鎖定規則 | 找不到時 | 1.7 部件 |
|---|---|---|---|---|
| 慣性 | `GuidanceType_Inertial`（或無導引部件） | 隨機一棟敵對建築 → 隨機一名敵對 Pawn → 地圖中心 | — | 各彈體的慣性導引 |
| 反輻射 | `GuidanceType_AntiRadiation` | 開機（`ActiveForSeeker`）的敵對搜索／火控雷達中 `powerLevel` 最高者，同級比耗電量；`immuneToAntiRadiationSeeker` 的雷達不列入 | 電磁訊號 → 慣性 | 輕型 `DMSE_Light_Guidance_AntiRadiation` |
| 熱訊號 | `GuidanceType_HeatSource`（`Infrared` 為舊名別名） | 最熱的室內房間若比室外高出 `minRoomTempAboveOutdoor`（15°C）→ 該房間內一格；否則體型最大的 Pawn（攻擊方與非敵對陣營除外，野生動物也算） | 慣性 | 輕型 `DMSE_Light_Guidance_IR`（熱訊號導引頭） |
| 電磁訊號 | `GuidanceType_ElectromagneticSignal`（`HighEnergy` 為舊名別名） | 通電中、耗電量（`-PowerOutput`）最大的敵對建築；電網正要開啟的建築以額定耗電計 | 慣性 | 巡航 `DMSE_Guidance_EMSignal` |
| 電視制導 | `GuidanceType_TV`（`PlayerChoosesTarget = true`） | 玩家發射：抵達後暫停，自由選點；敵方發射：慣性 | — | 巡航 `DMSE_Guidance_Precision`（改名「電視制導」）、巡飛彈內建 |
| 雷射 | `GuidanceType_Laser` | 目標地圖上攻擊方自己、仍有效的雷射引導點中最近放置者 | 慣性 | 輕型 `DMSE_Light_Guidance_Laser`、巡航 `DMSE_Guidance_Laser`、彈道 `DMSE_Ballistic_Guidance_Laser` |
| 火控 | `GuidanceType_FireControl` | 慣性（散布較小；主要語意是攔截彈） | — | 彈道 `DMSE_Ballistic_Guidance_FireControl` |

**電網啟動中的建築**：原版電網每 30–200 ticks 才開啟一棟新接上的耗電建築，而導彈打尚未生成地圖的據點時，地圖在抵達那一 tick 才生成，所有建築都還沒通電。`GuidanceType.PowerStarting` 把「開關為開、未故障、電網有可用電源且供得起、無停電事件」的未通電建築視為運作中（與原版 `PowerNet.PowerNetTick` 的開機條件相同）；反輻射用 `CompBVRDevice.ActiveForSeeker`（同 `Active`，但接受啟動中的電力），電磁訊號以額定耗電 `PowerConsumption` 計。人工操作的雷達仍須有人操作才算開機。熱訊號不受影響：新地圖的房間還沒升溫，會打體型最大的 Pawn。

**雷射引導標點**（`.source/DMSE/Scorer/Guidance/LaserDesignator.cs`、`1.7/Defs/ThingDefs_Misc/Weapons/DMSE_LaserDesignator.xml`）：

- `DMSE_LaserDesignatorMark`（`LaserDesignatorMark`）：Ethereal、`useHitPoints = false`，不會被攻擊、搬運、拆除或炸毀；記錄標定方陣營 `owner`、放置 tick 與失效 tick，到時自動消失（預設 5000 ticks = 2 遊戲小時）。以原版 `UI/Overlays/TargetHighlight_Square` 染紅並脈動顯示；檢視字串顯示標定方與剩餘時間。
- **裝備**：`DMSE_Gun_LaserDesignator`（雷射目標標定器，機械印表機製作，研究「積體電路」）。射出 `DMSE_Bullet_LaserDesignator`（`Projectile_LaserDesignator`），不造成傷害，命中處生成屬於射手陣營的標點；被護盾擋下則不生成。`ai_IsWeapon = false`、沒有 `weaponTags`：AI 不會拿它當武器，也不會出現在陣營武器池。標點 def 與存在時間由彈頭的 `ModExtension_LaserDesignator` 設定。貼圖暫用 C95 手槍。
- **能力**：`CompProperties_AbilityLaserDesignate`（`CompAbilityEffect_LaserDesignate`）可掛在任何 AbilityDef 上，對目標格施放即生成屬於施放者陣營的標點；目前**沒有隨附 AbilityDef**，需要時在 XML 定義並賦予（植入物、服裝等），能力的 `verbProperties.targetParams.canTargetLocations` 要設為 true。
- 用法：先派人到目標地圖標定，再從基地發射雷射制導導彈；導彈抵達時標點必須仍有效。

---

## C. 超視距火砲系統（Artillery，`.source/DMSE/Artillery/`）

> **現況**：1.6 的 `DMSE_ArtillerySystem.xml` 於 2026-07-25 改名停用（`.disabled`），1.7 拆分時未移植，**目前沒有任何 Def 掛 `CompProperties_ArtilleryStrike`**。以下只記程式碼行為。舊文件提到的 `docs/artillery-expansion-plan.md`（齊射指揮器/反砲兵雷達/ABM 攔截砲等規劃）並不存在於 repo，這些項目也都未實作。

### C-1. 架構角色

| 角色 | 類別 | 職責 |
|---|---|---|
| 火砲核心 | `CompArtilleryStrike`（**繼承 `CompBVRDevice`**，不註冊） | 世界選靶 Gizmo、持續射擊狀態機、彈藥與砲管檢查、砲管轉向、（選配）佔用火控通道 |
| 離場投射物 | `Projectile_ArtilleryDeparture` | 從建築以 `Projectile` 弧線（`flyOverhead`）飛到朝目標方位的出口邊緣格；`Impact` 時建立世界航跡並 Vanish |
| 世界航跡 | `WorldObject_ArtilleryStrike` | 世界地圖 Slerp 飛行（步進 `projectile.speed / 10 × 0.00025 / 距離`）；抵達時 `ResolveArrivalMap`（沒有地圖也沒有據點即落空）、排程齊射、自毀 |
| 地圖排程器 | `MapComponent_ArtilleryStrikes` | 持有 `List<ArtilleryShellRequest>`（Deep 序列化），每 tick 輪詢 `fireTick` 到時者生成砲彈 |
| 排程資料 | `ArtilleryShellRequest` | `fireTick`、`targetCell`、`entryEdgeCell`、`projectileDef`、`attackerFaction`、`scatterRadius` |
| 靜態工具 | `ArtilleryStrikeUtility` | `WorldDirectionAngle`（球面切平面方位角，0 = 北、順時針）、`EdgeCellFromAngle`、`CalcEntryEdgeCell`、`QueueSalvo`、`SpawnShell` |

`CompProperties_ArtilleryStrike`：`departureProjectileDef`、`worldObjectDef`（兩者必填，缺少時 `ConfigErrors` 報錯）、`projectileDef`（後備；優先使用 `CompChangeableProjectile` 已裝填的砲彈）、`cooldownTicks`（3600）、`warmupTicks`（240）、`scatterRadius`（2）、`salvoCount`（1）、`salvoIntervalTicks`（60）、`requiresFireControlChannel`（false）、`fireControlHoldTicks`（900）。

### C-2. 線性時間線（選靶 → 落彈）

```
玩家點擊「開火」Gizmo（需 Active、def 齊全、有砲彈；冷卻中也可先選靶）
   │   世界選靶：同圖層的任何有效 tile（無射程、最小距離限制；其他圖層拒絕）
   │   targetTile = 該 tile → 進入持續射擊；Gizmo 換成「停止」並顯示冷卻/瞄準/就緒狀態
   ▼
每 tick CompTick
   ├─ 火控通道到期（channelReleaseTick）→ ReleaseArtilleryChannel
   └─ TickSustainedFire：
        !Active → 暫停（保留目標，恢復後繼續）
        冷卻中 → 等待
        無砲彈 → 訊息 + 停止持續射擊
        砲管（CompRefuelable）耗盡 → 訊息 + 停止持續射擊
        requiresFireControlChannel 且無空閒火控通道 → 暫停瞄準，等通道釋放
        開始瞄準：warmupEndTick = now + warmupTicks；砲管轉向世界方位（TurretTop.CurRotation，
                  瞄準期間每 tick 拉回，蓋過原版閒置擺動）
        瞄準完畢 → FireSingleShot
   ▼
FireSingleShot
   │   • RemoveShell + 砲管 ConsumeFuel(1)
   │   • 出口邊緣格 = WorldDirectionAngle(本地 → 目標) 對應的地圖邊（同 tile 時取北邊）
   │   • 生成 Projectile_ArtilleryDeparture 射向出口格，攜帶目標 tile、彈種、齊射參數、攻擊方
   │     targetCell = 目標地圖中心（目標地圖未載入時用 (125, 0, 125)）——玩家不選落點格
   │   • 進入 cooldownTicks；需要火控時佔用最空閒火控雷達 1 通道，fireControlHoldTicks 後釋放
   ▼
Projectile_ArtilleryDeparture.Impact（抵達出口格）→ 建立 WorldObject_ArtilleryStrike（Tile = 本地圖）→ Vanish
   ▼
世界 Slerp 飛行 → Arrived：ResolveArrivalMap（落空 → 訊息、自毀）→ QueueSalvo → 鏡頭 Pan 至落點 → 自毀
   │   • 進入邊 = WorldDirectionAngle(目標 → 發射地)，即從發射地那一側飛入；沿邊座標取 targetCell 分量
   │   • salvoCount 發依 salvoIntervalTicks 錯開推入 MapComponent_ArtilleryStrikes
   ▼
每 tick：MapComponent_ArtilleryStrikes 輪詢 → 到時 SpawnShell
   │   • 散佈：targetCell + Rand.InsideUnitCircle × scatterRadius（夾入地圖，生成時定格）
   │   • 生成 Projectile 從進入邊緣格 Launch 至落點
   │     hitFlags = IntendedTarget | NonTargetPawns，launcher = null
   ▼
Projectile_Explosive 命中結算（原版彈道/爆炸邏輯）
```

### C-3. 重點設計

- **與 BVR 共用火控通道**：火控雷達設 `supportsArtilleryGuidance` 才能引導砲擊。`FindAvailableRadar` 挑同陣營、運作中、`FreeArtilleryChannels` 最多者；`FreeArtilleryChannels = maxTargets − 已借給火砲 − 依容量比例分攤的 BVR 占用（無條件進位）`。反過來，`MidcourseDefense` 計算容量時扣除各雷達的 `OccupiedArtilleryChannels`。
- **落彈是 `Projectile` 而非 Skyfaller**：砲彈從邊緣格水平飛入，走原版投射物邏輯，因此 **BVR 不會攔截砲彈**（只攔 skyfaller）。
- **與導彈鏈的差異**：離場用 `Projectile` 弧線（`ScorerProjectile` skyfaller 從建築正上方升起，視覺不像砲擊）；抵達時不開玩家選靶，直接打目標地圖中心 ± 散佈。
- **不可跨圖層、找不到目標即落空**：砲彈無導引，選靶時拒絕其他圖層的 tile；抵達時目標 tile 沒有地圖也沒有據點則落空並發訊息，不生成營地地圖（有據點但尚無地圖時會生成）。
- **存檔**：冷卻、`targetTile`、`channelReleaseTick`、借用的雷達（以 Thing 參照存檔）都會保存；`warmupEndTick`/`aimAngle` 不存，讀檔後從冷卻結束重新瞄準。離場投射物序列化建立世界航跡所需的全部資料；目標與世界航跡的 `PlanetTile` 以 `Scribe_Values` 完整保存「tileId,layerId」（舊存檔的 int 欄位仍可讀，但只能視為地表）；在途齊射由 `ArtilleryShellRequest` 全值序列化。

---

## D. 系統間的關係

BVR 是**防禦**，裝配/發射與火砲是**進攻**，但已互相牽動：

1. SAM 的彈藥是裝配系統產出的實體導彈，戰鬥部效果（連續桿/空爆）直接參與攔截判定（A-5/A-6）。輕型導彈發射箱的同一彈艙同時供對地發射與攔截。
2. 進攻導彈落地前先過目標地圖的 BVR（生成端同步 `MissileBVRUtility.TryRegister`，首 tick 為後備）；玩家攻擊有雷達的敵方基地時，己方導彈也會被攔截。
3. 反輻射導引頭 vs 雷達 `immuneToAntiRadiationSeeker` 構成電子對抗閉環。
4. NPC 導彈陣地的地圖內發射速率受其火控通道數限制；陣地的相控陣雷達、輕型導彈發射箱（SAM）與 CIWS 同時防禦玩家導彈，而 SAM 與對外打擊從同一批彈架取彈。
5. 火砲繼承 `CompBVRDevice` 共用可用性判定、不進 BVR 註冊表，但與 BVR 中過程攔截**共用火控通道**（C-3）；砲彈無法被 BVR 攔截。
6. 所有發射平台共用 `CompMissileLauncher` 基類（冷卻、有彈、可用性）；只有 `CompMissileLauncher_Interceptor` 會被 BVR 調用。發射平台建築掛 `CompMissileConfig` 只為接收/保存設定，不會被 `WorkGiver_AssembleMissile` 當成物品（限定 Item 類別）；彈道井另有專用 WorkGiver。
7. 載入 VGE 時，近防（`CompPointDefence`）與 `CompTerminalCIWS` 可同裝一座砲塔並共用砲管耐久，但兩者判定互不相通。

---

## E. 檔案清單

**BVR 核心（`.source/DMSE/SkyFallerTurret/BVR/`）**
`CompBVRDevice`、`MapComponent_BVRCombat`（含 `BVRTuning`）、`BVRWave`（含 `BVRTarget`）、`BVRTargetProps`、`CompSearchRadar`、`CompFireControlRadar`、`CompMissileLauncher`（`CompMissileLauncher_Interceptor`）、`CompMannableMissileLauncher`、`JobDriver_ManMissileLauncher`、`CompTerminalCIWS`、`CompTurretFacingRenderer`、`MapComponent_InterceptEffects`、`WeatherEvent_MissileFlash`

**BVR 周邊（`.source/DMSE/SkyFallerTurret/`）**
`InterceptProjectile`、`Patch_MakeDropPodAt`（含 `BVRDetection`）、`Alerts_BVRStatus`、`Alerts_Pods`、`Alerts_PlayerMissiles`、`Patch_QuestPart_Bossgroup`；舊系統空檔（只剩 superseded 註解，可自 csproj 移除）：`CompProperties_InterceptRadar`、`CompSkyfallerTurret`、`EarlyWarningUtility`、`InterceptSkyfallerUtility`、`MapComponent_InterceptSkyfaller`

**BVR 銜接（`.source/DMSE/Scorer/Assembly/`）**
`MissileBVRUtility`、`MissileIncoming`（`attacker`/`bvrHandled`/首 tick 後備註冊）、`WorldObject_IncomingMissile`、`IncomingMissileUtility`、`DebugActions_Missile`、`Patch_SpawnMissileIncoming`（**已廢棄留空**）

**發射平台與容器（`.source/DMSE/`）**
`CompMissileLauncher`（抽象基類 + `CompMissileLauncher_WorldTargeting`）、`Building_MissileRack`（含 `MissileRackExtension`）、`CompMissileRackRenderer`

**導彈裝配/發射（`.source/DMSE/Scorer/`）**
`Assembly/MissileDefs`、`Assembly/MissileConfig`、`Assembly/CompMissileConfig`、`Assembly/WarheadEffect`、`Assembly/PayloadEffect`、`Assembly/GuidanceType`、`Assembly/MissileLauncherExtension`（含 `MissileTaxonomy`）、`Assembly/ITab_MissileAssembly`、`Assembly/ITab_MissileInventory`、`Assembly/MissileAssemblyUtility`（含 `DMSE_MissileJobDefOf`）、`Assembly/WorkGiver_AssembleMissile`、`Assembly/JobDriver_DeliverMissileResource`、`Assembly/JobDriver_AssembleMissile`、`Assembly/WorkGiver_LoadMissileLauncher`、`Assembly/JobDriver_LoadMissileLauncher`、`Assembly/Patch_Refuel_CarryConfig`、`CompScorer`（`CompMissileLauncher_Scorer`）、`CompMissileRailLauncher`（`CompMissileLauncher_Rail`）、`Ballistic/CompBallisticLauncher`（`CompMissileLauncher_Ballistic`）、`Ballistic/WorkGiver_AssembleBallisticSilo`、`Ballistic/JobDriver_AssembleBallisticSilo`、`ScorerProjectile`、`ScorerProjectile_WorldObject`、`MissileWorldObject`、`MissileWorldUtility`、`Guidance/LaserDesignator`（標點、標定彈、標定能力 Comp）、`GameComponent_MissileEngage`、`Patch_ShouldBeRemoved`、`CompEffecterColdLaunch`、`CompEffecterHotLaunch`

**NPC 導彈陣地（`.source/DMSE/MissileBase/`）**
`WorldObjectComp_MissileBase`、`MapComponent_MissileBase`、`MissileAutoFillUtility`、`SitePartWorker_MissileBase`、`GenStep_MissileBaseCrew`、`GenStep_MissileBaseFog`、`LordJob_MissileCrewDuty`、`Patch_MissileBaseReentry`、`Patch_MissileBaseShuttleBlock`、`DebugActions_MissileBase`

**超視距火砲（`.source/DMSE/Artillery/`）**
`CompArtilleryStrike`、`Projectile_ArtilleryDeparture`、`WorldObject_ArtilleryStrike`、`MapComponent_ArtilleryStrikes`（含 `ArtilleryShellRequest`）、`ArtilleryStrikeUtility`

**VGE 相容（`.source/GravshipExpanded/`）**
`PointDefenceCompat`（含 `ModExtension_PointDefence` 與 `CompPointDefence` 補丁）

**Defs（`1.7/`）**
`Defs/ThingDefs_Buildings/`：`DMSE_AcousticEarlyWarningRadar.xml`、`DMSE_EarlyFireControlRadar.xml`、`DMSE_Misc.xml`（相控陣雷達）、`DMSE_RotaryMissileLauncher.xml`、`DMSE_LightMissileLauncher.xml`、`DMSE_Security.xml`（CIWS）、`DMSE_Security_Missile.xml`（巡航發射器/攔截彈 skyfaller/巡航落點導彈/來襲導彈世界物件）、`DMSE_LoiteringLauncher.xml`、`DMSE_BallisticSilo.xml`、`DMSE_MissileRack.xml`（儲存架）、`DMSE_LauncherBases.xml`（發射平台建造限制）；`Defs/ThingDefs_Misc/Weapons/DMSE_LaserDesignator.xml`（雷射引導標點、標定彈、雷射目標標定器）；`Defs/ThingDefs_Items/DMSE_Manufactured.xml`（導彈物品 + ITab）；`Defs/Missile/DMSE_MissileAssembly.xml`（巡航彈體+部件）、`DMSE_LightMissile.xml`、`DMSE_BallisticMissile.xml`、`DMSE_LoiteringMunition.xml`、`DMSE_MissileAssembly_Jobs.xml`（JobDef + WorkGiverDef）；`Defs/WorldObjects_MissileBase.xml`（導彈陣地）；`GravshipExpanded/PointDefence/Patches/Patch_VGE_PointDefence.xml`

---

## F. 審查發現與修正記錄

### 2026-10-18：遊戲內自動驗收

新增 `.source/DMSE.BVRAcceptance`（用法見該目錄 README）：以 `-quicktest` 啟動 1.7、在新殖民地自動跑地圖層、導引頭、世界層與存讀檔，跑完刪除暫存存檔並關閉遊戲；測試用 DLL 只在執行期間放進 `1.7/Assemblies`。最初 19 項連續三輪全數通過；修正下方第 3 點後新增 S3，20/20 通過；再補上世界層空爆、移動目標改飛、末端 CIWS 斷電、各倍速 ETA 與改飛後存讀檔（W8–W11、L2），25/25 通過（Release DLL），未發現新缺陷。世界層各情境的結果與未測項目見《BVR_世界層偵測攔截重構》§15-4。

1. **【已修正】雷射目標標定器會出現在敵人身上。** `DMSE_Gun_LaserDesignator` 繼承 `BaseHumanMakeableGun` 的 `weaponTags`（`Gun`），劫掠者的武器池會抽到這把無傷害的裝備。改為 `<weaponTags Inherit="False" />`。
2. **【已修正】世界層覆蓋圈、攔截彈虛線與會合擴散圈看不見。** `BVRWorldDrawer` 的線畫在球面上，被地形網格蓋住；改照原版世界線沿法線外推 0.08，並使用圖層的世界線材質與寬度。
3. **【已修正】反輻射、電磁訊號導引頭打剛生成的地圖時退化為慣性。** 兩者要求 `PowerOn`，但地圖在導彈抵達那一 tick 才生成，原版電網要 30–200 ticks 後才開啟耗電建築，打尚未生成地圖的 NPC 據點時，這兩種導引頭等同慣性。改為電網正要開啟的建築也算運作中（B-7「電網啟動中的建築」）；新增驗收 S3 在地圖生成當下直接選點，W6 改為必須命中相控陣雷達。熱訊號在新地圖上仍只會打最大的 Pawn（房間還沒升溫，符合「房間夠熱才選房間」）。
4. 測試過程中確認的原版行為：新接上電網的耗電建築是逐一開啟的（每 30–200 ticks），`DebugSettings.unlimitedPower` 只對已在電網內的建築有效；不在世界畫面時世界物件每 15 ticks 才更新一次。

### 2026-10-18 第三輪：全面覆核

**新發現（第 1、2、4 點已於同日修正，編譯通過、尚未進遊戲驗證；第 3 點仍未修正）**

1. **【已修正】CIWS 在多個波次同時處於末端時會中斷連射，且總是先打較晚登記的波次。** `MapComponentTick` 由 `Waves` 尾端往前處理，每個處於末端的波次都各自呼叫 `TerminalDefense` → `ciws.TryEngage(wave)`；但連射狀態（`burstShotsLeft`/`burstTargetId`/`nextShotTick`）每具 CIWS 只有一份，不分波次。若 CIWS 正在打 `Waves[0]` 的目標而 `Waves[1]` 也進入末端，下一發到時會先以 `Waves[1]` 呼叫 `FireBurstShot`，在該波次找不到 `burstTargetId` → `EndBurst`：本輪作廢、不擲骰、進冷卻；之後的新連射也一律先開在 `Waves[1]`。結果是較早進入末端（通常也較早落地）的波次分不到 CIWS 火力，直到後面的波次清空為止。觸發條件：兩個同防禦方波次的落地時間相差超過 `MergeWindowTicks`（600）但小於末端窗口（WingBack 為 1800），例如兩批空投相隔 10–30 秒抵達。修正方向：每具 CIWS 每 tick 只處理一次；連射中跨波次以 `burstTargetId` 找目標；新連射選 `tickToImpact` 最早的波次。
   - 修正：`MapComponentTick` 先收集末端波次，迴圈後 `TerminalDefense` 對每具 CIWS 只呼叫一次 `TryEngage(terminalWaves)`；連射中以 `burstTargetId` 跨該防禦方所有末端波次找目標，新連射選 `tickToImpact` 最早的波次；存檔欄位不變。
2. **【已修正】【1.7 迴歸】導彈陣地的「發射器全滅 → 陣地摧毀」永遠不會觸發。** `MapComponent_MissileBase.GetEnemyLaunchersCached` 以 `CompMissileLauncher_Scorer` 辨識發射器，但 1.7 的 `DMSE_Building_Scorer` 已改掛 `CompProperties_MissileRailLauncher`（1.6 仍是 `CompPropertiesScorer`），1.7 已無 Def 使用 `CompMissileLauncher_Scorer`。快取恆為空 → `hasEverHadLaunchers` 永遠 false → 不會呼叫 `OnBaseDestroyed`；`EnsureCrewLord` 的集結點也一律退回地圖中心。修正方向：改判 `CompMissileLauncher_WorldTargeting`（或基類 `CompMissileLauncher`），並同步更新該類別的 XML 註解。
   - 修正：改以 `CompMissileLauncher_WorldTargeting` 辨識（涵蓋 `_Rail`/`_Ballistic`/`_Scorer`，`LaunchSite_A` 的 Scorer 與輕型導彈發射箱都算在內），類別 XML 註解同步更新。
3. **【低】火砲傷害歸因沒有生效。** `ArtilleryShellRequest.attackerFaction` 有存檔，但 `SpawnShell` 以 `launcher: null` 發射，該欄位實際沒被用到。1.7 尚無火砲 Def，影響有限。
4. **【已修正】【低，潛在】導彈陣地取到「沒有 `CompMissileConfig` 的導彈」時會 NRE。** `TryFireFromOnMap` 先取第一個有彈的敵方彈架，再找架內第一枚帶 `CompMissileConfig` 的導彈；若該架只放了不帶此 comp 的彈（如 `DMSE_InterceptorMissile`），`missile` 為 null，`ExtractConfigFromMissile` 會退回預設設定，但接著的 `missile.Destroy()` 拋 NullReferenceException。現行 `LaunchSite_A` 只放輕型導彈與 Moskit（兩者都有此 comp），所以目前不會觸發。
   - 修正：`FindRackWithMissiles` 只回傳存有帶 `CompMissileConfig` 導彈的彈架並一併回傳該枚導彈；只放攔截彈的彈架直接跳過，不消耗任何東西。

**07-06 之後已修正（2026-10-13 提交，本輪已覆核程式碼）**

- 多人空投艙：原本以 `ActiveTransporterInfo.SingleContainedThing` 取 pawn，艙內多於一件時會報錯（例如圍攻的補給艙）。現改由 `BVRDetection.PawnsIn` 列出艙內全部 pawn：`GetDropFaction` 取第一個有陣營者，`SpawnDebris` 殺死所有 pawn 並把屍體合併成一個殘骸空投，`Patch_QuestPart_Bossgroup.IsOk` 也改用同一判定。
- `MapComponent_InterceptEffects` 的材質改在 `[StaticConstructorOnStartup]` 時於主執行緒建立，不再於 `MapComponentUpdate` 延遲建立。

**文件漂移（本輪已更新）**

- SAM 類別 `CompMissileLauncher` → `CompMissileLauncher_Interceptor`；`CompMissileLauncher` 現為所有發射平台的抽象基類，`CompScorer`/`CompMissileRailLauncher`/`CompBallisticLauncher` 只是檔名，類別為 `CompMissileLauncher_Scorer`/`_Rail`/`_Ballistic`。
- `CompBVRDevice.Active` 新增 `CompMannable` 條件；補上人工操作旋轉發射架與操作員自動補彈。
- 來襲導彈改為生成端同步登記，首 tick 自註冊降為後備。
- 火控雷達與火砲共用火力通道（`supportsArtilleryGuidance`）。
- 射程公式加入戰鬥部 `RangeOffset` 與酬載 `RangeFactor`；導引頭 7 種、戰鬥部 8 種、酬載 8 種。
- 裝配需按「確認裝配」；儲存架內的導彈可就地裝配；彈道井的專用裝配流程。
- 火砲章節整段改寫（世界選靶、持續射擊狀態機、離場 `Projectile`、依球面方位決定出入邊、無 `minRange`），並註明 1.7 無 Def。
- 新增 1.7 Def 配置與數值試算、敵方導彈來源（NPC 導彈陣地）、VGE 近防相容層。
- 移除失效引用：`docs/導彈四階分級架構.md`（07-06 已刪除，四階分級摘要併入 B-1）、`docs/artillery-expansion-plan.md`（repo 中不存在）。
- `README.md` 的 BVR／發射器摘要同步改用現行類名；CIWS 一欄移除「85 式多用途速射系統」（那是 Dawnblade Striker 的砲，沒有 `CompTerminalCIWS`）。

### 2026-10-18：導引頭選目標機制（新功能）

1. 落點選擇統一由 `GuidanceType.PickCell` 決定，玩家與敵方發射共用。玩家導彈只有電視制導（含巡飛彈內建）會暫停讓玩家選點，其餘導引頭與慣性一律自動鎖定（B-4、B-7）。過去玩家導彈不論導引頭都手動選點，導引頭只影響敵方。
2. 鎖定規則重寫：反輻射改看全部敵對建築（原本只看玩家建築），後備從「發電量最大」改為電磁訊號（耗電量最大）；熱訊號以房間溫差門檻決定打房間或體型最大的 Pawn（原本先打 Pawn）；新增電磁訊號與雷射制導；慣性改為打隨機敵對建築（原本以 `allBuildingsColonist` 判斷，玩家發射時幾乎找不到目標）。
3. 內容調整：`DMSE_Guidance_Precision` 改為電視制導並改名（defName 不變，舊存檔的導彈照常載入）；`DMSE_Light_Guidance_IR` 改用熱訊號邏輯並改名「熱訊號導引頭」；新增 4 個導引部件（輕型雷射、巡航電磁訊號、巡航雷射、彈道雷射）；巡飛彈加 `builtInGuidance = TV`，維持手動選點。
4. 新增雷射引導標點、雷射標定彈、雷射目標標定器（裝備）與標定能力 Comp（未隨附 AbilityDef）。標定器貼圖暫用 C95 手槍。
5. `GuidanceType_Infrared`／`GuidanceType_HighEnergy` 保留為舊名別名，行為分別等同熱訊號與電磁訊號。
6. 驗證：1.7 編譯 0 警告 0 錯誤；所有異動 XML 可解析；新 Keyed 鍵在三語齊全、DefInjected 的 defName 皆存在。之後的遊戲內驗收（本節上方）涵蓋各導引頭規則、雷射標點與電視制導手動選點。

### 2026-10-18：世界 tile 圖層與跨圖層打擊（9c3f5c5，已修正）

1. **【已修正】存讀檔後軌道目標變地表。** `ScorerProjectile_WorldObject`、`WorldObject_ArtilleryStrike` 只存 `tileId`，再以 `new PlanetTile(int)`（圖層 0）還原。原註解稱 `Scribe_Values` 無法序列化 `PlanetTile`，實際上原版已註冊解析器（`tileId,layerId`），`WorldObject_IncomingMissile` 也一直直接使用。改為完整保存，舊欄位保留讀取。
2. **【已修正】火砲永遠打地表。** `CompArtilleryStrike` 以 int 存目標（`PlanetTile` 隱式轉 int 會丟失圖層），`Projectile_ArtilleryDeparture` 再以地表重建；找目標地圖也只比對編號。全部改用 `PlanetTile`。
3. **【已修正】跨圖層距離錯誤。** 玩家發射器射程、導彈陣地選目標原本用原版 `ApproxDistanceInTiles(PlanetTile, PlanetTile)`，跨圖層時為錯誤值（可能越界報錯），等於隨機決定是否打軌道據點。改用 `MissileWorldUtility.RangeDistance` 並套用 B-6 規則。穿梭機封鎖（`Patch_MissileBaseShuttleBlock`）改為只計同圖層的陣地。
4. **【已修正】目標不在時生成垃圾地圖。** 三個抵達點原本呼叫 `GetOrGenerateMap(..., Camp)`，目標撤走或移動時會在該 tile（包含軌道）建營地並生成地圖。改為 `ResolveArrivalMap`：只取既有地圖或既有據點，否則落空。
5. **【新功能】終端更新與逃逸區**（B-6），為轉移飛行的移動船體預作準備。
6. 導彈陣地地圖內射擊：先以該枚導彈的導引能力與射程篩選目標，沒有可打目標的導彈保留在架上，不消耗。
7. 已知未修：`ImpactCraterUtility`（撞擊飛行）也以 `ApproxDistanceInTiles(PlanetTile, PlanetTile)` 量測到據點的距離，有同樣的跨圖層問題，留待轉移飛行改版時處理。
8. 合併進 main 時（947c93d），文件衝突整份取用了第三輪改寫的版本，本段與 B-5、B-6、G 第 13–15 項是之後補回的。

### 2026-07-06 第二輪：已修正

1. **【已修正】空爆額外攔截在枚舉中修改集合。** 原 `ApplyExtraInterceptRolls` 在 `MidcourseDefense` 的 `foreach (target in wave.targets)` 進行中直接 `RemoveTarget`（修改 `wave.targets`、必要時連 `Waves` 一起移除），會拋 `InvalidOperationException`。修正：命中的目標 id 先收集到 `airburstKills`（`ref List<int>`，跨多次空爆去重），待 foreach 結束後於 `MidcourseDefense` 尾端統一 `Discard()` + 從 `wave.targets` 移除；**刻意不移除空波次本身**——空波次交由 `MapComponentTick` 迴圈尾端既有的 `targets.Count == 0` 檢查處理，索引 `i` 因此始終有效（一併化解原審查第 2 點的索引錯位疑慮）。
2. **【已修正】註解漂移**：`BVRTuning.WindowPenaltyPerExtraTarget` 的 XML 註解已改為實際語意「波次合併時每多一目標縮短的 ticks」。
3. 修改僅涉及 `MapComponent_BVRCombat.cs`；當時尚未編譯驗證（沙箱無工具鏈）。

### 2026-07-06 第一輪：文件漂移（已更新）

舊版文件的預警窗口公式（功率線性、無反隱身加成）、SAM 彈藥（CompRefuelable 抽象燃料）、CIWS 判定（每發擲骰）、缺少鎖定階段/速度係數/戰鬥部攔截加成/導彈進入路徑等，均已按當時程式碼改寫。

（歷史紀錄）早前修正過「發射器被誤判為可裝配物」：`MissileAssemblyUtility` 限定 item 類別 + `Patch_Refuel_CarryConfig` 同步 `pending`，現行程式碼仍保有此雙保險。

---

## G. 測試檢查清單

`.source/DMSE.BVRAcceptance` 自動涵蓋第 2、3、6 項，以及第 4、7、12、14、16–18 項的一部分（測試代號見該目錄 README）。裝配、發射 UI、火砲、地圖層空爆、導彈陣地摧毀、跨圖層、逃逸區用完次數後落空、舊存檔仍需手動測試。

1. **編譯**：`.source/DMSE/DMSE.csproj`（SDK、net10.0，輸出至 `1.7/Assemblies`）。遊戲執行中會鎖住 DLL，建置前先關閉 RimWorld。
2. **BVR 空投**：建搜索雷達 → 敵方空投出現預警窗口 → 火控鎖定（觀察通道占用警報）→ SAM 消耗實體導彈、中過程攔截（地圖外閃光、無碎片）→ 末端 CIWS 連射（整輪一次判定；命中後機率碎片、耗砲管耐久）→ 未攔截者落地。無雷達時照常落地。
3. **多人空投艙**：含多名 pawn + 物品的空投艙被 CIWS 擊落且擲中碎片 → 所有 pawn 死亡、屍體以單一殘骸空投落下、無報錯。
4. **人工操作**：早期火控雷達/旋轉發射架無人操作時不鎖定、不發射、砲塔頭不追蹤；下令操作後操作員先自行補彈至滿再就位，彈打光時自動回去取彈。
5. **BVR 導彈**：敵方（或 Debug `Enemy missile salvo`）發射導彈 → 生成時即進入 BVR → 隱身/速度高的彈體窗口短、命中率低；**空爆回歸**：以多彈波次 + 空爆攔截彈齊射，確認一彈多殺、不拋例外、空波次正常清除。
6. **CIWS 多波次**（F 第三輪第 1 點的修正）：讓兩批來襲相隔約 15–25 秒抵達，確認 CIWS 不再中斷連射、先落地的波次優先被攔截。
7. **攻防互換**：玩家對有雷達的敵方基地發射導彈/空投 → 被敵方防空攔截，`Alerts_PlayerMissiles` 正確追蹤；反輻射導引頭優先打玩家雷達、早期預警雷達（豁免）不被選中。
8. **導彈陣地**：未生成地圖時定期來襲；進入地圖後發射數受敵方火控通道限制；摧毀全部發射器後陣地消失、收到摧毀信件（F 第三輪第 2 點的修正）。
9. **裝配**：ITab 改部件 → 缺料紅字 → 按「確認裝配」→ 小人搬料 → 施工 → config 生效；更換/移除部件部分退還；儲存架內導彈可就地裝配；彈道井裝配後可發射、發射後需重新裝配。
10. **發射**：各平台裝填 → 世界選靶受射程限制 → 玩家選落點 → 依彈頭/酬載結算；增程件與酬載係數影響射程。
11. **火砲**（需自備測試 Def）：選世界目標 → 冷卻 → 瞄準轉向 → 離場投射物飛向對應邊緣 → 世界航跡 → 從發射地一側飛入、散佈生效；`requiresFireControlChannel` 時佔用並釋放火控通道；砲彈不觸發 BVR。
12. **存讀檔**：BVR 波次/目標/CIWS 連射狀態、導彈 config/pending/delivered/assemblyConfirmed、各發射器冷卻、彈道井裝填旗標、火砲持續射擊目標與在途齊射皆正確還原；**在途導彈/砲擊的目標圖層（軌道）讀檔後不變**，終端更新剩餘次數保留。
13. **跨圖層**：地表陣地只有慣性制導彈時不攻擊軌道據點；換成具導引能力且射程足夠的導彈才會攻擊。玩家從軌道選地表目標：慣性/無導引彈被拒並顯示原因，導引彈可發射；火砲選其他圖層被拒。軌道地圖存在導彈陣地時，穿梭機不再跳紅字。
14. **逃逸區**：導彈飛行中移動目標（之後的轉移飛行船，或以 Debug 改動目標 MapParent 的 Tile）→ 導彈在更新次數內改飛新位置（檢視字串的次數遞減）；次數用完後再移動 → 抵達時落空並發訊息。
15. **落空**：玩家對空白 tile 發射 → 游標提示將落空，抵達時落空且不生成地圖；敵方導彈在途時玩家放棄該據點 → 落空，不生成營地。
16. **導引頭選目標（玩家）**：選靶游標與 ITab 顯示落點方式；電視制導巡航彈與巡飛彈抵達後暫停手選；反輻射彈打敵方開機中功率最高的雷達；熱訊號彈在有暖氣房間的據點打房間、否則打最大的生物；電磁訊號彈打耗電最多的建築；慣性彈打隨機敵對建築，且不暫停遊戲、發「已鎖定目標」訊息。
17. **導引頭選目標（敵方）**：導彈陣地以反輻射／熱訊號彈打玩家據點時落點符合規則；早期預警雷達（豁免）不被反輻射選中；敵方電視制導彈退化為慣性。
18. **雷射標點**：小人裝備雷射目標標定器、征召後對地面標定 → 出現紅色脈動標點，檢視字串顯示剩餘時間、到時消失；爆炸與攻擊不會打掉它；雷射制導導彈鎖定最近一個我方標點，沒有標點時退化為慣性；敵方標點不會被我方導彈鎖定。
19. **舊存檔**：在途的精確導引巡航彈（現為電視制導）讀檔後照常飛行，抵達時改為手動選點。
