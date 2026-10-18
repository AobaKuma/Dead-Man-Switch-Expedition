# DMSE BVR 世界層偵測與攔截重構 — 規劃

> 狀態：**P0–P3 已實作；遊戲內自動驗收 25/25 通過，部分情境未測（§15-4）**　｜　更新：2026-10-18　｜　基於最新 `main` `e0b6005`（含已合併的跨圖層航跡與新的導引頭／雷射指示器）。P4 保留為選配，未實作。
>
> §14 的 v1 決策採各列建議：全部導彈 150 ticks/格、點防禦、僅導彈陣地具抽象防空、空投維持舊流程、選取覆蓋圈加常駐開關、世界航跡抵達後只跑末端。§15 記錄實作、驗證與未測項目；§15-4 標為未測或部分的情境仍未實測。

---

## 0. 需求對照總表

| # | 需求 | 設計要點 | 主要類別 / Def |
|---|---|---|---|
| 1 | 在世界地圖上偵測來襲導彈 | 每個有 BVR 裝置的地圖在世界層形成「防空節點」；搜索雷達有以「格」計的世界偵測半徑，航跡進入半徑即被偵測 | `WorldComponent_BVRNetwork`、`BVRNode`、`CompProperties_SearchRadar.worldDetectionRange` |
| 2 | 在世界地圖上攔截（導彈不必先抵達） | 火控鎖定 → SAM 消耗實體彈 → 攔截彈以世界物件飛向會合點 → 會合時擲骰；成功則來襲導彈在世界上被摧毀 | `WorldObject_Interceptor`、`BVRInterceptSolver` |
| 3 | 地圖內只剩末端 | 世界層航跡抵達後生成的 `MissileIncoming` 只登記「末端波次」，CIWS 照舊運作；中過程不再在地圖內發生 | `MissileBVRUtility`（改）、`MapComponent_BVRCombat`（改） |
| 4 | 雙向：玩家導彈也會在世界層被敵方攔截 | NPC 據點沒有生成地圖時，以世界物件元件提供「抽象防空」 | `WorldObjectComp_AirDefense` |
| 5 | 看得到 | 世界地圖雷達覆蓋圈、航跡狀態與 ETA、攔截彈與會合線、首次偵測信件、世界層來襲警報 | `BVRWorldDrawer`、`Alert_WorldIncoming` |

---

## 1. 現況盤點（將被取代的部分）

### 1-1. 現行流程

```
發射（玩家 Gizmo / NPC 陣地 / Debug）
   ▼
世界物件 Slerp 飛行（MissileWorldObject：WorldObject_IncomingMissile / ScorerProjectile_WorldObject）
   │   BVR 完全看不到這段：沒有偵測、沒有攔截
   ▼
抵達 → ResolveArrivalMap（目標不在就落空；玩家導彈可生成據點地圖）→ 生成 MissileIncoming → MissileBVRUtility.TryRegister
   ▼
MapComponent_BVRCombat 把導彈 DeSpawn「收起」，用公式算一個人造窗口
   window = Σ(searchDistance × ticksPerDistance × power² × 隱身係數) × distanceLevel
   │   中過程（火控鎖定 + SAM）→ 末端（CIWS）→ 落地
```

問題：

1. **偵測與距離、方向無關**：窗口只由雷達參數與目標屬性決定。從 2 格外和 30 格外發射的導彈，到了地圖上得到一樣長的窗口。
2. **世界飛行時間被浪費、攔截窗口是憑空延長的**：導彈抵達後被收起再「飛」一次窗口，總時間 = 世界飛行 + 人造窗口。
3. **地圖必須存在**：攔截只能在已生成的地圖上發生。玩家打 NPC 據點時，要等抵達時生成地圖，再看生成出來的地圖上剛好有沒有 DMSE 雷達。
4. **玩家看不到**：世界地圖上的來襲導彈只是一個圖示，沒有「已被偵測」「被鎖定」「攔截彈在路上」等狀態。

### 1-2. 世界層的現有事實（設計約束）

| 事實 | 出處 | 對設計的影響 |
|---|---|---|
| 星球半徑 100；地表 `subdivisions = 10`，PlanetLayer 用 √3 細分（每輪三角形 ×3），全球約 59 萬格，**平均格距 ≈ 0.50 單位 ≈ 0.005 弧度** | `WorldGrid.RegisterPlanetLayer`、`PlanetLayer.InitializeLayer`/`Subdivide`、`ApproxDistanceInTiles = 弧度 × Radius / averageTileSize` | 程式內一律用 `Find.WorldGrid.ApproxDistanceInTiles` 與 `AverageTileSize` 換算，不寫死常數 |
| 原版運輸艙與 DMSE 導彈航跡都是**每 tick 0.00025 弧度**（DMSE 再 × 導彈 `WorldSpeedFactor`） | `TravellingTransporters.TravelSpeed`、`MissileWorldObject.TraveledPctStepPerTick` | 等於**約 20 ticks/格**：20 格的巡航導彈整趟只飛 ~400 ticks（約 7 秒）。見 §3 時間尺度 |
| 兩種導彈航跡共用基底 `MissileWorldObject`：鎖定目標 `targetParent`、分段航線（`legStart`）、**終端更新**（目標移動時改飛，次數由彈體 `terminalUpdates` 決定）、抵達時目標不在就**落空**（`OnArrived(null)`） | 9c3f5c5 `MissileWorldObject` | `IBVRTrack` 與速度模型只要改基底一處；航跡的目的地會在飛行中改變，防禦方與會合解必須能重算（§6-2） |
| 距離一律用「地表等效格」：兩點正規化後的球面夾角，以地表格大小換算；跨圖層另加射程成本 | 9c3f5c5 `MissileWorldUtility.SurfaceTilesBetween` / `RangeDistance` | 偵測半徑與會合計算沿用同一量尺，地表 ↔ 軌道的航跡也能處理；不用原版 `ApproxDistanceInTiles(PlanetTile, PlanetTile)`（跨圖層會算錯） |
| 世界物件的 `TickInterval` 在非世界畫面時**每 15 ticks** 才推進一次（世界畫面為每 tick） | `WorldObject.DoTick` / `UpdateRateTicks` | 偵測與會合判定由 `WorldComponent` 依遊戲 tick 計算，不能依賴航跡自己的 tick |
| `GenDraw.DrawWorldRadiusRing` 只快取**一個**圓心 | `GenDraw.DrawWorldRadiusRing` | 同時畫多個雷達圈要自己算（§9-1） |
| 劫掠空投不在世界地圖上飛，生成時直接是地圖上的 skyfaller | `Patch_MakeDropPodAt` 攔截的就是這一步 | v1 空投維持現行地圖內流程；可選的統一方案見 §13 P4 |
| 太空據點在 Orbit 層；轉移飛行的船是會移動的 `MapParent` | `PlanetTile.Layer`、`WorldObject_TransferShip` | 節點位置每次掃描都讀 `MapParent.Tile`；軌道上的太空站若有 BVR 裝置也是節點 |

