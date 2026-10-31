# 轉移飛行（Celestial Transfer）

狀態：P0–P8 實作（2026-10-12）；P3 之後未經完整遊戲內驗收，數值待實測。程式在 `.source/DMSE/CelestialTransfer/`（結構樁在 `Piles/`）。原始完整藍圖（含程式骨架、逐步驟對照原版、貼圖規格）在 `docs/_archive/`。

## 1. 需求與核心決策

| # | 需求 | 實作 |
|---|---|---|
| 1 | 不依附逆重船，獨立控制台 | `DMSE_TransferConsole`／`CompTransferConsole`／`ITab_TransferShip`；不碰 `Building_GravEngine`／`CompPilotConsole` |
| 2 | 3×3 結構樁界定範圍、需連結成一體 | `DMSE_StructuralPile`／`CompStructuralPile`；連結由玩家下令、殖民者施工；連通分量 = 一艘船 |
| 3 | 燃料 ∝ 樁數；地板下鋼樑 | `TransferFlightMath`；`MapDrawLayer_StructuralBeams` |
| 4 | 只在起終點點火；飛行中殖民地照常（含穿梭機） | 分離式船體地圖＋`WorldObject_TransferShip`；倒數 → 分離 → 離軌點火 → 滑行 → 入軌點火 → 抵達 |
| 5 | 前期化學引擎 | `DMSE_ChemicalTransferEngine` |
| 6 | 熱核推進器改低燃耗大推力 | `DMSE_NuclearThruster` 改掛 `CompTransferEngine`，需核融合爐支援 |

**分離式船體地圖**（取代舊的整圖搬遷）：起飛瞬間新建同尺寸太空地圖，船體範圍**以相同座標**搬過去，新地圖的父物件在世界上移動。同座標 → Comp 裡的 `IntVec3`、bill、床位、儲存設定都不用改；在一個 `LongEvent` 內同步完成 → 不需 `IExposable` 快照；抵達即停在目的地（或選位降落）。原版 Gravship 管線飛行中沒有地圖，且建構子強依賴 `Building_GravEngine`，不採用。

## 2. 結構樁與網路

| 名詞 | 定義 |
|---|---|
| 連結 | 玩家以樁 Gizmo 下達、殖民者施工完成的雙向連結；施工中的不算 |
| 網路 | 以已完工連結為邊的連通分量；未連結的樁即使覆蓋重疊也不是同一艘船 |
| Footprint | 網路覆蓋聯集 ∩ 非虛空格（有 foundation 或地形非 `Space`） |
| 船載物 | 完整位於 footprint 內的建築；Position 在內的 pawn／物品／汙物 |

- **樁**：3×3、Heavy 地形（可蓋在小行星岩地與軌道平台）、研究 `DMSE_OrbitalDynamics`。現行 XML：方形覆蓋半徑 11（23×23）、`linkRange` 23（2r+1，連結的兩樁覆蓋必相接）、`maxLinks` 4（含施工中）、`massWeight` 4、鋼樑每格 Steel 5／工作量 120、拆除工作 ×0.5 返還 50%。
- **施工**：Gizmo「架設鋼樑」→ Targeter 選目標（Shift 連續拉樑：此 overload 的 `BeginTargeting` 會把 `needsStopTargetingCall` 歸零，在 action 內再開選取不會被關掉）→ 施工單掛在來源樁 → `CompStructuralPile` 實作 `IThingHolder`，原版 `HaulToContainer` 直接送料（`WorkGiver_DeliverPileBeamMaterials`）→ `WorkGiver_BuildPileBeam`／`JobDriver_BuildPileBeam` 在來源樁旁施工（鋼樑可能跨虛空，不放 Frame）。拆除單在下令的樁旁施工；取消全額退料；上帝模式即時完工。
- **連結條件** `PileLinkUtility.CanOrderLink`：已完工、同圖同陣營、距離 ≤ 兩端較小 `linkRange`、未連結或施工中、未達上限、網路不在倒數中。
- **生命週期**：拆除／Minify 時連結解除並按比例退料；被摧毀不退料；陣營改變全部解除；**分離時** `BeingTransportedOnGravship == true`，不得解除連結或丟料。`MapComponent_PileNetwork.FinalizeInit` 補齊單向殘缺。
- **重疊（爭議格）**：起飛的網路帶走自己覆蓋範圍內**全部**格子；完整位於其內的他網結構樁也被帶走，與留下的樁之間的連結斷裂（不退料）。未完整包含的多格建築分離時以原版 `Destroy(KillFinalize)` 摧毀（藍圖／框架 `Cancel`，附掛建築隨母體），殘骸與彈出物落在 footprint 內就上船。爭議格標橘、將摧毀的建築紅框、附近未連結的樁黃框。
- **網路快取**：BFS 重建；footprint 250 ticks 快取，`TerrainGrid.SetTerrain`／`SetFoundation`／`RemoveFoundation` 在 Covered 內時失效，起飛強制重算。
- **鋼樑渲染**：整圖 `MapDrawLayer`（`MapDrawer` 自動實例化非 SectionLayer 子類，同原版 `MapDrawLayer_OrbitalDebris`），畫在 `AltitudeLayer.BelowTerrain`：有地板處被蓋住、虛空處透出。`relevantChangeTypes` 保持 0（原版從不清 `globalDirtyFlags`，掛旗標會每幀重建），改由 `MarkDirty` 直接設 `Dirty`。材質 renderQueue 1990（地形 2000+）。施工中的鋼樑以藍圖色線在上層顯示。
- 船體 footprint 內的物件規則：`bringAlongOnGravship == false` 不搬（懸空則 Vanish，先警告）；Mote／Skyfaller 不搬；`MapPortal` 搬並把 `PocketMapParent.sourceMap` 改指船體地圖；天然岩預設帶走（`carryNaturalRock`）。

