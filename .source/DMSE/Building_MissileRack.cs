using System.Collections.Generic;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 2x4 導彈儲存架。將導彈吸入內部容器（unspawned），最多 4 枚。
    /// 採用與基礎遊戲 Building_Bookcase 相同的模式：實作 IHaulDestination 後
    /// 小人會自動把導彈搬入；實際渲染交由 <see cref="CompMissileRackRenderer"/> 依旋轉方向排序繪製。
    ///
    /// 由於導彈被收入容器後為 unspawned，CompDurabilityDecay 的容器庇護判定
    /// 會沿 ParentHolder 找到此建築 → 視為受庇護（且 unspawned 物品不 tick，自然停止劣化）。
    /// </summary>
    public class Building_MissileRack : Building, IThingHolder, IHaulDestination, IStoreSettingsParent
    {
        protected ThingOwner<Thing> innerContainer;
        protected StorageSettings settings;

        private CompMissileRackRenderer rendererCompInt;
        private static readonly StringBuilder sb = new StringBuilder();

        public Building_MissileRack()
        {
            innerContainer = new ThingOwner<Thing>(this, oneStackOnly: false);
        }

        public CompMissileRackRenderer Renderer =>
            rendererCompInt ?? (rendererCompInt = GetComp<CompMissileRackRenderer>());

        /// <summary>最大可存放數量。預設取自渲染 Comp 設定，缺省為 4。</summary>
        public int MaxStored => Renderer?.Props?.maxStored ?? 4;

        public IReadOnlyList<Thing> HeldThings => innerContainer.InnerListForReading;
        public int StoredCount => innerContainer.Count;
        public bool Full => StoredCount >= MaxStored;

        // ---------------- IThingHolder ----------------
        public void GetChildHolders(List<IThingHolder> outChildren)
            => ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, GetDirectlyHeldThings());

        public ThingOwner GetDirectlyHeldThings() => innerContainer;

        // ---------------- IStoreSettingsParent ----------------
        public bool StorageTabVisible => true;

        public StorageSettings GetStoreSettings() => settings;

        public StorageSettings GetParentStoreSettings()
        {
            StorageSettings fixedStorageSettings = def.building.fixedStorageSettings;
            return fixedStorageSettings ?? StorageSettings.EverStorableFixedSettings();
        }

        public void Notify_SettingsChanged()
        {
            if (Spawned)
            {
                Map.haulDestinationManager.Notify_HaulDestinationChangedPriority();
            }
        }

        // ---------------- IHaulDestination ----------------
        public bool HaulDestinationEnabled => true;

        public bool Accepts(Thing t)
        {
            if (Full)
            {
                return false;
            }
            if (!GetStoreSettings().AllowedToAccept(t))
            {
                return false;
            }
            return innerContainer.CanAcceptAnyOf(t);
        }

        public int SpaceRemainingFor(ThingDef _) => Mathf.Max(0, MaxStored - StoredCount);

        // ---------------- 生命週期 ----------------
        public override void PostMake()
        {
            base.PostMake();
            settings = new StorageSettings(this);
            if (def.building.defaultStorageSettings != null)
            {
                settings.CopyFrom(def.building.defaultStorageSettings);
            }
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            if (mode != DestroyMode.WillReplace)
            {
                innerContainer.TryDropAll(Position, Map, ThingPlaceMode.Near);
            }
            base.DeSpawn(mode);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Deep.Look(ref innerContainer, "innerContainer", this);
            Scribe_Deep.Look(ref settings, "settings", this);
        }

        // ---------------- UI ----------------
        public override string GetInspectString()
        {
            sb.Clear();
            sb.Append(base.GetInspectString());
            if (Spawned)
            {
                if (sb.Length > 0)
                {
                    sb.AppendLine();
                }
                sb.Append("DMSE_MissileRackStored".Translate(StoredCount, MaxStored));
            }
            return sb.ToString();
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
            {
                yield return g;
            }
            foreach (Gizmo g in StorageSettingsClipboard.CopyPasteGizmosFor(settings))
            {
                yield return g;
            }
            foreach (Thing t in innerContainer)
            {
                Gizmo g = Building.SelectContainedItemGizmo(this, t);
                if (g != null)
                {
                    yield return g;
                }
            }
            if (DebugSettings.godMode)
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
                            foreach (ThingDef d in store.filter.AllowedThingDefs)
                            {
                                toMake = d;
                                break;
                            }
                        }
                        if (toMake == null)
                        {
                            return;
                        }
                        while (!Full)
                        {
                            Thing missile = ThingMaker.MakeThing(toMake);
                            if (!innerContainer.TryAdd(missile))
                            {
                                missile.Destroy();
                                break;
                            }
                        }
                    }
                };
            }
        }
    }
}
