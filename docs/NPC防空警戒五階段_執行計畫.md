# NPC 防空警戒五階段

適用：DMSE 1.7。首版 2026-10-23 實作；2026-10-31 第五階段改為主動攔截。數值為首輪測試起點，未經平衡驗證。原始完整計畫在 `docs/_archive/`。

## 1. 核心概念

NPC 只依「實際獲知的導彈活動」升級防空。陣營共享情報、據點各自升降級；遠方一發導彈不會讓整個陣營全球封鎖。

三件事分開看：

- **警戒階段**：可觀測／可攔截哪些目標、是否封鎖。
- **裝備能力**：雷達、射程、通道、彈藥。升階不生出裝備，也不提高命中。
- **當前可用性**：斷電、壓制、無彈。介面可顯示「第五階段／暫失執行能力」。

玩家節點不套用警戒（維持只攔截敵對）；NPC 節點只在節點陣營＝據點陣營時套用。

## 2. 程式位置

目錄 `.source/DMSE/SkyFallerTurret/BVR/World/Alert/`：

| 模組 | 職責 |
|---|---|
| `AirDefenseAlertTuningDef` | 參數；Def `DMSE_AirDefenseAlertTuning`（`1.7/Defs/DMSE_BVRWorld.xml`）。`enabled=false` 回到舊規則（全距偵測、只打敵對、不封鎖） |
| `AirDefenseAlertPolicy` | 純規則：友敵、可觀測、可接戰、升降級、補差額、衰減 |
| `AirDefenseObservation` | 每陣營對每航跡的情報，存於 `BVRTrackState.observations`；`detectedByFaction` 只給玩家介面 |
| `WorldComponent_FactionAirDefenseAlert` | 以據點 ID＋陣營存壓力、階段、去重事件、公告；事件以航跡為鍵，多錨點到達同據點只計一次 |
| `AirspaceRestrictionUtility`、`Patch_AirspaceRestriction` | 封鎖查詢；取代舊的 5 格 `Patch_MissileBaseShuttleBlock` |
| `BlockadeInterception.cs` | 第五階段主動攔截（§5） |
| `AirDefenseAlertUI`、`BVRWorldDrawer` | 檢視資訊；探測（實線）／攔截（點線）／封鎖（粗虛線＋紅色 tile 填色）圈；「DMSE Air Alert」開發指令 |

掃描流程：建立各陣營觀測 → 事件去重入帳 → 更新階段 → 依政策篩選接戰節點 → 沿用既有鎖定／求解／結算。

## 3. 五階段

| 階段 | 探測 | 導彈交戰 | 運輸 |
|---|---|---|---|
| 1 無探測 | 無本地預警；可收友方警報與受襲通報 | 不參與世界層攔截 | 不限 |
| 2 預警觀測 | 雷達半徑 ×0.4，情報傳同陣營 | 只觀測 | 不限 |
| 3 被動攔截 | 正常 | 只打已確認指向己方據點的導彈 | 不限 |
| 4 主動攔截 | 正常 | 打範圍內已探測的所有非盟友導彈（含中立過境） | 不限 |
| 5 防空封鎖 | 同 4 | 同 4 | 攔截非盟友空中載具（§5） |

約束：

- 同陣營與盟友免攔截／封鎖；「非盟友」含中立，不能只用 `HostileTo`。無陣營目標不開火，除非確認攻擊己方。
- 第三階段的「己方」是防守陣營的據點，不含盟友領土；改靶後重評。
- 地圖內自衛與 CIWS 末端不受第一／二階段限制。
- 中立陣營攔截玩家導彈只發一次說明信，不改外交；攔截彈結算前重查友軍身分，結盟則安全解除、不退款。
- 基準：無設備據點第一階段、有雷達者第二、專業軍事／導彈基地第三。

## 4. 情報與壓力

情報來源：本地觀測（進入有效探測且過隱身判定）、同陣營警報（20 世界格、同圖層；被瞄準據點可收定向通報）、受襲通報（只記實際可知攻擊者）。無探測且未遇襲時 NPC 不知道秘密發射——第一擊可突襲，這是刻意的。