## 3. 控制台與起飛流程

`DMSE_TransferConsole`：3×2、300 W、`operateTicks` 1250、`countdownTicks` 600（10 秒暖機）。必須位於網路 footprint 內；同網路多台時第一台可用者為主。船名：控制台實作 `IRenameable`，同船控制台同步；起飛時 `TakeShipNameForLaunch` 決定（否則自動「轉移艦 N」）。

1. 「制定轉移計畫」→ TilePicker（航程圈）→ `Dialog_TransferPlan`（樁數／質量、推力、TWR、燃料、時間、留下的人員與建築、將摧毀的建築、將帶走的爭議格）→ Planned。
2. 「啟動起飛程序」→ AwaitingOperator → `WorkGiver_InitiateTransfer`（智識）／`JobDriver_InitiateTransfer`。
3. Countdown：`Alert_TransferPawnsOutside`、`Alert_TransferExhaustDanger`；鋼樑 Gizmo 與 WorkGiver 鎖定；每 250 ticks 重做阻擋性檢查，失敗回 Planned。`MapComponent_TransferBurn` 暖機演出（`ShipReactor_Startup`、火焰逐漸增強、鏡頭震動、`Gravship_RetroThruster_Loop`），不造成傷害。
4. 分離（§5）→ `Gravship_Launch`、`TransferTakeoffCutscene`。

`TransferReadiness`（Error 擋起飛）：不在太空層、控制台不可用、無引擎或 TWR < minTWR、燃料不足、同圖已有倒數中的船、（撞擊）引擎門檻與撞擊點。Warning：範圍外人員、`bringAlongOnGravship` 物件、尾焰區有人、附近未連結的樁、將摧毀的建築（特別標出內有 pawn 的容器）、帶走爭議格、未完工施工單、封鎖攔截風險（見 [NPC 防空警戒](NPC防空警戒五階段_執行計畫.md)）。

## 4. 引擎、燃料與數值

- `CompTransferEngine`（不再是 `CompGravshipThruster`）：`thrust`、`fuelFactor`、`requiresReactorSupport`、`impactCapable`、尾焰區與火焰欄位（名稱同原版，方便搬 XML；`exhaustSettings` 直接用原版巢狀型別）。可用 = 在 footprint 內、未損壞、開、通電、尾焰未受阻（Impassable 或 fillPercent ≥ 0.5 擋）、需要時核融合爐支援足夠。效果集中在 `MapComponent_TransferBurn`／`TransferEngineEffects`，噴口位置由 `NozzlePos`／`FlameCenter` 提供。
- 化學引擎：2×3、`DMSE_OrbitalDynamics`、推力 3、燃耗 1.0、無核融合爐需求。熱核推進器：推力 12、燃耗 0.35、需核融合爐（`CompTransferReactor`，每座支援 2 具）、`impactCapable`；已從 GravEngine／Gravhulk `linkableFacilities` 移除。核融合爐、燃料筒倉改 Heavy 地形（保留逆重船設施身分）。
- 燃料來源：footprint 內 `CompGravshipFacility.providesFuel` 或 `TransferFuelSourceExtension` 的 `CompRefuelable`；`ITransferFuelSource` 介面，VGE 專案註冊 `PipeStorageFuelSource`（Astrofuel 每單位 = 10 / `resourceToRangeRatio` 化學當量，即 2）。等比例扣除，**分離時一次扣足**。

