# 導彈 Skyfaller → Projectile 遷移

狀態：P0–P7 完成（2026-10-21，總驗收 64 項全過）；轉向調整與貼圖尺寸 2026-10-26／27。原始完整計畫（含逐階段紀錄、P0 基準）在 `docs/_archive/`。

導彈的離場、落地、攔截彈三段改由 `Verse.Projectile` 子類實作。發射特效、BVR、波次打擊回報、存檔相容維持。

## 1. 已定案的行為變更（2026-10-21）

| # | 決策 | 實作 |
|---|---|---|
| D1 | 高角護盾可攔截落地導彈，在碰觸點完整引爆 | 只限 `interceptAirProjectiles`；離場彈與攔截彈不受護盾影響 |
| D2 | 爆炸歸因於發射方，被攔截則否 | 正常命中 instigator = 發射建築；護盾擋下 instigator = null；BVR／CIWS 攔截不爆炸 |
| D3 | 從地圖邊緣依世界航跡方向飛入 | 進入方位 = 最後一段在目標 tile 的切線反向，`PlanetLayer.GetHeadingFromTo` |
| — | 離場改沿世界航跡方向飛出邊緣 | 保留升空段，之後限速轉向巡航 |
| — | 攔截彈升空後轉向來襲方位飛出地圖，出圖時結算 | 2026-10-21 追加 |

## 2. 型別與檔案

`.source/DMSE/MissileProjectile/`：

| 類別 | 內容 |
|---|---|
| `Projectile_MissileBase` | 升空／墜落時間軸、繪製、影子、音效、`HitRoof`；實作 `IMissileFlight` |
| `Projectile_MissileLaunch` | 對地離場與攔截彈共用（帶 worldObject 或 isInterceptor） |
| `Projectile_MissileIncoming` | 落地彈；`LastImpactCell`（實際爆心） |
| `MissileFlightExtension` | 原 `<skyfaller>` 欄位＋`ascentHandoff`、`handoffMaxCells`、`maxTurnRate`、`maxTurnRadius` 等 |
| `IMissileFlight` | `VisualPos`、`GroundCell`、`LaunchDrawPos/Rotation`、`EffectEvaluate`、`EffectSpeed` |
| `MissileDepartureKinematics` | 升空公式、交接、巡航積分、`TurnStep`、`TurnRadiusAt`、`InwardSide`、`SimulateDepartureTicks`（實際飛行與時間軸估算共用） |
| `MissileArrivalKinematics` | `EntryTicks`：起點恰在邊緣外 |
| `MissileProjectileMaker` | `SpawnAscent`／`SpawnIncoming`、`DepartureHeading`、`IncomingBearing`、`WorldInterceptBearing` |
| `MissileMapEntryUtility` | 方位 ↔ 邊緣點 |
| `MissileShieldUtility` | 高角護盾攔截 |
| `MissileImpactUtility` | 新舊共用引爆 `Detonate`；`Instigator` 靜態欄位 |
| `MissileFlightTrace` | 逐 tick 追蹤，供新舊比對 |
| `MapComponent_MissileLaunchQueue` | 同圖升空彈至少相隔 6 tick 生成（存檔） |

- `IIncomingMissile` 讓 BVR、警報、打擊回報不需知道載體型別。
- `MissileBodyDef.launchProjectile`：每個彈體一個 `DMSE_Launch_<Body>`，由 `.source/Tools/gen_missile_launch_defs.py` 產生 `1.7/Defs/Missile/DMSE_MissileLaunchProjectiles.xml` 與中文標籤（冪等）。16 個 incoming def 由同腳本 `convert_incoming_defs()` 就地轉換（defName 不變、父節點 `DMSE_MissileIncomingProjectileBase`）。
- 對地發射（`TryLaunchRound`）、SAM（`FireInterceptor`）、單兵防空（`ManPortableSAM`）用實際裝填彈體的發射 Projectile；彈體沒設定時退回平台舊 skyfaller。
- 舊類別保留：`MissileIncoming`（舊存檔轉換）、`ScorerProjectile`／`InterceptProjectile`（平台後備）。欄位名 `incomingSkyfaller` 等不改名。
- `check_missile_content.py` 規則 10：每個彈體必須有發射 Projectile。

## 3. 引擎事實（rw17）

- Projectile 停用原版運動：`Launch()` 後設 `landed = true`，`TickInterval` 遇 `landed` 即 return（出界 Destroy、自由攔截、`ImpactSomething` 都不發生）。**依賴原版實作細節，1.7 更新需重查。**
- 原版 Projectile 出界直接 Destroy 不 Impact；`flyOverhead` 遇厚岩頂直接 Destroy 不爆炸 → 落地彈自寫 `HitRoof`。
- 護盾 `CheckIntercept` 以 `Launcher` 判敵我，launcher 為 null 不攔。
- Skyfaller 的 `angle` 與世界方位是同一套羅盤角（`PosAtDist` 位移 = (sin, cos)），邊緣入射墜落角 = 進入方位，不需換算。heading 0 = 地圖 +Z 邊、90 = +X 邊，與原版商隊離圖一致。
- 舊版尾焰位置取自上次繪製的快取（Ultrafast 最多落後 23.7 格）；新版每 tick 重算，高速時更貼彈體。
- 跨圖層／無水平方位時退回原垂直墜落（−33.7°、120–200 tick）。
- 不沿用 `ArtilleryStrikeUtility`：它假設北極 = `Vector3.up`、用起點 tile 而非最後一段、只取四邊中點。

