# DMSE 波次打擊規劃（飽和打擊介面）— 設計與實作計畫

> 狀態：**P0–P4 全部已實作（§15）**　｜　建立／更新：2026-10-19　｜　分支 `wave-strike`（基於 `main` `44a97c1`）
>
> 來源：波次打擊介面的設計討論紀錄，§0 逐條對照。§14 的待決策事項已於 2026-10-19 照建議值定案。

---

## 0. 需求對照總表

| # | 需求（討論紀錄） | 設計要點 | 主要類別 / Def |
|---|---|---|---|
| 1 | 用一個窗口對目標發射一整波導彈 | 指揮台 Gizmo → 世界選靶 → 回本地地圖開啟規劃窗口，一鍵發射整波 | `CompStrikeConsole`、`Dialog_StrikePlan` |
| 2 | 前面的低價值導彈替後面的高價值導彈擋槍 | 多波次 + 抵達時間預覽；世界防空改為「先到先打」，讓擋槍規則可預測 | `StrikeMission`／`StrikeWave`、`WorldComponent_BVRNetwork`（改） |
| 3 | 末期 10–20 發飽和打擊，彈體／導引頭／射程各異 | 候選清單依彈種分組、批次加入、依價值排序、自動分波 | `StrikeRoundUtility`、`Dialog_StrikePlan` |
| 4 | 由特定建築提供 Gizmo | 以 ThingComp 掛在指揮台建築上（沿用本 Mod「單一建築複合功能」的慣例，§14-10） | `CompProperties_StrikeConsole` |
| 5 | 先選世界目標，再回本地地圖開窗 | 世界選靶時即顯示「可及導彈數」；選定後鏡頭跳回指揮台所在地圖 | `CompStrikeConsole` |
| 6 | 依射程篩掉不可用的在架進攻導彈；攔截彈不列入 | 逐枚以 `MissileWorldUtility.CanEngage` 篩選；以導彈角色擴充排除攔截彈 | `MissileRoleExtension`、`StrikeRoundUtility` |
| 7 | 由雷達的火控導引上限決定單波最多幾發 | 進攻導引借用火控通道（與防空共用帳本），升空後占用一段「導引期」 | `BVRChannelLedger`（加 `Strike` 分項） |
| 8 | 人工版／自動版；人工版需科研殖民者完成彈道計算，期間導彈被保留 | `requiresComputation`；研究工作類型的 WorkGiver；確認計畫起即保留導彈 | `WorkGiver_ComputeFiringSolution`、`StrikeReservation` |
| 9 | 支援多波次；發射完一波後立即可發下一波，不需再選靶與計算 | 彈道解以「導彈 × 目標」為單位，一次算完全部波次；執行模式逐波發射 | `StrikeMission.state = Ready／Executing` |

---

## 1. 現況盤點（設計約束）

| 事實 | 出處 | 對設計的影響 |
|---|---|---|
| 世界選靶發射一次一發：`Launch(GlobalTargetInfo)` 是 protected，Rail 只射容器第一枚（`owner[0]`），每發後起算冷卻 | `CompMissileLauncher.cs`、`Scorer/CompMissileRailLauncher.cs` | 需要「指定導彈發射」的公開 API；照現規則，4 聯裝的輕型發射箱（冷卻 450）一波只能出 1 發 |
| Rail 的導彈若沒有 `CompMissileConfig`，會用平台 `missileBody` 補一份設定 | `CompMissileLauncher_Rail.BuildFiringConfig` | 巡飛彈（Termite，無 config）靠這條路徑；但輕型發射箱內的攔截彈（同樣無 config）現在也會被當成輕型導彈射出，看起來不是刻意的。不能只用「有無 config」判斷進攻彈 |
| 輕型發射箱的 SAM 與對地發射軌讀同一個容器；SAM 取 `owner[0]` | `CompMissileLauncher_Interceptor.LoadedMissile` | 保留中的進攻彈可能被 BVR 當攔截彈打掉，保留機制必須攔住 SAM |
| 彈道井沒有物品型態，以 `isAssembled` 與建築上的 `CompMissileConfig` 表示裝填 | `Scorer/Ballistic/CompBallisticLauncher.cs` | 候選清單要把「已裝配的井」當成一枚導彈；保留時要擋住重新裝配 |
| 火控通道一本帳：每張地圖 × 陣營，扣除砲擊預留、地圖內波次、世界層航跡 | `BVRChannelLedger` | 進攻導引加成第四個分項即可，狀態警報自動帶出 |
| NPC 陣地發射也受通道限制：通道 = Σ 火控 `maxTargets`，占用到導彈世界物件銷毀 | `MapComponent_MissileBase.TryFireFromOnMap` | 先例；但「占到抵達」會讓下一波要等上一波落地（§6-2） |
| 世界飛行 150 ticks/格 ÷ `WorldSpeedFactor`（巡飛 0.55、輕型 0.75、巡航 1、彈道 1.5） | `MissileWorldMotion`、`DMSE_BVRWorldTuning` | 同時發射的不同彈種抵達時間差很多；**發射順序不等於抵達順序**，窗口必須顯示抵達時間 |
| 世界防空的鎖定順序 = `Find.WorldObjects` 的列舉順序（約略是生成順序），有空閒通道就鎖 | `WorldComponent_BVRNetwork.WorldComponentTick → Scan` | 擋槍目前只是碰巧成立；要讓玩家可預測，改為依 ETA 先到先打（§11） |
| 抽象防空（導彈陣地）：2 通道、8 枚庫存、每日補 4 枚、單一冷卻 240 | `WorldObjectComp_AirDefense`、`WorldObjects_MissileBase.xml` | 飽和打擊要消耗的是「通道時間」與「SAM 庫存」；試算見 §2-3 |
| 「計畫 → 操作員」的先例：轉移控制台制定計畫 → `Dialog_TransferPlan` → 等待操作員（`WorkGiver_InitiateTransfer`，智識工作）→ 倒數 | `CelestialTransfer/Console/` | 人工版的狀態機、WorkGiver 與進度條 JobDriver 直接比照 |

---

## 2. 玩法目標

### 2-1. 名詞

- **打擊任務（Strike Mission）**：一個目標、一組已保留的導彈、一份彈道解、若干波次。一座指揮台同時只有一個任務（§14-7）。
- **波次（Wave）**：玩家按一次「發射」就一起升空的一組導彈。多聯裝平台以漣波（ripple）依序射出（§7-2）。
- **導引通道（Strike guidance channel）**：火控雷達的 `maxTargets`。進攻導彈升空後占用一段導引期，期間該通道不能用於防空。
- **彈道解（Firing solution）**：以「導彈 × 目標」為單位。人工版需殖民者計算；算好後，導彈在各波次之間怎麼調動都不需重算。

### 2-2. 擋槍為什麼成立

防禦方（玩家或 NPC）在世界層攔截一枚導彈要付出：

1. **一條火控通道**：從鎖定開始直到攔截彈會合結算（鎖定 120–250 ticks + 會合數百 ticks）。
2. **一枚 SAM 實體彈**（實體節點）或一枚庫存（抽象節點）。
3. **一次發射冷卻**。

三者都有限。先抵達的低價值導彈把通道占滿、把庫存打光，後到的高價值導彈就只剩更短的剩餘時間（會合必須早於 `ETA − 60` 才能發射），甚至沒有彈可攔，最後只面對抵達後的 CIWS 末端。

### 2-3. 試算：發射順序 ≠ 抵達順序

> 粗估，用來說明窗口為什麼要顯示抵達時間。方案 B 已在遊戲內實測（驗收 K4，§15-3）：陣地 8 次嘗試用光庫存，主力未被攔截。

目標：15 格外、尚未生成地圖的導彈陣地（抽象防空）。單波上限 4（相控陣 + 早期火控各 2 通道），導引期 600 ticks。

| 波次 | 內容 | 發射 tick | 飛行 ticks | 抵達 tick |
|---|---|---|---|---|
| 第 1 波 | 輕型 ×4（慣性／高爆，誘餌） | 0 | 3000 | 3000 |
| 第 2 波 | 輕型 ×4（誘餌） | 600 | 3000 | 3600 |
| 第 3 波（方案 A） | 巡航 ×2（主力） | 1200 | 2250 | **3450** ← 比第 2 波早到 |
| 第 3 波（方案 B） | 巡航 ×2（主力） | ≥ 1400 | 2250 | ≥ 3650 |

