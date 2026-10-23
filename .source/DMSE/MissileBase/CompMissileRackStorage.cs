using System.Collections.Generic;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    public class CompProperties_MissileRackStorage : CompProperties
    {
        public CompProperties_MissileRackStorage()
        {
            compClass = typeof(CompMissileRackStorage);
        }
    }

    /// <summary>
    /// 把 <see cref="Building_MissileRack"/> 的「容器＋儲存設定」搬到 comp 上，
    /// 讓任何建築（尤其是 <see cref="Building_TurretGun"/>，以及 CE 換掉 thingClass 之後的
    /// <c>CombatExtended.Building_TurretGunCE</c>）都能當飛彈平台。
    ///
    /// 為什麼是 comp 而不是建築類別：
    ///   - CE 的補丁會把砲塔的 thingClass 換成 <c>Building_TurretGunCE</c>，自訂建築類別會整個被換掉，
    ///     寫在類別裡的容器直接消失；寫在 comp 裡則跟著 def 走。
    ///   - <see cref="ITab_Storage"/> 會自己翻 comp 找 <see cref="IStoreSettingsParent"/>，UI 不用重寫。
    ///
    /// 注意：本 comp **不**實作 <see cref="IHaulDestination"/>。原版搬運 Job 的目標必須是 Thing，
    /// 而 comp 不是 Thing，硬登記進 haulDestinationManager 也搬不進來。飛彈進出平台一律走模組
    /// 既有的自訂 Job（<see cref="JobDriver_LoadMissileLauncher"/> 等），與其他平台行為一致。
    /// </summary>
    public class CompMissileRackStorage : ThingComp, IThingHolder, IStoreSettingsParent, IMissileRack
    {
        protected ThingOwner<Thing> innerContainer;
        protected StorageSettings settings;
        private static readonly StringBuilder sb = new StringBuilder();

        private MissileRackExtension rackExtInt;
        private bool rackExtResolved;

        public CompProperties_MissileRackStorage Props => (CompProperties_MissileRackStorage)props;

        /// <summary>容器本體，供 <see cref="CompMissileLauncher"/> 取用。</summary>
        public ThingOwner HeldContainer
        {
            get
            {
                EnsureInitialized();
                return innerContainer;
            }
        }

        // ---------------- IMissileRack / IThingHolder ----------------

        public MissileRackExtension RackExt
        {
            get
            {
                if (!rackExtResolved)
                {
                    rackExtInt = parent.def.GetModExtension<MissileRackExtension>();
                    rackExtResolved = true;
                }
                return rackExtInt;
            }
        }

        /// <summary>容量：擴充優先，其次渲染 comp 的 maxStored，最後預設 4（與 <see cref="Building_MissileRack"/> 一致）。</summary>
        public int MaxStored
        {
            get
            {
                MissileRackExtension ext = RackExt;
                if (ext != null) { return Mathf.Max(0, ext.capacity); }
                return Renderer?.Props?.maxStored ?? 4;
            }
        }

        public IReadOnlyList<Thing> HeldThings
        {
            get
            {
                EnsureInitialized();
                return innerContainer.InnerListForReading;
            }
        }

        public int StoredCount
        {
            get
            {
                EnsureInitialized();
                return innerContainer.Count;
            }
        }

        public bool Full => StoredCount >= MaxStored;
        public Thing RackThing => parent;
        public CompMissileRackRenderer Renderer => parent.GetComp<CompMissileRackRenderer>();

        public void GetChildHolders(List<IThingHolder> outChildren)
            => ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, GetDirectlyHeldThings());

        public ThingOwner GetDirectlyHeldThings() => innerContainer;

        public bool Accepts(Thing t)
        {
            if (Full) { return false; }
            if (!GetStoreSettings().AllowedToAccept(t)) { return false; }
            return innerContainer.CanAcceptAnyOf(t);
        }

        public int SpaceRemainingFor(ThingDef _) => Mathf.Max(0, MaxStored - StoredCount);

        // ---------------- IStoreSettingsParent ----------------

        public bool StorageTabVisible => true;

        public StorageSettings GetStoreSettings()
        {
            EnsureInitialized();
            return settings;
        }

        public StorageSettings GetParentStoreSettings()
        {
            StorageSettings fixedStorageSettings = parent.def.building?.fixedStorageSettings;
            return fixedStorageSettings ?? StorageSettings.EverStorableFixedSettings();
        }

        public void Notify_SettingsChanged()
        {
            if (parent.Spawned)
            {
                parent.Map.haulDestinationManager.Notify_HaulDestinationChangedPriority();
            }
        }

        // ---------------- 生命週期 ----------------

        private void EnsureInitialized()
        {
            if (innerContainer == null)
            {
                innerContainer = new ThingOwner<Thing>(this, oneStackOnly: false);
            }
            if (settings == null)
            {
                settings = new StorageSettings(this);
                StorageSettings defaults = parent.def.building?.defaultStorageSettings;
                if (defaults != null)
                {
                    settings.CopyFrom(defaults);
                }
            }
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            EnsureInitialized();
            // 敵方陣地自動填充：def 沒掛 MissileRackAutoFillExtension 就直接返回，玩家建築不受影響。
            MissileAutoFillUtility.TryAutoFill(this, respawningAfterLoad);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            if (mode != DestroyMode.WillReplace && innerContainer != null)
            {
                innerContainer.TryDropAll(parent.Position, map, ThingPlaceMode.Near);
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            EnsureInitialized();
            Scribe_Deep.Look(ref innerContainer, "innerContainer", this);
            Scribe_Deep.Look(ref settings, "settings", this);
        }

        // ---------------- UI ----------------

        public override string CompInspectStringExtra()
        {
            sb.Clear();
            if (parent.Spawned)
            {
                sb.Append("DMSE_MissileRackStored".Translate(StoredCount, MaxStored));
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in StorageSettingsClipboard.CopyPasteGizmosFor(GetStoreSettings()))
            {
                yield return g;
            }
            foreach (Thing t in innerContainer)
            {
                Gizmo g = Building.SelectContainedItemGizmo(parent, t);
                if (g != null) { yield return g; }
            }
            if (DebugSettings.godMode && parent.def.GetModExtension<MissileRackExtension>() != null)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Fill missiles",
                    action = delegate
                    {
                        StorageSettings store = GetStoreSettings();
                        ThingDef toMake = null;
                        if (store?.filter != null)
                        {
                            foreach (ThingDef d in store.filter.AllowedThingDefs) { toMake = d; break; }
                        }
                        if (toMake == null) { return; }
                        while (!Full)
                        {
                            Thing missile = ThingMaker.MakeThing(toMake);
                            if (!innerContainer.TryAdd(missile)) { missile.Destroy(); break; }
                        }
                    }
                };
            }
        }
    }
}
