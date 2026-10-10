# The Dead Man's Switch - Expedition (DMSE)

> 開發文檔：https://docs.qq.com/doc/DRnhtVGJYVU5SUndD
> 遠程作戰系統技術說明：[docs/BVR_Missile_系統流程.md](docs/BVR_Missile_系統流程.md)
> 轉移飛行重構規劃（設計稿）：[docs/CelestialTransfer_轉移飛行重構.md](docs/CelestialTransfer_轉移飛行重構.md)

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
擴充 Odyssey 引力船駕駛台。安裝「核融合爐」與「轉移推進器」後，啟動時彈出飛行模式選單：

- **Regular flight**：原版跳躍。
- **Transfer flight**：長程轉移（`WorldObject_Transfer`），以燃料半徑限制目的地。
- **Impact flight（Hellfire）**：核融合爐超載、以船體撞擊地表作為動能武器（`WorldObject_ImpactGravship`）。撞擊後生成「衝擊坑荒原」生態群系（`DMSE_ImpactCraterBiome`）與徑向礦脈，並依撞擊規模觸發不同結局文本。相關參數可於 Mod 設定（`PlayerConfigSettings`）調整。

同時提供引力船元件：核融合爐、轉移推進器、固體火箭助推器、核熱推進器、燃料筒倉、太陽輻射護盾、訊號隱匿裝置。

### 5. 敵方導彈發射陣地（Missile Base）
世界地圖任務地點（`DMSE_MissileBase` / `DMSE_MissileBaseSitePart`）：敵方機動彈道導彈陣地，持續向殖民地發射導彈並透過補給空投補彈，一段時間後轉移位置。含專用 GenStep（地形、FFF 結構、駐守人員、戰爭迷霧）與 `LordJob_MissileCrewDuty`。

### 6. 真空製造與軌道環境
- **真空密封室**：`MapPortal` 地下密室，可在大氣層內維持真空；搭配真空泵、氧氣蠟燭；廢棄後可回填（`CompLandFillable`）。
- **軌道平台地形**（`Terrain_OrbitalPlatform_DMS`）與微重力設施。
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
| `CelestialTransfer/` | 飛行模式選單、轉移 / 撞擊 WorldObject、`MapComponent_Ship`、VGE 相容 |
| `CelestialTransfer/Hellfire/` | 衝擊坑世界生成、礦脈 GenStep、Mod 設定 |
| `MissileBase/` | 敵方導彈陣地 SitePart、GenStep、LordJob、Debug |
| `VacuumRoom/` | 真空室 Prefab GenStep、回填 |
| `Renameable/` | 建築重新命名 Comp 與 UI |
| 根目錄 | `Building_MissileRack`、石墨烯電池、光伏板、氧氣蠟燭、真空泵、耐久衰減、`ScenPart_Huntdown`、Harmony 入口 `PatchMain` |

`.source/GravshipExpanded` 為 VGE 相容專案（`Patch_VGE_Compatibility`、`VGEFuelHandler`），將 Gravjumper / Gravhulk 引擎接入轉移設施與燃料筒倉。

### 建置
以 Visual Studio 開啟 `.source/DMSE.sln`，參考組件放於 `.source/DMSE/bin/Debug/`（`Assembly-CSharp`、`0Harmony` 等，已被 `.gitignore` 排除）。輸出 DLL 需複製至 `1.6/Assemblies/`（主專案）與 `1.6/GravshipExpanded/Assemblies/`（VGE 專案）。

---

## 相容性

| Mod | 處理方式 |
|---|---|
| Combat Extended | 新增 5x50mm 無殼彈、20x102mm NATO HV、7.62x38.5mm R-Cannon HV 彈藥與 PELE 彈種；裝備 / 砲塔 / 武器 CE 數值 patch |
| Cinders | 5x50mm 無殼彈配方與彈藥 patch |
| Vanilla Gravship Expanded | 引擎連結設施、燃料筒倉改為 Astrofuel、核熱推進器 patch |
| Dead Man Switch (Core) | 元件說明超連結、`DMS_Operate` 工作、玩家穿梭機 patch |
