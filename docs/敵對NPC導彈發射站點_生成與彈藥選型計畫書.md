# 敵對 NPC 導彈發射站點：生成與彈藥選型計畫書

> 狀態：提案，供後續實作與平衡調整使用  
> 適用版本：RimWorld 1.7 開發版。模組的 1.6 版本已封存，本計畫不回移 1.6。  
> 盤點日期：2026-10-29

## 1. 目標與設計原則

本計畫要讓敵對 NPC 導彈站點能以可預期的方式出現在世界地圖，依站點與敵對派系的能力裝載合適的對地打擊導彈，並讓世界層抽象攻擊和玩家進入站點後的實際發射共用同一套彈種、庫存與射擊節奏。除陸上陣地外，加入海上巡弋導彈發射平台：它生成在不可通行的遠洋 tile，地圖以不可通行深海包圍可行走的實體平台，並提供可用的登陸與撤離方式。

設計原則：

1. **先警告、再打擊、可反制**：玩家應有時間發現站點、調派隊伍或啟用防空。高階導彈提升精度或射程時，攻擊頻率和數量要相應下降。
2. **每個站點只選一次 doctrine**：站點建立時決定作戰等級與彈種，並存檔。避免載入存檔、切換地圖或 Def 載入順序改變後，站點換用另一種導彈。
3. **世界攻擊與地圖戰鬥一致**：地圖尚未生成時用站點 doctrine 建立預設配置；地圖生成後改用彈架中的實體導彈配置。兩者共用彈藥庫存及發射冷卻。
4. **只選可發射的打擊彈**：先檢查 Strike 角色、彈體、發射架容量／尺寸／發射模式、實際射程與目標地圖層；攔截彈、反艦專用彈和不相容的彈型不進一般殖民地打擊清單。
5. **缺少有效設定時安全停火**：不以 `AllDefsListForReading.FirstOrDefault()` 充當遊戲設定；無符合條件的彈種時，保留彈藥、不發射並記錄可診斷訊息。
6. **圖示代表站點任務，不代表單枚彈種**：世界地圖圖示固定對應站點 archetype；彈種與威脅等級由站點 Inspect、情報信函和來襲警告補充，避免玩家把「巡弋發射站」誤認為「洲際彈道發射井」。

## 2. 目前實作盤點

### 2.1 站點與地圖

- `DMSE_MissileBase` 世界物件搭配 `DMSE_MissileBaseSitePart`，使用專屬地圖生成器及 `LaunchSite_A` FFF 結構。地圖生成步驟包含地形、建築、駐守人員和戰爭迷霧。
- `GenStep_MissileBaseCrew` 依站點威脅點數生成守軍；`LordJob_MissileCrewDuty` 負責防守和搬運工作。
- 目前模組有開發用 DebugAction 建立站點，但 `1.7/Defs` 內沒有專屬的定期生成調度器或明確的站點生成任務。站點雖有 `MissileBase` site tag，仍需確認一般 Site／Quest 流程是否會選中它；不應把偶然被通用任務選到當作穩定的生成機制。
- 現有 `DMSE_MissileBase_MapGen` 是陸上配置，包含雪、岩石、礦塊和地面 FFF 結構；沒有遠洋 tile 選取、深海覆蓋、海上平台甲板或運輸艦登陸流程，不能直接把現有地圖生成器套到海上站點。
- 原版 `Ocean` biome 的 `impassable` 為 true，`WaterOceanDeep` 地圖地形的 `passability` 也是 `Impassable`。`SiteMaker.MakeSite` 可直接把 Site 指定到 tile；但原版 `CaravanArrivalAction_VisitSite` 進圖使用 `CaravanEnterMode.Edge`，因此水上站點不能只生成在海上，還必須實作甲板登陸點和可撤離的海上運輸流程。
- RimWorld 本體提供 `SiteMaker.MakeSite` 建立 Site 與 SitePart，也提供 `QuestNode_GenerateSite` 作為任務生成路徑。排程生成建議沿用這些本體入口，避免自行重造 Site 初始化流程。
- 目前新增的圖示檔位於 `1.7/Textures/UI/Icon/`，但尚未被 Def 引用。`DMSE_MissileBaseSitePart` 設有 `siteTexture=World/WorldObjects/Sites/Outpost`，沒有 `expandingIconTexture`；`DMSE_MissileBase` WorldObjectDef 的 `expandingIconTexture=.../Settlement` 不會成為 Site 的展開圖示。
- RimWorld 本體 `Site.ExpandingIcon` 直接讀取 `MainSitePartDef.ExpandingIconTexture`；SitePartDef 未指定時會回傳 `BaseContent.BadTex`。因此應把新圖示寫在各 archetype 的主 SitePartDef 上。`siteTexture` 是站點地圖／站點材質，保留 Outpost，不以白色 UI 符號取代。

### 2.2 發射與目前彈種

- 未生成地圖時，`WorldObjectComp_MissileBase` 依 `missileFireIntervalDays` 發射 `missilesPerSalvo` 枚導彈；世界層設定目前是 2–4 天一波、每波 2–3 枚、站點上限射程 20 tile。
- `CompProperties_MissileBase.missileBody` 在 XML 留空時，程式退回第一個 `MissileBodyDef`。這個結果依賴 Def 清單順序，而且 `BuildFireConfig()` 只建立空部件配置，不套用彈體的預設零件。世界層目前也沒有實體彈藥扣庫存。
- 生成地圖後由 `MapComponent_MissileBase` 接手：每 500 tick 檢查一次敵方活躍雷達的火力通道，從有有效 `CompMissileConfig` 的彈架取出實體導彈，並採用該枚導彈自己的配置。攔截專用彈不會被當成對地彈消耗。
- 地圖內以「上一波導彈全部結束後再開下一波」控制波次，但沒有套用世界層 2–4 天的發射間隔；因此玩家留在站點地圖時，實際攻擊頻率可能快於世界層。
- `MissileAutoFillUtility` 目前按派系 `TechLevel` 套用第一條匹配規則。Scorer 彈架只自動填入 `DMSE_Missile_Moskit`；這是反艦彈。小型導彈發射架填入攔截彈。現有 `LaunchSite_A` 的防空彈藥與對地打擊彈藥需要明確分工。
- 守軍的空投程式目前只補給食物，沒有導彈補給。README 對站點描述為空投補彈，需在實作時補齊行為，或同步修正文案。
- 站點倒數結束時目前直接摧毀世界物件，不會把同一個站點移到新 tile。若保留「機動發射陣地」設定，應實作搬遷，而非只做消失。

