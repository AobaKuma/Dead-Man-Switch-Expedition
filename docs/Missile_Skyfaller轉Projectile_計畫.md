# 導彈 Skyfaller → Projectile 遷移計畫

> 目標：導彈的**離場、落地、攔截彈**三段改由 `Verse.Projectile` 子類實作。
> 發射特效（冷射／熱射尾焰、煙、發射塵、點火音）、BVR 攔截、波次打擊回報、存檔維持不變；
> 依 §0 已定案的三項改變，落地改為**從地圖邊緣依世界航跡方向飛入**、**高角護盾可攔截**、**爆炸歸因於發射方**。

---

## 0. 已定案的行為變更（2026-10-21）

| # | 決策 | 實作重點 | 章節 |
|---|---|---|---|
| D1 | **高角護盾能攔截導彈，並在碰觸位置爆炸** | 只限 `interceptAirProjectiles` 的護盾（原版 mech 高角護盾類）；地面護盾不攔。完整引爆（彈體爆炸＋戰鬥部＋載荷），位置為碰觸點，不套用命中偏移 | §6 |
| D2 | **爆炸算在發射方頭上，除非被攔截** | 正常命中：`instigator = 發射建築`（沒有建築時以發射陣營歸因）；護盾攔截後的爆炸 `instigator = null`；BVR／CIWS 攔截照舊不爆炸 | §7 |
| D3 | **改為從地圖邊緣飛入，方向必須與大地圖進入點一致** | 進入方位 = 世界航跡**最後一段**在目標 tile 的切線反方向，用原版 `PlanetLayer.GetHeadingFromTo`（與商隊進出地圖同一套慣例）；從落點沿方位反向射線求精確邊緣點，不做四象限取整 | §5 |

衍生決定（建議值，若不同意請指出）：
- **離場也改為朝世界航跡方向飛出地圖邊緣**：保留原本的垂直升空段（冷射彈射延遲、熱射爆發都在這段），之後轉向沿出發方位飛到邊緣，世界航跡從那一刻出現。理由：與 D3 對稱，玩家看到的出發方向與大地圖航跡一致。
- **離場彈與攔截彈不受護盾攔截**：只有落地彈參與 D1（避免己方基地內的敵方護盾 mech 擋下剛升空的彈）。
- **攔截彈（SAM）維持原本的純視覺升空**：它不進世界航跡、目標也不在地圖內，沒有「方向正確」的對象。

---

## 1. 現況盤點

### 1.1 三個 Skyfaller 子類

| 類別 | 用途 | Def 數 | 關鍵行為 |
|---|---|---|---|
| `ScorerProjectile`（Scorer/ScorerProjectile.cs） | 發射平台離場 | 4（LightMissile / Loitering / BallisticSilo / Security_Missile） | `reversed` 動畫；`LeaveMap()` 時把 `worldObject` 加入世界並跳鏡頭；實作 `ILaunchTarget` 給尾焰用 |
| `MissileIncoming`（Scorer/Assembly/MissileIncoming.cs） | 抵達落點 | 16（含 13 個世代彈體） | 首 tick 交給 BVR；`Impact()` 依 `MissileConfig` 爆炸／戰鬥部／載荷／閃光；`Destroy()`/`Impact()` 回報波次打擊結果 |
| `InterceptProjectile`（SkyFallerTurret/InterceptProjectile.cs） | SAM 攔截彈（純視覺＋擲骰） | 2＋驗收用 `DMSE_SkyFaller_Interceptor` | `LeaveMap()` 時釋放火力通道並擲骰命中；實作 `ILaunchTarget` |

### 1.2 依賴 Skyfaller 型別／欄位的呼叫點

- **生成**：`CompMissileLauncher.cs:354`（離場）、`SkyFallerTurret/BVR/CompMissileLauncher.cs:83`（SAM）、`ManPortableSAM.cs:235`、
  `ScorerProjectile_WorldObject.SpawnIncoming`、`WorldObject_IncomingMissile.OnArrived`、`IncomingMissileUtility`、`DebugActions_Missile`（54/107）、
  `MapComponent_BVRCombat.RegisterSalvo`（`SkyfallerMaker.MakeSkyfaller`）。
- **發射特效**：`CompEffecterLaunch`（Scorer/CompEffecterColdLaunch.cs）直接 `parent as Skyfaller`，讀 `ticksToDiscard`、`def.skyfaller.speedCurve`、`DrawPos`；`CompEffecterHotLaunch` 繼承之。
- **時間軸估算**：`CompMissileLauncher_WorldTargeting.DepartureTicks` 讀 `def.skyfaller.ticksToDiscardInReverse`；
  使用者：`CompStrikeConsole.cs:394/400`、`StrikeRoundUtility.cs:146`（波次打擊時間軸）。
- **BVR**：`BVRTarget.skyfaller : ThingOwner<Skyfaller>`、`Resolve()` 用 `TryDrop` 重新生成同一個 skyfaller；
  `RegisterIncoming/RegisterTerminal/AddIncoming/ComputeWarningWindow(Skyfaller …)`；`MissileBVRUtility.TryRegister`（Spawn→DeSpawn）；
  `WorldComponent_BVRNetwork.TrySpawnDropPodTrack(…, Skyfaller)`。**同一套 BVR 也處理原版敵方空投艙（真正的 Skyfaller），不能拿掉。**
- **顯示／警報**：`Alerts_PlayerMissiles`（`target.Skyfaller is MissileIncoming`）、`Alerts_Pods`、`ManPortableSAM:197`。
- **過濾**：`Patch_MakeDropPodAt`（排除 `InterceptProjectile`）、`TransferReadiness:184`（`t is Skyfaller`，**沒排除 Projectile**）、`ShipSeparationUtility`（已含 Projectile）。
- **資料欄位**：`MissileBodyDef.incomingSkyfaller`、`ScorerProjectile_WorldObject.skyfallerIncoming`、`WorldObject_IncomingMissile.incomingSkyfaller`、
  `CompProperties_*.skyfaller / skyfallerIncoming`、`CompProperties_MissileLauncher.interceptorSkyfaller`、`ManPortableSAM.interceptorSkyfaller`；
  `CompMissileBodyRenderer` 用 `incomingSkyfaller.graphic` 畫井內彈體。
- **世界航跡**：`MissileWorldObject` 的 `Start`（`legStart` 或 `initialTile` 中心）／`End`（`destinationTile` 中心），`Vector3.Slerp` 飛行；終端更新會改寫 `legStart`。
- **火砲先例**：`ArtilleryStrikeUtility.WorldDirectionAngle/EdgeCellFromAngle/CalcEntryEdgeCell`、`Projectile_ArtilleryDeparture`（見 §5.4 為何不直接沿用）。
- **測試**：BVRAcceptance（`BVRFixture.InterceptorSkyfaller`、`ClearCell` 清 `Skyfaller`、Tests_Strike 695/699 直接 `SpawnSkyfaller`、Tests_Map/Generations/World 找 `InterceptProjectile`）、BVRTests Program.cs 278/428。
- **工具**：`.source/Tools/check_missile_content.py`（3 處檢查 skyfaller 欄位）。

