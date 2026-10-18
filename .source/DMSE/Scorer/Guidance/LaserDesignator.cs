using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 雷射引導標點：由雷射標定器（<see cref="Projectile_LaserDesignator"/>）或標定能力
    /// （<see cref="CompAbilityEffect_LaserDesignate"/>）在落點生成的不可破壞標記。
    /// 同陣營發射的雷射制導導彈（<see cref="GuidanceType_Laser"/>）抵達該地圖時鎖定最近放置的標點；
    /// 存在時間到了自動消失。
    ///
    /// ThingCategory 為 Ethereal：不會被攻擊、搬運或拆除（useHitPoints = false 亦不受爆炸傷害）。
    /// Ethereal 物件不能掛陣營，標定方存在 <see cref="owner"/>。
    /// </summary>
    public class LaserDesignatorMark : Thing
    {
        /// <summary>標定方陣營：只有此陣營的雷射制導導彈會鎖定這個標點。</summary>
        public Faction owner;

        /// <summary>放置時的遊戲 tick（多個標點時取最近放置者）。</summary>
        public int placedTick = -1;

        /// <summary>失效的遊戲 tick；-1 = 永不失效。</summary>
        public int expireTick = -1;

        public void Initialize(Faction owner, int lifespanTicks)
        {
            int now = Find.TickManager.TicksGame;
            this.owner = owner;
            placedTick = now;
            expireTick = lifespanTicks > 0 ? now + lifespanTicks : -1;
        }

        /// <summary>剩餘 tick；永不失效時為 -1。</summary>
        public int TicksLeft => expireTick < 0 ? -1 : Mathf.Max(0, expireTick - Find.TickManager.TicksGame);

        /// <summary>此標點是否可供 <paramref name="faction"/> 的雷射制導導彈鎖定。</summary>
        public bool IsActiveFor(Faction faction)
        {
            return Spawned && !Destroyed && owner == faction && TicksLeft != 0;
        }

        protected override void Tick()
        {
            base.Tick();
            if (expireTick >= 0 && Find.TickManager.TicksGame >= expireTick && !Destroyed)
            {
                Destroy(DestroyMode.Vanish);
            }
        }

        /// <summary>依 def 的圖像繪製，並以縮放脈動提示這是作用中的標定點。</summary>
        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            float pulse = 1f + 0.18f * Mathf.Sin(Time.realtimeSinceStartup * 6f);
            Vector2 size = def.graphicData != null ? def.graphicData.drawSize : Vector2.one;
            Matrix4x4 matrix = Matrix4x4.TRS(drawLoc, Quaternion.identity, new Vector3(size.x * pulse, 1f, size.y * pulse));
            Graphics.DrawMesh(MeshPool.plane10, matrix, Graphic.MatSingle, 0);
        }

        public override string GetInspectString()
        {
            string text = base.GetInspectString();
            string ownerName = owner != null ? owner.Name : "?";
            int left = TicksLeft;
            string line = left < 0
                ? "DMSE.LaserMark.InspectPermanent".Translate(ownerName)
                : "DMSE.LaserMark.Inspect".Translate(ownerName, left.ToStringTicksToPeriod());
            return text.NullOrEmpty() ? line : text + "\n" + line;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref owner, "owner");
            Scribe_Values.Look(ref placedTick, "placedTick", -1);
            Scribe_Values.Look(ref expireTick, "expireTick", -1);
        }
    }

    /// <summary>雷射引導標點的生成與查詢。</summary>
    public static class LaserDesignatorUtility
    {
        public const string DefaultMarkDefName = "DMSE_LaserDesignatorMark";

        /// <summary>標點預設存在時間（ticks）：2 遊戲小時。</summary>
        public const int DefaultLifespanTicks = 5000;

        private static List<ThingDef> markDefs;

        /// <summary>所有 thingClass 為 <see cref="LaserDesignatorMark"/>（含子類）的 ThingDef。</summary>
        public static List<ThingDef> MarkDefs
        {
            get
            {
                if (markDefs == null)
                {
                    markDefs = DefDatabase<ThingDef>.AllDefsListForReading
                        .Where(d => d.thingClass != null && typeof(LaserDesignatorMark).IsAssignableFrom(d.thingClass))
                        .ToList();
                }
                return markDefs;
            }
        }

        /// <summary>
        /// 在 <paramref name="cell"/> 生成一個屬於 <paramref name="owner"/> 的標點。
        /// <paramref name="markDef"/> 為 null 時用 <see cref="DefaultMarkDefName"/>；<paramref name="lifespanTicks"/> ≤ 0 表示永不失效。
        /// </summary>
        public static LaserDesignatorMark PlaceMark(IntVec3 cell, Map map, Faction owner, ThingDef markDef, int lifespanTicks)
        {
            if (map == null || !cell.InBounds(map)) { return null; }

            markDef = markDef ?? DefDatabase<ThingDef>.GetNamedSilentFail(DefaultMarkDefName);
            if (markDef == null || !typeof(LaserDesignatorMark).IsAssignableFrom(markDef.thingClass))
            {
                Log.ErrorOnce("[DMSE] Laser designator mark def missing or not a DMSE.LaserDesignatorMark.", 0x4C415352);
                return null;
            }

            LaserDesignatorMark mark = (LaserDesignatorMark)ThingMaker.MakeThing(markDef);
            mark.Initialize(owner, lifespanTicks);
            GenSpawn.Spawn(mark, cell, map);

            if (owner != null && owner.IsPlayer)
            {
                Messages.Message(
                    lifespanTicks > 0
                        ? "DMSE.LaserMark.Placed".Translate(lifespanTicks.ToStringTicksToPeriod())
                        : "DMSE.LaserMark.PlacedPermanent".Translate(),
                    mark, MessageTypeDefOf.SilentInput, historical: false);
            }
            return mark;
        }

        /// <summary>地圖上屬於 <paramref name="faction"/>、仍有效的標點中最近放置者；沒有則為 null。</summary>
        public static LaserDesignatorMark LatestMarkFor(Map map, Faction faction)
        {
            if (map == null) { return null; }
            LaserDesignatorMark best = null;
            List<ThingDef> defs = MarkDefs;
            for (int i = 0; i < defs.Count; i++)
            {
                List<Thing> things = map.listerThings.ThingsOfDef(defs[i]);
                for (int j = 0; j < things.Count; j++)
                {
                    if (things[j] is LaserDesignatorMark mark && mark.IsActiveFor(faction)
                        && (best == null || mark.placedTick > best.placedTick))
                    {
                        best = mark;
                    }
                }
            }
            return best;
        }
    }

    /// <summary>
    /// 掛在雷射標定彈（<see cref="Projectile_LaserDesignator"/>）的 ThingDef 上：指定生成的標點 def 與存在時間。
    /// </summary>
    public class ModExtension_LaserDesignator : DefModExtension
    {
        /// <summary>生成的標點；null = <see cref="LaserDesignatorUtility.DefaultMarkDefName"/>。</summary>
        public ThingDef markDef;

        /// <summary>標點存在時間（ticks）；≤ 0 = 永不失效。</summary>
        public int lifespanTicks = LaserDesignatorUtility.DefaultLifespanTicks;
    }

    /// <summary>
    /// 雷射標定器（裝備）射出的標定彈：不造成傷害，命中處生成屬於射手陣營的 <see cref="LaserDesignatorMark"/>。
    /// 被護盾擋下時不生成。
    /// </summary>
    public class Projectile_LaserDesignator : Projectile
    {
        protected override void Impact(Thing hitThing, bool blockedByShield = false)
        {
            Map map = Map;
            IntVec3 cell = Position;
            Faction owner = Launcher?.Faction;
            ModExtension_LaserDesignator ext = def.GetModExtension<ModExtension_LaserDesignator>();

            Destroy(DestroyMode.Vanish);

            if (!blockedByShield && map != null)
            {
                LaserDesignatorUtility.PlaceMark(cell, map, owner, ext?.markDef,
                    ext?.lifespanTicks ?? LaserDesignatorUtility.DefaultLifespanTicks);
            }
        }
    }

    /// <summary>
    /// 雷射標定能力的效果設定：能力對目標格施放時生成屬於施放者陣營的標點。
    /// 用法：在 AbilityDef 的 comps 中加入 <c>&lt;li Class="DMSE.CompProperties_AbilityLaserDesignate" /&gt;</c>，
    /// 並以 <c>verbProperties.targetParams.canTargetLocations = true</c> 讓能力可點地面。
    /// </summary>
    public class CompProperties_AbilityLaserDesignate : CompProperties_AbilityEffect
    {
        /// <summary>生成的標點；null = <see cref="LaserDesignatorUtility.DefaultMarkDefName"/>。</summary>
        public ThingDef markDef;

        /// <summary>標點存在時間（ticks）；≤ 0 = 永不失效。</summary>
        public int lifespanTicks = LaserDesignatorUtility.DefaultLifespanTicks;

        public CompProperties_AbilityLaserDesignate()
        {
            compClass = typeof(CompAbilityEffect_LaserDesignate);
        }
    }

    public class CompAbilityEffect_LaserDesignate : CompAbilityEffect
    {
        public new CompProperties_AbilityLaserDesignate Props => (CompProperties_AbilityLaserDesignate)props;

        public override void Apply(LocalTargetInfo target, LocalTargetInfo dest)
        {
            base.Apply(target, dest);
            Pawn pawn = parent.pawn;
            if (pawn?.Map == null) { return; }
            LaserDesignatorUtility.PlaceMark(target.Cell, pawn.Map, pawn.Faction, Props.markDef, Props.lifespanTicks);
        }
    }
}