---

## 2. 新架構總覽

### 2-1. 名詞

- **航跡（Track）**：在世界地圖上飛行、可被攔截的物件。v1 = `MissileWorldObject` 的兩個子類：`WorldObject_IncomingMissile`（敵方/NPC）與 `ScorerProjectile_WorldObject`（玩家）。由基底實作介面 `IBVRTrack`。
- **防空節點（Node）**：世界上某一格的防空能力彙總。兩種來源：
  - **實體節點**：已生成的地圖，由該地圖 `MapComponent_BVRCombat` 註冊表裡的雷達/SAM/CIWS 彙總而成（每個「地圖 × 陣營」一個）。
  - **抽象節點**：沒有地圖的 NPC 據點，由 `WorldObjectComp_AirDefense` 的 XML 數值提供（§8）。
- **會合點（Intercept point）**：攔截彈與航跡在世界上相遇的位置與時刻。
- **末端交接（Terminal handoff）**：航跡抵達目的地後交回地圖內 BVR，只跑 CIWS 末端。

### 2-2. 核心決策

1. **一條時間線**：攔截窗口 = 真實的世界飛行時間，不再有人造窗口。為了讓窗口夠長，導彈的世界飛行速度改由 Tuning Def 控制（§3）。
2. **中過程搬到世界層，末端留在地圖**：世界層負責偵測、鎖定、SAM 攔截；地圖內只負責 CIWS 與落地。現有 CIWS、碎片、落點結算全部沿用。
3. **裝置仍是地圖上的建築**：不新增「世界建築」。節點的數值每次從地圖上的 Comp 讀出；SAM 開火時照舊從地圖上的 `Building_MissileRack` 取出實體導彈、播放地圖內升空動畫，再由世界物件接手飛行。
4. **航跡狀態存在航跡自己身上**：每個航跡世界物件持有一份 `BVRTrackState`（隨世界物件存檔），`WorldComponent` 只做快取與調度，讀檔時掃描 `Find.WorldObjects` 重建。
5. **火力通道一本帳**：同一張地圖的火控通道同時供「世界層航跡」「地圖內空投波次」「火砲引導」使用，由單一帳本扣除（§6-5）。

### 2-3. 總流程

```
發射 → 航跡世界物件加入世界（ETA 可精確計算）
   │
   ▼  WorldComponent_BVRNetwork 每 15 ticks 掃描
偵測：航跡與敵對節點的角距離 ≤ 有效偵測半徑（隱身、目標尺寸修正）
   │   → BVRTrackState.detectedBy、首次偵測信件、航跡圖示變色、警報
   ▼
鎖定：節點有空閒火控通道 且 ETA ≤ maxRangeTicks → lockUntil = now + LockOnTicksFor(track)
   ▼
發射攔截彈：鎖定完成 + 有就緒 SAM → BVRInterceptSolver 求會合時刻 t*
   │   t* < ETA 才發射；地圖上消耗實體彈、播放 InterceptProjectile 升空（純視覺）
   │   建立 WorldObject_Interceptor（節點 tile → 會合點），通道續占
   ▼
會合（now == launchTick + t*）：擲 hitChance
   ├─ 命中 → 航跡世界物件 Destroy、世界閃光、訊息；空爆戰鬥部對附近航跡額外擲骰
   └─ 落空 → 釋放通道，ETA 仍夠就重新鎖定
   ▼
未被攔截的航跡抵達 → 生成 MissileIncoming（標記 worldPhaseDone）
   → MapComponent_BVRCombat 只登記末端波次：tickToImpact = now + 末端窗口
   → CIWS → 落地（無 CIWS 則直接落地）
```

---

## 3. 世界運動模型與時間尺度

### 3-1. 問題

依 §1-2，現行導彈約 20 ticks/格。即使偵測半徑涵蓋整條航線，20 格的攔截窗口也只有 ~400 ticks，比現行地圖內窗口（相控陣 6400–7680 ticks）短一個數量級，玩家在 1 倍速下也來不及看清楚。

### 3-2. 方案：導彈專用的世界速度

新增共用運動工具 `MissileWorldMotion`，由 `MissileWorldObject.TraveledPctStepPerTick` 呼叫（兩種導彈航跡一起生效）。下式的「起訖」指**目前航段**（終端更新後從改飛點重新起算）：

```
角速度 ω = ω_base × WorldSpeedFactor(config)
ω_base   = AverageTileSize / Radius / ticksPerTileAtSpeed1      （Tuning，建議 150）
每 tick 進度 = ω / 起訖角距離
ETA(now) = (1 − traveledPct) × 起訖角距離 / ω
PositionAt(Δt) = Slerp(Start, End, traveledPct + ω·Δt / 起訖角距離)
```

- 以「每格幾 tick」設定，直覺且與世界大小無關；`AverageTileSize` 在執行期讀取。
- `ticksPerTileAtSpeed1 = 150` 約是原版運輸艙的 7.5 倍慢。20 格巡航導彈約 3000 ticks（50 秒），量級接近現行「世界飛行 + 人造窗口」的總時長。
- 這同時改變**玩家導彈**的飛行時間（打敵方要等更久）。若只想放慢敵方導彈，可另設 `playerMissileTicksPerTile`（§14 決策 1）。
- 火砲（`WorldObject_ArtilleryStrike`）有自己的速度公式，v1 不動。

---

## 4. 防空節點

### 4-1. `BVRNode`（不存檔，快取）