## 4. 飛行

- **升空段**：逐 tick 重算原 Skyfaller 公式（含「z/rotation 曲線只在東西朝向生效」特例），以原 `Rot4`＋`extraRotation` 繪製。地面位置（影子、Position、護盾）與視覺位置（`DrawPos`）分開。驗收 F1：新舊 24 對 300 tick 逐 tick相等。
- **交接**：`ascentHandoff` 0.3 或畫面位移 `handoffMaxCells`（預設 16，彈道族 22，`FLIGHT_OVERRIDES`），實際上一律是後者先到。
- **轉向**（2026-10-26「晚一點、緩一點」）：`TurnStep` 轉速在 15 tick 內由 0 升到 `maxTurnRate`（2°/tick，彈道族 1.2），每 tick 不超過剩餘角 ×0.12；轉速下限 = 速度 ÷ `TurnRadiusAt`（沿航向到邊緣距離的一半，4–25 格），半徑限制只用 4 tick 漸增；>150° 迴轉往地圖中心側（`InwardSide`）。`cruiseSpeedFactor` 依 `TurnBlend` 漸入。
- **巡航速度** = 交接速度 × speedCurve(進度)/speedCurve(交接進度)，夾在 min／max；畫面位置出圖即加入世界航跡。尾焰 evaluate 交接後固定。存檔 `cruiseMotion`。
- **落地**：不做巡航段，直接 angle = 進入方位、起始 ticksToImpact = `EntryTicks`，原公式從邊緣直線飛向落點。BVR 不提早釋放（時序語意同舊版）。進入方位取 `MissileWorldObject.ArrivalHeading`（終端更新後用改飛點）。
- **時間軸**：`DepartureTicksFor(config, targetTile)`（模擬，有快取）；`ScorerProjectile_WorldObject.launchSpawnTick` 計入發射排隊延後。

## 5. 護盾與歸因

- `MissileShieldUtility.TryIntercept` 每 tick 以畫面位置線段呼叫原版 `CheckIntercept`；無發射建築時以攻擊方陣營判敵對，再暫時打開私有 `debugInterceptNonHostileProjectiles`。擋下時在碰觸格完整引爆、`StrikeOutcome.ShieldBlocked`（報告附「被護盾擋下 N」）。`DamageAmount` 回傳彈頭傷害；ShieldGeneratorMortar 無 HP，只觸發攔截特效。
- `TryLaunchRound` 把發射建築寫進 `ScorerProjectile_WorldObject.launcherThing` → 落地彈 `SetLauncher`。`Detonate` 期間設 `MissileImpactUtility.Instigator`，戰鬥部／載荷的爆炸、傷害、點火都讀它。已摧毀的建築不當 instigator。跨圖 instigator 無紅字（F10）。
- **未做**：發射建築不存在時以陣營歸因；中立陣營好感度扣減未驗證（測試世界無中立陣營）。

## 6. 存檔相容

- `BVRTarget.skyfaller` 改 `ThingOwner<Thing>`（Scribe 鍵不變），新增 `Payload`／`Missile`。
- 舊 `MissileIncoming` 遇到已轉換的 def 時，第一個 tick 前以新 Projectile 原落點重新墜落；BVR 容器內的於 PostLoadInit 轉換（存檔缺 `innerContainer` 時補建，否則 `Destroy` NRE）。驗收 L3 以改寫存檔類別名模擬。

## 7. 導彈貼圖尺寸（2026-10-26 定案）

- 完整彈體（含助推器）畫面長度 = max(1.2, 3.9 × (真實長度 ÷ 9.7 m)^0.75) 格，不夾量級區間。同型號所有 def（發射、落地、物品）共用一個 drawSize。
- 工具：`.source/Tools/apply_missile_sizes.py`（量貼圖、算 drawSize、寫入 def、再跑產生器；`--only`、`--report`）；`render_missile_reference.py` → `docs/review/missile_reference.png`。
- 參考長度（m，完整彈體）：刺針 1.52、地獄火 1.63、小牛 3.46、Grison 2.56、麻雀 3.66、戰斧 5.56、Shahed 3.5、白蟻 4.0、萊茵女兒 6.3、V1 8.3、鷹擊 11.1、現役巡航 7.0、白蛉 9.7、圓點U 6.4、現役彈道 12.0、V2 14.0、民兵3 18.2。
- 圓點U 2026-10-27 改用自有貼圖，drawSize 3.3。臨時工具 `.source/DMSE/Dev/MissileSizeTuner.cs`（debug「DMSE Missile Size」，V8），調完後刪除。

## 8. 驗證與風險

- 驗收：F1（升空逐 tick 等值）、F3（120 組世界格對方位誤差 0.007°）、F4（內容檢查）、F5（16 彈體 × 5 方位出圖方向與轉向）、F6／F8 截圖、F7（入射方位）、F9（護盾）、F10（歸因）、F11／F12（攔截彈轉向、發射錯開）、L3（舊存檔）。BVRTests：方位／邊緣點／轉向。
- 風險：BVR 與原版空投艙共用路徑；`landed = true` 依賴；導彈屬 `ThingRequestGroup.Projectile`，第三方點防禦可能瞄準（VGE `CompPointDefence` 只瞄有 `explosionRadius` 的，不受影響）。
- 後續：火砲改用 `MissileMapEntryUtility`。