- **方案 A**：玩家以為主力「最後才發」，但巡航比輕型快，反而插在兩波誘餌中間。防空依 ETA 先打第 1 波、再打主力，第 2 波誘餌白費。
- **方案 B**：主力晚 200 ticks 以上再發，抵達排在 8 枚誘餌之後。陣地 8 枚庫存若已用在誘餌上（2 通道、每次約 850 ticks，3000 多 ticks 內約可嘗試 7–8 次），主力只剩地圖生成後的 CIWS。

所以窗口要在發射前就算出每一波的**抵達時間與抵達順序**，並在執行模式提示「現在發射會比第 k 波早到」（§10-3）。

---

## 3. 架構總覽

```
打擊指揮台（CompStrikeConsole）Gizmo「規劃波次打擊」
   │  可用條件：指揮台 Active、地圖上至少一座運作中的玩家火控雷達、沒有進行中的任務
   ▼
世界選靶（WorldTargeter）
   │  CanTargetTile 同發射平台；游標旁顯示距離與「可及導彈 n／在架 m」；n = 0 時拒絕
   ▼
鏡頭跳回指揮台地圖 → Dialog_StrikePlan【規劃模式】
   │  候選導彈：掃描本地圖所有玩家世界選靶平台 → 逐枚判斷角色、射程、導引能力（§5）
   │  玩家把導彈分配到第 1..N 波（每波 ≤ 導引通道容量），預覽各波飛行時間與抵達順序
   ▼
確認計畫 → 保留全部已分配導彈（§8-4）
   ├─ 自動版：機器計算 autoComputeTicks（需通電）→ Ready
   └─ 人工版：AwaitingComputation → WorkGiver_ComputeFiringSolution（研究）
               → JobDriver_ComputeFiringSolution（進度可中斷保留）→ Ready
   ▼
Dialog_StrikePlan【執行模式】（Gizmo「開啟打擊計畫」或「發射第 k 波」快捷）
   │  發射第 k 波：逐枚重驗 → 檢查空閒導引通道 → 排入漣波佇列 → 升空後占用導引通道
   │  下一波按鈕立即出現；只受導引通道、平台冷卻、平台可運作與否限制
   ▼
全部波次發射完 → Completed（保留已全部釋放）；取消或目標消失 → Cancelled（釋放剩餘保留）
```

---

## 4. 資料模型

### 4-1. 存檔類別

```csharp
public enum StrikeMissionState { Planning, AwaitingComputation, Ready, Executing, Completed, Cancelled }

public class StrikeMission : IExposable
{
    public int id;                          // MapComponent_StrikePlanner 遞增配發，存檔
    public PlanetTile targetTile;
    public MapParent targetParent;          // Scribe_References；null = 打固定 tile（抵達時可能落空）
    public StrikeMissionState state;
    public List<StrikeWave> waves;
    public float computeWorkDone, computeWorkTotal;   // 人工版
    public int computeDoneTick = -1;                  // 自動版：機器計算完成的 tick
    public int createdTick;
}

public class StrikeWave : IExposable
{
    public List<StrikeSlot> slots;
    public int launchedTick = -1;           // -1 = 未發射
}

public class StrikeSlot : IExposable
{
    public ThingWithComps launcher;         // 平台建築（Scribe_References）
    public Thing round;                     // Rail：容器中的導彈 Thing；彈道井／Scorer：平台建築本身
    public int configHash;                  // 確認時的 MissileConfig 指紋，用來發現被改裝
    public bool solved;                     // 人工版：此枚的彈道解已算好
    public StrikeSlotResult result;         // Pending／Launched／Skipped（含原因鍵）
    public MissileWorldObject launched;     // 升空後的世界航跡（Scribe_References，追蹤用，可為 null）
}
```

- **任務放在指揮台 Comp 上**（`CompStrikeConsole.mission`，`Scribe_Deep`），隨建築存檔；指揮台被拆或摧毀時任務取消並釋放保留。
- **`MapComponent_StrikePlanner`**（每地圖一個）持有：
  - 指揮台與世界選靶平台的註冊表（比照 BVR 裝置在 `PostSpawnSetup`／`PostDeSpawn` 自行註冊）；
  - 保留索引（`Dictionary<Thing, int>`：彈藥 → 任務 id；彈道井以平台本身為彈藥，不另設索引），不存檔，讀檔後由各指揮台的任務重建；
  - 進攻導引通道占用（`StrikeGuidanceHold`：陣營 + 釋放 tick，存檔）；
  - 漣波發射佇列（`StrikeQueuedLaunch`：指揮台參照 + 任務 id + 波次／槽位索引 + 發射 tick + 是否漣波續射，存檔）。排隊中的每一枚也計入導引通道占用，按下發射時就訂好通道，漣波途中不會被防空鎖定搶走。
  - P1 實作：`StrikeSlot` 另有 `track`（升空後的世界航跡，存檔時已結束者清空）與 `skipReason`；`StrikeMission` 另有 `targetedSite`（選靶時有據點，用來判斷目標丟失）。彈道井／Scorer 的 `round` 是平台本身，沒有另設索引。
- 容器內的導彈雖然 unspawned，仍以 `ThingOwner` 深度存檔，`Scribe_References` 可正常回連；被摧毀或離開容器的，在下一次驗證時判定失效。

### 4-2. 候選導彈（不存檔）

```csharp
public struct StrikeRound
{
    public CompMissileLauncher_WorldTargeting launcher;
    public Thing round;                     // 彈道井／Scorer 為平台本身
    public MissileConfig config;            // BuildFiringConfig(round) 的副本
    public float distance;                  // MissileWorldUtility.RangeDistance(map.Tile, target)
    public int flightTicks;                 // 依 MissileWorldMotion.AngularSpeed(config) 與球面角距離計算
    public StrikeRoundIssue issue;          // None／OutOfRange／CrossLayerNoGuidance／LauncherInactive／Cooldown／ReservedElsewhere
    public bool pendingAssembly;            // CompMissileConfig.NeedsAssembly（只警示，不阻擋）
}
```

---

## 5. 候選導彈篩選

### 5-1. 列舉

`StrikeRoundUtility.EnumerateRounds(map, target)`：

| 平台 | 一座平台出幾枚 | 設定來源 |
|---|---|---|
| `CompMissileLauncher_Rail` | 容器中每一枚**進攻彈** | 該 Thing 的 `CompMissileConfig.config`；沒有時用 `Props.missileBody`（巡飛彈） |
| `CompMissileLauncher_Ballistic` | 已裝配（`isAssembled`）時 1 枚 | 建築上的 `CompMissileConfig.config` |
| `CompMissileLauncher_Scorer` | 燃料滿時 1 枚（1.7 無 Def 使用） | `Props` |

原本的 `BuildFiringConfig()` 只看 `owner[0]`，已改為公開的 `BuildFiringConfig(Thing round)`；平台以 `StrikeRounds` 列出在架彈藥（含保留中者），單發 Gizmo 用 `NextRound`（第一枚未保留的彈藥）。

### 5-2. 進攻彈與攔截彈

新增 `MissileRoleExtension`（DefModExtension，掛在導彈物品 ThingDef）：

```xml
<li Class="DMSE.MissileRoleExtension">
  <role>Interceptor</role>   <!-- Strike（預設）／Interceptor／Dual -->
</li>
```

- `DMSE_InterceptorMissile` = `Interceptor`：不列入候選，也不再能被 Rail 以平台 `missileBody` 補設定後射出（順手修正 §1 的誤射路徑；若原本是刻意設計，改標 `Dual` 即可保留）。
- `DMSE_LightMissile` = `Dual`：可進攻，未保留時也可被 SAM 取用（現行行為）。
- 未掛擴充 = `Strike`（巡航、巡飛彈）。
- 不用「戰鬥部是連續桿就算攔截彈」之類的推論：玩家可能故意裝連續桿打地面，規則要可預測。
- 角色只管世界打擊；SAM 取彈仍由建築的儲存篩選決定（旋轉發射架照舊可把巡飛彈當攔截彈）。

### 5-3. 篩選與標示

| 結果 | 條件 | 窗口呈現 |
|---|---|---|
| 可用 | `CanEngage` 通過，平台 `Active`、不在冷卻 | 正常列出 |
| 可用（暫不可射） | 平台斷電／關閉／損壞／無人操作，或冷卻中 | 標黃列出；可排進波次，發射時再判定（冷卻顯示剩餘時間） |
| 不可用 | 超出射程、跨圖層無導引能力 | 摺疊在「不可用（n）」區塊、灰顯並附原因；不能加入波次 |
| 不列入 | 攔截彈、被其他任務保留 | 不顯示；被其他任務保留的數量另列一行 |

