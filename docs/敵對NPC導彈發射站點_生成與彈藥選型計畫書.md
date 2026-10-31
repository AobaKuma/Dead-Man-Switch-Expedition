# 敵對 NPC 導彈發射站點：生成與彈藥選型

適用：1.7。狀態：**提案，部分已實作**（工業級野戰發射場，見 §1）。盤點 2026-10-29。原始完整計畫書（含 12 個 SitePart 的繁中 label／description 草稿 XML）在 `docs/_archive/`。

## 1. 已實作（2026-10-31）

- **工業級野戰發射場** `DMSE_FieldLaunchSite`／`DMSE_FieldLaunchSitePart`（`1.7/Defs/WorldObjects_FieldLaunchSite.xml`；`.source/DMSE/MissileBase/GenStep_FieldLaunchSite.cs`）：以 `LaunchSite_Mid.xml` FFF 結構圍繞中央搜索雷達組合，難度 tier 0–3（`threatScale` 0.5／0.9／1.4）；`firstPool` 保證至少一具對地發射器。
- **生成**：storyteller ThreatBig 事件 `DMSE_FieldLaunchSiteDeployed`（`IncidentWorker_FieldLaunchSite`、`FieldLaunchSiteIncidentExtension`）：第 15 天起、間隔 ≥20 天、距殖民地 6–12 tile 且在射程內、全世界最多 1 個 NPC 導彈站；需玩家已研究 `DMSE_RadarNetwork`。
- **補給**：`CompProperties_MissileBase.supplyDrop`（只在敵方雷達運作時）；`rackSupply`（`MissileBaseRackSupply.cs`：`GenStep_MissileBaseRackFill` 生成裝填，空架 2 天補給艙，守軍以 `DMSE_LoadMissileLauncher` 搬上發射器，最多 2 人）。
- **守軍**：`LordJob_MissileStationCrew`＋`JobGiver_ManMissileStation` 操作己方 `CompMannable`（原版 `JobGiver_ManTurrets` 只會操作迫擊砲）。
- MapGen 改繼承原版 `MapCommonBase`；道路 `GenStep_FieldLaunchSiteRoads`。
- 坑：MapGeneratorDef 內的 GenStep 拿到 `parms.sitePart == null`，威脅點數改從 `MapGenerator.mapBeingGenerated.Parent as Site` 讀。
- 驗收 N1–N4。尚未做：雷達網前的低機率「舊式導彈站」事件、§3 的 doctrine／庫存帳本、搬遷、海上平台、其他 archetype。

## 2. 設計原則

1. **先警告、再打擊、可反制**：高階彈提升精度或射程時，頻率與數量相應下降。
2. **站點建立時選定一次 archetype＋doctrine 並存檔**，讀檔、切圖、Def 順序都不改變。
3. **世界與地圖一致**：未生成地圖時用 doctrine 預設配置；生成後用彈架實體導彈；共用庫存與冷卻。
4. **只選可發射的 Strike 彈**：檢查角色、彈體、發射架 storage filter／尺寸／模式、射程、目標圖層。攔截彈、反艦彈不進一般打擊清單。
5. **無合法彈時停火並記錄**，不以 `AllDefsListForReading.FirstOrDefault()` 當設定。
6. **圖示代表站點任務，不代表單枚彈種**；彈種與威脅等級由 Inspect、信函、來襲警告補充。

## 3. 舊站點（`DMSE_MissileBase`）的已知缺口

| 項目 | 現況 | 方向 |
|---|---|---|
| 世界層彈種 | `CompProperties_MissileBase.missileBody` 留空時取第一個 `MissileBodyDef`，且不套預設零件 | 建站時保存 doctrine 與預設 `MissileConfig` |
| 庫存 | 世界層無限；地圖層初始裝填後無導彈補給 | 單一庫存帳本＋有限補給 |
| 節奏 | 世界層 2–4 天、每波 2–3 枚；地圖層每 500 tick 依通道空閒，可能更快 | 共用 `nextFireTick` 與 `salvoLimit` |
| 彈架 | Scorer 彈架自動填 `DMSE_Missile_Moskit`（反艦彈） | 依 doctrine 精確裝填，略過通用 autofill |
| 搬遷 | 倒數後直接摧毀 | 搬到新合法 tile，保留狀態 |
| 圖示 | `Site.ExpandingIcon` 讀 `MainSitePartDef.ExpandingIconTexture`，未設時為 BadTex；WorldObjectDef 的欄位無效 | 在主 SitePartDef 設 `expandingIconTexture`，`siteTexture` 保留 Outpost |