### 2.3 主要缺口

| 項目 | 現況 | 計畫方向 |
|---|---|---|
| 自動生成 | 有站點 Def、tag 與 DebugAction，缺少穩定的模組專屬排程 | 加入受限額的世界層生成調度器 |
| 世界層彈種 | 空值回退 Def 清單第一項，未套用預設部件 | 站點建立時保存 doctrine 與預設 MissileConfig |
| 地圖層彈種 | 由彈架實體導彈配置決定 | 依已保存 doctrine 精確裝填彈架 |
| 彈藥庫存 | 世界層無限；地圖層初始裝填後沒有導彈補給 | 加入共享庫存帳本及有限補給 |
| 射擊節奏 | 世界層固定間隔；地圖層由通道空閒狀態驅動 | 世界與地圖共用發射冷卻和單波上限 |
| 搬遷 | 倒數後站點消失 | 搬到新合法 tile，保留 doctrine、庫存與冷卻 |
| 世界圖示 | 新增 UI Icon 資產尚未引用；SitePart 缺少 `expandingIconTexture` | 主 SitePartDef 設定對應圖示，並依圖示拆分站點 archetype |
| 海上發射平台 | 無海上 archetype；現有 MapGen 含陸地專用 GenStep | 新增海上 SitePart／WorldObjectDef、遠洋 MapGen、不可通行深海及可行走平台甲板，並提供登陸／撤離 |

## 3. 站點生成方案

### 3.1 生成入口

新增 `WorldComponent_MissileBaseSpawner`（名稱可依專案慣例調整），保存下一次評估時間與每個玩家殖民地的生成冷卻。通過條件後建立一個明確的世界事件／情報信函，再建立站點。不要在每次世界 tick 中掃描並立即建立站點。

站點建立流程：

1. 找出符合條件、且與玩家敵對的派系；若派系無有效 TechLevel，使用最低階 doctrine。
2. 依 archetype 選擇 tile。陸上站點使用可生成地圖的陸地 tile；海上平台只選 Surface layer 的 `Ocean` 水域 tile，排除湖泊、河流、沿岸淺水與已有世界物件的 tile。海上 tile 必須在至少一個玩家殖民地的設定威脅距離內，且其實際攻擊距離同時符合站點和 doctrine 射程。
3. 依玩家進度及派系 TechLevel 決定站點 archetype，再選相容 doctrine，並設定威脅點數、守軍規模、初始彈藥及搬遷時間。先從 archetype 註冊表取得配對的主 SitePartDef 與 WorldObjectDef，不以單一固定站點 Def 建立所有類型。
4. 使用本體 `SiteMaker.MakeSite` 建立對應 Site，傳入該 archetype 的主 SitePartDef、敵對派系、威脅點數及配對 WorldObjectDef，再將結果註冊到世界物件管理器。DebugAction 也使用相同的 archetype 註冊表。
5. 保存 archetype、doctrine、彈藥庫存、目標冷卻及第一次可發射時間；送出包含方位與威脅程度的玩家信函。

若日後要做「接獲情報後接受任務並定位發射站」的任務線，另以本體 `QuestNode_GenerateSite` 建立任務委派；它與一般世界事件共用相同的 archetype／doctrine 選擇與站點初始化服務。

### 3.2 生成門檻與數量限制

第一輪建議以以下值作為待平衡起點，集中放在可調整設定，不散落於程式常數：

- 遊戲經過至少 15 天，且玩家有已生成地圖的殖民地後才開始抽選。
- 每個玩家殖民地最多一個有效的導彈站點；一般遊戲最多同時存在一個未清除站點。高難度或後期劇情可另行提高上限。
- 建立站點後至少 30 天才可再次抽選；若站點已被清除或自行撤離，冷卻可縮短但不可立即連續生成。
- 第一次打擊至少延後 2 天，並確保玩家先收到站點出現信函。進階 doctrine 的第一波可延後至 3 天。
- 若沒有合適敵對派系、合法 tile 或射程內玩家地圖，延後下一次評估，不建立空殼站點、不消耗生成冷卻。

威脅點數由 Storyteller 當前威脅點數與站點生成設定的上下限共同決定。建議先以現有 `SitePartDef.wantsThreatPoints` 和 `GenStep_MissileBaseCrew` 的點數換算維持守軍生成，再逐步校正；不要讓 NPC TechLevel 單獨決定大量守軍與高階彈藥。

### 3.3 站點生命週期

- **部署**：設定 archetype、相容 doctrine、初始發射時間、庫存、站點壽命及搬遷目標規則。
- **威脅期**：只攻擊具有效地圖且在射程內的玩家殖民地。保存上次攻擊的目標與時間，降低連續選中同一殖民地的機率。
- **玩家突襲**：地圖生成後，世界層暫停發射；改由地圖元件使用彈架中的真實彈藥。陸上站點沿用一般進場；海上站點使用海上運輸工具在專用甲板登陸格下船，並提供可被呼叫的撤離點。摧毀火控雷達或全部對地發射器應能明確削弱／終止攻擊能力。
- **玩家撤退**：依現有雷達存活檢查設定再進入封鎖；只有仍有有效指揮／發射能力時才封鎖。
- **搬遷**：倒數結束且站點地圖未被玩家佔用時，選取新 tile，移動同一 Site 並保留 archetype、doctrine、庫存與事件歷史。陸上站只搬到陸地，海上平台只搬到合法遠洋 tile；新位置須與殖民地和重要世界物件保持安全距離。若找不到合適 tile，延後搬遷；若站點遭清除則不搬遷。
- **存檔**：保存 archetype／doctrine Def、配置版本、庫存、已用彈數、冷卻時間、搬遷時間與上次目標。舊存檔沒有這些欄位時，以固定的野戰巡飛 archetype 和低階 doctrine 補值，不重設既有冷卻。

### 3.4 Site icon 與站點 archetype

新圖示是一組白色剪影符號，應用來辨識「這是哪種發射設施」，而不是暗示玩家已經知道精確彈頭或彈藥庫存。`Site.ExpandingIcon` 取主 SitePartDef 的圖示，所以正式生成與 DebugAction 必須建立相同的主 SitePartDef；不要只改 WorldObjectDef 的圖示欄位。

