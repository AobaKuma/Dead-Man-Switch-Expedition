using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>
    /// NPC 陣地彈架的裝填與補給設定（<see cref="CompProperties_MissileBase.rackSupply"/>）。<br/>
    /// ‧ 生成時依 <see cref="loadouts"/> 裝填（<see cref="GenStep_MissileBaseRackFill"/>）。<br/>
    /// ‧ 地圖存在期間每 <see cref="intervalDays"/> 天，為每座空彈架各空投一艙導彈，數量與彈種依彈架類型決定；
    ///   落地 <see cref="loadDelayTicks"/> 後若導彈仍在彈架附近，由守軍裝入（<see cref="MapComponent_MissileBase"/>）。
    /// </summary>
    public class MissileBaseRackSupply
    {
        public float intervalDays = 2f;

        /// <summary>生成地圖時依裝填表重新裝填所有敵方彈架。</summary>
        public bool fillOnGenerate = true;

        /// <summary>補給艙開艙後到守軍裝填完成的時間；期間玩家可以搶走或摧毀導彈。</summary>
        public int loadDelayTicks = 2500;

        /// <summary>導彈離彈架超過此距離（格）就不會被裝填。</summary>
        public float loadRadius = 12f;

        /// <summary>依彈架 ThingDef 指定彈種；未列出的彈架改用該建築的 <see cref="MissileRackAutoFillExtension"/> 規則。</summary>
        public List<MissileRackLoadout> loadouts;

        /// <summary>守軍自行從彈藥庫／儲存架搬導彈裝填發射架；同時最多幾人在搬。0 = 關閉。</summary>
        public int maxCrewLoaders = 2;

        public MissileRackLoadout LoadoutFor(ThingDef rackDef) => loadouts?.FirstOrDefault(l => l.rack == rackDef);
    }

    public class MissileRackLoadout
    {
        public ThingDef rack;

        /// <summary>每發從中隨機挑一種；彈架不接受的會被濾掉。</summary>
        public List<ThingDef> missiles;

        /// <summary>每次裝填／補給的發數；-1 = 裝滿。</summary>
        public int count = -1;
    }

    public static class MissileBaseRackSupplyUtility
    {
        /// <summary>發射平台（有發射元件）；其餘導彈平台視為彈藥庫／儲存架。</summary>
        public static bool IsLauncher(IMissileRack rack) => rack.RackThing is Building b && WorkGiver_LoadMissileLauncher.IsLauncher(b);

        /// <summary><paramref name="source"/> 裡有沒有 <paramref name="launcher"/> 收得下的導彈。</summary>
        public static bool HasMissileFor(IMissileRack source, IMissileRack launcher)
        {
            StorageSettings settings = launcher.GetStoreSettings();
            return source.HeldThings.Any(m => settings?.AllowedToAccept(m) ?? launcher.Accepts(m));
        }

        /// <summary>地圖上屬於 <paramref name="faction"/> 的所有導彈平台。</summary>
        public static List<IMissileRack> RacksOf(Map map, Faction faction)
        {
            List<IMissileRack> result = new List<IMissileRack>();
            if (faction == null) return result;
            foreach (Building b in map.listerBuildings.allBuildingsNonColonist)
            {
                if (b.Faction != faction || b.Destroyed) continue;
                IMissileRack rack = MissileRackUtil.Of(b);
                if (rack != null && rack.MaxStored > 0) result.Add(rack);
            }
            return result;
        }

        /// <summary>依裝填表，否則依建築自動填充規則，產生要裝進 <paramref name="rack"/> 的導彈（不超過剩餘空間）。</summary>
        public static List<Thing> MakeLoad(IMissileRack rack, MissileBaseRackSupply supply)
        {
            List<Thing> result = new List<Thing>();
            List<ThingDef> pool = PoolFor(rack, supply, out int count);
            if (pool.NullOrEmpty()) return result;

            int space = rack.MaxStored - rack.StoredCount;
            int n = count < 0 ? space : System.Math.Min(count, space);
            for (int i = 0; i < n; i++)
                result.Add(ThingMaker.MakeThing(pool.RandomElement()));
            return result;
        }

        private static List<ThingDef> PoolFor(IMissileRack rack, MissileBaseRackSupply supply, out int count)
        {
            ThingFilter filter = rack.RackThing.def.building?.fixedStorageSettings?.filter;
            MissileRackLoadout loadout = supply?.LoadoutFor(rack.RackThing.def);
            if (loadout != null)
            {
                count = loadout.count;
                return loadout.missiles?.Where(d => d != null && (filter == null || filter.Allows(d))).ToList();
            }

            count = -1;
            MissileRackAutoFillExtension ext = rack.RackThing.def.GetModExtension<MissileRackAutoFillExtension>();
            MissileAutoFillRule rule = ext == null ? null
                : MissileAutoFillUtility.FindFirstMatchingRule(ext.fillRules, rack.RackThing.Faction, rack.RackThing.MapHeld);
            if (rule == null) return null;
            if (rule.fillCount >= 0) count = rule.fillCount;
            return MissileAutoFillUtility.ResolvePool(rule, rack);
        }

        /// <summary>直接裝入（生成期）；裝不進的導彈銷毀。回傳裝入數。</summary>
        public static int LoadDirect(IMissileRack rack, List<Thing> missiles)
        {
            int loaded = 0;
            foreach (Thing m in missiles)
            {
                if (rack.Accepts(m) && rack.GetDirectlyHeldThings().TryAdd(m)) loaded++;
                else m.Destroy();
            }
            return loaded;
        }
    }

    /// <summary>補給艙送來、等待守軍裝填的一枚導彈。</summary>
    public class PendingRackLoad : IExposable
    {
        public Thing missile;
        public Thing rack;
        public int loadTick;

        public void ExposeData()
        {
            Scribe_References.Look(ref missile, "missile");
            Scribe_References.Look(ref rack, "rack");
            Scribe_Values.Look(ref loadTick, "loadTick");
        }
    }

    /// <summary>
    /// 地圖生成時依站點的 <see cref="MissileBaseRackSupply"/> 裝填敵方彈架：有裝填表的彈架清空後重裝，
    /// 其餘仍是空的彈架以建築自身的自動填充規則補上。須排在結構生成之後。
    /// </summary>
    public class GenStep_MissileBaseRackFill : GenStep
    {
        public override int SeedPart => 0x4A71C2E9;

        public override void Generate(Map map, GenStepParams parms)
        {
            Site site = map.Parent as Site;
            MissileBaseRackSupply supply = site?.GetComponent<WorldObjectComp_MissileBase>()?.Props.rackSupply;
            if (supply == null || !supply.fillOnGenerate) return;

            Faction faction = site.Faction ?? map.ParentFaction;
            int racks = 0, missiles = 0;
            foreach (IMissileRack rack in MissileBaseRackSupplyUtility.RacksOf(map, faction))
            {
                if (supply.LoadoutFor(rack.RackThing.def) != null)
                    rack.GetDirectlyHeldThings().ClearAndDestroyContents();
                else if (rack.StoredCount > 0)
                    continue;

                int n = MissileBaseRackSupplyUtility.LoadDirect(rack, MissileBaseRackSupplyUtility.MakeLoad(rack, supply));
                if (n > 0) { racks++; missiles += n; }
            }

            if (Prefs.DevMode)
                Log.Message($"[DMSE MissileBase] 生成裝填：{racks} 座彈架，共 {missiles} 發。");
        }
    }
}
