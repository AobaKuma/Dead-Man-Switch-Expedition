using System.Collections.Generic;
using System.Linq;
using Fortified;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 陣營的飛彈與防空準則：沒有地圖的據點用哪一套合成防空、地圖上的防空怎麼排序目標、導彈陣地打誰。
    /// 陣營用 <see cref="MissileDoctrineExtension"/> 指定；沒有指定時，依科技等級選
    /// <see cref="defaultMinTech"/>–<see cref="defaultMaxTech"/> 涵蓋該等級的準則。
    /// </summary>
    public class MissileDoctrineDef : Def
    {
        /// <summary>沒有地圖、也沒有專屬防空元件的據點用的合成防空；null = 沒有。</summary>
        public SettlementAirDefenseProps settlementDefense;

        /// <summary>地圖上的防空節點在通道不足時先打雷達截面最大的目標（玩家節點看戰情終端，不看這裡）。</summary>
        public bool threatAssessment;

        /// <summary>導彈陣地與發射站選目標的方式；null = 只打玩家地圖（原本的行為）。</summary>
        public StrikeTargetWorker targetWorker;

        /// <summary>作為科技等級預設時涵蓋的範圍；Undefined = 不當預設，只能由陣營明確指定。</summary>
        public TechLevel defaultMinTech = TechLevel.Undefined;
        public TechLevel defaultMaxTech = TechLevel.Undefined;

        private static readonly StrikeTargetWorker playersOnly = new StrikeTargetWorker_Players();

        public StrikeTargetWorker TargetWorker => targetWorker ?? playersOnly;

        public bool IsDefaultFor(TechLevel tech)
            => defaultMinTech != TechLevel.Undefined && tech >= defaultMinTech
               && (defaultMaxTech == TechLevel.Undefined || tech <= defaultMaxTech);

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors()) { yield return error; }
            if (defaultMaxTech != TechLevel.Undefined && defaultMaxTech < defaultMinTech)
            {
                yield return $"{defName}: defaultMaxTech is below defaultMinTech";
            }
            if (settlementDefense != null)
            {
                foreach (string error in settlementDefense.ConfigErrors(this)) { yield return error; }
            }
        }
    }

    /// <summary>沒有地圖的據點的合成防空（世界層抽象節點）。</summary>
    public class SettlementAirDefenseProps
    {
        /// <summary>雷達的世界探測半徑（格），再依目標特徵與反隱身縮放。</summary>
        public float detectionRange = 6f;
        public int antiStealthLevel = 1;
        public int channels = 1;
        public int maxStock = 2;
        public float restockPerDay = 0.5f;
        public int cooldownTicks = 900;
        public bool threatAssessment;

        /// <summary>提供火控參數（命中率、射程、攔截高度）的建築；取它的 <see cref="CompProperties_FireControlRadar"/>。</summary>
        public ThingDef fireControlDef;
        public MissileBodyDef interceptorBody;
        public MissilePartDef interceptorWarhead;

        [Unsaved] private MissileConfig interceptor;

        public CompProperties_FireControlRadar FireControl => fireControlDef?.comps?.OfType<CompProperties_FireControlRadar>().FirstOrDefault();

        public MissileConfig Interceptor
        {
            get
            {
                if (interceptor == null && interceptorBody != null)
                {
                    interceptor = new MissileConfig(interceptorBody);
                    if (interceptorWarhead != null) { interceptor.SetPart(MissilePartCategory.Warhead, interceptorWarhead); }
                }
                return interceptor;
            }
        }

        public IEnumerable<string> ConfigErrors(Def parent)
        {
            if (FireControl == null) { yield return $"{parent.defName}: settlementDefense.fireControlDef needs a fire-control radar comp"; }
            if (interceptorBody == null) { yield return $"{parent.defName}: settlementDefense.interceptorBody is required"; }
            if (channels < 1 || maxStock < 0 || restockPerDay < 0f || cooldownTicks < 0 || detectionRange < 0f)
            {
                yield return $"{parent.defName}: settlementDefense needs channels >= 1 and non-negative stock, restock, cooldown and range";
            }
        }
    }

    /// <summary>
    /// 掛在 <see cref="FactionDef"/> 上，指定陣營的準則。
    /// <c>&lt;li Class="DMSE.MissileDoctrineExtension"&gt;&lt;doctrine&gt;...&lt;/doctrine&gt;&lt;/li&gt;</c>
    /// </summary>
    public class MissileDoctrineExtension : DefModExtension
    {
        public MissileDoctrineDef doctrine;
    }

    /// <summary>查詢陣營的準則，以及依準則選導彈目標。</summary>
    public static class MissileDoctrine
    {
        /// <summary>陣營的準則：明確指定的優先，否則依科技等級的預設；都沒有回傳 null。</summary>
        public static MissileDoctrineDef For(Faction faction) => For(faction?.def, DefDatabase<MissileDoctrineDef>.AllDefsListForReading);

        public static MissileDoctrineDef For(FactionDef def, IEnumerable<MissileDoctrineDef> candidates)
        {
            if (def == null) { return null; }
            MissileDoctrineDef chosen = def.GetModExtension<MissileDoctrineExtension>()?.doctrine;
            if (chosen != null) { return chosen; }
            return candidates.FirstOrDefault(d => d.IsDefaultFor(def.techLevel));
        }

        /// <summary>
        /// 導彈陣地或發射站可以打的目標。<paramref name="inRange"/> 判斷射程與跨圖層能力；
        /// 選擇規則來自發射方陣營的準則。
        /// </summary>
        public static List<MapParent> Targets(MapParent source, Faction attacker, MissileConfig config, System.Predicate<MapParent> inRange)
        {
            var result = new List<MapParent>();
            if (config == null) { return result; }
            StrikeTargetWorker worker = For(attacker)?.TargetWorker ?? new StrikeTargetWorker_Players();
            var query = new StrikeTargetQuery { Source = source, Attacker = attacker, Loadout = config, InRange = inRange };
            worker.CollectTargets(query, result);
            result.RemoveAll(p => p == null || p.Destroyed || p == source || !inRange(p));
            return result;
        }

        public static MapParent Choose(MapParent source, Faction attacker, MissileConfig config, List<MapParent> targets)
        {
            if (targets.NullOrEmpty()) { return null; }
            StrikeTargetWorker worker = For(attacker)?.TargetWorker ?? new StrikeTargetWorker_Players();
            return worker.Choose(new StrikeTargetQuery { Source = source, Attacker = attacker, Loadout = config }, targets) ?? targets.RandomElement();
        }
    }
}