| 欄位 | 實體節點來源 | 抽象節點來源 |
|---|---|---|
| `tile`、`faction`、`map`（抽象為 null） | `map.Tile`、裝置的 `parent.Faction` | 世界物件 `Tile`、`Faction` |
| 偵測：`detectionRange`、`antiStealthLevel` | 運作中 `CompSearchRadar` 取**最大**有效半徑（逐目標計算，見 §5-2） | Props |
| 火控：通道數、各雷達的 `ComputeHitChance`/`LockOnTicksFor` 參數、`maxRangeTicks` | 運作中 `CompFireControlRadar` | Props（可引用一個火控雷達 ThingDef 複製數值） |
| SAM：就緒發射器清單、攔截彈 config（戰鬥部、`WorldSpeedFactor`） | 運作中 `CompMissileLauncher_Interceptor` | Props：庫存數、補充速度、攔截彈速度與加成 |
| 末端窗口 | `TerminalWindowTicks(faction)` | Props（抽象節點不跑末端，見 §8） |

重建時機：裝置 `PostSpawnSetup`/`PostDeSpawn` 標記該地圖節點 dirty；`Active` 狀態（斷電、無人操作、無彈）不發事件，所以每次使用前即時讀取 Comp，不快取 `Active`。

### 4-2. 誰是防禦方（v1：點防禦）

航跡目的地 `T`：取「位於 `T`、與攻擊方敵對」的節點；玩家優先（同現行 `ResolveDefender`）。v1 **只防自己這一格**，所以航跡的大圓路徑必然通過節點，會合點有封閉解（§6-2）。

區域防禦（攔截飛越自己覆蓋範圍、目標是同陣營其他據點的航跡）放在 P4，需要一般化的會合求解與「哪個節點負責」的仲裁。

---

## 5. 航跡與偵測

### 5-1. `IBVRTrack`

```csharp
public interface IBVRTrack
{
    WorldObject Self { get; }
    Faction Attacker { get; }
    PlanetTile Origin { get; }
    PlanetTile Destination { get; }
    BVRTargetProps TargetProps { get; }      // 取自落點 incoming skyfaller def
    MissileConfig Config { get; }            // 空爆額外擲骰、Alert 顯示用
    float AngularSpeed { get; }              // MissileWorldMotion
    int TicksToArrival { get; }              // 依遊戲 tick 計算，不受 15-tick 推進影響
    Vector3 PositionAt(int ticksAhead);
    BVRTrackState State { get; }             // 存在世界物件上
    void Notify_Intercepted(Faction by);     // 摧毀 + 訊息 + 世界閃光
}
```

`MissileWorldObject` 實作此介面：`Origin`/`Destination` 取目前航段，`PositionAt` 沿目前航段外推，`State` 是基底上的新欄位（隨 `ExposeData` 存檔）。

`BVRTrackState`（`IExposable`）：`detectedByFaction`、`firstDetectedTick`、`lockUntil`、`lockNodeTile`、`interceptor`（`WorldObject_Interceptor` 參照）、`interceptAttempts`、`lastKnownDestination`（偵測到終端更新用）。

### 5-2. 有效偵測半徑

新欄位 `CompProperties_SearchRadar.worldDetectionRange`（格）。對單一航跡：

```
R_eff = worldDetectionRange × distanceLevel × 隱身係數
隱身係數 = antiStealthLevel <  stealthLevel → 0.4（沿用 StealthDetectFactor）
           antiStealthLevel >= stealthLevel → min(1 + 0.25 × 差值, 1.5)
節點半徑 = 該節點所有運作中搜索雷達 R_eff 的最大值
偵測條件：MissileWorldUtility.SurfaceTilesBetween(節點 tile 中心, 航跡位置) ≤ 節點半徑
```

- 多座雷達取**最大值**而非現行的相加：半徑相加在幾何上沒有意義。多雷達的價值改由「冗餘」（一座被打掉還有另一座）與 P4 的數據鏈體現。
- `distanceLevel` 的意義變成「雷達截面積」：越大的彈體越早被看到。
- 偵測一旦成立就**持續**（航跡只會越來越近）；雷達全滅時清除 `detectedByFaction`，鎖定與在飛攔截彈照常結算。
- 掃描頻率：每 15 ticks。以 150 ticks/格計，誤差小於 0.1 格。

---

## 6. 世界層中過程攔截

### 6-1. 鎖定

- 條件：航跡已被節點偵測、節點有空閒火控通道、`ETA ≤ maxRangeTicks`（**沿用現有 XML 欄位**，意義仍是「剩餘時間多少以內才能導引」）。
- `lockUntil = now + fc.LockOnTicksFor(track)`：沿用現行公式（隱身差、速度）。航跡速度用 `BVRTargetProps.speed`，與世界速度分開，平衡上互不影響。

### 6-2. 會合求解（點防禦封閉解）

航跡沿大圓直線飛向節點；攔截彈從節點迎頭飛出：

```
d   = 航跡目前與節點的角距離
ω_t = 航跡角速度；ω_i = 攔截彈角速度 = ω_base × 攔截彈 WorldSpeedFactor × interceptorSpeedMultiplier
t*  = d / (ω_t + ω_i)                         （會合時刻，相對 now）
會合點 = 航跡.PositionAt(t*)
發射條件：t* < ETA − minInterceptMarginTicks
```

`interceptorSpeedMultiplier`（Tuning，建議 3）讓攔截彈明顯快於來襲彈；否則輕型導彈（`WorldSpeedFactor` 0.75）會比巡航導彈還慢。區域防禦（P4）改用通用求解：在 `[0, ETA]` 取樣 `f(t) = 角距離(節點, 航跡.PositionAt(t)) − ω_i·t`，找第一個 ≤ 0 的 t 後二分逼近。

**終端更新（目標移動、導彈改飛）**：掃描時若 `Destination ≠ lastKnownDestination`，視為新航段：

- 防禦方重新解析（新目的地的節點接手；原節點的鎖定取消、通道釋放）。
- 已發射的攔截彈以舊會合點為目標，結算時判定為「目標機動脫離」，不擲骰直接落空。
- 偵測狀態保留（同陣營的雷達已經看到它了）。

這讓「移動中的轉移飛行船」可以靠改變航向逼導彈消耗終端更新次數，同時讓攔截更難，和 9c3f5c5 的逃逸區設計一致。

### 6-3. 發射

1. 選 SAM：同現行 `SelectReadyLauncher`（同陣營、`ReadyToFire`、冷卻最早結束）。
2. 命中率：
   ```
   hitChance = fc.ComputeHitChance(track, guideTimeLeft = ETA_at_launch)
             + 攔截彈戰鬥部 InterceptBonus(N)，clamp01
   ```
   與現行唯一差別：可導引時間用發射當下的 ETA，不再扣末端窗口（末端現在發生在抵達之後）。