`NeedsAssembly`（`pending ≠ config`）的導彈照常列出並警示「將以現行設定發射；確認計畫後，待裝配的變更會擱置到任務結束」。

### 5-4. 分組與排序

依「彈體 → 導引頭 → 戰鬥部／酬載」分組，每組一列：數量、飛行時間、射程餘裕（`range − distance`）、有無終端導引（影響逃逸區）、單枚價值（物品 MarketValue；彈道井以彈體與部件成本換算）。可展開看各平台、各枚。預設依價值由低到高排序，方便把便宜彈排在前面當誘餌。

---

## 6. 導引通道

### 6-1. 容量與帳本

- 容量 = `BVRChannelLedger.Capacity(玩家)`：本地圖運作中玩家火控雷達的 `maxTargets` 總和。人工操作的早期火控雷達沒人操作時不算（沿用 `Active`）。
- `BVRChannelLedger` 新增分項 `Strike(faction)` = `MapComponent_StrikePlanner` 中 `releaseTick > now` 的占用數，併入 `Occupied`。防空、砲擊、進攻共用一池；`Alerts_BVRStatus` 分項顯示。
- 新欄位 `CompProperties_FireControlRadar.supportsStrikeGuidance`（預設 true），保留給日後只能防空的雷達。

### 6-2. 占用多久

每枚導彈升空即占 1 通道，`strikeGuidanceTicks`（Tuning，建議 600）後釋放，之後交給彈上導引頭。

| 方案 | 說明 | 問題 |
|---|---|---|
| 占到抵達或被毀（NPC 陣地現行做法） | 最直覺的「導引中」 | 下一波要等上一波落地才能發；誘餌與主力的抵達間隔必然很大，擋槍不成立，也違反需求 9 |
| 只在發射瞬間檢查、不占用 | 最簡單 | 連按兩次就等於一波兩倍大，「單波上限」失去意義 |
| **固定導引期（建議）** | 單波上限有意義；導引期就是波次的最短間隔 | 需要平衡一個數值 |

導引期也讓攻防互相排擠：敵方導彈來襲占著通道時，玩家發不出完整的一波；玩家剛發完一波時，防空能用的通道也變少。

### 6-3. 單波上限

- 規劃模式：每波上限 = 通道容量；超過時該波標紅、不能確認。
- 執行模式：發射需要「空閒通道 ≥ 本波枚數」；不足時按鈕停用並顯示「可用 a／需 b，最快 t 後釋放」。Shift+點擊 = **部分發射**：只射空閒通道數，剩下的留在本波等待補射。
- 指揮台另有 `maxWavesPerMission`、`maxRoundsPerMission`，作為人工版與自動版的進程差異（§8），不取代通道上限。

---

## 7. 發射執行

### 7-1. 發射前重驗（逐枚）

1. 導彈仍在原平台容器內（彈道井：仍 `isAssembled`），`configHash` 相同。
2. 平台 `Active`、不在冷卻（同平台漣波的後續各枚例外，見 §7-2）。
3. 目標仍存在；以**當下**目標 tile 重跑 `CanEngage`（移動目標可能已出射程）。
4. 有空閒導引通道。

失敗的枚數標為 Skipped 並附原因，本波其餘照射；結果以一則訊息彙總（「第 2 波：發射 3／4，1 枚超出射程」）。

### 7-2. 漣波發射（多聯裝平台）

- 一座平台在同一波可射出多枚；同平台各枚間隔 `salvoIntervalTicks`（新 Props，建議 30），**整波射完才起算一次 `launchCooldownTicks`**。
- 不同平台同時開始：4 座單聯裝平台同一 tick 升空，1 座 4 聯裝平台約 90 ticks 射完。
- `MapComponent_StrikePlanner` 持有發射佇列 `(slot, fireTick)`，每 tick 處理到時者，呼叫新的公開 API：

```csharp
// CompMissileLauncher_WorldTargeting
public ScorerProjectile_WorldObject TryLaunchRound(GlobalTargetInfo target, Thing round,
    bool salvoContinuation, out string failReason);   // 失敗回傳 null 並給出原因
```

  Rail 發射指定的 Thing 而非 `owner[0]`；彈道井與 Scorer 的彈藥是平台本身。發射前重驗平台可運作、彈藥仍在、冷卻（`salvoContinuation = true` 時略過）、設定與射程（`CanEngage`）；每枚發射後都重新起算冷卻，所以冷卻從本平台最後一枚起算。原本的 `Launch(GlobalTargetInfo)` 已移除，單發 Gizmo 改呼叫 `TryLaunchRound(t, NextRound, false, …)`。本方法不檢查保留（波次打擊要發射的正是保留中的彈藥）。

### 7-3. 航跡標記

`ScorerProjectile_WorldObject` 新增 `strikeMissionId`、`strikeWave`（預設 -1，舊存檔安全），讓窗口與 `Alerts_PlayerMissiles` 能依任務／波次彙整：在飛、被攔截、命中、落空。

---

## 8. 人工版與自動版

### 8-1. Props

```xml
<!-- 人工版範例 -->
<li Class="DMSE.CompProperties_StrikeConsole">
  <requiresComputation>true</requiresComputation>
  <computeWorkBase>1500</computeWorkBase>
  <computeWorkPerRound>400</computeWorkPerRound>
  <crossLayerWorkFactor>1.5</crossLayerWorkFactor>
  <autoComputeTicks>0</autoComputeTicks>            <!-- 自動版：機器計算時間（需通電），建議 300 -->
  <maxWavesPerMission>3</maxWavesPerMission>
  <maxRoundsPerMission>12</maxRoundsPerMission>
</li>
```

### 8-2. 人工版：彈道計算

- **工作**：`WorkGiver_ComputeFiringSolution`（`WorkGiver_Scanner`，工作類型 `Research`，掃描 `AwaitingComputation` 的指揮台）→ `JobDriver_ComputeFiringSolution`：走到互動格、帶進度條的工作 toil，每 tick 工作量 = `ResearchSpeed`，給智識經驗。比照 `JobDriver_InitiateTransfer` 與原版研究台。
- **工作量** = `computeWorkBase + computeWorkPerRound × 枚數`，跨圖層目標再 × `crossLayerWorkFactor`。進度存在任務上，中斷後可續算；右鍵指揮台可強制指派（Scanner 自動提供）。
- **逐枚解出**（P2 實作）：基礎工作量做完後，依波次順序每多做一枚的量就解出一枚；某一波的待發射導彈都解好就能發射，不必等後面的波次。每解完一波發一則訊息。
- **增量計算**（P3 已實作，§15-7）：Ready 後加入新導彈只補算新枚數的工作量，任務回到 AwaitingComputation；已算好的波次照常可發射（逐枚看 `solved`）。
- **警報** `Alert_StrikeComputationPending`：有任務待計算，但沒有能做研究工作的殖民者，或研究工作被全員停用。

### 8-3. 自動版

- 確認後進入短暫機器計算（`autoComputeTicks`，指揮台需 `Active`），之後 Ready；不需要殖民者。
- 兩版只靠數值區分：自動版上限較大（例：5 波／20 枚）；人工版較早可建、上限較小。研究前置待定（人工版建議接 `DMSE_Ballistics`，自動版接雷達線後段）。

### 8-4. 保留（兩版共用）

從**確認計畫**起，到所屬波次發射或任務取消為止，保留中的導彈：

| 消耗／改動路徑 | 處理 |
|---|---|
| 單發發射 Gizmo（Rail／彈道井） | 只看未保留的彈；全部被保留時停用並顯示「已保留給打擊任務」 |
| SAM 攔截（地圖內 `FireInterceptor` 與世界 `FireWorldInterceptor`） | `LoadedMissile` 改為第一枚**未保留**的彈，`HasAmmo` 同步 |
| `ITab_MissileAssembly` 編輯 | 保留中的導彈／彈道井禁止編輯（按鈕停用附原因） |
| `WorkGiver_AssembleMissile`、送料與裝配工作 | 略過保留中的導彈（`MissileAssemblyUtility.GetAssemblyComp` 對架內導彈跳過保留者） |
| `WorkGiver_AssembleBallisticSilo` | 不需改：它原本就只處理未裝填的井，而被保留的井必定已裝填 |
| 平台被拆除、搬走、摧毀 | 不阻擋；導彈掉出容器後該枚失效，發訊息並在窗口標示 |
| 平台儲存設定變更 | 不需處理：`Building_MissileRack` 只在拆除／DeSpawn 時吐出內容物，篩選器變更不會把已裝填的彈取出（P0 確認） |