---

## 2. Skyfaller 與 Projectile 的關鍵差異（rw17 反編譯確認）

| 面向 | Skyfaller | Projectile（原版） | 對策 |
|---|---|---|---|
| 運動 | 時間軸驅動：`TimeInAnimation`，`SkyfallerDrawPosUtility.DrawPos_*` ＋ speed/rotation/x/zPositionCurve | 直線 `origin→destination` 等速＋拋物線 `ArcHeightFactor`，旋轉固定 | 自訂分段軌跡（§3.2）；升空段重用 `SkyfallerDrawPosUtility`（public static）與 `Skyfaller.DrawDropSpotShadow`（public static） |
| 出界 | 不受邊界限制 | `TickInterval` 中 `ExactPosition` 出界就**直接 Destroy**，不呼叫 Impact | 停用原版運動（§3.2），邊緣處理全由我們決定 |
| 攔截 | 不參與 | 每步 `CheckForFreeInterceptBetween`：護盾、牆、Pawn | 牆／Pawn 一律不碰（導彈在高空）；護盾只對落地彈手動檢查（§6） |
| 屋頂 | `ticksToImpact<=15` 時 `HitRoof()`：打穿薄頂並崩塌周圍，**厚岩頂照樣爆炸** | `flyOverhead` 遇**厚岩頂直接 Destroy 不爆炸** | 落地彈自寫 `HitRoof` 等價邏輯，不走 `ImpactSomething` |
| Tick | `Tick()` 每 tick | `Tick()` 每 tick（含 `CompTick`）；`TickInterval(delta)` 畫面外每 15 tick | 時間軸放在 `Tick()`，畫面外仍 1 tick 精度 |
| 生成 | `SkyfallerMaker.SpawnSkyfaller`，`SpawnSetup` 隨機時長 | `GenSpawn.Spawn` 後必須 `Launch(...)` | 統一由 `MissileProjectileMaker` 生成＋Launch |
| 重新生成 | BVR `TryDrop` 後重新墜落 | 重新 Spawn 不會重設軌跡 | 落地彈提供 `BeginFlight(...)`，BVR 不再用 `TryDrop` |
| 歸因 | 無 launcher | `launcher` 欄位；護盾 `CheckIntercept` **以 `Launcher` 判斷敵我，launcher 為 null 時不攔截** | §6、§7 |

---

## 3. 目標架構

### 3.1 新型別

```
Verse.Projectile
 └─ DMSE.Projectile_MissileBase            ← 分段軌跡、繪製/影子/音效、實作 IMissileFlight
     ├─ DMSE.Projectile_MissileDeparture   ← 取代 ScorerProjectile（升空段 → 巡航段 → 出邊緣 → 世界航跡）
     ├─ DMSE.Projectile_MissileInterceptor ← 取代 InterceptProjectile（只有升空段，與現在相同）
     └─ DMSE.Projectile_MissileIncoming    ← 取代 MissileIncoming（邊緣入射 → 俯衝段 → HitRoof → Impact）

DMSE.MissileFlightExtension : DefModExtension   ← 原 <skyfaller> 內用到的欄位＋新增的巡航/俯衝參數
DMSE.IMissileFlight                              ← 取代 ILaunchTarget：LaunchDrawPos / LaunchRotation / Progress / CurrentSpeed / FlightTicks / DrawPos
DMSE.IBVRPayload                                 ← BVR 目標若是導彈：Attacker / Config / BeginFlight(cell, map, entry) / 預估飛行 tick
DMSE.MissileProjectileMaker                      ← Make / SpawnDeparture / SpawnIncoming / SpawnInterceptor
DMSE.MissileMapEntryUtility                      ← 世界方位 ↔ 地圖邊緣點（§5）
DMSE.MissileShieldUtility                        ← 高角護盾攔截（§6）
```

### 3.2 `Projectile_MissileBase`：分段軌跡

- **停用原版運動**：`Launch()` 後設 `landed = true`（protected）。原版 `TickInterval` 在 `lifetime -= delta` 後遇 `landed` 即 return，
  出界 Destroy、自由攔截、`ImpactSomething` 全部不發生；`ThingWithComps.TickInterval` 照常執行。須在註解註明此依賴（§10）。
- **軌跡由 1–2 段組成**，每段有自己的 tick 計數，`Tick()` 推進：

  | 類別 | 段 1 | 段 2 |
  |---|---|---|
  | 離場 | **升空**：原 skyfaller 公式逐 tick 重算，直到交接點 `ascentHandoff`（§8.2） | **巡航**：Hermite 交接後沿出發方位以 `cruiseSpeed` 飛到邊緣點，機頭在 `turnTicks` 內轉向（§8.3） |
  | 攔截彈 | **升空**：原公式跑完全程，同現在 | — |
  | 落地 | **巡航**：從邊緣點沿進入方位飛向落點上空，高度 `entryAltitude` | **終端墜落**：原落地公式，`angle` 改由進入方位換算；`ticksToImpact<=15` 時 `HitRoof`，0 時 `Impact`（§8.4） |

- 地面位置（`ExactPosition`）與視覺位置（覆寫的 `DrawPos`）分開，規則見 §8.2-2。
- **繪製**：`Graphic.Draw(..., extraRotation)` ＋ `Skyfaller.DrawDropSpotShadow`（影子大小依剩餘高度），記錄 `trueDrawPos/trueRotation` 給尾焰；fadeIn/fadeOut 照移植。def 不設 `projectile.shadowSize`。
- **音效**：`anticipationSound`（含 sustain）、`floatingSound`、`impactSound` 照 Skyfaller 邏輯。
- **存檔**：段索引、各段 tick、起訖點、方位、origin/destination/landed。
- **飛行時長可預先計算**（`EstimateTicks(map, cell, heading)`），給 BVR 與波次打擊時間軸用（§4、§8）。

### 3.3 Def 形態

新增抽象父 `DMSE_MissileProjectileBase`（取代 `SkyfallerBase`）：

```xml
<ThingDef Name="DMSE_MissileProjectileBase" Abstract="True">
  <category>Projectile</category>
  <tickerType>Normal</tickerType>
  <altitudeLayer>Skyfaller</altitudeLayer>   <!-- 維持原本的繪製層 -->
  <useHitPoints>False</useHitPoints>
  <neverMultiSelect>True</neverMultiSelect>
  <drawerType>RealtimeOnly</drawerType>
  <projectile>
    <flyOverhead>true</flyOverhead>          <!-- 讓 InterceptsProjectile 判定為「空中彈」，只有高角護盾會攔 -->
    <speed>…</speed>                         <!-- 設為實際巡航速度：護盾判定半徑 = radius + SpeedTilesPerTick + 0.1 -->
    <damageDef>Bomb</damageDef>
  </projectile>
</ThingDef>
```

