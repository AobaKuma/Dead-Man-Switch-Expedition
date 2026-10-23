# Combat Extended 的 C# 支持評估（Expedition 1.7）

> 評估對象：本模組整體功能在 Combat Extended 環境下「已經可用／缺 XML／缺 C#」的分界。
> 證據基準：CE **1.6.7.4.0**（`2890901044/Assemblies/CombatExtended.dll`）＋ DMSE 1.7 `DMSE.dll` ＋ RimWorld 1.7 三者反編譯交叉核對。
> ⚠️ CE 目前**沒有 1.7 版**（`About.xml` 的 supportedVersions 只有 1.6），以下所有 API 名稱都是 1.6 的；1.7 移植時必須重新核對（見 §6）。

---

## 0. 結論先講

| # | 子系統 | 現況 | 需要什麼 | 型別 | 優先 |
|---|---|---|---|---|---|
| A | 砲塔對地射擊（老炮／千禧年／Vulcan／Squall／Dawnblade） | **已可用** | `MakeGunCECompatible` | XML | — 已完成 |
| B | 無人砲塔補彈（千禧年） | **可開火、不會補彈** | 相鄰 `Building_AutoloaderCE` 或給它 `CompMannable` | 設計／XML | P1 |
| C | 野戰防空砲的內部機砲 | **無法 CE 化** | `CompBuildingTurretGun` 要接 CE 彈藥鏈 | **C#** | P0 |
| D | 末端 CIWS ↔ 來襲物 | **兩套防空各自為政，互不相認（但也不衝突）** | 要互通才需要 C#；不改也不壞 | C#（可選） | P2 |
| E | 戰鬥部爆炸 | **吃得到 CE 護甲**，但沒有 CE 爆壓／破片模型 | 可選：改走 `GenExplosionCE` | C#（可選） | P2 |
| F | 單兵防空具（背帶 SAM） | 可用但「CE 世界裡的玩具槍」 | verb 換 CE 彈藥／後座 | C# | P2 |
| G | 服裝護甲 | 已補 `Bulk/WornBulk` | — | XML | — 已完成 |

**一句話**：純 XML 已經能覆蓋 CE 兼容度的大部分（所有對地砲塔、彈藥、護甲）；真正的 C# 缺口只有 **一個**——「CE 的彈藥鏈綁在 `Building_TurretGunCE` 上，而野戰防空砲的機砲是 `CompTurretGun` 生出來的內部 gun」，沒有補彈途徑。其餘（CIWS 互通、CE 爆炸模型、單兵具）都是「不做也不壞」的可選項。

**與直覺相反的兩點**（都已用反編譯證實）：

1. **DMSE 的爆炸在 CE 下吃得到護甲**——CE 把 `DamageWorker_AddInjury.ApplyDamageToPart` 的護甲區塊換成 `ArmorUtilityCE.GetAfterArmorDamage`，任何 `DamageInfo` 打 pawn 都會被重算。
2. **CE 的 CIWS 不會誤射 DMSE 的來襲導彈**——CE 的 `VerbCIWSProjectile` 只認 `ProjectileCE`，而 DMSE 的 `Projectile_MissileIncoming` 是原版 `Projectile` 子類，型別就不符。兩套防空是平行世界，沒有互相搶目標、也沒有「打了不掉」的浪費。

---

## 1. 現況總覽：哪些子系統 CE 根本不管

先排除雜訊——以下功能**不需要**任何 CE 支持，因為它們不經過 CE 的戰鬥管線：

| 子系統 | 為什麼不需要 |
|---|---|
| BVR 世界層偵測／追蹤／火控通道（`WorldComponent_BVRNetwork`、`BVRChannelLedger`、`CompFireControlRadar`） | 純世界層狀態機，CE 沒有對應概念 |
| 導彈飛行（`Projectile_MissileBase`、`MissileWorldObject`、`ScorerProjectile_WorldObject`） | 自家軌跡積分，不走 CE 的彈道物理 |
| 導彈組裝／彈體／部件（`MissileBodies`、`ITab_MissileAssembly`） | 生產鏈，與 CE 無關 |
| 發射架／發射井／塔架（`Building_MissileRack` 系列） | 自家 `MissileRackExtension`，不發射 `Projectile` |
| 氣密／真空／重力（`VacuumRoom`、`CompVacuumPump`、`GravityFactor`） | 環境系統 |
| 打擊規劃／波次／警報 UI | UI |