窗口外的回饋：平台 inspect 多一行「已保留：打擊任務 #3 第 2 波（2 枚）」；指揮台 inspect 顯示任務狀態與計算進度。

---

## 9. 目標與狀態變動

| 情況 | 處理 |
|---|---|
| 目標據點移動（轉移船、陣地轉移） | 彈道解維持有效；每次發射以當下 tile 重驗射程。在飛航跡照舊靠終端更新追蹤 |
| 目標被摧毀或消失 | 任務 Cancelled、釋放保留、發訊息；已在飛的導彈照原規則落空 |
| 指揮台斷電／損壞 | 任務保留；計算暫停、不能發射（Gizmo 停用附原因） |
| 指揮台被拆除或摧毀 | 任務 Cancelled、釋放保留 |
| 所有火控雷達失效 | 容量 0；不能發射，任務不取消 |
| 存讀檔 | 任務、導引通道占用、漣波佇列隨存檔；保留索引讀檔後重建 |

---

## 10. 介面設計

### 10-1. 入口 Gizmo（指揮台）

| Gizmo | 顯示時機 | 停用條件 |
|---|---|---|
| 規劃波次打擊 | 沒有任務 | 指揮台不可運作；地圖上沒有運作中的玩家火控雷達；沒有任何在架進攻彈 |
| 開啟打擊計畫 | 有任務 | — |
| 發射第 k 波 | Ready／Executing 且有下一波 | 同 §6-3、§7-1 |
| 取消打擊任務 | 有任務 | —（需確認對話框） |

世界選靶沿用發射平台的 `CanTargetTile`；游標旁顯示距離、「可及導彈 n／在架 m」，該處沒有據點時提示「將會落空」。

### 10-2. 規劃模式

```
┌ 波次打擊規劃 ── 目標：海盜導彈陣地（15.2 格）── 狀態：規劃中 ─────────────────────┐
│ 導引通道 4（相控陣 2、早期火控 2）   單波上限 4   導引期 600 ticks                │
├──────────────────────────────────┬───────────────────────────────┤
│ 可用導彈 11        [彈體▼][導引▼] 排序：價值▲ │ ▼ 第 1 波  4/4    飛行 3000            │
│ ▸ 輕型・慣性・高爆      ×8  3000t  餘裕+12 $1.2k │     輕型・慣性・高爆 ×4        [−]     │
│ ▸ 巡航・電磁・空爆      ×2  2250t  餘裕+15 $4.5k │ ▼ 第 2 波  4/4    飛行 3000            │
│ ▸ 彈道・雷射・鎢芯      ×1  1500t  餘裕+25 $9.0k │     輕型・慣性・高爆 ×4        [−]     │
│ ▹ 不可用 3：超出射程 ×2、跨圖層需導引 ×1         │ ▼ 第 3 波  2/4    飛行 2250  ⚠        │
│                                                  │     巡航・電磁・空爆 ×2        [−]     │
│ 點一組 = 加一枚到選取中的波次；Shift = 整組      │ [＋新增波次]                           │
├──────────────────────────────────┴───────────────────────────────┤
│ 抵達預覽（各波以最短間隔接續發射）                                                 │
│  0      600    1200                         3000   3450  3600                       │
│  W1↑    W2↑    W3↑                          W1✸    W3✸   W2✸   ⚠ 第 3 波早於第 2 波抵達 │
│ [自動分波]  [清空]          計算工作量 5500（人工）          [取消]  [確認計畫]      │
└──────────────────────────────────────────────────────────────────┘
```

- **分配**：點右側某波設為「選取中」；左側點一組加一枚、Shift 加整組（超過單波上限時截斷）；右側 [−] 移除。組別可展開，指定特定平台的特定一枚（P3，§15-7）；直接點組別時優先挑現在就能發射的那幾枚。
- **自動分波**：依價值由低到高填滿各波，最高價值的放最後一波；每波 ≤ 通道容量，波數 ≤ `maxWavesPerMission`。
- **抵達預覽**：假設各波以最短間隔（導引期與平台冷卻取大）接續發射，畫出升空與抵達時間；抵達順序與波次順序不一致時警示（§2-3 方案 A）。
- **底部**：人工版的計算工作量，以及以目前最快研究者的 `ResearchSpeed` 估算的所需時間。
- **目標是尚未生成地圖的據點時**，顯示提示：第一枚抵達的導彈會生成據點地圖，地圖上的 SAM 照常滿彈，之後還在飛的導彈改由實體防空攔截（§15-4 決定 1）。
- 規劃模式 `forcePause`，比照 `Dialog_TransferPlan`。

### 10-3. 執行模式

（下例為 tick 1000：第 1 波於 0、第 2 波於 600 發射，第 2 波的導引期到 1200 才結束）

```
┌ 波次打擊 ── 目標：海盜導彈陣地 ── 狀態：執行中 ──── 導引通道 可用 0/4（200 ticks 後釋放 4）┐
│ 第 1 波  已發射 4/4   在飛 3  已被攔截 1                     ETA 2000–2010            │
│ 第 2 波  已發射 4/4   在飛 4                                 ETA 2600–2610            │
│ 第 3 波  待發射 2     [發射]（通道不足：可用 0／需 2，200 ticks 後）                   │
│          若現在發射：抵達 3250，⚠ 早於第 2 波；建議 410 ticks 後發射                  │
│                                                       [調整波次]  [取消任務]          │
└──────────────────────────────────────────────────────────────────────┘
```

- 每波顯示發射與戰果彙總（依 §7-3 的航跡標記），在飛的波次顯示 ETA 區間。
- 「若現在發射」即時計算本波抵達時間與相對其他波次的順序，並給建議最早發射時間（讓本波晚於前一波 `arrivalSpacingTicks`，建議 60）。
- **調整波次**（P3 已實作，§15-7）：回到規劃介面，但只能在已保留（且已解）的導彈之間調動，不需重算；加入新導彈觸發增量計算（§8-2）。P1 要改計畫只能取消任務重新規劃。
- 執行模式不強制暫停，玩家需要邊看邊等時機。

### 10-4. 其他回饋

- `Alerts_PlayerMissiles`：玩家航跡依「任務／波次」分組。
- `Alert_StrikeWaveReady`（可關）：任務 Ready 且有可發射的波次。
- 任務結束時一則訊息彙總：各波發射數、被攔截、命中、落空。

---

## 11. 防禦端調整：先到先打

`WorldComponent_BVRNetwork.WorldComponentTick` 掃描航跡時改依 `TicksToArrival` 由小到大，讓有限的通道優先分給最急迫的航跡：

```csharp
foreach (MissileWorldObject track in Tracks.OrderBy(t => t.TicksToArrival).ToList()) Scan(track, now);
```

- 攻守對稱：敵方對玩家齊射時，玩家防空也先打先到者。
- 偵測不受影響（每條航跡每輪仍檢查是否入圈）；改變的只有「通道不足時誰先鎖」。
- 已鎖定或攔截彈在飛的航跡維持原狀，不會被搶走。
- 驗證：BVRTests 加「兩條航跡、1 通道，晚發但先到者先被鎖」；BVRAcceptance 的 W 系列重跑確認無回歸。

**P4 已實作（§15-8）**：威脅評估（雷達截面大者先打）與誘餌酬載（`signatureFactor` 放大 `distanceLevel`）。`BVRTargetProps` 改為落點 skyfaller def 的值再乘上導彈設定的截面倍率。

---

## 12. 存檔與相容

| 項目 | 處理 |
|---|---|
| 任務 | `CompStrikeConsole` `Scribe_Deep`；導彈、平台、目標以 `Scribe_References` |
| 保留索引 | 不存檔；讀檔後由各指揮台的任務重建 |
| 導引通道占用、漣波佇列 | `MapComponent_StrikePlanner` 存檔 |
| `ScorerProjectile_WorldObject.strikeMissionId／strikeWave` | 新欄位，預設 -1 |
| `MissileRoleExtension` | 純 Def 資料；未掛者視為 `Strike` |
| Rail 不再以 `missileBody` 補射攔截彈 | 行為修正；舊存檔中位於輕型發射箱 `owner[0]` 的攔截彈，單發 Gizmo 改射後面的進攻彈，沒有就停用 |
| 1.6 | 不動（凍結） |