各導彈 def：`ParentName` 改為上述父節點，`<skyfaller>` 整段搬到 `<modExtensions><li Class="DMSE.MissileFlightExtension">`（欄位名相同，機械化轉換），
新增欄位（`cruiseSpeed`、`turnTicks`、`entryAltitude`、`diveTicks`、`diveCurve`）在父節點給預設值，個別彈種再覆寫。
`<comps>`（Hot/ColdLaunch）與 `BVRTargetProps` 原樣保留。**defName 一律不改**。

> 待驗證：ThingDef 同時帶 `<skyfaller>` 與 `<projectile>` 是否觸發 ConfigErrors；預設方案用 ModExtension。

---

## 4. BVR 泛化（最大風險區）

1. `BVRTarget.skyfaller` 型別改 `ThingOwner<Thing>`，**Scribe 鍵維持 `"skyfaller"`**（ThingOwner 存檔不記泛型參數）。
   `Skyfaller` 屬性保留（空投艙用），新增 `Thing Payload`、`IBVRPayload Missile`。
   建構子、`ComputeWarningWindow`、`RegisterIncoming/RegisterTerminal/AddIncoming`、`TrySpawnDropPodTrack` 參數改 `Thing`。
2. **BVRTarget 記住入射方位與邊緣點**（`entryHeading`、`entryCell`），在登記時就算好（§5）。
3. **落點時刻不變**：現在 `wave.tickToImpact` 是落地時刻、`Resolve` 在那時讓 skyfaller 開始墜落（墜落本身很短）。
   邊緣入射的地圖內飛行較長，所以 `Resolve` 改在 `tickToImpact - 預估飛行 tick` 釋放導彈，使實際落地仍在 `tickToImpact`；
   若剩餘時間不足（窗口極短），從航線中途點開始飛（入射點沿航線往落點內移）。末端 CIWS 的判定時刻不受影響。
4. `Resolve(wave)`：`Payload is IBVRPayload m` → 取出並 `m.BeginFlight(position, map, entry)`；否則維持 `TryDrop`（空投艙）。
5. `Discard / SpawnDebris / RemoveTarget`：不變（導彈 `Destroy` 照舊回報 TerminalKilled）。
6. **落地彈不再「先 Spawn 再 DeSpawn」**：`MissileProjectileMaker.SpawnIncoming` 先 `ThingMaker.MakeThing`、填欄位，
   `MissileBVRUtility.TryRegister(Thing, cell, map, entry)`；BVR 收下就以未生成狀態保存，否則直接 `BeginFlight`。
7. `RegisterSalvo`、`Alerts_PlayerMissiles`、`Alerts_Pods`、`ManPortableSAM`、`Patch_QuestPart_Bossgroup.IsOk` 改用 `Payload`/`ThingOwner`。

---

## 5. 地圖邊緣進出點與方位（D3）

### 5.1 方位定義

- 世界航跡是 `Vector3.Slerp(Start, End, pct)` 的大圓弧。導彈在目標 tile 的**進入方向**＝大圓在 `End` 的切線，反過來指向 `Start`。
- 使用原版 `destinationTile.Layer.GetHeadingFromTo(End, Start)`（0 = 北、順時針，北方由該圖層的 `NorthPolePos` 決定）。
  這與原版商隊離圖 `Find.WorldGrid.GetRotFromTo` → `Rot4` → `Pawn.ExitMap(exitDir)` 是同一套：**heading 0 對應地圖 +Z 邊、90 對應 +X 邊**，
  所以地圖內方向與玩家在大地圖上看到的方位一致。
- **一定要用最後一段的 `Start`**：有終端更新時是 `legStart`（不是 `initialTile`），否則改飛後的入射方向會錯。
  `MissileWorldObject` 新增 `public Vector3 LastLegStart` 與 `public PlanetLayer DestLayer`，在 `OnArrived` 前取值並傳給落地彈。
- 離場方位：`initialTile.Layer.GetHeadingFromTo(Start, End)`（發射時的 `End`）。離場後才出現的終端更新只影響之後的世界航跡，不回頭改地圖內的出發方向。

### 5.2 邊緣點

- 由落點（或發射建築）沿方位做射線，與地圖矩形 `[0, size-1]` 求交點，得到**精確的邊緣點**（`Vector3`，非取整到四邊中點）。
  地圖格座標：heading θ 的方向向量 = `(sin θ, 0, cos θ)`（x = 東、z = 北）；入射時射線方向取這個向量（指向來源），出發時同樣取指向目標的向量。
- 實際生成位置取交點往內縮 0.5 格（避免 `InBounds` 邊界），飛行起點仍以未內縮的交點計算，畫面上是從地圖外滑入。
- 方位的水平分量極小（跨圖層、近乎正上方，例如軌道 → 地表）時，退回**原本的垂直墜落**（skyfaller 曲線），因為「水平方向」已無意義。

### 5.3 驗證方式（必做）

- **純數學測試**（BVRTests）：在幾個已知 tile 對（同緯度東西向、經線南北向、跨換日線、近極點、不同圖層）上，比較
  `GetHeadingFromTo` 與「世界航跡最後 1% 的 `PositionAt` 差向量投影到切平面」的角度，誤差 < 2°。
- **遊戲內驗收**：新增 E 系列——從己方基地向四個方位與兩個斜向的據點發射，記錄落地彈的 `entryHeading` 與實際入射邊；
  截圖時同時記下大地圖上航跡末段的螢幕方向。終端更新改飛一次，確認入射方向跟著最後一段改變。

### 5.4 為何不直接沿用火砲的 `ArtilleryStrikeUtility`

- `WorldDirectionAngle` 假設星球中心在原點、北極是 `Vector3.up`，對非地表圖層（有 Origin 偏移）不保證正確；原版 `GetHeadingFromTo` 已處理圖層。
- 它用起點 tile 而不是最後一段起點，終端更新後方向會錯。
- `EdgeCellFromAngle` 只取四個邊、沿邊座標用目標格座標，入射角不是真實方位（例如東北 44° 會從正北邊垂直落下）。
- 計畫完成後，火砲改用同一個 `MissileMapEntryUtility`（列為後續項目，不在本次範圍）。

---

## 6. 高角護盾攔截（D1）

- **只在落地彈的入射段與俯衝段檢查**；每 tick 以上一 tick 與這一 tick 的地面位置呼叫原版 `CompProjectileInterceptor.CheckIntercept`。
  原版會處理：是否為高角護盾（`InterceptsProjectile` 依 `flyOverhead` 對應 `interceptAirProjectiles`）、護盾是否啟用、
  線段是否穿過護盾圓周、護盾特效／攔截錐、扣護盾 HP（`DamageAmount`）、打破護盾。