命名統一：`FortifiedLaunchSite` 直接使用「要塞化OO發射場」格式，將任務類型放在「要塞化」與「發射場」之間，例如「要塞化巡飛發射場」、「要塞化巡弋發射場」及「要塞化防空發射場」。

以下 12 個圖示各分配一個站點與 SitePartDef。`DMSE_MissileBaseSitePart` 是目前已存在的巡弋站 DefName，為避免存檔與既有引用失效而保留；其餘 DefName 是新增規劃，需隨站點 Def 一併建立後再將翻譯鍵移入實際 `DefInjected/SitePartDef` 目錄。

| 圖示檔 | SitePartDef | RimWorld label | 站點任務 |
|---|---|---|---|
| [`icon_Artillery.png`](../1.7/Textures/UI/Icon/icon_Artillery.png) | `DMSE_ArtillerySitePart` | 遠程火砲陣地 | 傳統遠程火砲與觀測設備；不是導彈發射站。 |
| [`icon_FortifiedLaunchSite_AA.png`](../1.7/Textures/UI/Icon/icon_FortifiedLaunchSite_AA.png) | `DMSE_FortifiedAASitePart` | 要塞化防空發射場 | 工事化雷達與攔截彈陣地，保護周邊敵方據點。 |
| [`icon_FortifiedLaunchSite_Cruise.png`](../1.7/Textures/UI/Icon/icon_FortifiedLaunchSite_Cruise.png) | `DMSE_MissileBaseSitePart`（既有） | 要塞化巡弋發射場 | 現有 `LaunchSite_A` Scorer／巡航發射架布局；主要使用 `DMSE_Missile_AdvancedCruise`。 |
| [`icon_FortifiedLaunchSite_Loitering.png`](../1.7/Textures/UI/Icon/icon_FortifiedLaunchSite_Loitering.png) | `DMSE_FortifiedLoiteringSitePart` | 要塞化巡飛發射場 | 多具傾斜發射軌、較高庫存與守軍，使用可攔截的巡飛彈。 |
| [`icon_LaunchSite_AA.png`](../1.7/Textures/UI/Icon/icon_LaunchSite_AA.png) | `DMSE_FieldAASitePart` | 野戰防空發射場 | 快速部署的區域防空站，只使用攔截彈。 |
| [`icon_LaunchSite_Loitering.png`](../1.7/Textures/UI/Icon/icon_LaunchSite_Loitering.png) | `DMSE_FieldLoiteringSitePart` | 野戰巡飛發射場 | 輕型、低庫存的巡飛彈站；MVP 使用 Shahed。 |
| [`icon_Marine_AA.png`](../1.7/Textures/UI/Icon/icon_Marine_AA.png) | `DMSE_MarineAASitePart` | 海上防空發射平台 | 艦載雷達與攔截彈保護遠洋航線及海上資產。 |
| [`icon_Marine_EarlyWarning.png`](../1.7/Textures/UI/Icon/icon_Marine_EarlyWarning.png) | `DMSE_MarineEarlyWarningSitePart` | 遠洋預警雷達哨站 | 偵測並通報空中威脅；不具對地打擊能力。 |
| [`icon_Marine_ICBM.png`](../1.7/Textures/UI/Icon/icon_Marine_ICBM.png) | `DMSE_MarineICBMSitePart` | 海上洲際導彈平台 | 後續稀有海上戰略平台，只使用具洲際射程的彈體。 |
| [`icon_Marine_cruiser.png`](../1.7/Textures/UI/Icon/icon_Marine_cruiser.png) | `DMSE_MarineCruiserSitePart` | 遠洋巡弋導彈平台 | 不可通行深海中的可行走甲板發射平台，需有海上登陸與撤離。 |
| [`icon_Mobile_ICBM.png`](../1.7/Textures/UI/Icon/icon_Mobile_ICBM.png) | `DMSE_MobileICBMSitePart` | 機動洲際導彈發射車 | 陸上機動戰略發射車；發射後優先轉移。 |
| [`icon_Silo_ICBM.png`](../1.7/Textures/UI/Icon/icon_Silo_ICBM.png) | `DMSE_SiloICBMSitePart` | 固定洲際導彈發射井 | 固定戰略設施，於井內組裝彈體並提供長預警。 |

以下為繁體中文 RimWorld `DefInjected/SitePartDef` 文案草案；標籤路徑採用該 DefName 的 `.label` 與 `.description` 欄位：