3. 地圖端：`FireInterceptor` 照舊消耗容器首枚導彈、進冷卻、生成 `InterceptProjectile`。新增 `targetId = -1` 的純視覺模式，`LeaveMap` 時不擲骰。
4. 世界端：建立 `WorldObject_Interceptor`（`launchTick`、`interceptTick = now + t*`、起點 = 節點 tile 中心、終點 = 會合點、`target` 參照、`hitChance`、`warhead`、`N`、`defenderFaction`）。

### 6-4. 會合結算

由 `WorldComponent_BVRNetwork` 在 `now ≥ interceptTick` 時結算（不靠攔截彈自己的 15-tick 推進）：

- 先確認航跡仍存在（可能已抵達或被別的攔截彈打掉）；不存在則攔截彈自毀、釋放通道。
- 擲 `hitChance`：
  - 命中 → `track.Notify_Intercepted`：世界閃光（新 `WorldObject` 特效或 `FleckMaker` 世界版，P2 決定）、訊息「已攔截」、`Destroy()`。NPC 陣地的發射通道因 `wo.Destroyed` 自動釋放（現行 `CleanEngagedChannels` 不用改）。
  - 空爆 → 對同防禦方、會合點 `airburstRadiusTiles`（Tuning，建議 1）內的其他航跡各擲一次，次數 `ExtraInterceptRolls(N)`。
  - 落空 → 訊息「攔截失敗」；`lockUntil = -1`，下一輪掃描若 ETA 仍足夠就重新鎖定。
- 攔截彈自毀。

### 6-5. 火力通道一本帳

新增 `BVRChannelLedger`（掛在 `MapComponent_BVRCombat`）：

```
可用通道 = Σ 運作中火控 maxTargets − 火砲預留 − 地圖內空投波次占用 − 世界層航跡占用
世界層占用 = 本地圖節點負責、lockUntil ≥ 0 或攔截彈在飛的航跡數
```

`MidcourseDefense`（空投仍用）、`CompFireControlRadar.FreeArtilleryChannels`、世界層鎖定三處都改讀這本帳。`Alerts_BVRStatus` 的占用數一併改用帳本。

---

## 7. 末端交接（地圖內）

- 航跡抵達：兩個子類的 `OnArrived(map)` 生成 `MissileIncoming` 時設 `worldPhaseDone = true`（存檔欄位）。`OnArrived(null)`（落空）時世界層只需把航跡從快取移除、釋放通道。
- `MissileBVRUtility.TryRegister`：
  - `worldPhaseDone` → `RegisterTerminal(missile, pos, defender)`：窗口 = `TerminalWindowTicks(defender)`。沒有 CIWS（窗口 0）時不收起，直接落地。
  - 否則（舊存檔在飛的導彈、Orbit 層航跡、Debug 直接生成）→ 走現行完整流程。
- 波次合併沿用 `MergeWindowTicks`：短時間內相繼抵達的導彈併入同一個末端波次，`WindowPenaltyPerExtraTarget` 照舊縮短窗口。
- 現行 `MidcourseDefense` 保留給空投與後備路徑；末端成為世界層航跡在地圖內的唯一一關，因此 CIWS 多波次修正（`docs/BVR_Missile_系統流程.md` F 第三輪第 1 點）是本重構的**前置條件**。

---

## 8. 抽象防空（沒有地圖的 NPC 據點）

### 8-1. `WorldObjectComp_AirDefense`

```xml
<li Class="DMSE.CompProperties_WorldAirDefense">
  <worldDetectionRange>12</worldDetectionRange>
  <antiStealthLevel>2</antiStealthLevel>
  <fireControlDef>DMSE_PhaseArrayRadar</fireControlDef>   <!-- 複製其 CompProperties_FireControlRadar 數值 -->
  <channels>2</channels>
  <interceptorStock>8</interceptorStock>
  <restockPerDay>4</restockPerDay>
  <interceptorBody>DMSE_MissileBody_Light</interceptorBody> <!-- 取 WorldSpeedFactor 與 N -->
  <interceptorWarhead>DMSE_Light_Warhead_ContinuousRod</interceptorWarhead>
  <launchCooldownTicks>240</launchCooldownTicks>
</li>
```

- 只在 `!MapParent.HasMap` 時作為節點；地圖生成後改由實體裝置接手（同 `MapComponent_MissileBase` 的交接模式）。庫存不與地圖上的彈架同步（地圖生成時彈架照舊自動填彈）。
- 抽象節點只做世界層中過程。玩家導彈抵達後會生成據點地圖（`ScorerProjectile_WorldObject.AllowGenerateTargetMap = true`），地圖上若有 CIWS 就照常末端攔截。
- 套用對象：`DMSE_MissileBase` 站點（v1，數值對應 `LaunchSite_A` 的相控陣雷達 + 2 座輕型導彈發射箱）。其他據點見 §14 決策 3。

### 8-2. 對玩家的回饋

- 玩家導彈被抽象節點偵測：`Alerts_PlayerMissiles` 擴充到世界層航跡（狀態：未偵測 / 被偵測 / 被鎖定 / 攔截彈逼近），點擊跳到世界物件。
- 被攔截時的訊息帶出防禦方據點，方便玩家判斷要先打雷達（反輻射導引頭仍只在落點地圖選格，P4 可考慮「反輻射彈命中後削弱抽象節點」）。

---

## 9. UI

### 9-1. 世界地圖雷達覆蓋圈

- `WorldComponent_BVRNetwork.WorldComponentUpdate`：世界畫面中，對玩家節點（以及選取中的敵方節點）畫偵測圈。
- 不用 `GenDraw.DrawWorldRadiusRing`（只快取一個圓心，多圈會每幀重算 flood fill）。改為每個節點快取一組小圓取樣點：以節點法線為軸旋轉 64 個點、半徑換算成弧度，再用 `GenDraw.DrawWorldLineBetween` 連線；節點半徑變動時才重算。
- 圈的半徑採「對隱身 0、`distanceLevel` 1 的目標」的值；inspect 字串列出對各型彈的有效半徑。
- 開關：世界畫面的玩家據點 Gizmo「顯示防空覆蓋」，或 Mod 設定（§14 決策 5）。