- **敵我判定**：原版以 `projectile.Launcher` 判斷，launcher 為 null 時直接不攔（除非 `interceptNonHostileProjectiles`）。
  - 有發射建築：`launcher = 發射建築`（D2 本來就要），原版邏輯可用。
  - 沒有發射建築（敵方世界導彈）或建築已摧毀：先由 `MissileShieldUtility` 以 `attacker` 陣營自行判斷敵對，通過才呼叫 `CheckIntercept`，
    呼叫期間以 `AccessTools.FieldRefAccess` 暫時打開該護盾的 `debugInterceptNonHostileProjectiles`，呼叫後還原。
  - 防呆：`Launcher` 不在地圖且 `Faction == null` 時原版會 NRE，這種情況一律走上一條路徑。
- **覆寫 `DamageAmount`** 回傳 `config.DamageAmount`，讓強力導彈較容易打破護盾。
- **攔截結果**：`CheckIntercept` 回傳 true → 以**碰觸點**（`newExactPos` 的格）為中心執行完整引爆：彈體爆炸＋戰鬥部＋載荷＋閃光，
  不套用命中偏移，`instigator = null`（D2）。打擊回報新增 `StrikeOutcome.ShieldBlocked`（波次打擊結果面板與翻譯一併補上）。
- 原版護盾不擋爆炸傷害本身，碰觸點在護盾圓周上，半徑內的東西（含護盾內側）照樣受傷——與原版迫擊砲打在高角護盾上的效果相同，屬預期。

---

## 7. 爆炸歸因（D2）

| 情況 | instigator | 備註 |
|---|---|---|
| 正常命中 | 發射建築（Thing） | 經世界航跡帶到落地彈：`TryLaunchRound` 時寫入 `ScorerProjectile_WorldObject.launcherThing`（`Scribe_References`），落地彈 `Launch(launcher: …)` |
| 發射建築已摧毀／不存在（含敵方世界導彈） | 以發射陣營歸因 | 原版沒有「只有陣營」的 instigator：命中後對受影響的非敵對陣營手動 `TryAffectGoodwillWith`，用與原版轟炸相同的好感度原因；敵對陣營不需處理 |
| 高角護盾攔截後爆炸 | null | §6 |
| BVR 中過程／CIWS 末端攔截 | 不爆炸 | 照舊 |

- 主爆炸、戰鬥部、載荷三處的 `GenExplosion.DoExplosion`／`warheadEffect.Apply`／`payloadEffect.Apply` 都要傳入 instigator；
  `WarheadEffect.Apply` / `PayloadEffect.Apply` 的介面加一個 `Thing instigator` 參數（現有實作一併更新）。
- **風險**：instigator 是**另一張地圖上的建築**。須檢查原版受傷流程（`Pawn_MindState.Notify_DamageTaken`、`Lord.Notify_PawnDamaged`、
  建築被攻擊的報復／逃跑 AI）在 instigator 不在同一地圖時不會報錯或讓 pawn 去追打。驗收加一條「命中有 pawn 的敵方據點」並檢查紅字。
- `ScorerProjectile_WorldObject.OnArrived` 已有「打擊非敵對陣營 → 直接轉敵對」的處理，與歸因不衝突，保留。

---

## 8. 發射尾焰與彈道效果移植規格

### 8.1 現有特效清單（全部要移植）

| 效果 | 來源 | 掛在哪 | 依賴的 Skyfaller 資料 |
|---|---|---|---|
| 彈體升空軌跡 | `Skyfaller.DrawPos`（`DrawPos_Accelerate`，`angle = 建築朝向`）＋`ScorerProjectile.GetDrawPositionAndRotation`（`zPositionCurve` 與 `rotationCurve` **只在東/西朝向生效**） | 離場 4、攔截 3 | `ticksToImpact`（reversed 遞增）、`LeaveMapAfterTicks`、`speedCurve × speed`、`angle`、`Rotation` |
| 落點影子 | `Skyfaller.DrawDropSpotShadow(base.DrawPos …)`：**影子固定在發射格**，大小 `1 + ticks/100`，ticks>150 起淡出 | 全部 | `shadow`、`shadowSize`、`ticksToImpact` |
| 冷射彈射段 | `CompEffecterColdLaunch`：前 `delayTicks`（20/40）只有彈體上升、沒有火焰 | 彈道井、Security 巡航 | `ticksSinceSpawn` |
| 熱射爆發 | `CompEffecterHotLaunch`：`InitialBlastScale`（0.7/0.85/1/1.1）隨 `evaluate×3` 淡回 1 | 傾斜箱、巡飛軌、SAM | 同上 |
| 點火瞬間 | `launchEffectTrigger`（`DMS_LaunchDust_Recoilless`）於 `DrawPos` 格觸發、`ignitionSound`（`DMSE_Missile_Flight`）於 `LaunchDrawPos` 播放 | 5 個有 comp 的 def | `DrawPos` |
| 尾焰＋煙 | `ThrowExhaust(LaunchDrawPos, LaunchRotation, evaluate, speedCurve(evaluate))`：火焰在機尾 `offsetY` 處、6 層疊加；煙 2 團，角度隨進度擴散 | 同上 | `evaluate = ticksSinceSpawn / (ticksToDiscard + delay)`、`speedCurve`、`Position.ShouldSpawnMotesAt` |
| 停止噴焰 | `ShouldShowEffecter`：`DrawPos` 出地圖、或不在目前地圖即停並清除 effecter | 同上 | `DrawPos` |
| 離場音效 | `anticipationSound = DropPod_Leaving`（第 1 tick 播放） | 離場、攔截 | `anticipationSoundTicks = -1` |
| 落地墜落 | `DrawPos_Accelerate`、預設 `angle = -33.7`、`rotateGraphicTowardsDirection`、`ticksToImpactRange` 預設、落點影子收斂 | 16 個 incoming | 同上（正向） |
| 發射後焰（建築端） | `CompLaunchBackblast`（建築的 comp，平台發射成功後觸發） | 平台建築 | 無——不受影響 |

`effecterDef`（`CompProperties_EffecterBase`）目前沒有任何 def 使用，但程式路徑照樣保留。

### 8.2 移植原則：升空段「逐 tick 等值」，之後才分歧

1. **升空段直接重算原公式，不做近似**：`Projectile_MissileBase` 內保存與 Skyfaller 同名同語意的 `ticksToImpact / ticksToDiscard / angle / ageTicks`，
   視覺位置＝`SkyfallerDrawPosUtility.DrawPos_*(地面位置, ticksToImpact, angle, CurrentSpeed)`＋z/rotation 曲線（**保留「只在東/西生效」的特例**），
   繪製時仍以原 `Rot4`＋`extraRotation` 呼叫 `Graphic.Draw`（不要改成 North＋總角度，以免 `drawOffsetEast/West` 等逐向設定失真）。
2. **地面位置與視覺位置分開**：
   - `ExactPosition`（地面）：升空段固定在發射格（與原本影子、`Position` 完全相同）；巡航段沿地面航線移動。給影子、`Position`、`ShouldSpawnMotesAt`、護盾檢查用。
   - `DrawPos`（覆寫為視覺位置）：與原 `Skyfaller.DrawPos` 同義。給繪製、`launchEffectTrigger`、`ShouldShowEffecter` 的出界判定、`TargetInfo(thing)` 的特效位置用。
     （原版 Projectile 的 `DrawPos = ExactPosition`，這裡必須改，否則點火塵與出界判定會跑到地面。）
