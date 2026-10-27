# The Dead Man's Switch - Expedition (DMSE)

> 開發文檔：https://docs.qq.com/doc/DRnhtVGJYVU5SUndD
> 遠程作戰系統技術說明：[docs/BVR_Missile_系統流程.md](docs/BVR_Missile_系統流程.md)
> 導彈與雷達科技重整執行狀態：[docs/導彈與雷達科技重整_執行計畫.md](docs/導彈與雷達科技重整_執行計畫.md)
> 天體轉移飛行設計與實作進度：[docs/CelestialTransfer_轉移飛行重構.md](docs/CelestialTransfer_轉移飛行重構.md)
> 三代裝備體系（老資歷／中登／小登）設計與實作計畫：[docs/三代裝備體系_實作計畫.md](docs/三代裝備體系_實作計畫.md)
> 三代裝備物品介紹文本：[docs/三代裝備體系_物品介紹.md](docs/三代裝備體系_物品介紹.md)

《Dead Man's Switch》的 Odyssey 擴充。以太空 / 軌道 / 遠程作戰為主題，在 DMS Core 的基礎上加入超視距攔截、導彈裝配與發射、天體轉移飛行、真空製造與軌道平台等內容。

| 項目 | 內容 |
|---|---|
| packageId | `Aoba.DeadManSwitch.Expedition` |
| 版本 | 1.7：開發版；1.6：凍結在 Steam 創意工坊版 0.35（2026-04-20 發佈） |
| 作者 | AobaKuma、Bread Mo、GoGaTio、Mortis |
| 必要前置 | RimWorld **Odyssey** DLC、[Dead Man Switch (Core)](https://steamcommunity.com/sharedfiles/filedetails/?id=3121742525) |
| 載入順序 | 於 `Aoba.DeadManSwitch.Core` 之後 |

---

## 目錄結構

```
_Dead-Man-Switch-Expedition/
├── About/                  About.xml、Mod 圖示
├── LoadFolders.xml         各版本的載入資料夾與條件式載入（CE / Cinders / Gravship Expanded）
├── Sounds/                 音效（1.6、1.7 共用，兩版內容相同）
├── 1.6/                    凍結：Steam 創意工坊版 0.35 的 1.6 內容，與 Steam 版逐位元組相同（含 .NET Framework 的 DMSE.dll）
│   └── Shared/             該版根目錄的 Textures/、Languages/，只有 1.6 載入
├── 1.7/                    開發版的全部內容
│   ├── Assemblies/         DMSE.dll（net10.0）
│   ├── Defs/               所有 Def（見下方「內容一覽」）
│   ├── Patches/            對原版 / DMS Core 的 PatchOperation
│   ├── Textures/、Languages/  美術資源；English / 繁體中文 / 简体中文
│   ├── CE/                 Combat Extended 相容（彈藥、AmmoSet、裝備 / 砲塔 patch），CE 尚無 1.7 版，暫不載入
│   ├── Cinders/            BreadMo.Cinders 相容（5x50mm 無殼彈，依賴 CE），暫不載入
│   └── GravshipExpanded/   Vanilla Gravship Expanded 相容（Assemblies/DMSE_VGE_Patch.dll + patch；PointDefence/ 為近防相容）
├── docs/                   系統設計文件
├── .source/                C# 原始碼（DMSE.sln），只建置 1.7
│   ├── DMSE/               主專案 → 1.7/Assemblies/DMSE.dll
│   └── GravshipExpanded/   VGE 相容專案 → 1.7/GravshipExpanded/Assemblies/DMSE_VGE_Patch.dll
└── _to_delete/             已遷移至 DMS Core 的舊內容（無人機、超重型 Automatroid、Boss 等），待清除
```

### 條件載入（LoadFolders.xml）

| 版本 | 依序載入 |
|---|---|
| 1.6 | `/`、`1.6/Shared`、`1.6`，再依模組條件載入 `1.6/CE`、`1.6/Cinders`、`1.6/GravshipExpanded`（與 Steam 版相同） |
| 1.7 | `/`、`1.7`，Gravship Expanded 啟用時加 `1.7/GravshipExpanded`，未啟用 CE 時再加 `1.7/GravshipExpanded/PointDefence` |

- 1.6 與 1.7 的內容完全分開：1.7 不讀 `1.6/`，1.6 不讀 `1.7/`；根目錄 `/` 只提供兩版相同的 `Sounds/`。
- `1.6/Shared` 緊接在 `/` 之後，也就是 Steam 版根目錄 `Textures/`、`Languages/` 的位置，所以 1.6 的載入順序與 Steam 版相同。
- Combat Extended 尚無 1.7 版，1.7 不載入 `1.7/CE` 與依賴它的 `1.7/Cinders`；CE 推出 1.7 後，驗證補丁再加進 `v1.7`。

### 1.6 凍結規則

- `1.6/`（含 `1.6/Shared`）與根目錄 `Sounds/` 不再修改，新內容一律放在 `1.7/`。真的要給 1.6 玩家的緊急修正，改完後必須重新確認與 Steam 版的差異只有該修正。
- 1.6 的開發版內容封存在標籤 `dmse-1.6-dev-sealed`（`446d760`），已原樣搬到 `1.7/`。
- 驗證 1.6 仍與 Steam 版相同（應無輸出）：
  ```bash
  WS="D:/Steam/steamapps/workshop/content/294100/3689879335"
  diff -rq -x Shared "$WS/1.6" 1.6; diff -rq "$WS/Textures" 1.6/Shared/Textures; diff -rq "$WS/Languages" 1.6/Shared/Languages; diff -rq "$WS/Sounds" Sounds
  ```

---

## 核心系統

### 1. 超視距攔截系統（BVR）
偵測並攔截敵方來襲空投與導彈的多層防空鏈。所有裝置皆為 `ThingComp`，繼承 `CompBVRDevice`，共用電力 / 損壞 / 開關 / 擊暈 / 操作員的可用性判定；雷達、SAM、CIWS 向 `MapComponent_BVRCombat` 註冊。

| 角色 | Comp | 建築 |
|---|---|---|
| 搜索雷達（決定預警窗口） | `CompSearchRadar` | 被動聲學雷達（不用電、免疫反輻射）、搜索雷達、野戰防空砲（短程）、相控陣雷達 |
| 火控雷達（火力通道、鎖定、命中率） | `CompFireControlRadar` | 老式火控雷達（需人操作，1 通道）、火控雷達（4）、野戰防空砲（需人操作，2）、相控陣雷達（24） |
| 中過程攔截（SAM） | `CompMissileLauncher_Interceptor` | 防空導彈發射架（四聯裝，需人操作）、旋轉發射架（需人操作）、軌道發射架、野戰防空砲、傾斜發射箱、小型垂直發射井；彈藥為容器中的實體導彈，只取攔截／兩用彈 |
| 單兵防空 | `CompManPortableSAM` | 防空導彈發射具（腰帶層 Apparel，原版裝填刺針，不占火控通道） |
| 末端攔截（CIWS） | `CompTerminalCIWS` | GAU-4 "Wing-Back"、老式防空砲 Flak 37（需砲手）、野戰防空砲機砲、先進防空砲「千禧年」 |

流程：**偵測 → 波次合併 → 火控鎖定 → SAM 中過程攔截 → CIWS 末端連射 → 未攔截者落地**。攔截判定考慮隱身 / 反隱身、目標速度、攔截彈彈體係數（`interceptHitFactor`：萊茵女兒 0.6、刺針 0.85、麻雀 1.25）、戰鬥部加成（連續桿、空爆）。防禦為陣營相對：玩家攻擊有雷達的敵方基地時，己方導彈同樣會被攔截。

**三代裝備體系**（老資歷／中登／小登）：裝備依人力、電力、通道三條軸分成三代，名冊、數值、研究與驗收見 [三代裝備體系_實作計畫](docs/三代裝備體系_實作計畫.md)，資訊卡文本見 [物品介紹](docs/三代裝備體系_物品介紹.md)。

### 2. 導彈自定義裝配系統
玩家在導彈物品的 ITab 中選擇部件（戰鬥部 / 導引 / 推進 / 酬載），由小人搬料、施工裝配（Crafting 工種）；彈體由發射平台或物品 def 的 `defaultBody` 固定，物品端不可更換；固定構型的物品（V1、白蟻、萊茵女兒）以 `MissileItemExtension` 宣告彈體，同一條發射軌可裝不同型號。彈道導彈沒有物品型態，所以在**塔式發射架／戰略導彈發射井**的裝配分頁選彈體（老式 V2 / 圓點U / 現役 / 先進民兵三型，共用同一組部件；各彈體有研究門檻，未研究的在選單上顯示為鎖定）。射程由 `燃料 × 比衝` 決定，發射時於世界地圖選靶並強制受射程限制。

彈道平台有兩種：3x3 封閉式**戰略導彈發射井**（`DMSE_Building_BallisticSilo`，發射不波及周圍），以及 2x2 開放式**塔式發射架**（`DMSE_Building_BallisticRack`，研究「基本導彈工程」即可建造，預設 V2）。基本導彈工程同時解鎖 V2 彈體與彈道儲存架；塔式發射架在彈體裝配完成後會在桁架中央畫出該型號的直立彈體（換彈體就換圖），點火時尾焰與衝擊波（`CompLaunchBackblast`）燒傷、震懾半徑 3.5 格內的人並點燃可燃物，不傷建築。

平台能裝什麼由建築的 storage filter 決定；`MissileLauncherExtension` 的 `sizeClass`／`extraSizeClasses` 必須與 filter 一致（`check_missile_content.py` 第 8 條）。攔截器只取攔截／兩用彈、對地發射只取非攔截彈（`MissileRoleExtension`）。

| 彈體 | 戰鬥部 | 導引 | 推進 | 酬載 | 建造時自帶 | 發射平台 |
|---|---|---|---|---|---|---|
| 老式巡飛彈（LegacyLoiter，V1） | — | 內建電視（只選點） | — | — | 固定構型 | 軌道發射架、旋轉發射架 |
| 老式攔截巡飛彈（LegacyInterceptor，萊茵女兒） | — | — | — | — | 固定構型（攔截係數 0.6） | 軌道發射架、旋轉發射架 |
| 反艦巡飛彈（Loiter，白蟻） | — | 內建反艦（具導引能力） | — | — | 固定構型 | 軌道發射架、旋轉發射架 |
| 野戰防空導彈（Light，SA-19） | HE / 燃燒 / 連續桿 | 慣性 / 紅外 / 反輻射 / 雷射 | — | 無 / 高爆 / 誘餌 / 毒氣 / 箔條 | HE＋慣性＋無 | 傾斜發射箱、小型垂直發射井、旋轉發射架、防空架、野戰防空砲 |
| 輕型防空導彈（Stinger，刺針） | 同 SA-19 | 同 SA-19 | — | — | HE＋紅外 | 同上＋單兵發射具（背包） |
| 對地攻擊導彈（Maverick，小牛） | 同 SA-19 | 同 SA-19 | — | 同 SA-19 | HE＋紅外＋無 | 傾斜發射箱、小型垂直發射井、旋轉發射架 |
| 先進防空導彈（Sparrow，麻雀） | 同 SA-19 | 同 SA-19＋半主動 | — | 同 SA-19 | 連續桿＋半主動＋無 | 傾斜發射箱、小型垂直發射井、旋轉發射架、防空架 |
| 巡航導彈（Cruise） | HE / AP / EMP | 慣性 / 電視 / 電磁 / 雷射 / 反艦 | 標準 / 增程 | 無 / 集束 / EMP | HE＋慣性＋標準＋無 | Scorer MLS、巡航導彈架（舊存檔的白蛉） |
| 超音速反艦（Moskit，白蛉） | 同巡航 | 同巡航 | 同巡航 | 同巡航 | HE＋反艦＋標準＋無 | 傾斜發射管、垂直發射井、Scorer |
| 高超音速反艦（Yj，鷹擊） | 同巡航 | 同巡航 | 同巡航 | 同巡航 | HE＋反艦＋標準＋無 | 傾斜發射管、垂直發射井、Scorer |
| 彈道導彈（Ballistic） | 熱核 / 空爆 / 鎢芯穿甲 | 慣性 / 火控 / 雷射 | — | 無 / 集束 / 反粒子 | — | 戰略導彈發射井、塔式發射架 |
| 老式彈道（LegacyBallistic，V2） | 阿馬托 / 特里亞倫 / 同現役彈道部件 | 同現役彈道部件 | — | 同現役彈道部件 | — | 發射井 / 塔式（就地切換） |
| 戰術彈道（Tochka，圓點U） | 同現役彈道部件 | 同現役彈道部件 | — | 同現役彈道部件 | — | 發射井 / 塔式（就地切換） |
| 先進彈道（AdvancedBallistic，民兵三型） | 熱核 / 低當量 / 空爆 / 鎢芯 | 慣性 / 星光 / 火控 / 雷射 | — | 無 / 突防誘餌 / 分導 / 集束 / 反粒子 | — | 發射井 / 塔式（就地切換） |
| 先進通用（AdvancedLight，地獄火） | 串聯成型 / 破片 / 溫壓 / 動能刀刃 | 慣性 / 影像紅外 / 雷射 / 毫米波 | — | 無 / 高爆 / 破片套筒 / 箔條 | HEAT＋慣性＋無 | 傾斜發射箱、小型垂直發射井、旋轉發射架、飛彈架 |
| 先進巡航（AdvancedCruise，戰斧） | 單一穿透 / 深穿透 | 地形比對 / 雙向資料鏈 / 反艦 | 渦輪扇 / 增程渦輪扇 / 助推 | 無 / 子彈藥 / 石墨 / 誘餌 / 箔條 | 深穿透＋地形比對＋渦輪扇＋無 | 傾斜發射箱、小型垂直發射井、Scorer、巡航導彈架 |

**建造時自帶零件**：**物品型態**的彈體在 `MissileBodyDef.defaultParts` 宣告一組預設零件，新造的導彈物品會直接套用（`MissileConfig.NewWithDefaults()`）；零件材料已併入該物品的 `costList` 與 `MarketValue`。**發射井／發射架建造完成時是空的**（不帶彈體），彈道彈體都在井／塔上裝配、材料依所選零件另計。之後換部件或發射後重新裝填也要另外收費。舊存檔不會白送零件（`CompMissileConfig.defaultsSeeded` 讀檔時視為已播種）。

**世界層 P4**：
- **數據鏈**：同陣營任一節點看到航跡，全陣營都能接戰（不必是目的地節點自己看到）。
- **區域防禦**：節點可攔截「飛越自己覆蓋範圍」的航跡；目的地節點優先，其餘用通用會合求解（`BVRInterceptSolver.TrySolveGeneral`，對交叉目標二分求解）。
- **一般據點自動防空**：沒有專屬防空元件的敵方據點，依科技等級取得合成防空（Industrial 6 格／1 通道／2 發；Spacer 以上 8 格／2 通道／4 發），庫存與冷卻持久化。
- **空投虛擬航跡**：`podsUseWorldTracks` 開啟時，空投在世界層也有一條幽靈航跡（從覆蓋圈邊緣飛入、世界層可攔截），地圖內只留末端 CIWS；預設關閉。
- **未偵測航跡隱藏**：敵方航跡在被玩家雷達偵測前不顯示、不可選取（`HiddenByRules` patch ＋ `Draw`／`ExpandingIcon`／`SelectableNow` 覆寫）。
- **反輻射削弱抽象節點**：反輻射彈命中尚未生成地圖的防空節點時，打掉 2 發庫存並壓制雷達一天，不生成地圖。

**高度與反應時間**：來襲目標的 `BVRTargetProps.altitude` 以雷達視距近似（`sqrt(高度/1000)`，夾在 0.6–1.3）縮放偵測距離與預警窗口。既有四種來襲目標維持 1000（中性，不影響既有平衡）；差異加在世代化彈體上——地獄火 150 m、白蛉 60 m、戰斧與鷹擊 50 m 極低空（更晚被發現），V2／民兵三 30000–100000 m（更早被發現）。速度仍只影響鎖定時間與命中率。

**反艦**：`GuidanceType_AntiShip` 針對移動中的世界地圖據點（轉移艦、會搬遷的陣地）：終端檢查間隔 60→20 ticks、額外 2 次修正，目標離開剩餘射程仍會落空。目標移動由世界層每 60 ticks 觀測 tile 變化推導，不 patch 原版。

**箔條／干擾絲**：`jamStrength`／`chaffCharges` 為有限次的主動干擾。世界層在敵方火控鎖定時擲斷鎖機率（成功則延後重新鎖定），地圖層只折減命中率；發射時裝填、飛行中不補充。

內容驗證（不啟動遊戲）：
```bash
python .source/Tools/check_missile_content.py .   # 槽位覆蓋、預設零件、建造成本、收納、高度、Class 參照、翻譯
python .source/Tools/check_bvr_world.py .
dotnet run --project .source/DMSE.BVRTests/DMSE.BVRTests.csproj -c Release --no-restore -p:RimWorldDir=<RimWorld>/
```

發射平台共用抽象基類 `CompMissileLauncher`（冷卻、有彈判定）；世界選靶平台再繼承 `CompMissileLauncher_WorldTargeting`，分為 `CompMissileLauncher_Rail`（實體導彈容器）、`CompMissileLauncher_Ballistic`（發射井，就地裝配）、`CompMissileLauncher_Scorer`（CompRefuelable 裝填，1.7 無 Def 使用）。反輻射導引頭會自動鎖定敵方運作中的雷達，與 BVR 構成電子對抗閉環。

世界導彈的偵測與 SAM 中過程攔截現由世界層處理：雷達覆蓋圈 → 火控鎖定 → 實體攔截彈升空 → 世界會合，漏網彈抵達後只交給 CIWS。玩家導彈也會受到未生成地圖的敵方導彈陣地抽象防空攔截。速度與平衡數值集中在 `1.7/Defs/DMSE_BVRWorld.xml`（預設 150 ticks/格）；覆蓋圈預設選取據點時顯示，可用世界 Gizmo 或 Mod 設定改為常駐。空投／舊地圖內波次保留原流程，與世界航跡、火砲共用通道。P0–P3 程式與自動檢查已完成，遊戲內驗收待進行；細節見 [BVR 重構與驗收記錄](docs/BVR_世界層偵測攔截重構.md)。

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

平衡常數集中在 `DMSE_TransferFlightTuning`（`1.7/Defs/DMSE_TransferFlightTuning.xml`）。舊版「駕駛台 → 飛行模式選單」的轉移已移除；飛行中的舊存檔讀檔後會直接抵達（撞擊則直接結算）。

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
- 研究樹：早期預警與人工火控 → 基本導彈工程／雷達探測網 → 彈道平台／火控與攔截導引／精準導引與戰術導彈 → 戰略投送／相控陣與整合防空；NPC 世界地圖防空圈由雷達探測網研究啟用。另有軌道動力學、航太應用、反艦導彈等旁支；獨立研究分頁（`KnowledgeCategoryDef`）。

---

## C# 原始碼結構（`.source/DMSE`）

| 目錄 | 內容 |
|---|---|
| `SkyFallerTurret/BVR/` | BVR 核心：`CompBVRDevice`、雷達、SAM（`CompMissileLauncher_Interceptor`、人工操作裝填）、CIWS、`MapComponent_BVRCombat`、攔截特效 |
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
兩個專案都是 SDK 格式，只建置 RimWorld 1.7（`net10.0`，定義 `RW_1_7`；需要 .NET 10 SDK）。參考遊戲根目錄的 `Assembly-CSharp.dll`、`UnityStubs.dll`、`Waylith.dll` 與 NuGet `Lib.Harmony 3.0.0-rc.2`，輸出到 `1.7/Assemblies/`、`1.7/GravshipExpanded/Assemblies/`。1.6 的 DLL 是凍結的 Steam 版，不再建置。

```bash
dotnet build .source/DMSE/DMSE.csproj -c Release
dotnet build .source/GravshipExpanded/DMSE_VGE.csproj -c Release
```

- 先建置主專案：VGE 專案參考 `1.7/Assemblies/DMSE.dll`。
- 直接參考遊戲本體的 DLL，預設模組位於 `<RimWorld>/Mods/<本模組>/`；遊戲裝在別處時加 `-p:RimWorldDir=<遊戲目錄>\`。遊戲執行中不要建置（DLL 會被鎖住）。
- VGE 專案另外參考 `VanillaGravshipExpanded.dll`（預設找 `<RimWorld>/Mods/VanillaGravshipExpanded`，再找 Steam 創意工坊 3609835606；其他位置用 `-p:VGEAssemblies=<VGE>\1.6\Assemblies\`）與 VEF 的 `PipeSystem.dll`（同一 `Mods` 資料夾下的 `VanillaExpandedFramework`）。VGE / VEF 目前只有 1.6 版，由 1.7 的相容層載入。
- 需要分版本的程式碼用 `#if RW_1_7`。1.7 使用 Harmony 3：補丁的 `__instance` / `__result` 型別必須與目標一致，一個補丁類別失敗會讓 `PatchAll` 中止其後所有補丁。
- 上傳創意工坊時可勾選「建立資源包」（`Assets.zip`，已被 `.gitignore` 排除），但**不要勾「略過原始貼圖」**：1.6 讀不懂 `Assets.zip`。

---

## 相容性

| Mod | 處理方式 |
|---|---|
| Combat Extended | 新增 5x50mm 無殼彈、20x102mm NATO HV、7.62x38.5mm R-Cannon HV 彈藥與 PELE 彈種；裝備 / 砲塔 / 武器 CE 數值 patch |
| Cinders | 5x50mm 無殼彈配方與彈藥 patch |
| Vanilla Gravship Expanded | 燃料筒倉與固體火箭助推器可連 Gravjumper / Gravhulk 引擎，聚變反應爐可連 Gravhulk；燃料筒倉改為 Astrofuel 儲存槽，並可作為轉移燃料來源；聚變反應爐、核熱推進器加入 VGE 維護 |
| Dead Man Switch (Core) | 元件說明超連結、`DMS_Operate` 工作、玩家穿梭機 patch |