---

## 13. 分期實作與驗收

| 期 | 內容 | 驗收 |
|---|---|---|
| **P0 底層**（已實作，§15） | `MissileRoleExtension` 與 XML 標記；`BuildFiringConfig(Thing)`、`TryLaunchRound`、漣波參數；世界選靶平台註冊表；`StrikeReservation` 與 §8-4 各掛點（先用 Debug 動作保留／釋放驗證）；防空依 ETA 排序；Rail 不再誤射攔截彈 | 沒有保留時，單發 Gizmo、SAM、裝配行為與現行一致；保留中的彈不會被 SAM、單發、裝配消耗；ETA 排序與 §2-3 試算有對應 BVRTests；既有 BVRTests、BVRAcceptance 全數通過 |
| **P1 自動版**（已實作，§15-5） | `CompStrikeConsole`（自動）、世界選靶、`Dialog_StrikePlan` 規劃／執行模式、帳本 `Strike` 分項與導引期、漣波佇列、航跡標記、Gizmo、inspect、三語 Keyed、`DMSE_StrikeCommandCenter` Def | 12 枚混合彈分 3 波：每波不超過通道；4 聯裝平台一波射完 4 枚、冷卻只起算一次；第 2 波在導引期結束後可發；超射程彈不出現在可用區；存讀檔後任務、佇列、通道占用一致 |
| **P2 人工版**（已實作，§15-6；增量計算在 P3 完成） | `requiresComputation`、WorkGiver／JobDriver（研究）、增量計算、`Alert_StrikeComputationPending`、`DMSE_StrikePlanningTable` Def | 確認後導彈立即保留；研究者算完才可發射；中斷後進度保留；計算中的導彈不被 SAM 取用；加彈只補算新枚數 |
| **P3 回饋**（已實作，§15-7） | 抵達預覽時間軸與順序警示、「若現在發射」提示、`Alerts_PlayerMissiles` 分組、任務結束彙總訊息（含攔截／命中）、組別展開指定單枚、執行中調整波次（自動分波已在 P1 完成） | §2-3 方案 A 被標警示、方案 B 不警示；彙總數字與實際戰果一致 |
| **P4 選配**（已實作，§15-8） | 定時發射（指定抵達間隔，在建議時機自動發射）、目標移動超過門檻要求重算、威脅評估與誘餌酬載、NPC 陣地改用波次發射 | 各項獨立驗收 |

**驗證方式**：純邏輯（候選篩選、通道帳、ETA 排序、工作量）放 `.source/DMSE.BVRTests`；遊戲內流程在 `.source/DMSE.BVRAcceptance` 新增 `Tests_Strike.cs`：建立平台與指揮台 → 以 API 建任務 → 逐波發射 → 檢查通道、冷卻、航跡標記與攔截結果。

**檔案（預估）**

- 新增（`.source/DMSE/Scorer/Strike/`）：`CompStrikeConsole.cs`、`StrikeMission.cs`（含 Wave／Slot）、`StrikeRoundUtility.cs`、`StrikeReservation.cs`、`MapComponent_StrikePlanner.cs`、`Dialog_StrikePlan.cs`、`WorkGiver_ComputeFiringSolution.cs`、`JobDriver_ComputeFiringSolution.cs`、`Alerts_Strike.cs`、`MissileRoleExtension.cs`
- 修改：`CompMissileLauncher.cs`（`TryLaunchRound`、單發 Gizmo 跳過保留彈）、`Scorer/CompMissileRailLauncher.cs`、`Scorer/Ballistic/CompBallisticLauncher.cs`、`SkyFallerTurret/BVR/CompMissileLauncher.cs`（SAM 跳過保留彈）、`BVRChannelLedger.cs`、`CompFireControlRadar.cs`、`WorldComponent_BVRNetwork.cs`（ETA 排序）、`ScorerProjectile_WorldObject.cs`、`ITab_MissileAssembly.cs`、`WorkGiver_AssembleMissile.cs`、`WorkGiver_AssembleBallisticSilo.cs`、`Alerts_BVRStatus.cs`、`Alerts_PlayerMissiles.cs`
- Defs（`1.7/`）：兩座指揮台建築、`JobDef`／`WorkGiverDef`、`MissileRoleExtension` 標記（攔截彈、輕型導彈）、Tuning 欄位（`strikeGuidanceTicks`、`arrivalSpacingTicks`）、三語 Keyed

---

## 14. 決策（2026-10-19 照建議值定案）

| # | 問題 | 選項 | 建議 |
|---|---|---|---|
| 1 | 導引通道占用時長 | (a) 到抵達；(b) 固定導引期；(c) 依導引頭類型 | **(b)** 600 ticks，Tuning 可調（§6-2） |
| 2 | 進攻導引與防空是否共用通道 | (a) 共用帳本；(b) 獨立池 | **(a)**：沿用「火力通道一本帳」，攻防互相排擠就是取捨點 |
| 3 | 多聯裝平台一波可射幾枚 | (a) 1 枚；(b) 漣波全射，整波只起算一次冷卻 | **(b)**，`salvoIntervalTicks` 30；否則 4 聯裝對飽和打擊沒有意義 |
| 4 | 不可用的導彈 | (a) 隱藏；(b) 摺疊灰顯附原因 | **(b)**：玩家才知道為什麼少了幾枚 |
| 5 | 人工版的工作類型 | (a) 研究（`ResearchSpeed`）；(b) 自訂智識工作 | **(a)**：符合「科研能力」，玩家不必另開工作欄 |
| 6 | 彈道解何時失效 | (a) 不失效，發射時重驗射程；(b) 目標移動超過 X 格需重算；(c) 逾時失效 | **v1 (a)**；P4 改為 **(b)**：移動 ≥ 5 格（`strikeRecomputeMoveTiles`）時未發射者重算（§15-8） |
| 7 | 一座指揮台同時幾個任務 | 1／多個 | **1**：多目標靠多座指揮台；介面與保留都單純 |
| 8 | 自動版要不要計算延遲 | 0／短延遲 | **短延遲 300 ticks**（需通電），保留「計算」的感覺；可設 0 |
| 9 | 平台單發 Gizmo 的去留 | 保留／移除 | **保留**：早期和單發仍方便；被保留的彈不能用 |
| 10 | 「特定建築 Class」的實作形式 | Building 子類／ThingComp | **ThingComp**（`CompStrikeConsole`）：與本 Mod 其他系統一致，也能掛到其他建築上（例如之後讓高階雷達自帶自動版） |
| 11 | 防空接戰順序 | (a) 現行生成順序；(b) ETA 先到先打 | **(b)**（§11） |
| 12 | 定時發射（TOT） | v1 做／延後 | **P4 已實作**：逐波勾選「定時發射」，在會排在前面各波之後抵達時自動發射（§15-8）；P3 的時間軸給建議延後量 |
| 13 | 漏網誘餌生成據點地圖後，SAM 彈與抽象庫存的對應（§15-4） | (a) 單一庫存（生成時裁減、移除時寫回）；(b) 只在生成時裁減；(c) 維持現狀＋提示 | **(c)**（2026-10-19 定案）：機制不變，P1 規劃窗口提示 |
| 14 | 節點沒有攔截彈時的鎖定（§15-4） | (a) 沒彈不鎖、打光即放，世界層與地圖內一致；(b) 只改世界層；(c) 維持現狀 | **(a)**（2026-10-19 定案並實作） |

---

## 15. 實作與驗證記錄（2026-10-19）

### 15-1. P0 已完成