3. **`CompEffecterLaunch` 只換資料來源，公式逐字保留**：`IMissileFlight` 提供 `VisualPos`(=原 DrawPos)、`GroundCell`(=原 Position)、`LaunchDrawPos/LaunchRotation`、
   `EffectEvaluate`（=原 `ticksSinceSpawn / (ticksToDiscard + delay)`，分母仍用**原升空段長度**）、`EffectSpeed`（=`speedCurve.Evaluate(evaluate)`）。
   升空段中這些值與舊版完全相同，所以火焰大小、煙的擴散角、熱射爆發、冷射延遲都不會變。
4. **只畫一次**：舊 `ScorerProjectile.DrawAt` 先呼叫 `base.DrawAt`（已畫一次彈體＋影子）又自己再畫一次彈體，半透明邊緣會比單次繪製略濃；
   攔截彈只畫一次。新版統一畫一次。這是唯一預期的像素差異，P0 截圖比對時以此解釋；若需完全一致可加 `drawTwice` 旗標給離場彈。

### 8.3 交接段：升空 → 巡航（離場）

原升空動畫在 300 tick 內視覺位移可達數百格（`dist = t^0.95 × 1.7 × speed`），彈體很快就飛出畫面，所以不能「跑完升空再轉向」。改為：

- **交接點**：`MissileFlightExtension.ascentHandoff`（`TimeInAnimation` 比例，預設 0.3；或視覺位移達 `handoffMaxCells` 時，取先到者），
  確保交接時彈體仍在畫面內附近。升空段在交接點之前與舊版逐 tick 相同。
- **連續性**：交接時記下視覺位置 `P_h`、視覺速度 `V_h`（前後 tick 差分）、機頭角 `R_h`。之後 `turnTicks` 內：
  - 視覺位置用 Hermite 曲線從 `P_h`（切線 `V_h`）接到巡航航線上的點（切線 = 出發方位 × `cruiseSpeed`），位置與速度都連續，不會跳格或急停；
  - 機頭角由 `R_h` 以最短角度補間到出發方位；
  - 地面位置從發射格開始沿出發方位加速到 `cruiseSpeed`，影子跟著移動，大小依視覺高度計算（與升空段末端的影子大小銜接）。
- **尾焰**：`EffectEvaluate` 繼續依原公式增加，超過 1 後固定在 1——火焰與煙維持升空末段的強度與形狀，方向自動跟著機頭角（`ThrowExhaust` 本來就用 `LaunchRotation` 決定機尾偏移與噴射方向）。
- **停止條件**：視覺位置離開地圖（同原 `ShouldShowEffecter`），也就是飛出邊緣的那一刻；隨即加入世界航跡。

### 8.4 落地段：巡航 → 終端墜落

- **巡航**：從邊緣點沿進入方位飛向落點上空，視覺高度 `entryAltitude`，機頭朝進入方位，影子在地面航線上。
- **終端墜落沿用原落地動畫**：剩餘 `ticksToImpact` 落入原 `ticksToImpactRange` 範圍時切換成原公式 `DrawPos_Accelerate(落點, ticksToImpact, angle, speed)`，
  只把 `angle` 由固定的 -33.7 改成**由進入方位換算的螢幕角**（`PosAtDist` 用 `FromAngleFlat(angle - 90)`，換算式在 `MissileMapEntryUtility` 內集中並單元測試），
  讓彈體從進入方向墜下；`rotateGraphicTowardsDirection` 與落點影子收斂照舊。交接同樣用 Hermite 曲線保證連續。
- **跨圖層／無水平方位**（§5.2）：整段退回原本的垂直墜落（`angle = -33.7`），畫面與現在相同。
- **巡航尾跡**：現有落地彈沒有任何 comp。邊緣入射後彈體會在畫面內飛較久，建議加一個可選的 `CompEffecterCruiseTrail`
  （重用 `ThrowExhaust`，固定 evaluate，強度較弱），**預設不開**，個別彈種在 def 中開啟。

### 8.5 攔截彈

只有升空段（不交接），與現在逐 tick 相同；`DMSE_InterceptProjectile`（無 comp）與 `DMSE_SkyFaller_Interceptor`（熱射）都照舊。

### 8.6 驗證：軌跡追蹤比對（新增，P1 起使用）

- **追蹤器** `MissileFlightTrace`：每 tick 記錄 視覺位置、地面格、機頭角、影子尺寸與透明度、`EffectEvaluate`、`EffectSpeed`、是否噴焰、點火 tick、
  以及 `ThrowExhaust` 收到的參數（drawPos/rotation/evaluate/speed），輸出 TSV。
- **新舊並行**：舊類別在存檔相容期間本來就保留（§9），驗收測試用同一 def、同一格、同一朝向、同一 `Rand` 種子分別生成舊 Skyfaller 與新 Projectile，
  逐 tick 比對到交接點：位置誤差 < 1e-4、角度 < 0.01°、evaluate/speed 完全相同、點火 tick 相同。四個朝向 × 5 個有特效的 def 全跑。
- **交接後**：斷言位置與速度連續（相鄰 tick 位移差 < 交接前最大位移差的 1.5 倍）、機頭角單調轉向不超過 180°、噴焰不中斷直到出界。
- **落地**：終端墜落段與舊版（同 angle）逐 tick 比對；巡航段斷言方位與 §5 的 `entryHeading` 一致。
- **目視**：P0 錄影與新版並排，各朝向、冷射／熱射各一。

### 8.7 時間軸

- `CompLaunchBackblast.TryTrigger(parent)` 不動。
- **`DepartureTicks` 改為依目標計算**：升空段 tick ＋ 巡航段 tick（取決於發射建築到出發邊緣點的距離）。
  介面改成 `DepartureTicksTo(PlanetTile target)`，`CompStrikeConsole` 與 `StrikeRoundUtility` 的三處呼叫一併修改；
  同樣地，落地端的地圖內飛行 tick 加進打擊時間軸的「抵達」估算（若時間軸有顯示落地時刻）。

---

## 9. 存檔相容

| 情境 | 處理 |
|---|---|
| 舊檔地圖上正在飛的 `ScorerProjectile` / `MissileIncoming` / `InterceptProjectile` | 三個舊類別保留為 `[Obsolete]` 薄殼。`SpawnSetup(respawningAfterLoad)` 時排入 `MapComponent` 轉換佇列，第一個 tick 前轉換：離場→立即把 worldObject 加入世界；攔截彈→立即結算；落地彈→以同欄位重新 `SpawnIncoming`（無世界航跡資訊時退回垂直墜落）。轉換後自毀。 |
| 舊檔 `BVRTarget` 容器內的舊 `MissileIncoming` | `BVRTarget` PostLoadInit 換成新類別（複製欄位）；`entryHeading` 缺值時以垂直墜落處理。 |
| 舊類別讀到新 def | 新 def 無 `skyfaller` 屬性 → 薄殼不得碰 `def.skyfaller`；轉換在第一個 tick／繪製之前完成。 |
| 欄位 `incomingSkyfaller / skyfallerIncoming / interceptorSkyfaller / skyfaller` | 不改名（XML 與 Scribe 鍵牽涉存檔與子模組）；日後要改名用 `[LoadAlias]`。 |
| 世界航跡物件新欄位（`launcherThing`） | 舊檔為 null → 走「以陣營歸因」。 |