也就是說，「模組整體」在 CE 下的問題**不是全面性的**，而是集中在 4 個與「傷害／彈藥／攔截」相接的介面。

---

## 2. 逐項分析

### A. 砲塔對地射擊 —— 已解決（XML）

CE 的砲塔鏈條是：

```
Building_TurretGunCE.MakeGun()
  → gunInt = ThingMaker.MakeThing(def.building.turretGunDef)
  → compAmmo = gunInt.TryGetComp<CompAmmoUser>()
  → InitGun(): 把每個 verb 的 caster 設成砲塔、castCompleteCallback = BurstComplete
```

所以只要「砲塔建築 = `Building_TurretGunCE`（或 `Building_CIWS_CE`）」＋「gun def 帶 `CompAmmoUser`」，CE 就會自動接管射速、後座、彈藥、補彈。這兩件都由 XML 完成（`1.7/CE/Patches/Buildings/GenerationTurrets_CE.xml`、DMS 核心的 `Security_CE.xml`）。

補充：CE 的 `AmmoInjector`（反編譯確認）只做**配方／工作台／交易**的注入，**不會**幫 mod 的槍自動接彈藥。所以沒寫 `MakeGunCECompatible` 的槍在 CE 下就是「原版槍」——能開火、傷害走原版算式，但子彈沒有穿甲值，對 CE 護甲幾乎無效。

### B. 無人砲塔補彈（千禧年）—— 設計決策，可能不用 C#

`Building_TurretGunCE` 的補彈路徑只有兩條（反編譯 `TryOrderReload` / `TryReloadViaAutoLoader`）：

```csharp
CompMannable obj = mannableComp;
if (obj == null || !obj.MannedNow || (!forced && ...)) return;   // 沒人操 → 直接 return
...
// 另一條：Tick 裡
if (mannableComp.MannedNow) TryOrderReload(); else TryReloadViaAutoLoader();
//   TryReloadViaAutoLoader 只認相鄰的 Building_AutoloaderCE
```

**後果**：千禧年（3×3、全自動、無 `CompMannable`）在 CE 下打完 300 發彈匣後，**必須**貼一台 CE 的自動裝彈機才會補彈，否則永久啞火。

三個選項：
1. **接受現狀**，在描述裡寫明「CE 下需搭配自動裝彈機」——0 工。
2. 給千禧年加 `CompMannable`（要人操）——與「全自動」設定衝突，不建議。
3. 寫一個 `CompAmmoFeeder`：相鄰儲存區有對應彈藥時自動餵——1～2 人日，但 CE 已有 autoloader，重造輪子。

建議：**選 1**，並在 CE 補丁檔加註解說明。

### C. 野戰防空砲的內部機砲 —— 必須 C#（P0）

現況（反編譯 `DMSE.CompBuildingTurretGun`）：

```csharp
public class CompBuildingTurretGun : CompTurretGun   // ← 繼承原版 CompTurretGun
{
    // 只加了「轉到目標方位才開火」與 aimSettle 延遲
    public override void CompTick() { ... ((CompTurretGun)this).CompTick(); }
}
```

它**不是** `Building_TurretGun`，建築本體是 `Building_MissileRack`。CE 的整條彈藥鏈（`CompAmmoUser.turret` 賦值、補彈 Job、autoloader、`BurstComplete` 重置）都掛在 `Building_TurretGunCE` 上，`CompTurretGun` 這條路**完全沒有補彈途徑**。

因此：**不要把 `DMSE_Gun_FieldAirDefense` 改成 CE 槍**。改了會變成「彈匣 0 發、永不開火」，比現況更糟（現況是鋼鐵燃料的原版 `Verb_Shoot`，至少會響）。

要真的 CE 化，有三條路：

| 方案 | 做法 | 成本 | 代價 |
|---|---|---|---|
| C-1 | 新增 `CompProperties_BuildingTurretGunCE`：`CompTick` 內自行維護一個 `int magazine`，用既有 `CompRefuelable` 的鋼鐵換彈（1 鋼 = N 發），發射前扣彈 | 2～3 人日 | 不接 CE 的 autoloader／彈種 UI，等於「仿 CE 彈匣」 |
| C-2 | 讓野戰防空砲改繼承 `Building_TurretGunCE`，飛彈發射器拆成子建築或另一個 comp | 4～6 人日 | 架構大改，`Building_MissileRack` 的儲彈／ITab 要重做 |
| C-3 | 保留原版機砲，只把「對地機砲」的傷害換成 CE 彈頭 def（`Bullet_30x165mm_HE`），不接彈匣 | 0.5 人日 | 不消耗 CE 彈藥（免費彈），但傷害與穿甲對得上 CE 尺度 |