```xml
<?xml version="1.0" encoding="UTF-8"?>
<LanguageData>
  <DMSE_ArtillerySitePart.label>遠程火砲陣地</DMSE_ArtillerySitePart.label>
  <DMSE_ArtillerySitePart.description>一處配備遠程火砲、觀測設備與彈藥庫的陣地，可支援對遠方地面目標的炮擊。此據點使用火砲，不屬於導彈發射設施。</DMSE_ArtillerySitePart.description>

  <DMSE_FortifiedAASitePart.label>要塞化防空發射場</DMSE_FortifiedAASitePart.label>
  <DMSE_FortifiedAASitePart.description>這座要塞化防空發射場由厚重工事、火控雷達與攔截導彈構成，用來保護周邊敵方據點和發射設施；它不會向殖民地發射對地導彈。</DMSE_FortifiedAASitePart.description>

  <DMSE_MissileBaseSitePart.label>要塞化巡弋發射場</DMSE_MissileBaseSitePart.label>
  <DMSE_MissileBaseSitePart.description>這座要塞化巡弋發射場配備巡弋導彈發射架、火控雷達與守軍，可對射程內的地面目標發動有限齊射。監控預警訊號可能為防空系統提供攔截窗口。</DMSE_MissileBaseSitePart.description>

  <DMSE_FortifiedLoiteringSitePart.label>要塞化巡飛發射場</DMSE_FortifiedLoiteringSitePart.label>
  <DMSE_FortifiedLoiteringSitePart.description>這座要塞化巡飛發射場設有多具傾斜發射軌、較大彈藥庫與防守工事，能持續威脅附近地面目標。巡飛彈飛行較慢，通常提供較長的預警與攔截時間。</DMSE_FortifiedLoiteringSitePart.description>

  <DMSE_FieldAASitePart.label>野戰防空發射場</DMSE_FieldAASitePart.label>
  <DMSE_FieldAASitePart.description>一處快速部署的野戰防空發射場，依靠機動雷達與攔截彈為附近部隊提供局部防空；它沒有對地打擊任務。</DMSE_FieldAASitePart.description>

  <DMSE_FieldLoiteringSitePart.label>野戰巡飛發射場</DMSE_FieldLoiteringSitePart.label>
  <DMSE_FieldLoiteringSitePart.description>一處輕型野戰巡飛發射場，配備少量發射軌與有限庫存。它部署迅速、防守薄弱，主要以低成本巡飛彈打擊附近地面目標。</DMSE_FieldLoiteringSitePart.description>

  <DMSE_MarineAASitePart.label>海上防空發射平台</DMSE_MarineAASitePart.label>
  <DMSE_MarineAASitePart.description>這座海上防空發射平台以艦載雷達和攔截彈保護遠洋航線及附近海上設施。平台四周是不可通行的深海，登陸需使用海上運輸。</DMSE_MarineAASitePart.description>

  <DMSE_MarineEarlyWarningSitePart.label>遠洋預警雷達哨站</DMSE_MarineEarlyWarningSitePart.label>
  <DMSE_MarineEarlyWarningSitePart.description>遠洋預警雷達哨站負責偵測來襲飛行器與導彈，並向附近友軍提供警戒資訊。它本身不具備對地打擊能力。</DMSE_MarineEarlyWarningSitePart.description>

  <DMSE_MarineICBMSitePart.label>海上洲際導彈平台</DMSE_MarineICBMSitePart.label>
  <DMSE_MarineICBMSitePart.description>這座海上洲際導彈平台搭載戰略彈道導彈，是極少部署的終局級目標。發射前會出現長時間預警，平台彈藥也十分有限。</DMSE_MarineICBMSitePart.description>

  <DMSE_MarineCruiserSitePart.label>遠洋巡弋導彈平台</DMSE_MarineCruiserSitePart.label>
  <DMSE_MarineCruiserSitePart.description>遠洋巡弋導彈平台將發射架、雷達與支援區建於可行走甲板上，四周深海不可通行。它能打擊射程內的地面目標，登陸與撤離都需要海上運輸。</DMSE_MarineCruiserSitePart.description>

  <DMSE_MobileICBMSitePart.label>機動洲際導彈發射車</DMSE_MobileICBMSitePart.label>
  <DMSE_MobileICBMSitePart.description>這支機動洲際導彈發射車隊可在陸上轉移並隱蔽部署，發射後會盡快撤離。其洲際射程與稀少彈藥使它成為高威脅目標。</DMSE_MobileICBMSitePart.description>

  <DMSE_SiloICBMSitePart.label>固定洲際導彈發射井</DMSE_SiloICBMSitePart.label>
  <DMSE_SiloICBMSitePart.description>固定洲際導彈發射井設於強化地下設施內，並需在井內完成彈體組裝。它射程極遠、發射次數稀少，同時會提供較長的預警與反制窗口。</DMSE_SiloICBMSitePart.description>
</LanguageData>
```

圖示落地規則：

1. 在 `DMSE_MissileBaseSitePart` 設定 `<expandingIconTexture>UI/Icon/icon_FortifiedLaunchSite_Cruise</expandingIconTexture>`，作為現有 `LaunchSite_A` 的第一個明確對應；維持 `<siteTexture>World/WorldObjects/Sites/Outpost</siteTexture>`。
2. 將「站點 archetype」與「導彈 doctrine」拆成兩個選擇：SitePartDef／GenStep 決定圖示、地圖布局與可用發射架；doctrine 再依敵對派系 TechLevel、玩家進度及威脅點數，從該布局允許的彈種中選擇。圖示不可因每次補彈或搬遷而變動。
3. 每個主要 archetype 使用自己的 SitePartDef 和 FFF `useTag`／MapGen 配置，讓圖示、建築、彈架尺寸與可裝彈藥相符。注意 `mapGenerator` 和世界層 comps 掛在 WorldObjectDef，不在 SitePartDef；推薦每個有不同地圖或攻擊能力的 archetype 配對獨立 WorldObjectDef，由 `SiteMaker.MakeSite(..., worldObjectDef)` 指定。這也讓 AA-only 站點不會繼承 `CompProperties_MissileBase` 的對地攻擊循環。防空站不得只換圖示仍生成攻擊站布局；戰略井亦不得只換圖示仍用 Scorer。
4. AA-only WorldObjectDef 只掛防空行為；混合型巡弋站可同時掛防空與對地發射行為。若日後為減少 Def 數而共用 WorldObjectDef，必須加入存檔的 `strikeEnabled`／archetype gate，並在世界層與地圖層同時檢查，不能只靠 SitePartDef tag 阻止攻擊。
5. 圖示只表達可觀察到的站點類型。Inspect 與情報信函另顯示偵測程度、已知打擊家族、射程級別、下一波倒數及是否具防空能力；尚未偵明的彈種用「未確認」呈現。
6. 白色剪影以 faction／expanding icon tint 著色後，需檢查遠近縮放和高低對比背景的可辨識度。若圖示被遮蔽或看不清，調整圖素或 icon priority，不更動地圖站點材質。

### 3.5 遠洋水上發射平台與不可通行地圖

海上平台是獨立的 `MarineCruisePlatform` archetype，使用 `icon_Marine_cruiser`、專用 SitePartDef、WorldObjectDef、MapGeneratorDef 與 FFF `useTag`。世界站點只生成於 Surface layer 的 `Ocean` biome；使用 `Lake`、沿岸淺水、河道或一般陸上 tile 都不符合此 archetype。平台攻擊仍選取合法陸上殖民地目標，射程依海上發射 tile 到目標 tile 的 Surface 距離計算。

地圖分成「不可通行的海面」與「有限可行走的人工平台」兩部分：