- **導彈角色**：`MissileRoleExtension`（`Strike`／`Interceptor`／`Dual`）；`DMSE_InterceptorMissile` 標為 `Interceptor`、`DMSE_LightMissile` 標為 `Dual`，其餘未標者為 `Strike`。只影響世界打擊。
- **平台彈藥 API**（`CompMissileLauncher_WorldTargeting`）：`StrikeRounds`（含保留中者）、`NextRound`（第一枚未保留）、`OwnsRound`（參照比較；`Thing.Equals` 比的是 ID 與 def）、公開的 `BuildFiringConfig(Thing)`、`TryLaunchRound(target, round, salvoContinuation, out failReason)`、`SalvoIntervalTicks`（Rail 新欄位 `salvoIntervalTicks`，預設 30）。彈道井與 Scorer 以平台本身為彈藥。原 `Launch` 已移除；單發 Gizmo 改走 `TryLaunchRound`，選靶途中狀態改變時會留在選靶畫面並顯示原因（以前是靜默關閉）。
- **Rail 修正**：攔截彈不再被補成輕型導彈射出；Rail 的 `HasAmmo` 只計可進攻的導彈，所以只裝攔截彈的輕型發射箱，對地 Gizmo 會顯示「未裝填導彈」。
- **`MapComponent_StrikePlanner`**：世界選靶平台註冊表與保留索引（`Dictionary<Thing, int>`，不存檔；開發者模式的手動保留讀檔後即消失）。`StrikeReservation` 為查詢入口。
- **保留掛點**（§8-4）：單發 Gizmo（全數保留時停用並說明）、SAM 地圖內與世界攔截（取第一枚未保留的彈；全數保留時 `HasAmmo = false`）、`MissileAssemblyUtility.GetAssemblyComp`／`WorkGiver_AssembleMissile`（略過保留中的導彈，待裝配變更擱置）、`ITab_MissileAssembly`（紅字提示並停用編輯按鈕，數值照常顯示）、平台 inspect 顯示保留數。
- **防空接戰順序**：`WorldComponent_BVRNetwork.InEngagementOrder`，依 ETA 由小到大的穩定排序（§11）。
- **開發者模式動作**：「Strike: reserve rounds on selected launchers」「Strike: release debug reservations」「Strike: log strike rounds on map」。
- **資料**：三語 Keyed `DMSE_Strike.xml`（3 個鍵）；兩個導彈物品 Def 加角色擴充。1.6 未改動。

### 15-2. 自動驗證結果

- `dotnet build .source/DMSE/DMSE.csproj -c Release --no-restore`：**0 警告、0 錯誤**；已更新正式 `1.7/Assemblies/DMSE.dll`。
- `.source/DMSE.BVRTests`：**147 項檢查通過**（新增 25 項：角色預設與標記、保留的擁有者衝突／釋放／銷毀自動失效、攔截彈與輕型導彈混裝的 Rail 彈藥列舉與設定、彈道井彈藥、接戰順序與同 ETA 時的穩定性）。
- `.source/DMSE.BVRAcceptance` 新增 K1–K4，完整套件 **29/29 通過、313 項檢查**（世界層與地圖內既有的 M／S／W／L 系列全數無回歸）：

| 測試 | 驗證內容 |
|---|---|
| K1 | 攔截彈不列入打擊彈藥、不會被補成對地設定；保留後單發改用下一枚；保留彈排在容器第一格時 SAM 改射後面的攔截彈；全數保留時 SAM 無彈、單發 Gizmo 停用並說明；超射程發射被拒且不耗彈不進冷卻；指定保留彈發射、冷卻、漣波續射略過冷卻且冷卻從最後一枚起算 |
| K2 | 保留中的導彈不產生裝配工作，釋放後恢復；已裝配的彈道井以自身為彈藥，保留後單發 Gizmo 停用並說明 |
| K3 | 2 條通道、3 條同時生成的來襲航跡：最先生成但最晚抵達者被偵測卻不被鎖定，兩條先到的航跡占用通道（舊的生成順序會鎖錯對象） |
| K4 | §2-3 方案 B 實測（見 §15-3） |

### 15-3. §2-3 方案 B 實測（K4，兩次執行結果一致）

約 15 格外、尚未生成地圖的導彈陣地（抽象防空：2 通道、8 枚庫存、冷卻 240）。第 1 波 4 枚輕型誘餌，600 ticks 後第 2 波 4 枚，約 1500 ticks 後 2 枚巡航主力；主力抵達（約 +3800）排在最後一枚誘餌（約 +3650）之後。觀察到第一枚導彈抵達前 200 ticks 為止（避免生成據點地圖）。

| 項目 | 結果 |
|---|---|
| 誘餌 | 8/8 被擊落，共 8 次攔截嘗試（每發都命中） |
| 陣地庫存 | 約 +2650 時已打光（0/8），比誘餌預定抵達早約 1000 ticks |
| 主力 | 到觀察結束都沒有被攔截嘗試。最初兩次執行時主力約在 +2650 被鎖定但已無彈可射；套用決定 2（沒彈不鎖）之後，庫存打光的陣地不再鎖定主力 |
| 接戰順序 | 沒有任何時刻在先到的誘餌還在等待時去攔主力 |

結論：§2-3 對「2 通道約可嘗試 7–8 次、庫存 8 是瓶頸」的估計成立，實際為 8 次，受庫存限制。主力抵達後的 CIWS 末端不在本測試範圍內。

### 15-4. 發現與決定

1. **漏網的誘餌會提早生成據點地圖，主力反而面對滿彈的實體防空**（程式推論，K4 未實測，因為誘餌全數被擊落）。玩家導彈打尚未生成地圖的據點時，第一枚抵達的導彈會生成地圖；依世界層設計 §8-1，地圖上的實體彈架照舊自動填滿、不扣抽象庫存，之後還在飛的主力改由實體節點攔截（陣地為 2 座輕型發射箱共 8 枚）。所以誘餌「先漏網抵達」對攻方比「被打光」更糟，玩家很難理解。
   **決定（2026-10-19）：維持現狀**，機制不改；P1 規劃窗口在目標為尚未生成地圖的據點時提示這一點（§10-2）。考慮過的另兩案：單一庫存（生成時把 SAM 彈裁到抽象庫存剩餘量、移除時寫回），以及只在生成時裁減。
2. **沒有攔截彈時，節點仍會鎖定並一直占著通道**：鎖定不檢查有無可用的 SAM，鎖定完成後只是等待發射器。抽象節點影響不大；但 P1 進攻導引與防空共用通道帳本，玩家自己的 SAM 打光時，來襲航跡會把火控通道占滿，導致波次發不出去。
   **決定並已實作（2026-10-19）：沒彈不鎖、打光即放**，世界層與地圖內中過程一致。`MapComponent_BVRCombat.HasInterceptorAmmo(faction)`：任一 Active 的 SAM（有未保留彈藥、通電、有人操作；裝填冷卻中也算）；抽象節點看庫存 > 0（`BVRNode.HasInterceptorAmmo`）。沒有時：新目標不鎖定，鎖定中或已完成但未發射的鎖定釋放；已在飛的攔截彈不受影響。被打擊任務保留的導彈不算可用攔截彈。
   驗證：BVRTests 新增 5 項（SAM 可運作才算有彈且只算本陣營、抽象節點庫存歸零即無彈、地圖節點讀取 SAM），共 **152 項通過**；驗收新增 K5（沒彈時世界航跡與空投都不鎖、保留彈不算、射出最後一枚後另一個待發射的鎖定立即釋放）；M8 與 K3 原本靠「沒有 SAM 也會鎖定」讓鎖定停住，改用一座有彈但長時間裝填中的 SAM（`BVRFixture.HoldInReload`）。完整套件 **30/30 通過、327 項檢查**。

### 15-5. P1 自動版（2026-10-19）

**已完成**

- **資料**：`StrikeMission`／`StrikeWave`／`StrikeSlot`（存在指揮台上，§4-1）；`MapComponent_StrikePlanner` 新增任務 id 配發、導引期 `StrikeGuidanceHold`、漣波佇列 `StrikeQueuedLaunch`（皆存檔），以及指揮台註冊表。保留索引照舊不存檔，指揮台在 `PostSpawnSetup` 依任務重建。
- **通道帳本**：`BVRChannelLedger.Strike`（排隊中 + 導引期內的打擊導彈）併入 `Occupied`；`StrikeCapacity`（只算 `supportsStrikeGuidance` 的火控雷達）與 `StrikeFree = min(Free, StrikeCapacity − Strike)`。導引期 `strikeGuidanceTicks` = 600，放在 `DMSE_BVRWorldTuning`。狀態警報的通道分項多了「打擊導引」。
- **`CompStrikeConsole`**（繼承 `CompBVRDevice` 的可運作判定）：世界選靶（游標旁顯示距離、「射程內 n／在架 m」、無據點提示；沒有任何導彈打得到時拒絕）→ 規劃視窗 → `Commit` 保留全部導彈、記下設定指紋、進入機器計算（`autoComputeTicks` 300，斷電時暫停）→ 就緒後逐波 `FireWave`。發射前逐枚重驗：彈藥不在、改裝過、超出射程即略過；平台斷電或冷卻中、通道不足時整波不發（Shift 部分發射則留在本波）。佇列到時 `ExecuteQueued` 以 `TryLaunchRound` 發射並掛上任務標記；排隊期間出現的暫時性問題讓該枚退回待發射。全部發射或略過後任務結束、釋放保留；目標消失或指揮台被移除時任務取消。
- **`Dialog_StrikePlan`**：規劃模式（暫停）左側候選依「彈體 · 導引 · 彈頭／酬載」分組，顯示數量、飛行時間、射程餘裕、單價，黃色為目前無法立即發射；不可用者摺疊附原因；右側波次（上限＝打擊通道容量，可新增／刪除波次、[−] 移除）；自動分波；確認鈕在草稿不合法時停用並顯示原因。目標為尚未生成地圖的非玩家據點時顯示 §15-4 決定 1 的提示。執行模式（不暫停）逐波顯示待發射／發射中／已發射與在飛／略過（滑鼠提示略過原因），發射鈕的提示說明不能發射的原因；任務結束時視窗自動關閉。
- **Gizmo**：規劃波次打擊／開啟打擊計畫／發射第 k 波（Shift 部分發射）／取消打擊任務（確認對話框）；開發者模式「Finish firing solutions」。檢視字串顯示打擊通道與任務狀態。
- **航跡標記**：`ScorerProjectile_WorldObject.strikeMissionId`／`strikeWave`（預設 -1）。
- **Def 與翻譯**：`DMSE_StrikeCommandCenter`（3×2、耗電 400、研究 `DMSE_IntegratedCircuits`；貼圖暫用原版通訊台），三語 Keyed `DMSE_Strike.xml`（73 鍵）與 DefInjected。