```
W = Σ massWeight（網路內）；T = Σ 可用引擎 thrust；TWR = T/W；η = Σ(thrust×fuelFactor)/T；d = 軌道層格數
F      = W × (B + P×d) × η                          B 60、P 12
d_max  = floor((Fuel/(W×η) − B) / P)
TWR ≥ minTWR 0.5
t_burn = clamp(burnHoursRef 3 × TWRref 1 / TWR, 1, 12) h
t_coast= d × 2 h；t_total = 2×t_burn + t_coast
```

試算（W = 4 × 樁數）：1 樁、化學 ×2、d 5 → 燃料 480、14 h；3 樁、熱核 ×1、d 10 → 756（化學 ×4 為 2160）；8 樁、熱核 ×3、d 20 → 3360（化學 ×11 為 9600）。燃料壓力讓大船走向熱核。常數全在 `DMSE_TransferFlightTuning`（另含 `exhaustDamageIntervalTicks`、`footprintCacheTicks`、撞擊門檻、安全期參數）。

## 5. 分離（`ShipSeparationUtility`）

同步 `LongEvent`，順序對照原版 `GravshipUtility.GenerateGravship` 與 `PlaceGravshipInMap`，但原座標、無旋轉：

1. 重算網路、再跑整備；處理邊界（摧毀跨界建築、斷裂他網連結、取消跨界施工單退料）；重新收集船載物。
2. 建 `WorldObject_TransferShip`，`MapGenerator.GenerateMap(origin.Size, ship, DMSE_ShipVoid)`（只鋪 Space）。
3. 擷取地圖層資料：zones（跨界時**拆分**，用新 Zone ID）、areas 與 pawn 區域指派、storage groups、各層地形與顏色、屋頂、氣體、迷霧、designation、房間溫度與真空、電力狀態。原版 `MoveableArea` 需要 `Gravship` 物件，不能傳 null 重用 → 自寫輕量快照。
4. 物件依 `ThingSpawnPriority` 排序 → `PreSwapMap()` → `DeSpawn(WillReplace)`；pawn 先停工、放下搬運物、非玩家 pawn 脫離原 Lord。`BeingTransportedOnGravship` 讓原版數十處生成／拆除副作用略過——DMSE 自己有副作用的 Comp 也要檢查。
5. 原地圖清理：footprint 設為 `Space`（**不能**用 `RemoveGravshipTerrainUnsafe`：它還原 under 層，小行星岩地不會變虛空）、清屋頂、氣體、designation，檢查屋頂崩塌；移除後呼叫原地圖 `UpdatePowerNetsAndConnections_First`（否則重新接線報「PowerNet registered connector it already had」）。
6. 船體地圖：開啟 `regionAndRoomUpdater` → 地基 → 地形 → 氣體 → zones → 物件 → pawn（恢復徵召、倒地者上床）→ 屋頂 → areas → designation。
7. 收尾：`UpdateBillDestinations`、電力還原、`PostSwapMap()`、溫度／真空、迷霧、`storyState`、`autoSlaughterManager`；口袋地圖改指；原地圖去留（`SpaceMapParent` 自理；無人 Settlement 棄置；否則保留）；扣燃料，進入 DepartureBurn。

必測矩陣（未完整實測）：儲存區／種植區／儲存群組、bill 目標、床上倒地者、囚犯奴隸、動物區域、徵召、搬運中、藍圖框架、Minified、電網與電池、溫度真空、迷霧、屋頂、船上的逆重船、BVR 裝置重新註冊、導彈架、真空密封室、跨界建築（一般與爭議格邊界）、`bringAlongOnGravship`、樁連結與施工單。

## 6. 飛行

`WorldObject_TransferShip : ClaimableSpace`（`DMSE_TransferShip`、`canBePlayerHome`、`useDynamicDrawer`）；飛行中 `ShouldRemoveMapNow` 回 false；船上仍有樁時不因無人移除，另有「放棄船隻」Gizmo。存檔：phase、origin／destination、departTick、burn／coast ticks、fuelSpent、isImpact、`reroutedFrom`、`landingTarget`；航線讀檔重建。