1. 專用 MapGeneratorDef 保留必要的地形初始化，再於所有地形生成步驟之後、平台建築與 Pawn 生成之前，執行 `GenStep_DMSE_SetOpenOcean`，把全圖基底設為原版 `WaterOceanDeep`。海面必須保持 `Impassable`；不可用淺水、可走地板或只在畫面上畫水的方式假裝遠洋。
2. 新增海上 FFF 結構，例如 `DMSE_MarineCruisePlatform`，由客製 `DMSE_SeaPlatformDeck` 覆蓋必要的甲板、通道、雷達和發射區。甲板要可行走並承載指定的發射架、防空炮塔和雷達；平台以外的每個格子仍為深海且不可通行。不要依賴原版橋樑鋪設在 `WaterOceanDeep` 上。
3. 海上 MapGen 不執行雪、岩石、地面礦塊和地表廢料等陸地步驟；補給、殘骸與掩體只生成在甲板可用格。站點班與守軍落點限制在平台的可站立格，所有巡邏、防守和搬運路徑只能沿甲板連通區移動。
4. 新增海上專用 fog GenStep。現有 `GenStep_MissileBaseFog` 以 `CanReachMapEdge` 尋找揭霧點；四周都是不可通行深海時只會回退地圖中心，無法正確揭露開放海面。海圖應揭露所有無屋頂的海面與甲板，封閉艙室仍可保持戰爭迷霧。
5. 海洋世界 tile 本身不可通行，原版訪問 Site 的 caravan 進場會以 `CaravanEnterMode.Edge` 從地圖邊緣進入，無法在深海邊緣放置步行隊伍。海上站點必須提供專用的海上運輸／登陸 arrival action，將隊伍放到指定甲板 landing zone，並有同一運輸系統的撤離方式；可沿用模組內相容的船艦或飛行載具，若沒有則新增專用登陸艇。不可生成一個只能看見、卻無法登陸或撤退的海上 Site。
6. 登陸區與撤離區須保留無障礙甲板、可達路徑及載具操作所需空間。運輸載具遭摧毀或玩家撤退時，提供明確的備援撤離／任務結束方式，避免殖民者被永久困在遠洋地圖。

世界層 SiteMaker 可把 Site 直接指派至 Ocean tile，但生成器仍需驗證 tile 有效、位於 Surface、屬於 Ocean biome、未被其他世界物件占用且可建立 map。海上 archetype 的尋址、搬遷及 DebugAction 都重用相同的 Ocean tile validator；不可讓通用陸上站點選到 Ocean，也不可讓海上站點遷到陸地。生成前若沒有符合條件且在有效射程內的海域，就延後，不生成不可互動的站點。

## 4. 站點 Archetype 與導彈 Doctrine

### 4.1 選型原則

先選擇 Site archetype，再從該 archetype 允許的 missile doctrine 中選一種主要打擊彈。這可避免把巡飛、巡弋、防空與戰略彈道設施混成一個只換 Def 的通用站點。Doctrine 在站點建立時選定，優先序如下：

1. 站點 doctrine 明確指定的 Strike 彈藥及配置。
2. 當地圖已生成時，取符合該 doctrine 的實體導彈配置。
3. 若 doctrine 彈藥在該版本或模組組合中不可用，只在同一 archetype 的固定 fallback 清單選下一種合法 Strike 彈。
4. 若沒有任何相容導彈，停火並輸出一次診斷訊息；不可退回 Def 載入順序。

候選彈須同時通過：ThingDef 存在、有有效 `CompMissileConfig`、`MissileRoleExtension` 為 Strike、彈體合法、該 archetype 的實際發射架 storage filter 接受、尺寸／發射模式相容、配置有效、射程足夠且目標所在 map layer 可攻擊。候選清單不得只因 Icon 名稱相近就加入；必須和地圖中實際生成的發射架一致。

### 4.2 建議的初始彈種表

| Doctrine | 站點 archetype／圖示 | 主要彈種與平台 | 用途與限制 |
|---|---|---|---|
| 野戰巡飛 | `LaunchSite_Loitering` | MVP 使用 `DMSE_Missile_Shahed`；`DMSE_Building_LoiteringLauncher` | Industrial 或 TechLevel 未定派系的低階方案。速度慢、軌跡較容易被發現與攔截；每波 1 枚為主，有限庫存。`DMSE_Missile_LegacyLoiter` 固定配置但沒有 `CompMissileConfig`；在地圖層發射器支援 `MissileItemExtension` 前不可放進 NPC 彈藥候選清單。 |
| 要塞化巡弋發射場 | `FortifiedLaunchSite_Cruise` | `DMSE_Missile_AdvancedCruise`；現有 `LaunchSite_A` 的 Scorer 發射架 | Spacer 以上或後期高威脅站點。使用穿透與地形匹配預設配置；每波 1 枚，較長冷卻及低庫存。這是現有 FFF 結構最直接的 icon／平台對應。 |
| 海上巡弋平台 | `Marine_cruiser` | `DMSE_Missile_AdvancedCruise`；新建可置於海上甲板的發射模組與 `DMSE_MarineCruisePlatform` FFF 結構 | 遠洋 Surface Ocean tile 上的稀有機動發射站。使用專屬海圖、甲板 landing zone 和海上運輸進退場；MVP 以有限初始彈藥運作，不在深海地圖投放一般補給艙。 |
| 野戰防空／要塞化防空發射場 | `LaunchSite_AA`／`FortifiedLaunchSite_AA` | 攔截角色彈藥與 `WorldObjectComp_AirDefense` | 防禦型世界物件，不列入對殖民地發射清單。作為其他敵方資產或導彈站的護衛目標。 |
| 要塞化戰術彈道發射場（後續 archetype） | 建議新增 `FortifiedLaunchSite_Ballistic` 圖示 | `DMSE_Missile_Tochka`；相容的中型傾斜發射架 | 中階劇情用的戰術彈道彈。圖示尚未提供前不納入隨機站點生成，且不可使用 `Silo_ICBM` 冒充戰略核彈井。 |
| 固定戰略發射井（後續） | `Silo_ICBM` | `DMSE_MissileBody_AdvancedBallistic`；Ballistic Silo | 極稀有終局威脅。彈體在發射井上組裝，須新增專用 FFF 結構、裝配／彈藥狀態及明確反制窗口；長預警、低發射次數。 |
| 機動戰略發射車（後續） | `Mobile_ICBM` | `DMSE_MissileBody_AdvancedBallistic`；新增移動發射布局 | 僅供洲際戰略型 doctrine。發射後縮短停留時間並優先搬遷；不可和一般 Tactical／Cruise 站點共用相同威脅設定。 |