## 4. Archetype 與圖示

先選 archetype（SitePartDef、圖示、WorldObjectDef、MapGen／FFF、tile 限制、進退場、可用發射架），再在其合法候選中選 doctrine。`mapGenerator` 與世界層 comps 掛在 WorldObjectDef，所以有不同地圖或攻擊能力的 archetype 要配獨立 WorldObjectDef（`SiteMaker.MakeSite(..., worldObjectDef)`）；AA-only 站不掛 `CompProperties_MissileBase`。DebugAction 與正式生成共用同一建站服務。

| 圖示（`1.7/Textures/UI/Icon/`） | SitePartDef | 名稱 | 任務 |
|---|---|---|---|
| `icon_Artillery` | `DMSE_ArtillerySitePart` | 遠程火砲陣地 | 火砲，非導彈 |
| `icon_FortifiedLaunchSite_AA` | `DMSE_FortifiedAASitePart` | 要塞化防空發射場 | 只防空 |
| `icon_FortifiedLaunchSite_Cruise` | `DMSE_MissileBaseSitePart`（既有） | 要塞化巡弋發射場 | `LaunchSite_A` Scorer；`DMSE_Missile_AdvancedCruise` |
| `icon_FortifiedLaunchSite_Loitering` | `DMSE_FortifiedLoiteringSitePart` | 要塞化巡飛發射場 | 多發射軌、高庫存 |
| `icon_LaunchSite_AA` | `DMSE_FieldAASitePart` | 野戰防空發射場 | 只防空 |
| `icon_LaunchSite_Loitering` | `DMSE_FieldLoiteringSitePart` | 野戰巡飛發射場 | 輕型低庫存；MVP 用 Shahed |
| `icon_Marine_AA` | `DMSE_MarineAASitePart` | 海上防空發射平台 | 只防空 |
| `icon_Marine_EarlyWarning` | `DMSE_MarineEarlyWarningSitePart` | 遠洋預警雷達哨站 | 只偵測 |
| `icon_Marine_ICBM` | `DMSE_MarineICBMSitePart` | 海上洲際導彈平台 | 稀有戰略 |
| `icon_Marine_cruiser` | `DMSE_MarineCruiserSitePart` | 遠洋巡弋導彈平台 | §6 |
| `icon_Mobile_ICBM` | `DMSE_MobileICBMSitePart` | 機動洲際導彈發射車 | 發射後優先轉移 |
| `icon_Silo_ICBM` | `DMSE_SiloICBMSitePart` | 固定洲際導彈發射井 | 井內組裝、長預警 |

命名：`FortifiedLaunchSite` 一律「要塞化OO發射場」。沒有對應地圖結構前，ICBM 與海上圖示不進隨機生成。戰術彈道（Tochka）需另做圖示，不得冒用 ICBM；Maverick 等 Light 彈需有對應 archetype 才進 NPC 站。

## 5. Doctrine

| Doctrine | Archetype | 彈種 | 節奏起點 |
|---|---|---|---|
| 野戰巡飛 | LaunchSite_Loitering | `DMSE_Missile_Shahed`（`LegacyLoiter` 無 `CompMissileConfig`，暫不可用） | 3–4 天 1 枚 |
| 要塞化巡弋 | FortifiedLaunchSite_Cruise | `DMSE_Missile_AdvancedCruise`（穿透＋地形匹配） | 4–6 天 1 枚 |
| 海上巡弋 | Marine_cruiser | 同上，甲板發射模組 | 4–6 天 1 枚，只用初始庫存 |
| 防空 | AA 系列 | 攔截彈＋`WorldObjectComp_AirDefense` | 不對地 |
| 戰術彈道（後續） | 待新圖示 | `DMSE_Missile_Tochka` | 3–5 天 1 枚 |
| 戰略（後續） | Silo／Mobile ICBM | `DMSE_MissileBody_AdvancedBallistic` | 極稀有、長預警 |

- 選型依 `Faction.def.techLevel`、威脅點數、遊戲天數；NPC 不讀玩家研究。TechLevel 未定與舊存檔用確定性的野戰巡飛低階 doctrine。
- 候選順序：doctrine 指定 → 地圖已生成時取符合的實體配置 → 同 archetype 固定 fallback → 停火並診斷一次。
- 反艦彈（Moskit、Yj）、攔截彈（Stinger、Sparrow）不進對地清單；特殊酬載只在特殊 doctrine。