建議：**先做 C-3 當止血**（讓傷害尺度正確、不破壞現有機制），C-1 排 P1。

#### C-2 詳細設計（已驗證可行，2026-10-24）

把「彈砲一體」的野戰防空砲改成真正的 `Building_TurretGun`，飛彈儲存／發射全部移到 comp。三個關鍵疑問都已用反編譯與 Def 對照排除：

**① 儲存能不能放在 comp？→ 可以，原生存取支援**

```csharp
// ITab_Storage.GetThingOrThingCompStoreSettingsParent
if (t is IStoreSettingsParent result) return result;
if (t is ThingWithComps { AllComps: var allComps })
    for (int i = 0; i < allComps.Count; i++)
        if (allComps[i] is IStoreSettingsParent result2) return result2;
```

`ITab_Storage` 會自己翻 comp 找 `IStoreSettingsParent`，所以過濾器／優先級 UI 不用重寫。反過來，`IHaulDestination` 不行——`HaulDestinationManager` 只收 `IHaulDestination` 實例，而搬運 Job 的目標必須是 `Thing`，所以「小人自動把飛彈搬進砲塔」這條要嘛自己實作 Job、要嘛沿用模組既有的 `JobDriver_LoadMissileLauncher`（它本來就是自訂裝填，不靠通用搬運）。

**② 飛彈讀取點只有一個 → 一行就能兼容**

```csharp
// CompMissileLauncher
private ThingOwner HeldContainer => (parent as IThingHolder)?.GetDirectlyHeldThings();
```

改成「先找 comp、再退回 `IThingHolder`」即可同時支援「舊的 rack 建築」與「新的砲塔＋comp」。

**③ 貼圖朝向不是問題**

`DMS_Turret_Tunguska` 在 DMS 核心本來就是 `Building_TurretGun` + `turretGunDef = DMS_Gun_Tunguska`，用的就是同一張 `Things/Turrets/DMS_Gun_Tunguska`。原本 `CompTurretFacingRenderer` 的 `artworkRotation -90` 是那個自訂繪製器自己的座標補償，改回 `TurretTop` 後不需要。

**改動清單**

| # | 檔案 | 改動 |
|---|---|---|
| 1 | 新 `MissileBase/IMissileRack.cs` | 介面：`RackExt / MaxStored / StoredCount / Full / HeldThings / Accepts / SpaceRemainingFor / Renderer` |
| 2 | 新 `MissileBase/CompMissileRackStorage.cs` | `ThingComp, IThingHolder, IStoreSettingsParent, IMissileRack`；把 `Building_MissileRack` 的容器＋`StorageSettings`＋存檔搬過來 |
| 3 | `Building_MissileRack.cs` | 加 `: IMissileRack`（成員已齊，零行為改變） |
| 4 | `CompMissileLauncher.cs` | `HeldContainer` 先找 comp |
| 5 | `ITab_MissileInventory / ITab_MissileAssembly / WorkGiver_LoadMissileLauncher / JobDriver_LoadMissileLauncher / JobDriver_ManMissileLauncher / MissileAutoFillUtility / MapComponent_MissileBase / MissileAssemblyUtility / MapComponent_GodModeAssembly / CompMissileRackRenderer` | 30 處 `Building_MissileRack` → `IMissileRack`；`AllBuildingsColonistOfClass<Building_MissileRack>()` → `<Building>()` + `is IMissileRack` |
| 6 | `1.7/Defs/.../DMSE_GenerationPlatforms.xml` | 野戰防空砲：`thingClass=Building_TurretGun`、`building.turretGunDef=DMSE_Gun_FieldAirDefense`、`turretTopDrawSize 4`；移除 `CompProperties_BuildingTurretGun` 與 `CompProperties_TurretFacingRenderer`；加儲存 comp |

**做完會得到什麼**：CE 下 `thingClass` 被核心補丁換成 `Building_TurretGunCE` 也不會壞（邏輯都在 comp），機砲終於能接 `CompAmmoUser`——**前提是它仍需要「有人操作」或「相鄰自動裝彈機」才會補彈**（`Building_TurretGunCE.TryOrderReload` 的硬條件）。若不想依賴 autoloader，順手給它 `CompProperties_Mannable` 即可（變成有人操的彈砲一體，也符合「老資歷」的調性）。