壓力：本地 `L`、區域 `R`（0–100），有效 `T = max(L, 0.5R)`。區域壓力最多推到第四階段，封鎖必須有本地威脅。情報有效期 600 ticks。

| 事件（每航跡一次） | L | R | 備註 |
|---|---:|---:|---|
| 觀測到非盟友導彈過境 | +4 | +2 | 單齊射最多 +12／+6 |
| 確認瞄準己方據點 | +20 | +10 | |
| 命中己方設施／壓制雷達（抵達目標據點即計） | +25 | +12 | |
| 24 小時內新的獨立攻擊波 | +15 | +8 | 每波一次 |

同航跡升格取較高權重補差額，不疊加。齊射分組：同來源、同目標區、相隔 ≤600 ticks。

| 轉換 | 升入 | 降出 |
|---|---|---|
| 1→2 | 收到可信警報且有雷達 | 只回到基準 |
| 2→3 | `T ≥ 20` 或確認遭攻擊 | `T < 10` 且無有效來襲 |
| 3→4 | `T ≥ 50` | `T < 35` |
| 4→5 | `T ≥ 80` 且 24 小時內 ≥2 個獨立攻擊波 | `T < 60` 或攻擊紀錄過期 |

衰減每遊戲小時 2 點（惰性計算）。每階段至少維持 6 小時（只限降級；升級立即），每次降一階、不低於基準。雷達開機延遲 600 ticks。第五階段先公告 2 小時（期間維持第四，壓力 <60 即取消），已生效的封鎖補彈後不再重給公告期。

## 5. 第五階段封鎖（2026-10-31 起為主動攔截）

有效封鎖圈 = min(配置攔截半徑, 實際追蹤覆蓋)，需有火控與至少一枚攔截彈；同陣營多圈取聯集，只看同圖層航段，以線段相交判定過境。

- `WorldComponent_BlockadeInterception` 每 60 ticks 掃描圈內的 `TravellingTransporters`、`Gravship`、飛行中的 `WorldObject_TransferShip`，以 `WorldObject_BlockadeInterceptor`（`DMSE_BlockadeInterceptor`）射擊。消耗據點庫存、火控與通道；每據點對每載具最多一發在途。
- 命中效果：空投艙／穿梭機每發擊落一具，乘員交給 `InterceptedPawnUtility`（玩家小人失蹤）；重力船最多 4 棟建築扣 25–50% HP（不低於 1）；轉移艦在其地圖上引發 3 次爆炸。
- 起飛前改為警告：`InterceptRiskUtility.ConfirmOrDefer` 掛在 `CompLaunchable.TryLaunch`、`CaravanShuttleUtility.LaunchShuttle`、重力船 `CheckConfirmSettle`；轉移艦在 `TransferReadiness` 顯示 Warning。
- NPC 空投：`PawnsArrivalModeWorker.CanUseWith` 在落點位於不允許該陣營的封鎖圈時停用空投類入場（`Patch_Airspace_ArrivalMode`，未改）。
- 不控制：任務強制入場的空投、任務穿梭機（`CompShuttle`）、第三方載具、`DebugSettings.skipGravshipTileSelection`。

## 6. 存檔

以穩定據點 ID＋陣營 ID 為鍵，不掛在 `BVRNode` 快取。地圖生成／卸載不重置警戒或補滿彈藥；據點易主清除舊命令；舊存檔依基準初始化，不在載入瞬間封鎖。

## 7. 驗證與未完成

- 自動：`DMSE.BVRTests` 的 `AirDefenseAlert()`（交戰矩陣、升降級、補差額、衰減、航段距離）。
- 實機：驗收 A1／A2（`.source/DMSE.BVRAcceptance/README.md`）。
- 未做：效能基準（100／300 據點 × 50／200 航跡，世界掃描 p95 增幅 ≤20%）；穿梭機、重力船、轉移艦穿越封鎖的實機測試。
- 範圍外：新雷達建築、NPC 自動擴建、防空外交、跨陣營聯合火控。
