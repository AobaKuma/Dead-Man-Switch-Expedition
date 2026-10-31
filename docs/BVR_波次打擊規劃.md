# 波次打擊（飽和打擊介面）

狀態：P0–P4 全部實作（2026-10-19）。之後的變更：通道成本改依導引方式、每任務上限 20 枚／5 波／每波 8 枚（見[科技樹](導彈與雷達科技重整_執行計畫.md) §4）；離場時間改由 Projectile 模擬（見 [Skyfaller→Projectile](Missile_Skyfaller轉Projectile_計畫.md)）。原始完整計畫（含介面草圖、逐期驗證紀錄）在 `docs/_archive/`。

## 1. 玩法

一座指揮台 → 世界選靶 → 回本地地圖開規劃視窗 → 把在架導彈分配到多波 → 確認（保留導彈）→ 計算彈道解 → 逐波發射。

**擋槍**：防禦方每次攔截要付出一條火控通道（鎖定到會合）、一枚 SAM、一次冷卻。先抵達的低價值導彈耗盡通道與庫存，後到的主力只剩 CIWS。

**發射順序 ≠ 抵達順序**：世界飛行 150 ticks/格 ÷ `WorldSpeedFactor`（巡飛 0.55、輕型 0.75、巡航 1、彈道 1.5），所以視窗要顯示每波的抵達時間並警示順序顛倒。實測 K4：15 格外的抽象防空陣地（2 通道、8 庫存）用 8 次嘗試耗盡庫存在 8 枚誘餌上，主力未被攔截。

**已知取捨**（維持現狀並提示）：漏網誘餌會提早生成據點地圖，地圖上實體彈架照常滿彈，後續主力面對滿彈防空。

## 2. 架構與檔案

```
CompStrikeConsole Gizmo「規劃波次打擊」（指揮台 Active、有運作中的玩家火控雷達、無進行中任務）
  ▼ 世界選靶（游標顯示距離、「射程內 n／在架 m」；n = 0 拒絕）
  ▼ Dialog_StrikePlan 規劃模式（forcePause）
  ▼ Commit：保留導彈、記設定指紋
     ├ 自動版：autoComputeTicks 300（需通電）→ Ready
     └ 人工版：AwaitingComputation → WorkGiver_ComputeFiringSolution（研究）→ 逐枚解出
  ▼ 執行模式（不暫停）：FireWave → 逐枚重驗 → 預約通道 → 漣波佇列 → TryLaunchRound
  ▼ 全部發射／略過 → Completed；目標消失或指揮台移除 → Cancelled（釋放保留）
```

`.source/DMSE/Scorer/Strike/`：`CompStrikeConsole`、`StrikeMission`（Mission／Wave／Slot）、`StrikeRoundUtility`、`StrikeReservation`、`MapComponent_StrikePlanner`、`Dialog_StrikePlan`、`StrikeComputation`、`StrikeTimeline`、`WorkGiver_ComputeFiringSolution`、`JobDriver_ComputeFiringSolution`、`Alerts_Strike`、`WorldComponent_StrikeReports`、`MissileRoleExtension`。

Def：`DMSE_StrikeCommandCenter`（自動版，3×2、耗電 400、`DMSE_IntegratedCircuits`）、`DMSE_StrikePlanningTable`（人工版，3×2、不需電、`DMSE_LoiteringMunitions`）、`DMSE_ComputeFiringSolution` Job／WorkGiver、RulePack `DMSE_NamerStrikeMission`。Tuning（`DMSE_BVRWorldTuning`）：`strikeGuidanceTicks` 600、`strikeArrivalSpacingTicks` 60、`strikeRecomputeMoveTiles` 5。

## 3. 資料模型

- `StrikeMission`（存在指揮台 `Scribe_Deep`，一座指揮台一個任務）：`id`（`WorldComponent_StrikeReports` 從 1000 起全世界唯一）、`name`（`IRenameable`）、`targetTile`／`targetParent`／`targetedSite`、`state`（Planning／AwaitingComputation／Ready／Executing／Completed／Cancelled）、`waves`、`computeWorkDone/Total`、`solutionTile`。
- `StrikeWave`：`slots`、`launchedTick`、定時發射旗標。`StrikeSlot`：`launcher`、`round`（彈道井／Scorer 為平台本身）、`configHash`、`solved`、`result`、`skipReason`、`track`。
- `MapComponent_StrikePlanner`：平台與指揮台註冊表、保留索引（`Dictionary<Thing,int>`，**不存檔**，讀檔由指揮台重建）、`StrikeGuidanceHold`、漣波佇列 `StrikeQueuedLaunch`（存檔）。
- 航跡標記 `ScorerProjectile_WorldObject.strikeMissionId`／`strikeWave`（預設 -1）。

## 4. 候選導彈

- 平台 API（`CompMissileLauncher_WorldTargeting`）：`StrikeRounds`、`NextRound`（第一枚未保留）、`OwnsRound`（參照比較——`Thing.Equals` 比 ID 與 def）、`BuildFiringConfig(Thing)`、`TryLaunchRound(target, round, salvoContinuation, out failReason)`。原 `Launch` 已移除；單發 Gizmo 也走 `TryLaunchRound`。
- `MissileRoleExtension`：`Strike`（預設）／`Interceptor`／`Dual`。攔截彈不列入，Rail 也不再用平台 `missileBody` 補設定把攔截彈射出；`DMSE_Missile_Grison` 為 Dual。只管世界打擊，SAM 取彈由儲存篩選決定。
- 篩選：可用／可用但暫不可射（黃，斷電或冷卻）／不可用（摺疊灰顯附原因：超射程、跨圖層無導引）／不列入（攔截彈、被其他任務保留）。待裝配的照常列出並警示。
- 分組：彈體 → 導引 → 戰鬥部／酬載；預設依價值由低到高（便宜的先當誘餌）；可展開到單枚。