> **狀態（2026-10-24 已實作）**：`IMissileRack`／`CompMissileRackStorage` 已落地，全專案 30 處 `Building_MissileRack` 引用已改走介面，野戰防空砲的 def 已改成 `Building_TurretGun` + `turretGunDef` + 儲存 comp，並維持**全自動**（不掛 `CompMannable`；手操只留給老式防空砲）。CE 補丁已對 `DMSE_Gun_FieldAirDefense` 套 `MakeGunCECompatible`（`AmmoSet_30x165mm`，彈匣 300／換彈 10 秒），因為無人操作，CE 下補彈要靠相鄰的 `Building_AutoloaderCE`。`csc` 編譯 0 error、`1.7/Assemblies/DMSE.dll` 已更新。
> 若之後想改成「有人操」，只要在 def 加 `CompProperties_Mannable` 即可讓 CE 改走砲手補彈；代價是 `CompBVRDevice.IsOperational` 會把整座平台（雷達／火控／飛彈／CIWS）一起視為停擺。
> 尚未驗證：① 視覺（TurretTop 畫通古斯卡砲塔頭）② 舊存檔（FieldAD 的 thingClass 變了）③ CE 下 autoloader 補彈與飛彈裝填 Job 的實際手感。

### D. 末端 CIWS ↔ 來襲物 —— 兩套防空互不相認，但沒有衝突（P2，可不做）

**先確認 DMSE 的來襲物到底是什麼**（`1.7/Defs/Missile/DMSE_Shahed.xml`、`DMSE_MissileGenerationBodies.xml`）：

```xml
<ThingDef ParentName="DMSE_MissileIncomingProjectileBase">
  <defName>DMSE_Incoming_Shahed</defName>
  <thingClass>DMSE.Projectile_MissileIncoming</thingClass>   <!-- : Verse.Projectile，不是 Skyfaller -->
```

→ 12 個 `DMSE_Incoming_*` 全部是**原版 `Projectile` 的子類**，既不是 `Skyfaller`，也不是 CE 的 `ProjectileCE`。

**CE 的兩套 CIWS 各自只認自己人**（反編譯）：

```csharp
public class VerbCIWSProjectile : VerbCIWS<ProjectileCE> {
    public override IEnumerable<ProjectileCE> Targets =>
        map.listerThings.ThingsInGroup((ThingRequestGroup)60).OfType<ProjectileCE>();  // 60 = Projectile（1.7 列舉；CE 是照 1.6 編的）
}
public class VerbCIWSSkyfaller : VerbCIWS<Skyfaller> {
    public override IEnumerable<Skyfaller> Targets =>
        map.listerThings.ThingsInGroup((ThingRequestGroup)43).OfType<Skyfaller>();     // 同上，索引值有版本風險
}
// VerbProperties_CIWS.Interceptable(ThingDef) => true（預設不排除任何 def，但型別已先把關）
```

→ **CE 的 CIWS 根本不會鎖定 DMSE 的來襲導彈**（型別不符，`.OfType<ProjectileCE>()` 直接濾掉）。所以不存在「攔截炮拼命射、導彈照樣落地」這種浪費，也沒有互相搶目標的問題。

**這是設計取捨，不是 bug**：DMSE 的防空網（雷達 → 火控通道 → 攔截彈／CIWS）本來就是為自家 `BVRWave` 設計的；CE 的防空只管 CE 自己的 `ProjectileCE`／drop pod。

唯一的可見後果：**在 CE 下，CE 的 CIWS 塔對 DMSE 的空襲完全無效**，玩家必須用 DMSE 的雷達＋CIWS。若接受這個分工，**不需要任何工作**。

**如果要讓兩邊互通**（可選，P2，3～5 人日，收益低）：

| 方向 | 做法 | 風險 |
|---|---|---|
| CE 的 CIWS 打 DMSE 飛彈 | 讓 `Projectile_MissileIncoming` 改繼承 `ProjectileCE` | CE 的彈道物理／`TrajectoryWorker`／碰撞會接管，與 DMSE 自訂飛行（`Projectile_MissileBase` 自己積分）正面衝突，等於重寫進場段 |
| 同上（較安全） | 給 DMSE 的 incoming 掛一個 CE `ProjectileCE` 的「影子彈」，真正擊落由 DMSE 判定 | 兩份狀態要同步，容易漂移 |
| DMSE 的 CIWS 打 CE 的來襲物 | `MapComponent_BVRCombat` 掃描時把 `ProjectileCE`／敵方 `Skyfaller` 包成 `BVRTarget` | 要複製 CE 的 trajectory 預測，且 CE 的來襲物多半是砲彈而非戰略打擊 |