舊薄殼保留至少一個公開版本後再移除。

---

## 10. 實作流程

> 建置須在遊戲關閉時進行（DLL 鎖定）；驗收專案編譯的是部署後的 `1.7/Assemblies/DMSE.dll`。

### 進度（2026-10-21）

- **P0 完成**：完整驗收 51 PASS；W11 FAIL（已知的時間抖動）；L2 未跑完（第三方 MapModeFramework 第二次讀檔崩潰，已知）。這就是之後 P7 的對照基準。
- **P1 完成**（尚未改任何 def，遊戲行為不變）：
  - 新增 `.source/DMSE/MissileProjectile/`：`MissileFlightExtension`、`IMissileFlight`、`Projectile_MissileBase`（升空／墜落時間軸、繪製、影子、音效、HitRoof）、`MissileProjectileMaker.SpawnAscent`、`MissileFlightTrace`、`MissileMapEntryUtility`。
  - `CompEffecterLaunch` 改經 `IMissileFlight`；舊 `ScorerProjectile`／`InterceptProjectile` 實作該介面並加上追蹤點（`ScorerProjectile.LeaveMap` 對 null worldObject 加防呆）。
  - 驗收 F1：6 個升空 def × 4 朝向，新舊 24 對 300 tick 逐 tick 相等（位置、角度、時間軸、點火 tick、尾焰 evaluate/speed）；冷射點火在 19／39 tick 與舊版一致。
    F2：新舊截圖。F3：120 組世界格對，進入方位與航跡末段切線最大誤差 0.007°，邊緣分界與 `GetRotFromTo` 全部一致；改飛後方位跟著最後一段。
  - BVRTests 新增方位／邊緣點／墜落角換算檢查（263 項全過）。
- **彈體專屬發射 Projectile（P2 的一部分，提前完成）**：
  - `MissileBodyDef.launchProjectile`：16 個彈體各一個 `DMSE_Launch_<Body>`（thingClass `DMSE.Projectile_MissileLaunch`），
    由 `.source/Tools/gen_missile_launch_defs.py` 產生 `1.7/Defs/Missile/DMSE_MissileLaunchProjectiles.xml` 與兩種中文標籤；
    改 `SPECS` 後重跑即可（冪等，保留原檔換行）。
  - 外觀用彈體自己的機頭朝上貼圖（有 `skyFaller` 用之，否則用朝上的 incoming 貼圖），大小同落地彈；
    飛行沿用同家族平台 skyfaller 的曲線；尾焰依真實發射方式選熱射（爆發倍率）或冷射（彈射延遲），見腳本 SPECS 註解。
    原本就有專屬平台 skyfaller 的 4 個彈體（Ballistic／Light／Loiter／Cruise）整段照抄，外觀不變。
  - 對地發射（`TryLaunchRound`）、SAM 攔截（`FireInterceptor`）、單兵防空（`ManPortableSAM`）都改用「實際裝填彈體」的發射 Projectile；
    彈體沒有設定時退回平台的 `skyfaller`／`interceptorSkyfaller`（仍是舊 Skyfaller）。離場時間改為 `DepartureTicksFor(config)`。
  - 同一 def 兩用：`Projectile_MissileLaunch` 帶 worldObject（對地）或 isInterceptor（攔截）結算，邏輯同舊 ScorerProjectile／InterceptProjectile。
- **P2 完成**：離場巡航段（§8.3）。
  - `MissileDepartureKinematics`：升空公式、交接判定、巡航積分與 `SimulateDepartureTicks` 共用一套純函式，實際飛行與時間軸估算逐 tick 相同。
  - 對地離場彈（`TryLaunchRound` 傳入世界航跡出發方位 `MissileProjectileMaker.DepartureHeading`）在 `ascentHandoff`（0.3）或畫面位移 `handoffMaxCells`（12 格）時交接，
    航向與機頭以 smoothstep 在 `turnTicks`（45）內轉到出發方位；**巡航速度 = 交接速度 × speedCurve(進度)/speedCurve(交接進度)**，升空的加速曲線在巡航段繼續生效
    （夾在 `minCruiseSpeed`／`maxCruiseSpeed`）；畫面位置離開地圖即加入世界航跡。跨圖層（無水平方位）與攔截彈照舊跑完升空動畫。
  - 尾焰進度在交接後固定（`IMissileFlight.EffectEvaluate`），火焰維持交接時的強度直到出圖；影子留在發射點並照原公式淡出。
  - 波次打擊時間軸改用 `DepartureTicksFor(config, targetTile)`（模擬結果，有快取）。
  - 驗收 F5：16 彈體 × 5 方位（80 枚）全數通過出圖方向、speedCurve 比例（誤差 < 1e-3）、交接連續、尾焰不斷、估算＝實際；F6 截圖。
    同輪 M4/W2/K1/K6/K11/K12/F1–F4 皆 PASS。輕型導彈交接速度約 0.25 格/tick（低於下限 0.4 時會小幅加速），全程約 140–320 tick 出圖，與原本固定 300 tick 相近。
  - `check_missile_content.py` 規則 10：每個彈體必須有發射 Projectile（類別、升空擴充、貼圖存在、發射特效、翻譯）。驗收 F4。