### 9-2. 航跡與攔截彈

- 航跡 inspect 字串：類型、目的地、ETA、狀態（未偵測 / 已偵測 / 鎖定中 / 攔截彈在飛 / 攔截次數）。
- 被玩家偵測的敵方航跡 `ExpandingIconColor` 改紅色。
- 攔截彈世界物件：小圖示，`Draw` 時以 `DrawWorldLineBetween` 畫到會合點的虛線。
- （P4，選配）未被玩家偵測的敵方航跡在世界地圖上隱藏，需要先確認 1.7 世界物件的可見性 API。

### 9-3. 通知

- 首次被玩家節點偵測：ThreatBig 信件「偵測到來襲導彈」，附 ETA 與目標據點。取代現行 `WorldObject_IncomingMissile.Arrive` 的抵達信件；未被偵測的航跡仍在抵達時發信。
- 攔截成功 / 失敗：`Messages.Message`，目標為航跡（失敗）或會合點（成功）。
- `Alert_WorldIncoming`：所有飛向玩家據點的已偵測航跡，依 ETA 排序；左右鍵循環跳轉。地圖內的 `Alerts_Pods` 保留給空投與末端。

---

## 10. 存檔與相容

| 項目 | 處理 |
|---|---|
| 航跡狀態 | `BVRTrackState` 由航跡世界物件 `ExposeData` 保存；`WorldComponent` 讀檔後掃描 `Find.WorldObjects` 重建清單 |
| 攔截彈 | `WorldObject_Interceptor` 一般世界物件；`Scribe_References` 存航跡參照（`WorldObject` 為 `ILoadReferenceable`） |
| 舊存檔在飛的導彈 | 沒有 `BVRTrackState` → 預設未偵測，下一輪掃描照新規則處理；改速度只影響剩餘行程（以 pct 推進，不會跳點） |
| 舊存檔的地圖內波次 | `MapComponent_BVRCombat.waves` 原樣保留並跑完 |
| `MissileIncoming.worldPhaseDone` | 新欄位，預設 false → 舊導彈走完整舊流程 |
| 1.6 | 不動（凍結） |

---

## 11. 效能

- 航跡數量通常 < 50，節點 = 有 BVR 裝置的地圖數 + 抽象站點數（個位數到十幾）。偵測掃描每 15 ticks 做「航跡 × 敵對節點」角距離比較，成本可忽略。
- 會合結算放在 `WorldComponentTick` 依 `interceptTick` 觸發（排序佇列），不必每 tick 遍歷攔截彈。
- 覆蓋圈取樣點快取在節點上，只在半徑變動時重算。

---

## 12. 數值初值與試算

### 12-1. Tuning Def `DMSE_BVRWorldTuning`

| 欄位 | 初值 | 說明 |
|---|---|---|
| `ticksPerTileAtSpeed1` | 150 | 導彈世界速度（`WorldSpeedFactor` = 1 時每格 ticks） |
| `interceptorSpeedMultiplier` | 3 | 攔截彈相對自身彈體的世界速度倍率 |
| `stealthDetectFactor` | 0.4 | 反隱身不足時的偵測半徑倍率（沿用） |
| `antiStealthRangeBonusPerLevel` / `max` | 0.25 / 1.5 | 反隱身高出目標每級的半徑加成與上限 |
| `minInterceptMarginTicks` | 60 | 會合必須早於抵達的最小餘裕 |
| `airburstRadiusTiles` | 1 | 空爆額外擲骰的世界半徑 |
| `scanIntervalTicks` | 15 | 偵測掃描間隔 |

雷達新欄位建議：早期預警雷達 `worldDetectionRange = 8`，相控陣雷達 `12`。

### 12-2. 偵測距離與預警時間

發射距離取各彈型的典型值（巡飛彈 12、輕型 15、巡航 20、彈道 40 格）。

| 彈型（`distanceLevel`／隱身／`WorldSpeedFactor`） | 全程 ticks | 早期預警：半徑格／預警 ticks | 相控陣：半徑格／預警 ticks | 現行地圖內窗口（預警／相控陣） |
|---|---|---|---|---|
| 巡飛彈（1／0／0.55） | 3273 | 8／2182 | 18 → 全程／3273 | 1200／6400 |
| 輕型導彈（1.2／0／0.75） | 3000 | 9.6／1920 | 21.6 → 全程／3000 | 1440／7680 |
| 巡航導彈（1.5／1／1） | 3000 | 4.8／720 | 22.5 → 全程／3000 | 720／7200 |
| 彈道導彈（2／2／1.5） | 4000 | 6.4／640 | 24／2400 | 960／6400 |

只有早期預警雷達時，預警時間與現行相近；相控陣下降到現行的 1/2–1/3，但換來真實的距離與方向感。要拉長，調 `ticksPerTileAtSpeed1` 或雷達半徑即可。

### 12-3. 會合試算（相控陣火控 + 輕型導彈攔截彈）

巡航導彈在 ETA 3000 被偵測：鎖定 150 ticks → ETA 2850 發射，距離 19 格。攔截彈速度 0.75 × 3 = 2.25（約 67 ticks/格），接近速率 1/150 + 1/67 ≈ 0.0217 格/tick → **877 ticks 後會合**（ETA ≈ 1973），命中率 0.74 + 連續桿 0.15 = 0.89。落空的話還有時間再鎖定一次（ETA ≈ 1820 發射，約 560 ticks 後會合）。抵達後另有 1800 ticks 的 CIWS 末端。

彈道導彈（ETA 2400、相控陣命中率 0.20）大約能打 2–3 發，主要仍靠末端與數量：攔截高速彈道彈本來就該很難。

---

## 13. 分期實作與驗收

**前置條件**

1. 合併 9c3f5c5（`MissileWorldObject` 基底、跨圖層距離、終端更新、落空規則）。若決定不合併，P0 要先自行把 `WorldObject_IncomingMissile` 與 `ScorerProjectile_WorldObject` 的飛行邏輯抽成共用基底，本文 §5–§7 的掛點才成立。
2. CIWS 多波次修正（`docs/BVR_Missile_系統流程.md` F 第三輪第 1 點）已提交。