建議：**不做**。兩套防空各管各的，對玩家是清楚的（DMSE 的威脅只有 DMSE 的防空能解）。

順帶：`DMSE_SkyFaller_*`（我方發射時的視覺 skyfaller）確實是 `Skyfaller`，會被 CE 的 `VerbCIWSSkyfaller` 看見，但 `IsFriendlyTo` 會把同陣營的過濾掉，不會被自家 CIWS 打，這點沒問題。

### E. 戰鬥部爆炸 —— 現況可接受，升級可選（P2）

`MissileImpactUtility.DetonateInner` 走的是原版 `GenExplosion.DoExplosion`（40 參數版），戰鬥部／酬載另外呼叫 `warheadEffect.Apply` / `payloadEffect.Apply`。

CE 對這條路的處理（反編譯 `Harmony_DamageWorker_AddInjury_ApplyDamageToPart`）：

```csharp
[HarmonyPatch(typeof(DamageWorker_AddInjury), "ApplyDamageToPart")]
// Transpiler 把 ArmorUtility.GetPostArmorDamage 換成 ArmorUtilityCE.GetAfterArmorDamage
```

→ **任何** DamageInfo 打在人／動物身上都會經過 CE 的護甲重算，所以 DMSE 的爆炸傷害**不會**無視 CE 護甲（這是好消息，很多人以為第三方爆炸在 CE 下會穿甲）。

代價是：爆炸幾何仍是原版（沒有 CE 的逐格穿甲值、爆壓衰減、破片模型、`explosionDamageFalloff`）。CE 自己的爆炸走 `GenExplosionCE.DoExplosion`。

升級路線（可選）：
- 用反射／條件編譯偵測 `CombatExtended.GenExplosionCE`，有 CE 時改呼叫它，並把 `WarheadEffect` 的傷害帶上 `armorPenetrationSharp/Blunt`。
- 成本 1～2 人日；收益主要是「對 CE 的重甲單位（動力甲、機械族）手感正確」。

### F. 單兵防空具 —— 可選（P2）

`DMSE_Apparel_SamLauncher` 是腰帶層 Apparel ＋ `CompProperties_ManPortableSAM`（繼承原版 `CompProperties_ApparelReloadable`），對地發射用原版 `Verb_LaunchProjectile`，彈藥是自家 `DMSE_Missile_Stinger`。

CE 有專門的 apparel verb 管線（`Harmony_CompApparelVerbOwner_Charged_CompygetWornGizmosExtra`、`Harmony_JobGiver_Reload_TryGiveJob`），但只服務 CE 自己的 apparel 武器。現況下這把槍：
- 能發射、能裝填（自家 comp），**不吃 CE 的後座／瞄準／Bulk 懲罰**（Bulk 已用 XML 補）；
- 發射的是自家 `Projectile`，傷害最終仍走 CE 護甲（同 E）。

要 CE 化就是把它改成 CE 的 apparel 武器（`CompApparelVerbOwner_Charged` ＋ ammo）——2～3 人日，而且會與現有的「多枚導彈各帶不同戰鬥部」設計打架（CE 彈藥是單一 ammo class）。**建議不做**，維持現狀。

---

## 3. Harmony 衝突面：乾淨

把兩邊的 patch 目標拉出來對照：

- **DMSE（27 個）**：`CompApparelReloadable.ReloadFrom`、`CompRefuelable.Refuel`、`CompLaunchable.*`、`GenSpawn.Spawn`、`SkyfallerMaker.SpawnSkyfaller`、`MapParent.*`、`PawnsArrivalModeWorker.CanUseWith`、`Thing.LabelNoCount`、`WorldObject.GetFloatMenuOptions`、UI 類……
- **CE（約 200 個 patch 類別）**：`DamageWorker.Apply`／`DamageWorker_AddInjury.ApplyDamageToPart`、`Verb.TryStartCastOn`、`VerbTracker.VerbsTick`、`ExplosionCE.*`、`GenRadial`、`Fire.*`、`JobGiver_Reload`、`Thing.SpawnSetup/DeSpawn`……