## 6. 庫存、節奏與目標

- **單一庫存帳本**（存檔）：初始、剩餘、已發射、補給次數。巡弋 `LaunchSite_A` 起點：3 枚＋最多 2 次各 1–2 枚可見補給。世界層成功發射才扣；首次進圖把剩餘數轉成實體導彈（只一次）；地圖層升空時同步扣；補給與食物補給分開計數。
- **共用節奏**：每波 = min(空閒火控通道, doctrine 上限, 可用彈數)；地圖層 500 tick 只是檢查，不可繞過天級冷卻。
- **目標**：只選 `MissileWorldUtility.CanEngage` 可打、在站點與彈射程內的玩家殖民地；輪替降低重複；預設不打移動艦船、軌道站、轉移艦。信函顯示來源、ETA、彈種等級、攔截窗口，不提前透露落點。
- **生命週期**：玩家進圖時世界層暫停；摧毀火控雷達或全部對地發射器即終止攻擊；撤退後只在仍有有效防禦時封鎖；搬遷保留同一 Site 與全部狀態，玩家佔用時不搬、找不到 tile 延後。

## 7. 海上巡弋平台（`MarineCruisePlatform`）

- 只生成在 Surface 層 `Ocean` biome（排除湖、河、淺水、已占用），validator 與搬遷、DebugAction 共用；陸上站不得選 Ocean，海上站不得遷到陸地。
- 原版 `Ocean` biome `impassable`、`WaterOceanDeep` 不可通行；原版 `CaravanArrivalAction_VisitSite` 用 `CaravanEnterMode.Edge`，進不去 → **必須先有海上登陸／撤離 arrival action 與甲板 landing zone，否則此 archetype 不生成**；載具損毀時要有備援撤離。
- MapGen：地形初始化後 `GenStep_DMSE_SetOpenOcean` 全圖設 `WaterOceanDeep`，再鋪自訂可行走甲板 `DMSE_SeaPlatformDeck`（不依賴原版橋、不繼承 `VoidSpace`），FFF `DMSE_MarineCruisePlatform`；跳過雪、岩、礦等陸地步驟；守軍、工作、巡邏只在甲板連通區。
- 專用 fog GenStep（`CanReachMapEdge` 在全海圖失效）：揭露所有無屋頂海面與甲板。
- 不用陸上空投補給；MVP 只用初始庫存。
- 新檔規劃：`1.7/Defs/WorldObjects_MarineMissilePlatform.xml`、`Terrain_MarinePlatform_DMS.xml`、`GenStep_DMSE_SetOpenOcean.cs`、`GenStep_MarinePlatformFog.cs`、`CaravanArrivalAction_MarinePlatform.cs`。

## 8. 驗收重點

- 生成：門檻前不生成；合法 tile、敵對派系；超上限或無合法 tile 延後重試、不生成空殼；狀態存讀一致，舊存檔有安全預設。
- 圖示：每種站點用主 SitePartDef 的圖示，搬遷／補彈／讀檔後不變；DefName 與 `DefInjected/SitePartDef` 翻譯鍵一致。
- 彈種：各 TechLevel 得到預期 doctrine；缺 Def 時走固定 fallback；世界層用 doctrine 預設零件、地圖層用相同彈種實體；無目標／超射程不扣彈，成功發射恰扣一枚；補給有上限且不因重載重複。
- 海上：甲板外全深海不可通行；無人生成或走入深海；可登陸可撤離；搬遷仍在 Ocean。
- 風險：通用任務流程可能選中 site tag → 上限檢查涵蓋所有來源；FFF 生成時彈架 `SpawnSetup` 可能先套通用 autofill → 導彈站略過。

## 9. 主要位置

`.source/DMSE/MissileBase/`（`WorldObjectComp_MissileBase`、`MapComponent_MissileBase`、`MissileAutoFillUtility`、`CompMissileRackStorage`、`LordJob_MissileCrewDuty`、`SitePartWorker_MissileBase`）；`1.7/Defs/WorldObjects_MissileBase.xml`、`WorldObjects_FieldLaunchSite.xml`；彈種 `1.7/Defs/ThingDefs_Items/DMSE_GenerationMissiles.xml`、`DMSE_AdvancedMissiles.xml`、`1.7/Defs/Missile/DMSE_Shahed.xml`。