- **P3（落地彈＋BVR 泛化）**：
  - `Projectile_MissileIncoming`：16 個 incoming def 就地轉換（defName 不變、`<skyfaller>` 搬進 `MissileFlightExtension`、父節點 `DMSE_MissileIncomingProjectileBase`），
    由 `gen_missile_launch_defs.py` 的 `convert_incoming_defs()` 完成（冪等）。
  - **與原計畫 §8.4 的差異（簡化）**：不另做「巡航段＋Hermite 交接」。原版 skyfaller 的墜落本身就是沿 angle 方向從遠處直線飛向落點，
    且 angle 與世界方位同一套羅盤角，所以只要 angle＝進入方位、起始 ticksToImpact 取成「起點恰在地圖邊緣外」（`MissileArrivalKinematics.EntryTicks`，不短於原 ticksToImpactRange 下限），
    彈體就從正確的地圖邊緣飛入、直線飛向落點，速度與 speedCurve 完全是原公式。無方位時照舊（-33.7°、120–200 tick 隨機）。
  - 進入方位：`MissileWorldObject.ArrivalHeading`（最後一段起點→目標，終端更新後用改飛點）；兩個 `OnArrived` 都經 `MissileProjectileMaker.SpawnIncoming` 傳入。
  - **與原計畫 §4-3 的差異**：BVR 不提早釋放。舊 skyfaller 本來就在 `tickToImpact` 釋放後再墜落 120–200 tick；新版釋放後墜落 `EntryTicks`（同量級），維持原本的時序語意。
  - BVR：`BVRTarget.skyfaller` 改 `ThingOwner<Thing>`（存檔鍵不變），新增 `Payload`／`Missile`；`Resolve` 仍用 TryDrop，Projectile 的 SpawnSetup 每次重算墜落（保留進入方位）。
    `IIncomingMissile` 介面讓 BVR、警報、波次打擊回報不需知道載體型別；`MissileImpactUtility.Detonate` 為新舊共用的引爆。
  - 舊存檔：`MissileIncoming` 遇到已轉成 Projectile 的 def 時，第一個 tick 前以新 Projectile 在原落點重新墜落；BVR 容器內的舊物件於 PostLoadInit 轉換（P5 的一部分提前完成）。
  - 驗收 F7（16 def × 5 方位 + 無方位 + BVR 收起釋放）、F8 截圖；W1 加查進入方位。
- **P4（高角護盾＋傷害歸因）**：
  - 護盾：`MissileShieldUtility.TryIntercept` 每 tick 以落地彈畫面位置的線段呼叫原版 `CheckIntercept`，只有 `interceptAirProjectiles` 的護盾會攔；
    沒有發射建築時以攻擊方陣營判斷敵對，再暫時打開護盾私有的 `debugInterceptNonHostileProjectiles` 讓原版做幾何與結算。
    擋下時在碰觸格完整引爆（不套命中偏移、instigator = null），打擊報告新增 `StrikeOutcome.ShieldBlocked`（報告行尾附「被護盾擋下 N」，三語翻譯）。
    `DamageAmount` 改回傳彈頭傷害（有 HP 的護盾扣得多）；Royalty 的 ShieldGeneratorMortar 本身沒有 HP（-1），所以只會觸發攔截特效。
  - 歸因：`TryLaunchRound` 把發射建築寫進 `ScorerProjectile_WorldObject.launcherThing`，落地彈 `SetLauncher` → Projectile 的 `launcher`；
    `MissileImpactUtility.Detonate(..., instigator)` 期間設 `MissileImpactUtility.Instigator`，戰鬥部／載荷效果的爆炸、直接傷害、點火都讀它（15 個子類不改簽名）。
    已摧毀的發射建築不存參照、不當 instigator。原版戰鬥紀錄對非 pawn 的 instigator 只記 ThingDef。
  - 未做：發射建築已不存在時的「以陣營歸因」（計畫 §7 表第二列）。玩家導彈打到非敵對陣營時，原本 `OnArrived` 就會把目標據點轉為敵對；
    地圖上其他中立陣營的 pawn 在這種情況下不會扣好感度。中立陣營好感度扣減也因測試世界沒有可用的中立陣營而未驗證（F10 note）。
  - 驗收 F9（護盾 10 項）、F10（同圖、跨圖歸因 10 項）皆 PASS，跨圖 instigator 無紅字；同輪 W1／K11／F7 PASS。P4 之後未重跑完整驗收。
- **P5（存檔相容）**：驗收 L3 以「存檔 → 把落地彈改寫成舊 `DMSE.MissileIncoming` 類別 → 讀檔」模擬舊存檔：飛行中的舊導彈在第一個 tick 轉成 Projectile、
  保留 config／攻擊方／打擊任務；BVR 收起的舊導彈讀檔即轉換；舊的離場與攔截 skyfaller（平台後備 def 仍是 skyfaller）照舊完成。
  過程中找到並修正一個問題：存檔缺 `innerContainer` 節點時舊類別的 `Destroy` 會 NRE、轉換中斷（`MissileIncoming` PostLoadInit 補建容器）。
  舊類別（`MissileIncoming`、`ScorerProjectile`、`InterceptProjectile`）保留：前者供舊存檔轉換，後兩者是彈體沒有發射 Projectile 時的平台後備。
- **P6（清理與工具）**：`TransferReadiness` 的「甲板上未帶走物品」排除 Projectile；`BVR_Missile_系統流程.md` 改寫生成／攔截／落點流程與護盾、歸因；
  驗收 README 補 F1–F10、L3。外部相依檢查：本模組 Patches、GravshipExpanded、同資料夾其他 DMS 模組都沒有以 XPath 或 C# 依賴舊 def／類別；
  VGE 近防（`CompPointDefence`）只瞄準 def 有 `explosionRadius` 且 launcher 敵對的投射物，導彈 def 沒有，所以不受影響。
  `Patch_MakeDropPodAt` 保留 `InterceptProjectile` 豁免（平台後備仍走 `SpawnSkyfaller`）；欄位名 `incomingSkyfaller` 等未改名（避免牽動存檔與 XML）。
- **P7（總驗收，2026-10-21）**：完整驗收 64 項中 63 PASS（M0–L3 全跑完，這次第三方 MapModeFramework 沒有崩潰，L2 也通過；W11 通過）。
  唯一失敗是 F10 的測試寫法：受害 pawn 用 TickWatch 臨落地才生成，例外被吞掉，加上導引命中偏移會讓爆心偏離目標格。
  改為測試開始就生成並麻醉（不會走開），並以新增的 `Projectile_MissileIncoming.LastImpactCell`（實際爆心）判斷是否在爆炸範圍內後，F10、L3 重跑 PASS。
  與 P0 基準相比沒有回歸；日誌中的錯誤只有既有雜訊（VoidMonolith／SteamGeyser 清場、D4 的 DropPodTrack 重複銷毀，P0 即有）。
  BVRTests 263 項、`check_missile_content.py`、產生器冪等性皆通過。
- **追加（2026-10-21）：攔截彈轉向＋發射錯開**
  - 攔截彈（SAM、單兵防空、世界層攔截的視覺升空彈）升空後轉向「來襲方位」並沿 speedCurve 飛出地圖邊緣，飛出時才結算（沿用離場巡航段）：
    導彈目標用它的進入方位；空投艙等沒有方位的目標用發射點到預測落點的方向；世界層攔截用發射據點到會合點的方位（`MissileProjectileMaker.IncomingBearing`／`WorldInterceptBearing`）。
  - `MapComponent_MissileLaunchQueue`：同一張地圖的升空彈至少相隔 6 tick 生成（對地發射、SAM、單兵防空都經它）。只錯開畫面上的生成：彈藥、冷卻、BVR 通道、世界航跡物件在發射當下處理；
    後焰在實際生成時觸發；等待中的發射隨地圖存檔；`ScorerProjectile_WorldObject.launchSpawnTick` 讓打擊時間軸計入延後。
  - 驗收 F11、F12，L3 加查「排程中的發射存讀檔後照常生成」。