**沒有方法級重疊**。唯一三個「行為耦合」要留意：

| 耦合點 | 風險 | 處置 |
|---|---|---|
| `CompApparelReloadable.ReloadFrom`（DMSE patch） vs CE 的 apparel 裝填 Job | 只有 CE 化單兵具（F）才會撞 | 不做 F 就無事 |
| `CompRefuelable.Refuel`（DMSE patch） vs CE 移除砲塔的 Refuelable | CE 下老炮／千禧年**保留** Refuelable（供自家 CIWS），所以 patch 照常生效 | 已在新補丁註解說明 |
| `SkyfallerMaker.SpawnSkyfaller`（DMSE patch） vs CE 對 skyfaller 的 comp 注入 | DMSE 只影響自家視覺 skyfaller，CE 的 `CompCIWSImpactHandler` 是 patch 在 `SkyfallerBase` 上；兩者不衝突 | 無需處置 |

---

## 4. 建議工作包與估時

| 包 | 內容 | 估時 | 依賴 |
|---|---|---|---|
| **P0-1** | C-3 止血：野戰防空砲機砲改用 CE 彈頭 def（傷害尺度正確、不改機制） | 0.5 人日 | — |
| **P0-2** | B：千禧年在 CE 下需搭配 `Building_AutoloaderCE`，寫進描述與補丁註解 | 0.2 人日 | — |
| **P1-1** | C-1：`CompBuildingTurretGun` 的 CE 彈匣（鋼鐵換彈／autoloader） | 2～3 人日 | P0-1 |
| **P1-2** | F：單兵防空具的 CE 化（可選，評估結論是「不做」） | 2～3 人日 | — |
| **P2-1** | E：戰鬥部爆炸改走 `GenExplosionCE`（反射／條件編譯） | 1～2 人日 | — |
| **P2-2** | D：兩套防空互通的實驗（結論：收益低，只在真的需要時做） | 3～5 人日 | — |

**合計**：**0.7 人日**（P0）就能把 CE 下的「不合理處」清掉（機砲傷害尺度、千禧年補彈說明）；要到「行為完整」（P1）再加 2～3 人日。D 與 F 建議不做。

---

## 5. 明確不建議做的事

1. **不要**把 DMSE 導彈改成 CE 的 `ProjectileCE`／接 CE 彈道物理——BVR 世界層的追蹤、攔截、殘骸都建立在自家 `Projectile_MissileBase` 上，改了等於重寫整個 BVR（>15 人日，且收益為 0：玩家看不出差別）。
2. **不要**把野戰防空砲改成 `Building_TurretGunCE`（見 C-2）。
3. **不要**把單兵防空具改成 CE apparel 武器（見 F）。
4. **不要**為 CE 另開一套獨立的 Def 樹（例如 `*_CE` 版本的砲塔）——現有 `1.7/CE` 的「原 def ＋ patch」寫法是對的，維護成本最低。

---

## 6. 風險與未驗證

| 項目 | 狀態 |
|---|---|
| CE 1.7 不存在 | **阻塞實機驗證**。所有 C# 都建立在 1.6 API 上；CE 移植 1.7 時下列符號必須重新核對：`Building_TurretGunCE`、`Building_CIWS_CE`、`Verb_ShootCE`／`VerbCIWSSkyfaller`、`CompAmmoUser`、`CompCIWSImpactHandler`、`GenExplosionCE`、`ArmorUtilityCE`、`PatchOperationMakeGunCECompatible` |
| `ThingRequestGroup` 的索引（CE 用 (43)、(60) 之類的硬編碼） | ⚠️ 1.7 的列舉第 60 項是 `Projectile`、第 43 項是 `ActiveTransporter`；CE 1.6 是對著 1.6 的列舉編譯的。**若 CE 直接跑在 1.7 上，這兩個索引很可能指到錯的組**——這是 CE 端 1.7 移植的風險，不是本模組的 |
| 戰鬥部爆炸在 CE 下對「建築」的傷害 | 未走 CE 的爆炸管線，只有對 pawn 是確定的（走 `AddInjury` 轉接） |
| 千禧年 300 發彈匣 vs 原版 600 燃料／1800 發 | 數值待平衡，非技術問題 |

---

*本文所有 CE 行為皆可追溯至 `2890901044/Assemblies/CombatExtended.dll` 的反編譯；DMSE 行為追溯至 `1.7/Assemblies/DMSE.dll` 與 `.source/DMSE`。*