| 期 | 內容 | 驗收 |
|---|---|---|
| **P0 運動模型** | `MissileWorldMotion`、`DMSE_BVRWorldTuning`；兩個導彈世界物件類別改用它並提供 `TicksToArrival`；inspect 顯示 ETA | 顯示的 ETA 與實際抵達相差 < 15 ticks；改 Tuning 能調整飛行時間 |
| **P1 偵測與顯示** | `IBVRTrack`、`BVRTrackState`、`WorldComponent_BVRNetwork`、實體節點、`worldDetectionRange`、覆蓋圈、航跡狀態與變色、首次偵測信件、`Alert_WorldIncoming` | 敵方導彈進入圈內才被標為已偵測；隱身/尺寸改變偵測距離；地圖內攔截行為不變 |
| **P2 世界層攔截 + 末端交接** | 鎖定、`BVRInterceptSolver`（點防禦）、`WorldObject_Interceptor`、通道帳本、`InterceptProjectile` 純視覺模式、`MissileIncoming.worldPhaseDone` 與末端登記、空爆額外擲骰、`Alerts_PlayerMissiles` 擴充 | 世界上看得到攔截彈飛出與會合；命中的導彈不再抵達；漏網的抵達後只經 CIWS；SAM 彈藥與冷卻正確扣除；空投仍走舊流程且與世界層共用通道不超額 |
| **P3 抽象防空** | `WorldObjectComp_AirDefense`、`DMSE_MissileBase` 套用、庫存補充、地圖生成時交接 | 玩家打未生成地圖的導彈陣地時，在世界上被攔截；陣地地圖生成後改用實體裝置 |
| **P4 擴充（選配）** | 區域防禦與數據鏈（同陣營節點共享偵測）、通用會合求解、劫掠空投轉為虛擬航跡（從偵測半徑邊緣以空投速度飛入，讓地圖內中過程完全退場）、火砲/運輸艙航跡、未偵測航跡隱藏、反輻射彈削弱抽象節點 | 各項獨立驗收 |

**檔案（預估）**

- 新增（`.source/DMSE/SkyFallerTurret/BVR/World/`）：`IBVRTrack.cs`、`BVRTrackState.cs`、`WorldComponent_BVRNetwork.cs`、`BVRNode.cs`、`BVRInterceptSolver.cs`、`WorldObject_Interceptor.cs`、`BVRChannelLedger.cs`、`BVRWorldDrawer.cs`、`Alert_WorldIncoming.cs`、`WorldObjectComp_AirDefense.cs`、`MissileWorldMotion.cs`、`BVRWorldTuningDef.cs`
- 修改：`MissileWorldObject`（實作 `IBVRTrack`、`BVRTrackState` 欄位、改用運動模型）、`WorldObject_IncomingMissile` / `ScorerProjectile_WorldObject`（`OnArrived` 標記 `worldPhaseDone`；偵測信件取代抵達信件）、`MissileIncoming`、`MissileBVRUtility`、`MapComponent_BVRCombat`（帳本、末端登記）、`CompFireControlRadar`（`FreeArtilleryChannels` 改讀帳本）、`CompSearchRadar`（新欄位）、`InterceptProjectile`（純視覺模式）、`Alerts_PlayerMissiles`、`Alerts_BVRStatus`
- Defs（`1.7/`）：`DMSE_BVRWorldTuning.xml`、雷達 `worldDetectionRange`、`WorldObjectDef` `DMSE_Interceptor`、`WorldObjects_MissileBase.xml` 加 `CompProperties_WorldAirDefense`；三語 Keyed

---

## 14. v1 決策（本次採建議值）

| # | 問題 | 選項 | 建議 |
|---|---|---|---|
| 1 | 導彈世界速度 | (a) 維持原版 ~20 ticks/格，窗口只有數百 ticks；(b) 全部導彈放慢到 ~150 ticks/格；(c) 只放慢敵方，玩家導彈維持原速 | **(b)**：攻守對稱，玩家也能看到自己的導彈被攔；用 Tuning 微調 |
| 2 | 防禦範圍 | (a) 點防禦：只攔目標是自己這格的航跡；(b) 區域防禦：攔截飛越覆蓋範圍、目標是同陣營其他據點的航跡 | **v1 (a)**，P4 再加 (b) |
| 3 | 哪些沒有地圖的 NPC 據點有防空 | (a) 只有導彈陣地；(b) 加上指定陣營（FactionDef 擴充）的定居點；(c) 依科技等級套用到所有陣營 | **(a) 先做，(b) 作為開關**；(c) 會讓原版陣營也能打下玩家導彈，影響太廣 |
| 4 | 劫掠空投 | (a) 維持地圖內舊流程；(b) 轉為虛擬航跡，地圖內中過程完全退場 | **v1 (a)**，P4 評估 (b)。(b) 能統一模型，但空投原本沒有世界航跡，需要決定它「從哪個方向來」 |
| 5 | 覆蓋圈顯示方式 | (a) 選取據點時才畫；(b) 世界畫面常駐（Mod 設定開關）；(c) 兩者都有 | **(c)**：預設選取時畫，設定可改常駐 |
| 6 | 地圖內中過程的去留 | (a) 世界層航跡完全不跑地圖內中過程；(b) 抵達後若仍有時間，地圖內 SAM 再補一輪 | **(a)**：避免同一枚導彈被攔兩段，平衡較好算 |


## 15. 實作與驗證記錄（2026-10-18）

### 15-1. 已完成