`DMSE_Missile_Moskit` 與 `DMSE_Missile_Yj` 是反艦導彈，保留給艦船或可移動目標任務，不作一般殖民地站點預設。`DMSE_Missile_Stinger`、`DMSE_Missile_Sparrow` 與其他攔截角色彈藥只用於防空，不納入對地發射清單。高爆預設配置作為一般威脅基線；毒氣、燃燒、集束、EMP 等特殊酬載只在特殊 doctrine 中逐項平衡，不作一般站點的隨機結果。

`DMSE_Missile_Maverick` 是有效的 Strike 對地彈，但新增圖示中沒有通用「地面打擊站」符號。它可留作玩家發射架彈種；若要做 NPC 對地站點，先指定一個能接受 Light 彈的 site archetype 與專用／通用 strike icon，不要把它直接併入巡飛或 ICBM 類別。

### 4.3 派系等級與彈種決策

先由 `Faction.def.techLevel`、Storyteller 威脅點數和遊戲天數選擇可生成的站點 archetype，再從該 archetype 的合法彈種中選 doctrine。玩家防禦進度用於調整預警與攻擊節奏，不直接改變 SitePart 圖示或解鎖不相容的彈種。NPC 不讀玩家研究狀態。

每個 archetype 設定：主 SitePartDef／icon、WorldObjectDef、MapGen／FFF tag、tile biome 限制、進退場方式、可用發射架及候選 doctrine。每個 doctrine 設定：`candidateMissiles`、每波枚數上限、射擊間隔、初始庫存、可補給次數、站點壽命和預警時間。候選清單按確定順序排列；只在同一 archetype 的相容 doctrine 之內做帶權重抽選。存檔同時保存 archetype 與 doctrine，避免每次發射或搬遷重新抽選。

## 5. 彈藥、射擊與補給流程

### 5.1 單一庫存帳本

為每個站點新增可存檔的 strike inventory 狀態：初始庫存、剩餘庫存、已發射數、預備補給與已用補給次數。庫存上限依 archetype 分開設定：現有 Cruise `LaunchSite_A` 可先以「3 枚裝填彈 + 最多 2 次、每次 1–2 枚的可見補給」作平衡起點；海上巡弋平台 MVP 使用有限初始庫存且不空投補給，後續若新增補給船，再另定海上補給次數及登陸風險。Loitering、AA、Silo 和 Mobile ICBM 不共用這些數值。AA 站不建立對地 strike inventory。

- 世界層發射成功才扣除一枚；無目標、超射程、跨層不支援或找不到有效彈體時不扣庫存。
- 玩家首次進入地圖時，把帳本剩餘數量轉成對應的實體導彈，且只轉換一次。這個站點的通用 Rack AutoFill 必須略過，以免先自動填充預設彈後又重複播種。
- 地圖層成功升空時，移除實體導彈並同步扣帳本。重新載入存檔不得再次補回已消耗的彈。
- 補給由守軍補給 Job 或 `MapComponent_MissileBase` 管理；僅庫存低於門檻時投放，使用可見的補給艙、有限次數與短暫延遲。玩家能摧毀補給艙或在空投抵達前摧毀站點。
- 食物補給與導彈補給分開計數及發送。README、信函及站點狀態必須反映真正可用的補給行為。
- 海上平台不得沿用陸上補給艙空投：遠洋補給需由明確可見的補給船／海上運輸流程執行；若該流程未實作，海上站只使用初始庫存並耗盡後停止打擊。

### 5.2 共用攻擊節奏

新增站點共用的 `nextFireTick` 與 `salvoLimit`。世界層和地圖層都先確認冷卻，再以 `min(空閒火控通道、doctrine 每波上限、可用彈數)` 決定本波發射量。地圖層的 500 tick 更新只用來檢查狀態，不可因此繞過天數級冷卻。

建議初始節奏：野戰巡飛每 3–4 天 1 枚；要塞化巡弋發射場與海上巡弋平台每 4–6 天 1 枚；戰術彈道若日後加入，先以每 3–5 天 1 枚為起點。海上平台不因海域條件而增加齊射數，並使用較長部署預警。第一波延遲依 archetype 設定。這會低於目前世界層 2–4 天、2–3 枚的基線，並避免玩家突襲時火控通道瞬間清空多波彈藥；數值應以實際攔截窗口和損害測試調整。

### 5.3 目標選擇

- 目標只從 `MissileWorldUtility.CanEngage` 判定可打的玩家殖民地地圖選出；距離需同時符合站點與導彈射程。
- 預設不把移動艦船、軌道站點或轉移中的船當成一般地面站點目標；需要跨 map layer／追蹤移動目標的導彈應由專用 doctrine 開啟。
- 使用輪替或有權重的選擇，降低短時間重複鎖定同一殖民地的機率。保存目標冷卻，讀檔後不重複觸發同一攻擊排程。
- 通知內容顯示來源站點、預估抵達時間、彈種等級及現有雷達是否有攔截窗口；不要提前透露精確落點，除非該彈種的遊戲設計本來就提供精確情報。

## 6. 實作工作包

### P0：Def 與規則整理

- 將 `SiteArchetype`（SitePartDef、圖示、GenStep／FFF 布局）與 `MissileDoctrine`（候選彈、配置、庫存、節奏）分開定義；集中保存派系等級範圍、發射架能力、搬遷和預警參數。
- 先為現有 `DMSE_MissileBaseSitePart` 掛上 `icon_FortifiedLaunchSite_Cruise`；依 §3.4 對照表為 12 個 icon 固定 SitePartDef、繁中 label 與 description。其餘站點的正式 SitePartDef／WorldObjectDef／MapGen／FFF 依 archetype 分階段實作；沒有相應地圖結構前，ICBM 與海上圖示只保留文案規劃，不加入隨機生成。
- 若要加入 `DMSE_Missile_Tochka` 專用站點，先補戰術彈道站 icon 或確定不會與 ICBM 圖示混淆的表現方式。
- 檢查現有 Cruise archetype 彈種都有有效 `MissileConfig`、strike role 與合法發射架；`Maverick` 暫留給日後有對應 site icon／布局的通用地面打擊 archetype，再決定是否加入 Scorer storage filter。
- 將 `DMSE_Missile_Moskit` 從一般殖民地打擊預設移除；保留現有設定供反艦任務使用。
- 為舊存檔與 TechLevel 未定的 NPC 設定確定性低階 doctrine。
- 新增海上巡弋平台的 WorldObjectDef／SitePartDef／MapGeneratorDef／FFF `useTag`，使用 `icon_Marine_cruiser`；加上遠洋地形與 walkable 甲板 Def。可參考 `Terrain_OrbitalPlatform_DMS.xml` 的自訂平台地形模式，但海上甲板不依賴 `VoidSpace`。
- 新增海上登陸／撤離 arrival action 或相容的船艦運輸入口；若遊戲內尚無有效載具流程，海上平台 archetype 先保持不可生成，直到可完成登陸和撤退。