**與設計的差異（延到 P3）**：組別展開、指定特定平台的特定一枚；執行中調整波次；抵達時間軸與順序警示。P1 的漣波第一枚在按下發射的下一個 tick 升空，之後每 30 ticks 一枚。

**驗證**

- 建置 0 警告、0 錯誤；`check_bvr_world.py` 通過。
- BVRTests **167 項通過**（P1 新增 15 項：只算支援打擊的雷達、排隊與導引期共用通道、取消佇列釋放通道、陣營隔離、導引期準時結束、設定指紋不受部件順序影響但能發現改裝、自動分波、任務輔助屬性、任務 Scribe 輸出與預設值）。
- 遊戲內驗收新增 K6–K8，完整套件 **33 項中 32 項通過、367 項檢查**；唯一失敗的 W8（世界層空爆）單獨重跑通過，前兩次完整執行也都通過，屬間歇性失敗，已在 W8 加上診斷訊息，原因尚未查明。

| 測試 | 驗證內容 |
|---|---|
| K6 | 兩座相控陣給 4 條打擊通道；12 枚輕型導彈在射程內、巡飛彈（射程 12）標為超出射程、攔截彈不列入；5 枚的波次被拒；確認後 12 枚保留、單發 Gizmo 停用、計算中不能發射、第二個任務被拒；就緒後第 1 波 4 枚：按下即訂滿 4 條通道、第 2 波因通道不足被拒、4 聯裝平台每 30 ticks 一枚、冷卻從最後一枚起算、航跡帶任務與波次標記；導引期結束後通道才全部釋放、第 2 波可發；第 2 波發射途中取消：佇列停止、保留釋放、第 3 波導彈恢復可單發 |
| K7 | 第 1 波兩枚之間存檔並讀檔：任務、波次與狀態還原，存檔中的佇列在原定的 +30 tick 發射，存檔前的導引期照常到期，第 2 波的保留重建；之後發射第 2 波，任務結束並釋放保留 |
| K8 | 規劃視窗（經由視窗自動分波成 4 波×2）與執行視窗都能正常繪製、沒有錯誤；任務結束時執行視窗自動關閉。截圖已目視確認版面 |

**尚未驗證**：玩家實際以滑鼠操作規劃視窗（點選、Shift 加入、刪除波次）只有繪製與自動分波經過驗收；世界選靶游標標籤沒有自動化測試。

### 15-6. P2 人工版（2026-10-19）

**已完成**

- **`CompProperties_StrikeConsole`** 新增 `computeWorkBase`（1500）、`computeWorkPerRound`（400）、`crossLayerWorkFactor`（1.5）；`StrikeMission` 新增 `computeWorkDone`／`computeWorkTotal`（存檔）。
- **逐枚解出**：`StrikeComputation.TotalWork`／`SolvedCount`。人工版確認後進入 AwaitingComputation，工作量 =（基礎 + 每枚 × 枚數）× 跨圖層倍率；研究者每 tick 貢獻「研究速度」的工作量，基礎做完後依波次順序每多做 400 就解出一枚。發射判定改為逐波看 `solved`：本波都解好就能發射，計算可在任務執行中繼續；每解完一波、全部解完各發一則訊息。自動版不變（到時一次全解）。
- **工作**：`WorkGiver_ComputeFiringSolution`（工作類型「研究」，`priorityInType` 110，高於一般研究）、`JobDriver_ComputeFiringSolution`（比照原版研究：互動格、研究特效、進度條、智識經驗、座椅舒適；全部解完自行結束，中斷不丟進度）。右鍵指揮台可強制指派。
- **警報** `Alert_StrikeComputationPending`：有人工版任務待計算，但該地圖沒有能做研究工作的殖民者（未倒地、研究工作已啟用）。
- **介面**：規劃視窗顯示人工版的計算工作量，以及此地圖最快研究者約需的時間（沒有研究者時說明）；狀態顯示「等待彈道計算（%）」或「執行中；彈道已算 %」；不能發射時說明本波還差多少。開發者模式「Finish firing solutions」也適用人工版。
- **Def 與翻譯**：`DMSE_StrikePlanningTable`（3×2、不需電力、研究 `DMSE_Ballistics`、最多 3 波／12 枚；貼圖暫用原版簡易研究台），`DMSE_ComputeFiringSolution` 的 JobDef／WorkGiverDef，三語 Keyed（新增 9 鍵）與 DefInjected。

**延到 P3**：增量計算（加入新導彈只補算新枚數）要等「執行中調整波次」一起做。

**驗證**

- 建置 0 警告、0 錯誤。BVRTests **174 項通過**（P2 新增 7 項：總工作量、跨圖層倍率、基礎工作量前不解、逐枚解出、達總量全解並截斷、跨圖層縮放各門檻、每枚工作量為 0 時基礎做完即全解）。
- 遊戲內驗收新增 K9，完整套件 **34/34 通過、399 項檢查**（W8 本輪通過，診斷訊息顯示雙發同行的另一枚在會合 tick 被空爆擊落，距離 0.00 格）。

| 測試 | 驗證內容 |
|---|---|
| K9 | 規劃桌不需電力；確認後保留 4 枚、要求 3100 工作量、計算前不能發射；沒有人能研究時警報指向規劃桌，有人能研究後警報消失；WorkGiver 提供工作；研究者計算，中斷後進度保留；第 1 波先解出並在第 2 波仍在計算時發射（第 1 波約 tick 8817、第 2 波 10167 解出）；工作量達總量、研究者獲得智識經驗、全部解完後工作自行結束 |

**驗收過程的修正**：完整套件在約凌晨 3 點跑到 K9，殖民者在睡覺，單次指派的工作沒有接上；測試改為選清醒可用的研究者，必要時叫醒並在對方改做別的事時重新指派。另外 1.7 的 `SkillRecord.XpTotalEarned` 只計已完成的等級，經驗檢查改為加上 `xpSinceLastLevel`。

### 15-7. P3 回饋（2026-10-19）

**已完成**

