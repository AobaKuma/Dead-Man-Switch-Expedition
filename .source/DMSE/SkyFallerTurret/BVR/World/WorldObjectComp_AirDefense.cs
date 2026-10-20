using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    public class CompProperties_WorldAirDefense : WorldObjectCompProperties
    {
        public float worldDetectionRange = 12f;
        public int antiStealthLevel = 2;
        public ThingDef fireControlDef;
        public int channels = 2;
        public int interceptorStock = 8;
        public float restockPerDay = 4f;
        public MissileBodyDef interceptorBody;
        public MissilePartDef interceptorWarhead;
        public int launchCooldownTicks = 240;
        /// <summary>威脅評估（P4）：通道不足時先接戰雷達截面最大的航跡（誘餌酬載會吸引它），否則先到先打。</summary>
        public bool threatAssessment = false;
        /// <summary>最低陣營科技等級；Undefined 表示不設限（DMSE 自己的導彈陣地維持不設限）。</summary>
        public TechLevel minTechLevel = TechLevel.Undefined;
        public CompProperties_WorldAirDefense() { compClass = typeof(WorldObjectComp_AirDefense); }
        public override IEnumerable<string> ConfigErrors(WorldObjectDef parentDef)
        {
            foreach (string error in base.ConfigErrors(parentDef)) yield return error;
            if (fireControlDef?.comps?.OfType<CompProperties_FireControlRadar>().FirstOrDefault() == null)
                yield return "World air defense requires a fire-control radar ThingDef.";
            if (interceptorBody == null || channels < 1 || interceptorStock < 0 || restockPerDay < 0)
                yield return "Invalid world air-defense inventory/body/channel configuration.";
        }
    }

    public class WorldObjectComp_AirDefense : WorldObjectComp
    {
        private float stock = -1;
        private int lastRestockTick = -1;
        private int cooldownUntil;
        private int radarDownUntil;
        public CompProperties_WorldAirDefense Props => (CompProperties_WorldAirDefense)props;
        public bool Available => parent is MapParent mp && !mp.HasMap && !parent.Destroyed && parent.Faction != null
            && !parent.Faction.IsPlayer
            && (parent.Faction.def == null || parent.Faction.def.techLevel >= Props.minTechLevel);
        /// <summary>反輻射打擊後雷達壓制期間：偵測歸零，但攔截彈庫存仍可被其他（有雷達的）節點資料鏈帶動。</summary>
        public bool RadarOnline => Find.TickManager.TicksGame >= radarDownUntil;
        public CompProperties_FireControlRadar FireControl => Props.fireControlDef?.comps?.OfType<CompProperties_FireControlRadar>().FirstOrDefault();
        public int Stock { get { Refresh(); return Mathf.FloorToInt(stock); } }
        public void Refresh()
        {
            int now = Find.TickManager.TicksGame;
            if (stock < 0) stock = Props.interceptorStock;
            if (lastRestockTick >= 0 && Available)
                stock = Mathf.Min(Props.interceptorStock, stock + Mathf.Max(0, now - lastRestockTick) * Props.restockPerDay / GenDate.TicksPerDay);
            lastRestockTick = now;
        }
        public bool Ready(int now) => Available && Stock > 0 && now >= cooldownUntil;
        public MissileConfig Config
        {
            get
            {
                var config = new MissileConfig(Props.interceptorBody);
                config.SetPart(MissilePartCategory.Warhead, Props.interceptorWarhead);
                return config;
            }
        }
        public bool TryConsume(int now)
        {
            if (!Ready(now)) return false;
            stock -= 1;
            cooldownUntil = now + Props.launchCooldownTicks;
            return true;
        }

        /// <summary>
        /// 反輻射彈命中抽象節點（P4）：打掉兩發庫存並壓制雷達一天，不生成地圖。
        /// </summary>
        public void Degrade(int now)
        {
            Refresh();
            stock = Mathf.Max(0, stock - 2);
            radarDownUntil = now + GenDate.TicksPerDay;
            cooldownUntil = Mathf.Max(cooldownUntil, now + 600);
        }
        public override void CompTick() { base.CompTick(); Refresh(); }
        public override string CompInspectStringExtra() => Available
            ? "DMSE.BVR.World.AbstractInspect".Translate(Stock, Props.interceptorStock, Props.worldDetectionRange, Props.channels) : null;
        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref stock, "bvrStock", -1f);
            Scribe_Values.Look(ref lastRestockTick, "bvrRestockTick", -1);
            Scribe_Values.Look(ref cooldownUntil, "bvrCooldownUntil");
            Scribe_Values.Look(ref radarDownUntil, "bvrRadarDownUntil");
        }
    }
}