### P1：確定性站點生成

- 實作世界層排程與生成上限、合法派系／tile 選擇、冷卻及預警信函。
- 按 archetype 分開選陸地與 Ocean tile；海上 tile 通過 Surface／Ocean／未佔用／可生成 map／有效目標射程檢查後才可生成，DebugAction 和搬遷沿用相同 validator。
- 使用 `SiteMaker.MakeSite` 建立 archetype 對應的 SitePart／WorldObjectDef 配對與威脅點數，確保 icon、地圖生成器和世界層 comps 一致。
- 在站點建立時決定並保存 archetype 與相容 doctrine，不把選型留到第一次發射。
- 保留 DebugAction 作為開發工具，但改用同一個站點建立服務，避免 Debug 與正式生成流程不同步。

### P2：世界／地圖彈藥一致化

- 以已存的 archetype／doctrine 產生含預設部件的 `MissileConfig`；移除所有「第一個 MissileBodyDef」回退。
- 增加專用站點裝填步驟，讓 FFF 建築生成後按 doctrine 裝填 Strike 彈；防空發射架繼續只裝攔截彈。
- 增加單一庫存帳本，接通世界層扣庫存、首次生成地圖轉換、地圖層實體消耗、存檔讀取及補給。
- 世界層與地圖層共用冷卻、波次上限、有效目標篩選及彈種配置。
- 海上對地導彈只從甲板發射器取彈；水面保持不可通行，守軍、維修和搬運工作只能在平台連通的甲板格執行。

### P3：搬遷、任務與平衡

- 讓到期站點搬到新 tile；玩家正在突襲時不搬遷，玩家撤退後沿用既有再進入封鎖規則。
- 增加情報任務／追蹤信函，或在特定劇情中使用 `QuestNode_GenerateSite` 產生有明確目標的站點。
- 評估進階巡弋 doctrine；戰略彈道 doctrine 另立專用 FFF 結構與作戰測試，不併入一般站點。
- 完成海上抵達、登陸、地圖戰鬥、補給（若啟用）與撤離的完整流程；驗證運輸工具失效時仍有任務收束方式，並測試海上站搬遷仍只選 Ocean tile。
- 以難度、玩家雷達與可用防空通道進行平衡；若沒有足夠攔截窗口，降低彈數或增加警告時間。

## 7. 驗收條件

### 生成與存檔

- 到達門檻前不生成；達門檻後能生成一個合法、可探索且屬於敵對派系的站點。
- 陸上站不會生成在水域；海上平台只生成在合法 Ocean tile；兩者都不會放在不可建立 map 的 tile、玩家殖民地 tile 或已被其他世界物件占用的位置。
- 超出站點上限時不生成；沒有可用派系或 tile 時會延後重試，不形成空站點或永久卡住生成器。
- 站點 archetype、doctrine、庫存、下次發射與搬遷時間在存檔往返後保持一致；舊存檔得到固定安全預設。
- 每種世界站點使用與其主 SitePartDef 對應的 icon；`DMSE_MissileBase` 顯示 `icon_FortifiedLaunchSite_Cruise`，海上巡弋平台顯示 `icon_Marine_cruiser`，海上防空／預警／戰略平台各保留自己的任務圖示。
- 12 個 icon 都恰好對應一個站點 archetype、SitePartDef 名稱、繁中 label 與 description；DefName 與 `DefInjected/SitePartDef` 翻譯鍵一致。`FortifiedLaunchSite` 名稱均採「要塞化OO發射場」直述格式，不使用括號命名。
- 海上巡弋平台只生成在 Surface `Ocean` tile；遠洋地圖除平台甲板外全為 `WaterOceanDeep`，甲板外格不可通行、建造或生成守軍。海圖不會因氣候或陸地 GenStep 出現雪地、岩石與礦塊。
- 海上甲板能承載平台建築與守軍，所有守軍出生格、工作目標與路徑都在甲板連通區；無人會生成或走入深海。
- 海上 Site 不可由一般陸上 caravan 選取後卡在不可通行 Ocean tile；專用運輸流程可把隊伍送到甲板 landing zone，並能安全撤離或以明確任務結束流程收束。
- 海上地圖進場後，開放海面和甲板可見，封閉艙室仍依設計保持迷霧；海上站搬遷後仍留在 Ocean tile。
- Site icon 從 `MainSitePartDef.ExpandingIconTexture` 載入，圖示路徑有效；Site 地圖材質仍使用 `siteTexture`，不變成 UI 剪影。
- 世界地圖圖示和 Inspect／信函的站點角色、威脅級別一致；同一站點在搬遷、讀檔或補彈後不突然改圖示。

### 彈種與射擊

- Industrial、Spacer、Ultra／Archotech 和 TechLevel 未定派系會取得預期 archetype／doctrine；任何候選 Def 缺失或不相容時會走固定 fallback，而非依 Def 載入順序變動。
- 世界層發射使用 doctrine 的預設部件；地圖層發射使用相同彈種的實體配置。
- 攔截彈、反艦彈不會被一般殖民地站點當成對地彈；不相容發射架不會接收該彈。
- 無目標、超射程、無有效導引或找不到Strike彈時不扣彈；成功發射恰好扣一枚。
- 世界層及地圖層都遵守同一發射間隔與每波上限，並讓火控雷達失效、彈架摧毀及庫存耗盡各自產生預期效果。
- 地圖層補給有上限、可被玩家觀察及中止，且不會因重載地圖或存檔重複投放。

### 遊戲流程

- 玩家收到站點出現與來襲警告，並有足夠時間安排防空或出征。
- 摧毀站點指揮／發射能力會停止後續打擊；撤退後的再進入封鎖只在敵方有效防禦仍存活時觸發。
- 站點搬遷後仍使用相同 archetype／doctrine 與剩餘彈藥，不重置為滿庫存；已清除站點不會搬遷。
- 1.7 開發版可載入；1.6 封存版保持不變。

