using System.Collections.Generic;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 導彈儲存平台的統一描述擴充（容量 + 量級 + 發射模式 + 渲染選項），與「渲染 Comp」分離。
    /// 掛在 ThingDef 的 modExtensions 即可宣告容量、量級（sizeClass）、發射模式（launchMode）與是否渲染彈體，
    /// <b>不需</b> <see cref="CompMissileRackRenderer"/>，因此「發射箱／發射管」等類型亦可只設定資料。
    ///
    /// 渲染判定見 <see cref="ShowStoredBody"/>：由總開關 <see cref="renderStoredBody"/> 決定是否繪製彈體
    ///（封閉發射箱／發射管設為 false 即可）。量級（sizeClass）與發射模式（launchMode）僅用於裝配相容性判定，不影響渲染。
    ///
    /// 此為基底；發射平台另用 <see cref="MissileLauncherExtension"/>（繼承本類）加掛裝配分類語意。
    /// 未掛任何此類擴充時，<see cref="Building_MissileRack"/> 會向後相容地沿用渲染 Comp 的 maxStored。
    /// </summary>
    public class MissileRackExtension : DefModExtension
    {
        /// <summary>最大可存放數量（容量）。</summary>
        public int capacity = 4;

        /// <summary>導彈尺寸分級（量級）：用於裝配相容性判定（不影響渲染）。</summary>
        public MissileSizeClass sizeClass = MissileSizeClass.Medium;

        /// <summary>發射方式：用於裝配相容性判定（不影響渲染）。Tilt 或 Vertical；不應為 Both（平台限制）。</summary>
        public MissileLaunchMode launchMode = MissileLaunchMode.Tilt;

        /// <summary>
        /// 是否渲染容器中的彈體（總開關）。預設 true。
        /// 設為 false 則一律不繪製彈體（如封閉發射箱／發射管）。
        /// </summary>
        public bool renderStoredBody = true;

        /// <summary>是否顯示容器中的彈體。由 <see cref="renderStoredBody"/> 決定。</summary>
        public bool ShowStoredBody => renderStoredBody;

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors())
            {
                yield return e;
            }
            if (capacity < 1)
            {
                yield return "MissileRackExtension.capacity 必須 >= 1。";
            }
        }
    }

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

        private MissileRackExtension rackExtInt;
        private bool rackExtResolved;

        /// <summary>
        /// 統一描述擴充（容量／量級／發射模式／渲染選項；可能為 null）。
        /// 以 GetModExtension&lt;MissileRackExtension&gt;() 取得，依 is 比對亦涵蓋子類
        /// <see cref="MissileLauncherExtension"/>，故發射平台與純彈架共用同一來源。
        /// </summary>
        public MissileRackExtension RackExt
        {
            get
            {
                if (!rackExtResolved)
                {
                    rackExtInt = def.GetModExtension<MissileRackExtension>();
                    rackExtResolved = true;
                }
                return rackExtInt;
            }
        }

        /// <summary>
        /// 最大可存放數量（容量）。容量來源與「渲染」分離：
        ///   1) 優先採用 def 的 <see cref="MissileRackExtension"/>.capacity（涵蓋子類 MissileLauncherExtension）。
        ///   2) 向後相容：未指定擴充時，沿用渲染 Comp 的 maxStored。
        ///   3) 皆無則預設 4。
        /// </summary>
        public int MaxStored
        {
            get
            {
                MissileRackExtension ext = RackExt;
                if (ext != null) { return Mathf.Max(0, ext.capacity); }
                return Renderer?.Props?.maxStored ?? 4;
            }
        }

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

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            // 敵方陣地生成後自動填充：讀取 MissileRackAutoFillExtension 規則，依 Faction TechLevel 選彈。
            // 載入存檔（respawningAfterLoad=true）時跳過，容器內容已由 ExposeData 還原。
            MissileAutoFillUtility.TryAutoFill(this, respawningAfterLoad);
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
