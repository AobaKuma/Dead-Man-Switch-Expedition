# The Dead Man's Switch - Expedition (DMSE)

> 開發文檔：https://docs.qq.com/doc/DRnhtVGJYVU5SUndD
> 遠程作戰系統技術說明：[docs/BVR_Missile_系統流程.md](docs/BVR_Missile_系統流程.md)
> 天體轉移飛行設計與實作進度：[docs/CelestialTransfer_轉移飛行重構.md](docs/CelestialTransfer_轉移飛行重構.md)

《Dead Man's Switch》的 Odyssey 擴充。以太空 / 軌道 / 遠程作戰為主題，在 DMS Core 的基礎上加入超視距攔截、導彈裝配與發射、天體轉移飛行、真空製造與軌道平台等內容。

| 項目 | 內容 |
|---|---|
| packageId | `Aoba.DeadManSwitch.Expedition` |
| 版本 | 0.35（支援 RimWorld 1.6） |
| 作者 | AobaKuma、Bread Mo、GoGaTio、Mortis |
| 必要前置 | RimWorld **Odyssey** DLC、[Dead Man Switch (Core)](https://steamcommunity.com/sharedfiles/filedetails/?id=3121742525) |
| 載入順序 | 於 `Aoba.DeadManSwitch.Core` 之後 |

---

## 目錄結構

```
_Dead-Man-Switch-Expedition/
├── About/                  About.xml、Mod 圖示
├── LoadFolders.xml         條件式載入（CE / Cinders / Gravship Expanded）
├── 1.6/
│   ├── Assemblies/         DMSE.dll（主程式）
│   ├── Defs/               所有 Def（見下方「內容一覽」）
│   ├── Patches/            對原版 / DMS Core 的 PatchOperation
│   ├── CE/                 Combat Extended 相容（彈藥、AmmoSet、裝備 / 砲塔 patch）
│   ├── Cinders/            BreadMo.Cinders 相容（5x50mm 無殼彈）
│   └── GravshipExpanded/   Vanilla Gravship Expanded 相容（獨立 DLL + patch）
├── Languages/              English / 繁體中文 / 简体中文
├── Textures/、Sounds/      美術與音效資源
├── docs/                   系統設計文件
├── .source/                C# 原始碼（DMSE.sln）
│   ├── DMSE/               主專案 → 1.6/Assemblies/DMSE.dll
│   └── GravshipExpanded/   VGE 相容專案 → 1.6/GravshipExpanded/Assemblies/DMSE_VGE_Patch.dll
└── _to_delete/             已遷移至 DMS Core 的舊內容（無人機、超重型 Automatroid、Boss 等），待清除
```

### 條件載入（LoadFolders.xml）

| 資料夾 | 觸發條件 |
|---|---|
| `1.6/CE` | `CETeam.CombatExtended` |
| `1.6/Cinders` | `BreadMo.Cinders` |
| `1.6/GravshipExpanded` | `vanillaexpanded.gravship` |

---

## 核心系統

### 1. 超視距攔截系統（BVR）
偵測並攔截敵方來襲空投與導彈的多層防空鏈。所有裝置皆為 `ThingComp`，繼承 `CompBVRDevice`，共用電力 / 損壞 / 開關的可用性判定，並向 `MapComponent_BVRCombat` 註冊。

| 角色 | Comp | 建築 |
|---|---|---|
| 搜索雷達（決定預警窗口） | `CompSearchRadar` | 早期預警雷達（聲學，被動）、相控陣雷達 |
| 火控雷達（火力通道、鎖定、命中率） | `CompFireControlRadar` | 早期火控雷達 |
| 中過程攔截（SAM） | `CompMissileLauncher` | 導彈架 + 實體攔截彈 |
| 末端攔截（CIWS） | `CompTerminalCIWS` | GAU-4 "Wing-Back"、85 式多用途速射系統 |

流程：**偵測 → 波次合併 → 火控鎖定 → SAM 中過程攔截 → CIWS 末端連射 → 未攔截者落地**。攔截判定考慮隱身 / 反隱身、目標速度、戰鬥部加成（連續桿、空爆）。防禦為陣營相對：玩家攻擊有雷達的敵方基地時，己方導彈同樣會被攔截。

### 2. 導彈自定義裝配系統
玩家在導彈物品的 ITab 中選擇彈體與部件（戰鬥部 / 導引 / 推進 / 酬載），由小人搬料、施工裝配（Crafting 工種）。射程由 `燃料 × 比衝` 決定，發射時於世界地圖選靶並強制受射程限制。

| 彈體 | 戰鬥部 | 導引 | 酬載 | 發射平台 |
|---|---|---|---|---|
| 巡飛彈（Loiter） | — | — | — | 巡飛彈發射軌 |
| 輕型導彈（Light） | HE / 燃燒 / 連續桿 | 慣性 / 紅外 / 反輻射 | 無 / 高爆 / 毒氣 | 輕型導彈容器、VLS 發射單元 |
| 巡航導彈（Cruise） | HE / AP / EMP | 慣性 / 精確 | 集束 / EMP | Scorer MLS、旋轉發射器、巡航導彈架 |
| 彈道導彈（Ballistic） | 熱核 / 空爆 / 鎢芯穿甲 | 慣性 / 火控 | 無 / 集束 / 反粒子 | 彈道導彈發射井 |

發射器實作三型：`CompScorer`（CompRefuelable 裝填）、`CompMissileRailLauncher`（實體導彈容器）、`CompBallisticLauncher`（發射井）。反輻射導引頭會自動鎖定敵方運作中的雷達，與 BVR 構成電子對抗閉環。

### 3. 超視距火砲（Artillery）
`CompArtilleryStrike` 選靶後建立世界航跡，抵達時排程齊射，砲彈以 `Projectile` 從地圖邊緣飛入落點。程式碼已完成；建築 Def（`DMSE_ArtillerySystem.disabled`）**目前停用**。

### 4. 天體轉移飛行（Celestial Transfer）
把太空中的基地（小行星上的一角、軌道平台）整塊搬到同一軌道層的另一個位置。不依賴引力船與駕駛台；船在飛行中仍是一張正常運作的地圖。

1. **結構樁與鋼樑**：建造結構樁（3×3，覆蓋 23×23），以 Gizmo 對範圍內的其他樁下達鋼樑施工單，殖民者送料施工完成才算連結。經鋼樑相連的樁組成一艘船；船體範圍 = 各樁覆蓋範圍的聯集（不含虛空）。鋼樑在虛空處可見、地板下隱藏。
2. **引擎與燃料**：
   - 化學轉移引擎：前期，推力 3、燃耗 100%。
   - 核熱推進器：推力 12、燃耗 35%，每座聚變反應爐支援 2 具。
   - 燃料來自船上的燃料槽（原版化學燃料槽、DMSE 燃料筒倉；VGE 下為 Astrofuel）。燃料需求與樁的總質量成正比，推力決定能否起飛與點火時間。
3. **轉移飛行控制台**：在世界地圖選目的地，確認視窗顯示燃料、時間與整備檢查（範圍外人員、尾焰區、將被摧毀的跨界建築等）。之後啟動起飛程序：殖民者操作控制台 → 10 秒倒數（引擎暖機）→ 分離。
4. **分離與飛行**：船體範圍以相同座標搬到一張新的虛空地圖，原地圖留下其餘部分。飛行依序為離軌點火 → 滑行 → 入軌點火 → 抵達。只有點火階段有尾焰傷害、震動與穿梭機禁飛；滑行期殖民地完全照常，穿梭機可以往返（會追蹤移動中的船）。
5. **終結巡航（Hellfire）**：船上有 3 具以上運作中的核熱推進器時，控制台可規劃撞擊地表。船不減速、直接撞向目標，生成「衝擊坑荒原」生態群系（`DMSE_ImpactCraterBiome`）與徑向礦脈，並依撞擊規模觸發不同結局文本；4 具以上為行星殺手結局。相關參數可於 Mod 設定（`PlayerConfigSettings`）調整。

平衡常數集中在 `DMSE_TransferFlightTuning`（`1.6/Defs/DMSE_TransferFlightTuning.xml`）。舊版「駕駛台 → 飛行模式選單」的轉移已移除；飛行中的舊存檔讀檔後會直接抵達（撞擊則直接結算）。

同時提供引力船元件：固體火箭助推器、燃料筒倉、太陽輻射護盾、訊號隱匿裝置（聚變反應爐與燃料筒倉也可作為引力船設施）。

### 5. 敵方導彈發射陣地（Missile Base）
世界地圖任務地點（`DMSE_MissileBase` / `DMSE_MissileBaseSitePart`）：敵方機動彈道導彈陣地，持續向殖民地發射導彈並透過補給空投補彈，一段時間後轉移位置。含專用 GenStep（地形、FFF 結構、駐守人員、戰爭迷霧）與 `LordJob_MissileCrewDuty`。

### 6. 真空製造與軌道環境
- **真空密封室**：`MapPortal` 地下密室，可在大氣層內維持真空；搭配真空泵、氧氣蠟燭；廢棄後可回填（`CompLandFillable`）。
- **軌道平台地形**（`Terrain_OrbitalPlatform_DMS`）與微重力設施。另有輕型平台（`DMS_LightPlatform`，只承載輕型 / 中型建築）。不需 Orbital Platforms 模組即可在太空虛空上建造（`Patch_OrbitalPlatform.xml`）；Odyssey 分頁的「拆除平台」指令可把 DMS 平台與原版軌道平台、機械體平台拆回虛空並回收材料（`RemovablePlatformExtension`）。
- **環境限定配方**：潔淨室、微重力、真空（`DMSE_Recipe_*.xml`），對應材料：碳水化合物、鉭鎢合金、石墨烯、氮化鎵。
- 生產建築：精密製造中心、旋轉鍛造機、石墨烯生物反應器、有機組裝機；電力：石墨烯電池、光伏板。

### 7. 裝備與武器
- **無殼彈模組化槍械**（DMS Core 模組化系統）：突擊步槍、PDW、C95 / C95BP 手槍、MG93 輕機槍。
- **服裝**：制服 / 制服帽、IVA 太空衣、軌道遊騎兵頭盔、重型外骨骼。
- Dawnblade "Striker"、Squall 砲塔。

### 8. 劇本與研究
- 劇本 **The Deviant**（WIP）：殖民艦隊叛徒，駕駛偷來的引力船；含 `ScenPart_Huntdown` 追捕機制。
- 研究樹：真空化製程、雷達、巡飛彈、軌道動力學、碳合成、微重力製造、航太應用、彈道學、積體電路、戰略系統；獨立研究分頁（`KnowledgeCategoryDef`）。

---

## C# 原始碼結構（`.source/DMSE`）

| 目錄 | 內容 |
|---|---|
| `SkyFallerTurret/BVR/` | BVR 核心：`CompBVRDevice`、雷達、SAM、CIWS、`MapComponent_BVRCombat`、攔截特效 |
| `SkyFallerTurret/` | `InterceptProjectile`、空投偵測 patch、BVR 警報 |
| `Scorer/Assembly/` | 導彈 Def / Config / 部件效果、ITab、裝配與裝填 Job、`MissileIncoming`、`WorldObject_IncomingMissile` |
| `Scorer/`、`Scorer/Ballistic/` | 三型發射器 Comp、離場 / 世界航跡投射物、冷熱發射特效 |
| `Artillery/` | 火砲打擊 Comp、世界航跡、地圖排程器 |
| `CelestialTransfer/` | 數值模型（`TransferFlightMath`、Tuning Def）、整備檢查、船體勘查 |
| `CelestialTransfer/Piles/` | 結構樁、鋼樑施工（WorkGiver / JobDriver）、結構網路與船體範圍、鋼樑 MapDrawLayer |
| `CelestialTransfer/Console/` | 轉移飛行控制台、計畫視窗、ITab、操作員工作、警報 |
| `CelestialTransfer/Engines/`、`Fuel/` | 轉移引擎、聚變反應爐支援、燃料來源 provider |
| `CelestialTransfer/Ship/` | 分離（`ShipSeparationUtility`）、`WorldObject_TransferShip` 飛行狀態機、點火 / 暖機效果、穿梭機 patch、撞擊結局、起飛演出 |
| `CelestialTransfer/Legacy/` | 舊版駕駛台轉移飛行的存檔遷移殼（遷移期結束後刪除） |
| `CelestialTransfer/Hellfire/` | 衝擊坑世界生成、礦脈 GenStep、Mod 設定 |
| `MissileBase/` | 敵方導彈陣地 SitePart、GenStep、LordJob、Debug |
| `VacuumRoom/` | 真空室 Prefab GenStep、回填 |
| `Renameable/` | 建築重新命名 Comp 與 UI |
| 根目錄 | `Building_MissileRack`、石墨烯電池、光伏板、氧氣蠟燭、真空泵、耐久衰減、`ScenPart_Huntdown`、Harmony 入口 `PatchMain` |

`.source/GravshipExpanded` 為 VGE 相容專案（`VGEFuelHandler`）：把 VGE 的 Astrofuel 儲存槽（PipeSystem `CompResourceStorage`）註冊為轉移燃料來源，以化學燃料當量計（1 Astrofuel = 2）。

### 建置
以 Visual Studio 開啟 `.source/DMSE.sln`，參考組件放於 `.source/DMSE/bin/Debug/`（`Assembly-CSharp`、`0Harmony` 等，已被 `.gitignore` 排除）。輸出 DLL 需複製至 `1.6/Assemblies/`（主專案）與 `1.6/GravshipExpanded/Assemblies/`（VGE 專案）。VGE 專案另外參考 `VanillaGravshipExpanded.dll` 與 VEF 的 `PipeSystem.dll`（預設路徑為同一 `Mods` 資料夾下的 `VanillaExpandedFramework`）；VGE 裝在 Workshop 時以 `-p:ReferencePath=<VGE>\1.6\Assemblies` 指定。

---

## 相容性

| Mod | 處理方式 |
|---|---|
| Combat Extended | 新增 5x50mm 無殼彈、20x102mm NATO HV、7.62x38.5mm R-Cannon HV 彈藥與 PELE 彈種；裝備 / 砲塔 / 武器 CE 數值 patch |
| Cinders | 5x50mm 無殼彈配方與彈藥 patch |
| Vanilla Gravship Expanded | 燃料筒倉與固體火箭助推器可連 Gravjumper / Gravhulk 引擎，聚變反應爐可連 Gravhulk；燃料筒倉改為 Astrofuel 儲存槽，並可作為轉移燃料來源；聚變反應爐、核熱推進器加入 VGE 維護 |
| Dead Man Switch (Core) | 元件說明超連結、`DMS_Operate` 工作、玩家穿梭機 patch |