## 8. 風險與處理

| 風險 | 處理方式 |
|---|---|
| Site tag 可能被通用任務流程選中，造成與專屬生成器重複 | 站點上限檢查必須涵蓋所有來源；若通用流程會選到此 tag，調整 tag 使用方式或讓通用選取排除它。 |
| 新 icon 放在 `WorldObjectDef.expandingIconTexture`，但 Site 仍顯示預設／錯誤圖示 | Site 的展開圖示取 `MainSitePartDef.ExpandingIconTexture`；每種 archetype 要在其主 SitePartDef 設定圖示，不以 WorldObjectDef 欄位代替。 |
| 圖示暗示的武器種類與實際 FFF 布局或彈藥不同 | 圖示與 archetype 綁定，doctrine 只可從該布局合法候選中選擇；戰術彈道沒有專用圖示前不冒用 ICBM。 |
| 把 UI icon 當作 Site 地圖材質，導致地圖上的站點呈現不合適 | 保留 `siteTexture` 的 Outpost 圖片；新符號只指定給 `expandingIconTexture`。 |
| 海上 Site 建立在不可通行 Ocean tile，原版 caravan 路徑或地圖邊緣進場無法抵達 | 海上站點提供獨立的船艦／登陸艇 arrival action 和甲板 landing zone；不能以一般 caravan 的 Edge 進場作為唯一方式。 |
| 全圖深海後 FFF 建築、Pawn 或工作排程落在不可通行格 | 先生成專用 walkable 甲板 footprint，再生成平台結構和守軍；出生格、工作點與巡邏點只從甲板連通區取樣。 |
| `GenStep_MissileBaseFog` 從地圖邊緣尋找可達點，但海面不可通行 | 海圖使用專用 fog GenStep，直接揭露所有無屋頂海面與甲板，保留封閉艙室的迷霧。 |
| 海上平台沿用陸上空投補給，補給艙落在深海且無法操作 | MVP 僅使用有限初始庫存；海上補給需另做可登陸的補給船／運輸事件。 |
| FFF 結構生成時 Rack 的 `SpawnSetup` 可能先套用通用自動填充 | 導彈站地圖略過通用 autofill，等結構完成後依已保存 doctrine 一次裝填。 |
| 世界層抽象庫存與地圖層實體彈藥不同步 | 只保留一個站點庫存帳本；地圖生成只轉換一次，成功發射時兩者同步。 |
| 高階導彈越過一般防空窗口 | 降低每波數、延長冷卻、提高首次預警；特殊彈種限於稀有任務。 |
| 站點搬遷 tile 搜尋失敗或世界物件引用失效 | 找不到 tile 時延後搬遷；維持同一 Site 引用並在搬遷前驗證新 tile，不先刪除舊站點。 |
| Def 載入順序、其他模組或存檔缺少候選彈 | 固定 fallback 表、Def 存在與配置有效性檢查、舊存檔遷移；沒有合法彈時停火而不亂選。 |

## 9. 主要修改位置

- 站點行為與持久狀態：[WorldObjectComp_MissileBase.cs](../.source/DMSE/MissileBase/WorldObjectComp_MissileBase.cs)
- 地圖層射擊與彈藥帳本：[MapComponent_MissileBase.cs](../.source/DMSE/MissileBase/MapComponent_MissileBase.cs)
- NPC 彈架自動裝填：[MissileAutoFillUtility.cs](../.source/DMSE/MissileBase/MissileAutoFillUtility.cs)、[CompMissileRackStorage.cs](../.source/DMSE/MissileBase/CompMissileRackStorage.cs)
- SitePart、世界物件與 MapGen：[WorldObjects_MissileBase.xml](../1.7/Defs/WorldObjects_MissileBase.xml)
- 海上平台 SitePart、WorldObject、MapGen 與 FFF（新增）：`1.7/Defs/WorldObjects_MarineMissilePlatform.xml`；海上地形／甲板 Def（新增）：`1.7/Defs/Terrain_MarinePlatform_DMS.xml`。
- 遠洋覆蓋與海圖視野 GenStep（新增）：`GenStep_DMSE_SetOpenOcean.cs`、`GenStep_MarinePlatformFog.cs`；海上登陸與撤離（新增）：`CaravanArrivalAction_MarinePlatform.cs` 或既有載具系統的專用 ArrivalAction。
- 軌道平台地形只作自訂平台 TerrainDef 的設計參考：[Terrain_OrbitalPlatform_DMS.xml](../1.7/Defs/Terrain_OrbitalPlatform_DMS.xml)；海上甲板不可繼承 `VoidSpace` 或軌道專用 affordance。
- 世界地圖站點圖示：見 §3.4 的逐檔連結；目前涵蓋野戰及要塞化巡飛／巡弋／防空發射場、固定／機動 ICBM，以及另保留的火砲和海軍圖示家族。
- 巡飛站點候選彈及發射架：[DMSE_Shahed.xml](../1.7/Defs/Missile/DMSE_Shahed.xml)、[DMSE_LoiteringLauncher.xml](../1.7/Defs/ThingDefs_Buildings/DMSE_LoiteringLauncher.xml)
- 彈種與發射架：[DMSE_GenerationMissiles.xml](../1.7/Defs/ThingDefs_Items/DMSE_GenerationMissiles.xml)、[DMSE_AdvancedMissiles.xml](../1.7/Defs/ThingDefs_Items/DMSE_AdvancedMissiles.xml)、[DMSE_Security_Missile.xml](../1.7/Defs/ThingDefs_Buildings/DMSE_Security_Missile.xml)、[DMSE_LightMissileLauncher.xml](../1.7/Defs/ThingDefs_Buildings/DMSE_LightMissileLauncher.xml)
- 守軍與補給：[LordJob_MissileCrewDuty.cs](../.source/DMSE/MissileBase/LordJob_MissileCrewDuty.cs)、[SitePartWorker_MissileBase.cs](../.source/DMSE/MissileBase/SitePartWorker_MissileBase.cs)
- 玩家訊息：[1.7/Languages/ChineseTraditional (繁體中文)/Keyed/KeyedFile.xml](../1.7/Languages/ChineseTraditional%20(%E7%B9%81%E9%AB%94%E4%B8%AD%E6%96%87)/Keyed/KeyedFile.xml)
- 功能說明：[README.md](../README.md)

