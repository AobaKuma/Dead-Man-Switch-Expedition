# Combat Extended 支持評估（1.7）

證據：CE **1.6.7.4.0**（`2890901044/Assemblies/CombatExtended.dll`）＋ DMSE 1.7 ＋ RimWorld 1.7 反編譯交叉核對。⚠️ CE 目前**沒有 1.7 版**，以下 API 名稱都是 1.6 的，CE 移植後須重新核對。原始完整評估在 `docs/_archive/`。

## 1. 結論

| # | 子系統 | 現況 | 處置 |
|---|---|---|---|
| A | 對地砲塔（老炮／千禧年／Vulcan／Squall／Dawnblade） | 已可用 | XML `MakeGunCECompatible`（`1.7/CE/Patches/Buildings/GenerationTurrets_CE.xml`） |
| B | 無人砲塔補彈（千禧年） | 能開火，打完 300 發不會補 | 需相鄰 `Building_AutoloaderCE`；寫進描述與補丁註解 |
| C | 野戰防空砲內部機砲 | **已改架構**（2026-10-24，見 §3） | CE 下靠相鄰 autoloader 補彈 |
| D | CIWS ↔ 來襲物 | 兩套防空互不相認、不衝突 | 不做 |
| E | 戰鬥部爆炸 | 吃得到 CE 護甲，沒有 CE 爆壓／破片模型 | 可選：偵測到 CE 時改走 `GenExplosionCE`（1–2 人日） |
| F | 單兵防空具 | 可用，不吃 CE 後座／瞄準 | 不做（CE 單一 ammo class 與多戰鬥部設計衝突） |
| G | 服裝護甲 | 已補 `Bulk/WornBulk` | — |

CE 不管的子系統：BVR 世界層、導彈飛行、組裝、發射架、氣密／重力、打擊 UI。問題集中在傷害／彈藥／攔截的介面。

## 2. 反編譯事實

- **CE 砲塔鏈**：`Building_TurretGunCE.MakeGun()` 從 `turretGunDef` 生 gun，取 `CompAmmoUser`。只要建築是 `Building_TurretGunCE` 且 gun 有 `CompAmmoUser` 就自動接管。`AmmoInjector` 只注入配方／工作台／交易，**不會**自動幫 mod 槍接彈藥。
- **CE 補彈**：`TryOrderReload` 要求 `CompMannable.MannedNow`；否則只走 `TryReloadViaAutoLoader`（相鄰 `Building_AutoloaderCE`）。`CompTurretGun` 路徑完全沒有補彈。
- **DMSE 爆炸吃 CE 護甲**：CE transpile `DamageWorker_AddInjury.ApplyDamageToPart`，把 `ArmorUtility.GetPostArmorDamage` 換成 `ArmorUtilityCE.GetAfterArmorDamage`，任何 DamageInfo 打 pawn 都重算。對建築的傷害未走 CE 管線。
- **CE CIWS 不打 DMSE 導彈**：`VerbCIWSProjectile` 只取 `.OfType<ProjectileCE>()`，`VerbCIWSSkyfaller` 只取 `Skyfaller`；`Projectile_MissileIncoming` 是原版 `Projectile` 子類。DMSE 我方的視覺 skyfaller 會被看見，但同陣營過濾掉。
- **Harmony 無方法級重疊**。行為耦合：`CompApparelReloadable.ReloadFrom`（只在 CE 化單兵具時撞）；`CompRefuelable.Refuel`（CE 下老炮／千禧年保留 Refuelable，patch 照常）；`SkyfallerMaker.SpawnSkyfaller`（與 CE 的 `CompCIWSImpactHandler` 不衝突）。
- `ThingRequestGroup` 索引：CE 硬編碼 (43)、(60) 對應 1.6 列舉；1.7 的 60 = `Projectile`、43 = `ActiveTransporter`。CE 若直接跑在 1.7 上可能指錯——CE 端風險。

## 3. 野戰防空砲架構（2026-10-24 已實作）

原本是 `Building_MissileRack`＋`CompBuildingTurretGun : CompTurretGun`，CE 彈藥鏈接不上。改為：

- `IMissileRack` 介面（`MissileBase/IMissileRack.cs`）＋ `CompMissileRackStorage`（`ThingComp, IThingHolder, IStoreSettingsParent`），`Building_MissileRack` 實作同介面；全專案 30 處引用改走介面。`ITab_Storage` 會自己在 comp 找 `IStoreSettingsParent`。
- `CompMissileLauncher.HeldContainer` 先找 comp 再退回 `IThingHolder`。
- 野戰防空砲 def：`thingClass=Building_TurretGun`、`turretGunDef=DMSE_Gun_FieldAirDefense`、`turretTopDrawSize 4`、儲存 comp；維持全自動（不掛 `CompMannable`，否則 `CompBVRDevice.IsOperational` 會讓整座平台隨砲手停擺）。
- CE 補丁：`DMSE_Gun_FieldAirDefense` 套 `MakeGunCECompatible`（`AmmoSet_30x165mm`，彈匣 300、換彈 10 秒）。
- 未驗證：TurretTop 視覺、舊存檔（thingClass 改變）、CE 下 autoloader 補彈手感。

## 4. 不要做

1. 把導彈改成 `ProjectileCE`／接 CE 彈道（等於重寫 BVR，>15 人日，玩家看不出差別）。
2. 把單兵防空具改成 CE apparel 武器。
3. 為 CE 另開 `*_CE` Def 樹；維持「原 def ＋ `1.7/CE` patch」。

## 5. CE 1.7 移植時要重查的符號

`Building_TurretGunCE`、`Building_CIWS_CE`、`Verb_ShootCE`／`VerbCIWSSkyfaller`、`CompAmmoUser`、`CompCIWSImpactHandler`、`GenExplosionCE`、`ArmorUtilityCE`、`PatchOperationMakeGunCECompatible`。千禧年彈匣 300 發 vs 原版 600 燃料／1800 發仍待平衡。