- **抵達時間軸** `StrikeTimeline.Simulate`（純函式，可單元測試）：依打擊通道容量、導引期（600）、已占用通道的釋放時刻、平台冷卻與漣波間隔，排出每波最快的發射時刻與抵達區間。已發射且在飛的導彈取實際 ETA；已結束的不計。抵達比前面某波早的波次標記 `earlierThan`，並算出 `suggestedDelay`（延後多久才會排在那波之後，再加上 `strikeArrivalSpacingTicks` 60）。超過通道容量的波次不會發射，另外標記。
- **規劃視窗**：每波顯示預估抵達區間，順序顛倒時以黃字警示並提供建議延後量；底部時間軸條畫出各波的發射點與抵達區間。執行視窗用同一套計算顯示「若現在發射」的抵達時間。
- **組別展開、指定單枚**：候選組可展開到每座平台的每一枚（平台名稱、射程餘裕、目前能否發射），點一下加入選取中的波次；直接點組別時仍優先挑現在就能發射的那幾枚。
- **執行中調整波次**：發射佇列為空時（佇列以波次／槽位索引指向導彈），執行視窗的「調整波次」會開啟與規劃相同的編輯介面。已發射、已略過的槽位固定不動。只是換波次的導彈沿用原本的彈道解；新加入的導彈先保留，再進入計算；被移除的導彈釋放保留。套用或取消後都回到執行視窗。
- **增量計算**：`StrikeComputation` 改用「基礎工作剩餘量 + 累積額度」模型，依波次順序逐枚解出。人工版加彈只增加新枚數 × 400 的工作量；自動版重新倒數 300 ticks。已解好的波次照常可發射。
- **警報**：`Alert_StrikeWaveReady`（有可發射的波次；Mod 設定可關閉）。`Alerts_PlayerMissiles` 把同一任務同一波的我方打擊航跡合成一行：枚數、最早／最晚抵達、被偵測數、被接戰數。
- **戰果彙總** `WorldComponent_StrikeReports`：任務編號改由世界元件配發，從 1000 起全世界唯一，跨指揮台、跨地圖都不重複。依波次記錄發射、被攔截、落空（抵達無地圖的格子）、抵達、命中（`MissileIncoming.Impact`）、末端被毀（落地前被 CIWS 等摧毀）。任務結束後，所有導彈都有結果時送出彙總信件；只剩「已抵達但地圖上沒有結果」時，最後一筆事件後 3000 ticks 送出。

**驗證**

- BVRTests 新增 P3 檢查：§2-3 方案 A 第 3 波被標記為早於第 2 波，建議延後 209；方案 B 不警示；單一平台漣波 4 枚相隔 30、冷卻從最後一枚起算；超過容量的波次不發射；已占用通道延後發射；在飛導彈依 ETA 計入、已結束者忽略。另外補了增量計算的檢查。
- 遊戲內驗收新增 K10、K11：

| 測試 | 驗證內容 |
|---|---|
| K10 | 調整波次：發射佇列中不能調整；換波次的導彈保留彈道解；新加入的導彈被保留並需計算（自動版倒數；人工版只多 400 工作量）；移除的導彈釋放保留；已發射者不能移除 |
| K11 | 時間軸把較快的彈道波標記為早於輕型波；波次就緒警報；我方打擊航跡在玩家導彈警報中分組；任務編號全世界唯一；彙總正確計入發射、落空、一次命中、一次末端被毀，並送出結果信件 |

### 15-8. P4 選配（2026-10-19）

**已完成**

- **定時發射**（§14-12）：每波一個「定時發射」勾選框，規劃與執行視窗都有。勾選框前加了「定時」標籤，因為未勾選時的紅叉緊鄰刪除鈕，容易被看成「移除」；已全部發射的波次不顯示。`MissionTick` 每 60 ticks 檢查一次：下一波已勾選、現在能整波發射，而且依時間軸它會在前面各波最晚抵達之後（再加 60）才抵達時，就自動發射並發出訊息。調整波次時保留這個設定。
- **離場時間**：導彈升空後，要等離場（反向）skyfaller 飛出地圖才出現世界航跡。輕型、彈道、巡航三種都是 300 ticks（`ticksToDiscardInReverse`）。`CompMissileLauncher_WorldTargeting.DepartureTicks` 讀取這個值。候選導彈的飛行時間與時間軸都加上它；已發射但還在離場中的導彈，抵達時間從離開地圖的時刻起算（K12 的實測發現：舊估算把彈道波的抵達算早了約 300 ticks）。
- **目標移動重算**（§14-6 改為 (b)）：任務記下解算時的目標格 `solutionTile`。目標（例如移動中的世界物件）偏離 ≥ `strikeRecomputeMoveTiles`（5）格時，所有尚未發射的彈道解作廢：自動版重新倒數，人工版需研究者重算，就緒任務退回 AwaitingComputation，並發出訊息。移動未達門檻則不影響。
- **誘餌酬載與威脅評估**：`MissilePartDef.signatureFactor`（各部件相乘，即 `MissileConfig.SignatureFactor`）。新部件 `DMSE_Light_Payload_Decoy`（輕型彈體，截面 ×2，不造成額外傷害；鋼 10、工業零件 1）。`MissileWorldObject.TargetProps` 把落點 skyfaller 的 `distanceLevel` 乘上倍率（快取），所以誘餌的偵測距離加倍；隱身與速度不變。裝配分頁在倍率不為 1 時顯示雷達截面。`BVRNode.ThreatAssessment`：抽象防空依 `WorldObjectComp_AirDefense.threatAssessment`（預設 false，目前沒有 Def 開啟）；地圖節點則是太空時代以上的非玩家陣營。航跡掃描順序改為 `InEngagementOrder(tracks, priority)`：飛向具威脅評估節點的航跡，截面大者先接戰，同截面依 ETA；其他節點仍是 ETA 先到先打。
- **NPC 陣地齊射**：`CompProperties_MissileBase.missilesPerSalvo`。地圖未生成時，每次開火對同一個據點同時發射 2–3 枚；間隔改為 2–4 天，平均每日枚數約與舊設定相當。地圖已生成時（`fireInWaves`，預設 true），`MapComponent_MissileBase` 等上一波全部結束（通道全空）才整波發射，不再有空通道就補射。

**驗證**

- 建置 0 警告、0 錯誤。BVRTests **195 項通過**（P3／P4 共新增 21 項。P4：截面倍率相乘、誘餌只放大雷達截面、偵測距離加倍、威脅評估與 ETA 兩種接戰順序）。
- 遊戲內驗收新增 K12–K15：

| 測試 | 驗證內容 |
|---|---|
| K12 | 勾選定時發射的彈道波：等過它最早能發射的時刻才自動發射，抵達時間在輕型波之後再加間隔（以世界航跡出現後的實際 ETA 比對） |
| K13 | 目標移動 2 格不影響彈道解；移動 6–7 格後解作廢、本波不能發射，自動版重新計算後恢復 |
| K14 | 誘餌酬載使雷達截面加倍；只有 1 條通道時，具威脅評估的陣地先鎖定誘餌，ETA 先到先打的陣地先鎖定一般導彈 |
| K15 | 地圖未生成的 NPC 導彈陣地對同一個殖民地齊射 3 枚 |

- 打擊相關子集（M0、W6、K3–K15）**15/15 通過**；完整套件 **40/40 通過、458 項檢查**；Player.log 沒有錯誤，只有一則與本 Mod 無關的 Vulkan 載入訊息。之後補上兩項修正，並重跑相關測試：一是離場中導彈的抵達時刻（M0、K8、K10–K13 **6/6 通過**；K12 實測彈道波比輕型波晚 103 ticks 抵達）；二是定時發射勾選框加上標籤（M0、K8、K12 **3/3 通過**，截圖已目視確認）。

**尚未驗證**：地圖已生成的 NPC 陣地「整波發射」規則（沒有遊戲內測試）；玩家以滑鼠實際操作規劃視窗（點選、展開組別、勾選定時發射）只驗收了繪製與 API；世界選靶游標標籤沒有自動化測試。

### 15-9. 介面補充（2026-10-19）

- **通道方格**：規劃／調整視窗的波次標題，把「n/容量」改畫成仿原版機械師頻寬 Gizmo 的方格：黃色＝本波占用的打擊導引通道，灰色＝空閒，紅色＝超出容量，後面附小字數字與說明提示。已全部發射的波次不畫。
- **任務名稱**：`StrikeMission.name`（存檔；實作 `IRenameable`）。規劃新任務時以 RulePackDef `DMSE_NamerStrikeMission` 隨機命名（英文「Operation [形容詞] [名詞]」，中文「「鐵雨」行動」式，避開進行中任務已用的名稱）。視窗標題旁的重新命名鈕開啟 `Dialog_RenameStrikeMission`（原版重新命名視窗加「隨機生成」）。訊息、視窗標題、檢視字串、警報與戰果信都改用任務名稱；沒有名稱的任務仍稱「打擊任務 #id」。戰果帳記下名稱（`StrikeReport.missionLabel`），改名時同步更新，所以在飛導彈的警報與之後的戰果信會用新名稱。
- 驗證：BVRTests 195 項通過（任務 Scribe 檢查含名稱）；K8 加入重新命名視窗的繪製與截圖，K11 加入隨機名稱、避開使用中名稱、改名同步戰果帳與警報、戰果信標題用新名稱；M0、K6、K7、K8、K11 **5/5 通過**，截圖已目視確認。