- **P0**：`MissileWorldMotion` 與 `BVRWorldTuningDef` 共用運動尺度；`MissileWorldObject` 以遊戲 tick 外推位置與 ETA，避免非世界畫面 15-tick 更新造成顯示停頓。讀舊檔缺少時序欄位時從目前進度繼續；終端更新保留航程檢查與更新次數。
- **P1**：`IBVRTrack`、可存檔 `BVRTrackState`、實體防空節點、隱身／尺寸修正（多雷達取最大半徑）、偵測信件、紅色航跡、世界來襲警報、航跡類型／目的地／ETA／狀態／嘗試次數。搜索雷達 inspect 另顯示對四類彈的有效世界半徑。
- **P2**：點防禦會合解、世界攔截彈、真實 SAM 彈藥與冷卻、鎖定與在飛期間通道占用、會合時擲骰與鄰近航跡空爆。導彈改飛時以目的地與航段版本檢查判定原攔截彈落空。世界航跡抵達標記 `worldPhaseDone`；只有 CIWS、沒有搜索雷達時也能末端攔截。末端波次不與完整舊波次混併，CIWS 斷電後也不會退回 SAM 階段。
- **通道帳本**：每張地圖、每個陣營分開核算砲擊預留、地圖內鎖定／在飛、世界鎖定／在飛。`FreeArtilleryChannels` 與世界發射共用帳本，地圖內中過程扣除世界占用；狀態警報顯示分項數字。
- **P3**：`WorldObjectComp_AirDefense` 掛於 `DMSE_MissileBase`，2 通道、8 枚庫存、每天補 4 枚、240 ticks 發射冷卻；引用相控陣火控與輕型連續桿攔截彈。地圖存在時停用抽象防空及補彈，交由實體建築接手；庫存不與實體彈架同步（沿用 §8 設計）。
- **介面與資產**：世界覆蓋圈預設只在選取據點時顯示；玩家據點 Gizmo 與 Mod 設定共用常駐開關。每個「據點 × 陣營」快取球面小圓；攔截彈顯示虛線航路，會合顯示短暫擴散圈。`Alerts_PlayerMissiles` 同時顯示世界航跡及地圖內波次，支援左右鍵跳轉。
- **資料**：新增 `1.7/Defs/DMSE_BVRWorld.xml`；早期／相控陣雷達世界半徑分別為 8／12 格；新增英文、繁中、簡中 Keyed 與攔截彈 DefInjected。兩張透明 PNG 為大致外觀的佔位圖（`1.7/Textures/UI/DMSE/BVR/`），並非最終美術。
- **存檔**：航跡保有偵測／通知／鎖定／嘗試次數，攔截彈保有目標、會合 tick、航段版本與戰鬥部；世界組件讀檔後重建排程。保存攔截彈時不寫入已摧毀航跡／發射據點的懸空參照。
- 最新 `main` 的非電視導引頭自動選靶、電視制導手動選點、雷射指示器流程皆保留。1.6 未改動。

### 15-2. 實作細節

- 世界節點每 15 ticks 從既有裝置註冊表重建，不另增 dirty 事件；Active 狀態使用時即時讀取。覆蓋圈按位置、半徑快取，不逐幀 flood fill。
- `InterceptProjectile.targetId = -1` 的現有守衛已能做純視覺升空，不需改寫其離圖結算；世界端另建立 `DMSE_Interceptor`。
- 擴散光效使用世界組件繪製，不新增持久特效世界物件；成功訊息跳至目的地，會合位置另以擴散圈標示。
- 常駐覆蓋開關屬本機 Mod 設定，Gizmo 立即保存；不占用遊戲狀態／火控通道。
- `DMSE_Interceptor` 世界物件引用具體 `MissileWorldObject`；目前兩種正式航跡都由此基底實作 `IBVRTrack`。要接第三方航跡時再擴充序列化轉接。

### 15-3. 自動驗證結果

- `dotnet build .source/DMSE/DMSE.csproj -c Release --no-restore -p:RimWorldDir=<1.7 遊戲組件目錄>/`：**0 警告、0 錯誤**；已更新正式 `1.7/Assemblies/DMSE.dll`，未把測試依賴放進模組 Assemblies。
- `.source/DMSE.BVRTests`：**122 項檢查通過**，直接引用正式 DMSE 程式及 1.7 遊戲組件。涵蓋四類彈的有效半徑、反隱身上限、世界速度尺度、20 組球面會合案例及餘裕、火控命中率／鎖定時間、陣營通道隔離與共用、過期通道釋放、末端波次分類、NPC 庫存／冷卻／補彈／地圖交接、Scribe 寫入內容、舊欄位預設與鎖定釋放。
- `python .source/Tools/check_bvr_world.py .`：Def／火控／彈體／戰鬥部引用、兩類雷達半徑、兩張 RGBA 貼圖、三語 Keyed／格式參數及世界物件翻譯通過。
- 測試 fixture 不啟動完整遊戲；完整 Scribe 載入還需要 Steamworks 與遊戲日誌組件。上述測試驗證存檔寫入與預設值，**不代表完整存讀檔或實際戰鬥已驗收**。

重跑指令與測試限制見 `.source/DMSE.BVRTests/README.md`。XML 檢查只需 Python 標準函式庫。

### 15-4. 遊戲內驗收（2026-10-18，自動驗收 25/25 通過）

以 `.source/DMSE.BVRAcceptance` 在 1.7 實機跑完整遊戲迴圈（quicktest 新殖民地、Ultrafast），最初 19 項 185 個檢查連續三輪全數通過；修正導引頭（發現 3）後新增 S3，20 項 200 個檢查再次全數通過；之後補上空爆、移動目標、末端 CIWS 斷電、各倍速與改飛後存讀檔（W8–W11、L2）及 W1／W2 的覆蓋圈與擴散圈檢查，25 項 265 個檢查全數通過（Release DLL）；用法與各項內容見該目錄 README。測試關閉說書人事件並開啟 `unlimitedPower`，部分攔截以強制命中／落空驗證時序，實際命中率只記錄為備註。

