# BVR 世界層偵測與攔截

狀態：P0–P5 已實作（2026-10-18～20）；遊戲內驗收全過，部分情境未測（§8）。原始完整規劃（含現況盤點、試算、逐項驗收表）在 `docs/_archive/`。

## 1. 核心決策

1. **一條時間線**：攔截窗口 = 真實世界飛行時間，沒有人造窗口；為此導彈世界速度由 Tuning 控制（§3）。
2. **中過程在世界層，末端留在地圖**：世界層偵測、鎖定、SAM 攔截；航跡抵達後地圖內只跑 CIWS 與落地。地圖內中過程只剩空投與後備路徑。
3. 裝置仍是地圖建築；節點數值每次從 Comp 讀。SAM 開火照舊從地圖彈架取實體彈、播放純視覺升空，再由世界物件接手。
4. 航跡狀態存在航跡世界物件身上（`BVRTrackState`）；`WorldComponent` 只做快取與調度，讀檔時重建。
5. **火力通道一本帳**（`BVRChannelLedger`，每地圖每陣營）：世界航跡、地圖內空投、火砲引導共用。
6. 攻守對稱：玩家導彈也會被 NPC 世界層攔截；沒有地圖的 NPC 據點用抽象防空。

v1 決策：全部導彈 150 ticks/格；覆蓋圈預設選取時畫、設定可常駐；世界航跡抵達後不再補跑地圖 SAM；只有導彈陣地有專屬抽象防空（一般據點另有合成防空，§6）。

## 2. 流程與檔案

```
發射 → 航跡世界物件（ETA 可精確計算）
  ▼ WorldComponent_BVRNetwork 每 15 ticks 掃描
偵測：與敵對節點的地表等效格距 ≤ 有效半徑 → detectedByFaction、首次偵測信件、紅色圖示、警報
  ▼
鎖定：有空閒通道、有攔截彈、ETA ≤ maxRangeTicks → lockUntil = now + LockOnTicksFor
  ▼
發射：BVRInterceptSolver 求會合 t*（t* < ETA − 60）→ 地圖消耗實體彈、純視覺升空；WorldObject_Interceptor 飛向會合點
  ▼
會合（依遊戲 tick 由 WorldComponent 結算）：擲 hitChance
  ├ 命中 → 航跡 Destroy、訊息、擴散圈；空爆對 1 格內同目的地敵對航跡額外擲骰
  └ 落空 → 釋放通道，ETA 足夠則重鎖
  ▼
漏網航跡抵達 → MissileIncoming（worldPhaseDone）→ RegisterTerminal → CIWS → 落地（無 CIWS 直接落地）
```

目錄 `.source/DMSE/SkyFallerTurret/BVR/World/`：`IBVRTrack`、`BVRTrackState`、`WorldComponent_BVRNetwork`、`BVRNode`、`BVRInterceptSolver`、`WorldObject_Interceptor`、`BVRChannelLedger`、`BVRWorldDrawer`、`Alert_WorldIncoming`、`WorldObjectComp_AirDefense`、`WorldComponent_SettlementDefense`、`WorldObject_DropPodTrack`、`MissileWorldMotion`、`BVRWorldTuningDef`。Def：`1.7/Defs/DMSE_BVRWorld.xml`（`DMSE_BVRWorldTuning`、`DMSE_Interceptor`）。

## 3. 世界運動

```
ω_base = AverageTileSize / Radius / ticksPerTileAtSpeed1      （150）
ω      = ω_base × WorldSpeedFactor(config)
ETA    = (1 − traveledPct) × 目前航段角距離 / ω
```

- 原版運輸艙約 20 ticks/格，太快；150 ticks/格讓 20 格巡航彈約 3000 ticks。
- 世界物件非世界畫面時每 15 ticks 才 Tick → 位置與 ETA 一律以遊戲 tick 外推；偵測與會合由 `WorldComponent` 計時。
- 距離一律用 `MissileWorldUtility.SurfaceTilesBetween`／`RangeDistance`（跨圖層正確），不用原版 `ApproxDistanceInTiles(PlanetTile, PlanetTile)`。
- 航跡基底 `MissileWorldObject`：分段航線（`legStart`）、終端更新（彈體 `terminalUpdates`）、目標不在即落空。火砲有自己的速度公式，不受影響。

## 4. 偵測與攔截

- **有效半徑**：`worldDetectionRange × distanceLevel × 隱身係數 × 高度係數`。隱身係數：反隱身不足 0.4；足夠時 `min(1 + 0.25×差值, 1.5)`。多雷達取**最大值**（不相加）。早期預警 8 格、相控陣 12 格。雷達全滅清除偵測；已發射彈照常結算。
- **鎖定**：需偵測、空閒通道、`BVRNode.HasInterceptorAmmo`（冷卻中也算；無彈時釋放鎖定）。分配順序：ETA 先到先打；飛向有威脅評估節點的航跡改以雷達截面大者優先（見[波次打擊](BVR_波次打擊規劃.md)）。鎖定時間用 `BVRTargetProps.speed`，與世界速度分開。
- **會合**：點防禦封閉解 `t* = d / (ω_t + ω_i)`，`ω_i = ω_base × 攔截彈 WorldSpeedFactor × interceptorSpeedMultiplier(3)`。區域防禦用 `TrySolveGeneral`（對 `g(t)=角度(節點, 航跡(t)) − ω_i·t` 二分）。
- **命中率** = `ComputeHitChance(track, ETA_at_launch)` + 戰鬥部 `InterceptBonus`。
- **終端更新**：目的地改變視為新航段——原鎖定取消、通道釋放、新目的地節點接手；在飛攔截彈到舊會合點判定「目標機動脫離」直接落空（航段版本檢查）。偵測保留。
- **末端交接**：`worldPhaseDone` → `RegisterTerminal`，窗口 `TerminalWindowTicks`；末端波次不與完整舊波次合併，CIWS 斷電也不退回 SAM。CIWS 多波次統一分配是前置條件（已完成）。