## 5. 通道、發射與計算

- **通道**：`BVRChannelLedger.Strike`（排隊＋導引期內）併入 `Occupied`，與防空、砲擊共用；`StrikeCapacity` 只算 `supportsStrikeGuidance` 的火控雷達；`StrikeFree = min(Free, StrikeCapacity − Strike)`。按下發射即預約，升空後占用 600 ticks 導引期。
- **單波**：規劃時超出容量標紅不能確認；執行時空閒不足則停用並顯示「可用 a／需 b，t 後釋放」；Shift = 部分發射。
- **重驗**（逐枚）：彈仍在、`configHash` 相同、平台 Active 且不冷卻（漣波續射例外）、當下目標 tile 仍在射程、有通道。失敗者 Skipped 並彙總訊息。
- **漣波**：同平台間隔 `salvoIntervalTicks` 30，冷卻從最後一枚起算。
- **人工計算**：工作量 =（1500 + 400 × 枚數）× 跨圖層 1.5；研究者每 tick 貢獻研究速度；基礎做完後依波次逐枚解出，本波都解好即可發射。`priorityInType` 110。`Alert_StrikeComputationPending`：無人能研究。
- **增量**：Ready 後加彈只補算新枚數；換波次的導彈沿用解。
- **保留掛點**：單發 Gizmo、SAM（地圖與世界都取第一枚未保留；全保留時 `HasAmmo = false`）、裝配工作與 `ITab_MissileAssembly`（停用編輯）。平台被拆或摧毀不阻擋，該枚失效。被保留的導彈不算可用攔截彈。
- **目標移動 ≥5 格**：未發射的解作廢，重算。

## 6. 回饋

- `StrikeTimeline.Simulate`（純函式）：依通道容量、導引期、已占用釋放時刻、冷卻、漣波、離場時間排出每波發射與抵達區間；顛倒時標 `earlierThan` 並給 `suggestedDelay`。執行模式顯示「若現在發射」。
- **定時發射**：每波勾選；`MissionTick` 每 60 ticks 檢查，能整波發射且會晚於前面各波最晚抵達＋60 時自動發射。
- 通道方格（仿機械師頻寬）：黃＝本波、灰＝空閒、紅＝超出。
- `Alert_StrikeWaveReady`（可關）；`Alerts_PlayerMissiles` 依任務／波次合併；`WorldComponent_StrikeReports` 記錄發射、被攔截、落空、抵達、命中、末端被毀，結束後送彙總信（只剩無結果的抵達時，最後事件後 3000 ticks 送出）。

## 7. 防禦端與 NPC

- 世界防空掃描順序 `InEngagementOrder`：ETA 先到先打（穩定排序）；飛向具威脅評估節點的航跡改以雷達截面大者優先。已鎖定者不被搶。
- **沒彈不鎖、打光即放**：`HasInterceptorAmmo`（任一 Active SAM 有未保留彈，冷卻中也算；抽象節點庫存 > 0）。否則玩家 SAM 打光時來襲航跡會占滿通道，波次發不出去。
- **誘餌**：`MissilePartDef.signatureFactor` 相乘；`DMSE_Light_Payload_Decoy`（截面 ×2）。`MissileWorldObject.TargetProps` 的 `distanceLevel` 乘上倍率。威脅評估：抽象防空 `threatAssessment`（預設 false）；地圖節點為太空時代以上的非玩家陣營。
- **NPC 陣地**：無地圖時每 2–4 天齊射 2–3 枚（`missilesPerSalvo`）；有地圖時 `fireInWaves` 等上一波通道全空才整波發射（未實機驗證）。

## 8. 驗證

- BVRTests：角色與保留、Rail 彈藥列舉、接戰順序、通道帳、導引期、設定指紋、自動分波、任務 Scribe、計算工作量與逐枚解出、時間軸（方案 A 警示建議延後 209、方案 B 不警示）、截面倍率與威脅評估。
- 實機（`Tests_Strike.cs`）：K1 角色／保留／單發；K2 保留擋裝配；K3 ETA 先到先打；K4 擋槍實測；K5 沒彈不鎖；K6 通道與漣波；K7 存讀檔；K8 視窗繪製與改名；K9 人工計算；K10 調整波次；K11 時間軸與戰果；K12 定時發射；K13 目標移動重算；K14 誘餌與威脅評估；K15 NPC 齊射。
- 測試寫法：K9 要選清醒的研究者（夜間會睡），必要時叫醒並重指派；1.7 的 `SkillRecord.XpTotalEarned` 只計已完成等級，要加 `xpSinceLastLevel`。M8／K3 用 `BVRFixture.HoldInReload` 讓 SAM 有彈但長時間裝填。K 系列以 `BVRFixture.PinLegacyRadarValues` 釘住舊雷達通道數。
- 未驗證：玩家實際滑鼠操作規劃視窗（只驗收繪製與 API）、世界選靶游標標籤、有地圖 NPC 陣地的整波規則。