| 情境 | 預期 | 結果 |
|---|---|---|
| 敵方航跡從覆蓋圈外接近 | 入圈後才偵測、信件一次、紅色航跡與世界警報；與隱身／尺寸數值一致 | ✅ W1：巡航彈對相控陣半徑 22.5 格，在 22.4 格左右偵測；信件不重複。其他彈種的半徑只有 BVRTests 公式驗證 |
| 暫停、切換世界／地圖、1／2／3 倍速 | 不因觀看畫面改變飛行時間；顯示 ETA 與實際抵達差小於 15 ticks | ✅ W1：Ultrafast 中途暫停、切到世界畫面再回地圖，抵達與預測相差 7–10 ticks。W11：暫停時 ETA 不動；1 倍速（地圖、世界畫面）、2 倍速（世界畫面）、3 倍速各跑一段，ETA 偏差皆為 0，抵達 tick 與預測完全一致 |
| 玩家據點選取／常駐開關、多圖層、多據點 | 覆蓋圈正確；移動船節點跟隨 tile；關閉常駐後選取仍顯示 | ◐ W1：選取殖民地時畫出覆蓋圈；不選取且常駐關閉時不畫；常駐開啟時不選取也畫出（截圖確認）。攔截彈虛線航路已目視確認；W2 確認擊落時登記擴散圈，但未截到畫面。W9 確認 NPC 據點移動後節點跟著新 tile。軌道圖層、玩家移動船、多據點未測 |
| 火控鎖定、SAM 實體裝填 | 先鎖定、再消耗一枚與冷卻；地圖升空僅視覺；世界彈於會合 tick 結算 | ✅ W2：鎖定占一條世界通道、在飛期間保留、結算後釋放；會合 tick 與預定完全一致 |
| 密集空投、世界航跡、火砲同時引導 | 同陣營共用通道；另一陣營的裝置不被扣用 | ◐ M8：兩個空投占滿通道後，火砲與世界航跡都無法鎖定。陣營隔離只有 BVRTests 驗證 |
| 攔截成功／失敗／空爆 | 成功航跡不抵達；失敗後時間足夠才重鎖；空爆只影響同目的地範圍內的敵對航跡 | ✅ W2 成功、W3 落空後重鎖再擊落（共耗兩枚）。W8：空爆戰鬥部（酬載 10，額外 2 次擲骰）在同一 tick 擊落同行的第二枚；同位置（距離 0.00 格）但目的地不同的航跡、落後約 4 格的航跡都不受影響 |
| 轉移船移動造成終端更新 | 舊鎖定取消；原攔截彈到舊點後落空；新目的地防空可接手 | ✅ W9（以移動 NPC 據點模擬，玩家轉移船本身未測）：鎖定期間據點移動 → 導彈在一次終端檢查（60 ticks）內改飛，舊鎖定取消並重新起算；攔截彈在飛時再移動 → 必中的攔截彈在會合 tick 落空；據點防空接著對最新航線再攔截並擊落（2 次嘗試、庫存 8 → 6） |
| 攔截中雷達斷電或摧毀 | 搜索全失效清偵測；火控失效取消未發射鎖定；已發射彈照會合排程結算 | ✅ W5：關閉全部搜索雷達後清除偵測、不再鎖定；已發射彈準時結算 |
| 世界導彈抵達，有 CIWS／無 CIWS | 有 CIWS 只進末端；無 CIWS 正常落地；不補跑 SAM | ✅ W1 無 CIWS 直接落地；W4 有 CIWS 時成為單一末端波次（窗口約 1800 ticks），SAM 不再發射，CIWS 擊落並耗砲管 |
| 多個末端波次、末端 CIWS 斷電 | CIWS 每 tick 統一分配；波次不退回完整中過程 | ✅ M6：兩個相隔 700 ticks 的空投波次，CIWS 先打先落地者。W10：末端波次中 CIWS 關閉 400 ticks，波次維持末端、保留到落地 tick，SAM 不接手；恢復供電後 CIWS 重新開火擊落 |
| 玩家攻擊未生成的敵方導彈陣地 | NPC 在世界層偵測／鎖定／消耗庫存；漏網彈抵達才生成地圖並由實體 CIWS 接手 | ✅ W6：抽象防空偵測並發射（庫存 8 → 7），玩家導彈警報顯示航跡；漏網彈抵達時才生成地圖 |
| 生成陣地地圖／撤退移除地圖 | 實體與抽象防空不並行；抽象庫存與補充在無圖時恢復 | ✅ W6：有地圖時抽象防空停用，移除地圖後恢復。每日補彈只有 BVRTests 驗證 |
| 偵測、鎖定、在飛、改飛後存讀檔 | 不重複信件、不免費重發、通道與參照正確恢復 | ◐ L1：已偵測且攔截彈在飛時存讀檔，航跡、信件旗標、嘗試次數、攔截彈參照與會合 tick 都還原，不重發信件、不免費重發，讀檔後照原 tick 結算。L2：改飛後、舊航線攔截彈在飛時存讀檔，新航線、剩餘更新次數、ETA（偏差 0）、攔截彈的舊航線資料都還原；讀檔後該彈照樣落空，據點再對還原的航線攔截 |
| 舊檔在飛航跡、舊地圖內波次 | 新欄位安全預設；舊波次照原流程跑完 | — 未測（需要舊版存檔） |
| 最新 main 的 TV／主動／雷射／反輻射導引 | 選靶仍依原導引規則；世界 BVR 不改寫落點選擇 | ◐ S1 各導引頭規則；W1 世界航跡抵達後依雷射標點落地；W7 電視制導抵達時暫停讓玩家選點；S3／W6 反輻射與電磁訊號在抵達時才生成的地圖上鎖定相控陣雷達（發現 3） |

**驗收發現**

1. **【已修正】世界覆蓋圈、攔截彈虛線與會合擴散圈看不見。** `BVRWorldDrawer` 直接畫在球面上，被地形網格蓋住。改照原版世界線（`WorldPath.DrawPath`、`GenDraw.DrawWorldRadiusRing`）把每點沿法線外推 0.08，覆蓋圈改用圖層的 `WorldLineMaterial`（太空圖層用 HighVis）、寬度 `5 × lineWidthFactor`。修正後覆蓋圈與虛線已目視確認。
2. **【已修正】雷射目標標定器會出現在敵人身上。** `DMSE_Gun_LaserDesignator` 繼承 `BaseHumanMakeableGun` 的 `weaponTags`（`Gun`），劫掠者可能帶著無傷害的標定器出場。改為 `<weaponTags Inherit="False" />`。
3. **【已修正】反輻射與電磁訊號導引頭打「剛生成的地圖」時一律退化為慣性。** 兩者都要求建築 `PowerOn`（雷達另要 `Active`），但原版電網在接上後 30–200 ticks 才逐一開啟耗電建築，導彈抵達那一 tick 地圖剛生成，沒有任何建築在運作，玩家打尚未生成地圖的 NPC 據點（最常見的情況）時，這兩種導引頭與慣性無異。改為電網正要開啟的建築也算運作中（`GuidanceType.PowerStarting`，條件同原版開機判定；雷達用 `CompBVRDevice.ActiveForSeeker`），電磁訊號以額定耗電計；S3 在地圖生成當下直接選點驗證，W6 改為必須命中相控陣雷達。熱訊號維持原樣：新地圖的房間還沒升溫，打體型最大的 Pawn，符合「房間夠熱才選房間」。

可利用開發者工具「Enemy missile strike (incoming, world)」與「Spawn NPC Missile Base (near player)／Missile Base: Fire at player NOW」手動重現；原有直接生成地圖內波次工具仍走完整後備流程。

**本次未包含**：P4 區域防禦、數據鏈、空投虛擬航跡、一般據點自動防空、未偵測航跡隱藏及抽象節點反輻射削弱。