## 5. UI

- 覆蓋圈：每「據點 × 陣營」快取球面小圓取樣點（`GenDraw.DrawWorldRadiusRing` 只快取一個圓心，不能用）；沿法線外推 0.08、用圖層 `WorldLineMaterial`（太空層 HighVis）、寬 `5 × lineWidthFactor`——否則被地形網格蓋住。常駐開關由玩家據點 Gizmo 與 Mod 設定共用。
- 攔截彈虛線航路、會合擴散圈（世界組件繪製，非持久物件）。
- 航跡 inspect：類型、目的地、ETA、狀態、嘗試次數；被玩家偵測的敵方航跡紅色。搜索雷達 inspect 顯示對四類彈的有效半徑。
- 首次偵測 ThreatBig 信件（取代抵達信件）；`Alert_WorldIncoming`、`Alerts_PlayerMissiles`（世界航跡＋地圖波次，左右鍵跳轉）、`Alerts_BVRStatus`（帳本分項）。

## 6. 抽象防空與 P4 擴充

- **`WorldObjectComp_AirDefense`**（`DMSE_MissileBase`）：2 通道、庫存 8、每天補 4、冷卻 240、相控陣火控＋輕型連續桿攔截彈。只在 `!HasMap` 時運作；地圖生成後交給實體建築，庫存不與彈架同步。
- **數據鏈**：`DetectorFor`——任一同陣營敵對節點看到即算偵測，同陣營節點都可接戰。
- **區域防禦**：`BVRNode.CanEngage` = 目的地是本節點或飛越覆蓋；`NodeFor` 目的地節點優先，其餘取最近。`areaDefenseEnabled`（預設 true）。
- **一般據點合成防空** `WorldComponent_SettlementDefense`：Industrial 6 格／1 通道／2 發；Spacer 8 格／2 通道／4 發；持久化。
- **空投虛擬航跡** `WorldObject_DropPodTrack`：由覆蓋圈邊緣飛入，被攔截時移除地圖波次目標；`podsUseWorldTracks` 預設關。
- **未偵測航跡隱藏**：`Patch_HiddenTracks`（`ExpandableWorldObjectsUtility.HiddenByRules`）＋`MissileWorldObject.VisibleToPlayer` 等。
- **反輻射削弱抽象節點**：`TryStrikeAbstractNode`——命中無地圖防空節點時打掉 2 發庫存、壓制雷達一天，不生成地圖。
- **P5**：`BVRTargetProps.altitude`＋`AltitudeFactor`；`GuidanceType_AntiShip`（+2 終端更新、移動目標每 20 ticks 檢查）；移動目標登錄每 60 ticks 取樣；箔條 `jamStrength`／`chaffCharges`（世界斷鎖、地圖命中折減）。電子戰後續見 [ECM 計畫](ECM與ECCM_實作計畫.md)。
- 飛行中的 `Gravship` 無地圖，不列為打擊目標。NPC 防空警戒與封鎖見 [NPC 防空警戒](NPC防空警戒五階段_執行計畫.md)。

## 7. 存檔

- `BVRTrackState`：`detectedByFaction`、`firstDetectedTick`、`lockUntil`、`lockNodeTile`、`interceptor`、`interceptAttempts`、`lastKnownDestination`、通知旗標。
- `WorldObject_Interceptor`：目標參照、會合 tick、航段版本、戰鬥部；保存時不寫入已摧毀航跡／據點的懸空參照。
- 舊存檔：無狀態 = 未偵測；`MissileIncoming.worldPhaseDone` 預設 false 走舊流程；舊地圖波次照跑完。

## 8. 驗證

- BVRTests：有效半徑、反隱身上限、世界速度、20 組球面會合、命中率／鎖定、陣營通道隔離、過期通道釋放、末端波次分類、NPC 庫存／冷卻／補彈／交接、Scribe 輸出與預設。`check_bvr_world.py`：Def 引用、雷達半徑、貼圖、三語 Keyed。
- 實機（`Tests_World.cs`、`Tests_WorldEdge.cs`）：W1 偵測與 ETA（偏差 7–10 ticks；W11 各倍速偏差 0）、W2 鎖定／會合、W3 落空重鎖、W4 末端 CIWS、W5 雷達斷電、W6 抽象防空與地圖交接、W7 電視選點、W8 空爆、W9 移動目標改飛、W10 末端 CIWS 斷電、W11 倍速、L1／L2 存讀檔、M8 通道共用、D1 區域防禦。
- 測試寫法：其他陣營可能經數據鏈偵測，斷言用 `detectedByFaction != Faction.OfPlayer`；W8 測空爆時關閉區域防禦。K7／L1／L2 要寫 `Saves`，受限沙箱下會 `UnauthorizedAccessException`。
- 未測：軌道圖層、玩家移動船、多據點覆蓋圈；陣營通道隔離只有單元測試；舊版存檔。
- 驗收修正：反輻射／電磁訊號導引打剛生成的地圖時建築還沒開機 → `GuidanceType.PowerStarting`、`CompBVRDevice.ActiveForSeeker` 把正要開機的算運作中；雷射標定器 `weaponTags Inherit="False"`（避免劫掠者攜帶）。
- 開發工具：「Enemy missile strike (incoming, world)」「Spawn NPC Missile Base (near player)」「Missile Base: Fire at player NOW」。