- **剩餘未做**：發射建築已不存在時的「以陣營歸因」；中立陣營好感度扣減未驗證（測試世界沒有可用的中立陣營）；火砲改用 `MissileMapEntryUtility`（後續項目）。
- **P1 中確認的事實**（影響後續設計）：
  - 原版 skyfaller 的 `angle` 與世界方位是同一套羅盤角（`PosAtDist` 位移 = (sin, cos)），所以邊緣入射的墜落角＝進入方位，不需換算。
  - 舊版尾焰位置取自「上一次繪製」的快取，Ultrafast 下最多落後 23.7 格；新版每 tick 依時間軸重算。這是刻意的差異：1× 速度下兩者相同，高速時新版的尾焰會貼著彈體。

### P0　基準
1. 完整 BVRAcceptance 一次，保存 log/TSV。
2. 各平台（傾斜發射箱、巡飛軌、彈道井、Security、旋轉 SAM、單兵 SAM）的升空畫面與 3 種落地彈錄影／截圖，作為升空段比對基準。

### P1　基礎設施（零行為變化）
1. `MissileFlightExtension`、`IMissileFlight`、`Projectile_MissileBase`（先只做升空段）、`MissileProjectileMaker`。
2. `CompEffecterLaunch` 改走 `IMissileFlight`；舊 `ScorerProjectile`/`InterceptProjectile` 暫時也實作，確認特效不變。
3. `MissileFlightTrace` 追蹤器＋§8.6 新舊並行比對測試：此時新類別只有升空段，**4 朝向 × 5 def 必須逐 tick 等值**才進 P2。
4. `MissileMapEntryUtility`＋§5.3 的純數學測試（此階段就能跑，不需進遊戲），含 heading → skyfaller `angle` 的換算。

### P2　攔截彈與離場彈
1. `Projectile_MissileInterceptor`（只有升空段，畫面應與 P0 一致）。
2. `Projectile_MissileDeparture`：升空段＋轉向＋巡航段＋出邊緣 → 世界航跡；`TryLaunchRound` 寫入 `launcherThing` 與出發方位。
3. `DepartureTicksTo(target)` 與打擊時間軸三處呼叫。
4. Def：離場 4 個、攔截 3 個改父節點。
5. 驗證：§8.6 交接段連續性斷言；升空段與 P0 錄影比對；離場方向與大地圖航跡起始方向比對；S 系列（時間軸）、M/G 系列（攔截）。

### P3　落地彈＋BVR
1. `Projectile_MissileIncoming`：入射段＋俯衝段＋`HitRoof`＋Impact（搬移現有爆炸邏輯）＋打擊回報；`IBVRPayload`。
2. `MissileWorldObject.LastLegStart/DestLayer`，兩個 `OnArrived` 改用 `SpawnIncoming(…, entry)`。
3. §4 BVR 泛化，含提早釋放以維持落地時刻。
4. Def：16 個 incoming 改父節點，調整 `entryAltitude` 與交接時機；終端墜落段與舊版（同 angle）逐 tick 比對（§8.6）。
   `CompEffecterCruiseTrail` 實作但預設不開。
5. 驗證：T/K/W/L 系列、空投艙攔截（原版路徑不受影響）、薄頂/厚岩頂落點、§5.3 E 系列方位驗收。

### P4　高角護盾與歸因
1. `MissileShieldUtility`、`DamageAmount` 覆寫、碰觸點引爆、`StrikeOutcome.ShieldBlocked`（含報告 UI 與翻譯）。
2. instigator 傳遞（`WarheadEffect/PayloadEffect.Apply` 加參數）、無建築時的陣營好感度處理。
3. 驗證：
   - 高角護盾擋下並在圓周爆炸、扣護盾 HP、打破後下一枚能穿過；地面護盾不攔。
   - 己方高角護盾不攔己方導彈；中立陣營護盾的行為依敵對判定。
   - 命中中立據點 pawn 的好感度變化、無紅字（跨地圖 instigator）。

### P5　存檔相容
1. 舊類別薄殼＋轉換佇列；`BVRTarget` PostLoadInit 轉換。
2. 驗證：改版前的存檔（離場中、BVR 窗口中、攔截彈飛行中、落地前各一份）載入無紅字且結果正確；新存檔在每一段飛行中存讀各一次。

### P6　清理與工具
1. `Patch_MakeDropPodAt` 移除 `InterceptProjectile` 特例。
2. `TransferReadiness:184` 補 `!(t is Projectile)`；`BVRFixture.ClearCell` 同時清 `Projectile_MissileBase`。
3. Def 轉換腳本 `.source/Tools/convert_missile_skyfaller_defs.py`（可重複執行）。
4. `check_missile_content.py`：驗 thingClass、`MissileFlightExtension` 存在、reversed 與用途相符、`projectile.speed` 與 `cruiseSpeed` 一致。
5. 測試更新（Tests_Strike 695/699、找攔截彈的型別、BVRTests 假資料），新增 E 系列與護盾／歸因測試。
6. 文件：`BVR_Missile_系統流程.md` 更新生成流程、方位與護盾規則。

### P7　總驗收
- 完整 BVRAcceptance 對照 P0，差異逐條解釋（預期差異：落地時刻估算、新增的 ShieldBlocked）。
- 升空段畫面比對（尾焰、冷射延遲、熱射爆發）。
- 存讀測試（P5 清單），Dev 模式無新的 `ERROR [Verse]`。

---

## 11. 風險與注意事項

- **BVR 共用空投艙路徑**：泛化錯誤會影響原版敵方空投攔截，P3 必須單獨驗證空投艙。
- **`landed = true` 依賴原版實作細節**：1.7 更新若改動 `Projectile.TickInterval` 需重新確認；驗收保留「畫面外飛行仍準時」的測試。
- **地圖內飛行時間變長**：BVR 提早釋放（§4-3）的計算若出錯，會改變末端窗口與打擊時間軸——以「預測落地 tick 與實際落地 tick 相同」做斷言。
- **導彈在地圖內可見的時間變長**：屬 `ThingRequestGroup.Projectile`，DMS 本體或第三方的點防禦可能開始瞄準；需掃描 DMS 系列與常見模組，必要時加排除。
- **跨地圖 instigator**（§7）：最可能出現意外紅字的地方，P4 優先測。
- **私有欄位存取**：`debugInterceptNonHostileProjectiles` 是 private，用 `AccessTools.FieldRefAccess` 並在找不到欄位時降級為「無建築的導彈不受護盾攔截」且只警告一次。
- **ThingDef 驗證**：`category=Projectile` 缺 `projectile` 節點會報錯；轉換腳本保證每個 def 都有。defName 結尾不可為數字。
- **子模組／Patch**：搜尋 `1.7/Patches` 與 GravshipExpanded 是否以 XPath 修改 `<skyfaller>` 節點或繼承這些 def。
- **後續項目（不在本次範圍）**：火砲改用 `MissileMapEntryUtility`；攔截彈飛向來襲方向（目前目標不在地圖內，不做）。