- **進度**：梯形速度剖面（加速 → 等速 → 減速）。入軌點火時重算推力，以「從當下進度減速收斂到 1」保持連續；推力歸零則以 maxBurnHours × 2 慢速減速並發信件（不做飛過頭的死局）。
- **格子推進**：航線從分離時的第一個空格 `flightStart` 開始，沿貪婪鄰格折線；`DrawPos` 與背景相機沿折線移動；`Tile` 每次 `TickInterval` 取顯示位置所在格（前半前一格、後半後一格），途經已有地圖的格子時暫停在前一格。背景移動覆寫原版 `MapParent.WorldCameraPosition`，不需 patch。
- **點火**：火焰、排氣、光暈、震動、每 60 ticks 尾焰區 Flame 30；穿梭機禁飛。太空地圖以世界為背景時 `WorldRendererUtility.WorldRendered` 也是 true → 判斷改用 `WorldSelected`。
- **目的地**：同層、未占用、`d ≤ d_max`（`ignoreGravshipRange` 略過）；抵達時被占用改停鄰格。目的地是已有地點（`TransferLandingUtility.IsLandableSite`）時飛往旁邊空格，抵達後 `Designator_LandTransferShip` 選位（逐格同原版 `IsValidCell`，不檢查地形），`ShipSeparationUtility.MoveHull` 以位移搬入，受阻時自動找最近可降落處；只平移不旋轉。等待降落期間 `Patch_MapParent_CheckRemoveMapNow_PendingLanding` 擋下原版移除空地圖；`GameComponent_TransferLanding` 在切圖與長事件後才選上設計器。
- **安全期** `DMSE_TransferGrace`（`preventIncidents`）：`min(5, 5 × 2000 / 船體格數)` 天 × 難度係數（和平 1.5 → 極難 0.5）；降落時只轉移剩餘時間；撞擊與開發者分離沒有。`parms.forced` 的事件不受影響。
- **穿梭機**：`CompLaunchable.CanLaunch` postfix 點火時禁飛；`TravellingTransporters.TickInterval` prefix 把飛往轉移艦的運輸艙 `destinationTile` 同步為船的目前格。
- **導彈**：船是 `MapParent`，導彈終端更新自動追蹤（逃逸區）；打軌道船需導引能力與射程 +10；BVR 裝置隨分離重新註冊。NPC 封鎖可攔截飛行中的轉移艦（在其地圖上爆炸 3 次）。

## 7. 撞擊飛行（Hellfire）

控制台「規劃終結巡航」（運作中 `impactCapable` 引擎 ≥ 3）→ 確認視窗（6 秒）→ 選地表撞擊點（無已載入地圖）→ 計畫。航程以起點到 `approachTile`（太空層最接近撞擊點的格）計算；不減速，撞擊時刻 = 點火 + 滑行 + 半個點火；前 5 秒畫面淡白。`TransferImpactUtility.Resolve`：摧毀目標據點、撞擊坑、移除 DMS 軍隊據點、全派系敵對、銷毀船、片尾；≥4 具為行星殺手結局。

## 8. 相容與遷移

- 舊流程已刪除（駕駛台攔截、模式選單、`FlightModeLauncher`、`FlightUtility`、`Patch_Visible`、`Patch_Background`、`VGECompatibility`、VGE 攔截）。`Legacy/` 只留 `MapComponent_Ship`（空殼）、`TravelingObject`／`WorldObject_Transfer`、`WorldObject_ImpactGravship`：舊檔第一個 tick 直接 `Arrive()`（不在 `FinalizeInit`，避免載入中銷毀地圖或播片尾）。`DMSE_TransferThruster` 元件類型 Def 保留一個版本。
- VGE：熱核推進器移除 `CompProperties_ResourceThruster` 與管線指令；燃料筒倉 `comps Inherit="False"` 改用 `CompProperties_PipeNetFuelProvider`；`DMSE_VGE_Patch` 不再有 Harmony patch，參考 `PipeSystem.dll`。
- **非太空圖層**（2026-11-01）：在 `PlanetLayerDef` 掛 `DMSE.TransferEnvironmentExtension`（`voidTerrain`、`shipMapGenerator`、`shipWorldObject`，皆可省略）即可在該圖層起飛；`TransferEnvironment` 取代起飛判定、樁放置、`PileNetwork.IsSolidCell`、分離／降落中原本寫死的 `isSpace` 與 `TerrainDefOf.Space`。太空圖層不掛擴充時行為不變；`shipWorldObject` 的類別必須是 `WorldObject_TransferShip` 或其子類（`ConfigErrors` 檢查）。撞擊飛行與平台拆除仍只限太空。
- `DMS_OrbitalPlatform` 不再依賴 Orbital Platforms 模組（`Patch_OrbitalPlatform.xml` 為虛空補 `VoidSpace` 承載力）；拆除用 `Designator_RemovePlatform`。

## 9. 待辦

遊戲內實測與數值調整；化學引擎等白色佔位貼圖、化學引擎火焰（暫借熱核 `Visual/NuclearThruster_Burn`）；世界地圖圖示尾焰；pawn 自動避開尾焰區；舊翻譯鍵 `DMSE.Flight.*`、`DMSE.Cannot.Reason.*`、`DMSE.Disabled.*`、`DMSE_ShipWarmUpBegin` 移除（`DMSE.Flight.Impact.Confirm`、`DMSE_ShipWarmUpCountdown` 仍在用）；`ImpactCraterUtility` 跨圖層距離；降落旋轉；起飛前自動拆除跨界建築按鈕；飛行中經度變化讓當地時鐘跳動（接受）。
