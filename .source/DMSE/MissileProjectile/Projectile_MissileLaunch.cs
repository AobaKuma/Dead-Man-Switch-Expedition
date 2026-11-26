using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 導彈的升空 Projectile：彈體專屬的 <see cref="MissileBodyDef.launchProjectile"/>，或平台設定的預設升空 def
    /// （2026-11-26 起取代舊的 ScorerProjectile／InterceptProjectile skyfaller，兩者已刪除）。
    /// 同一彈體可能兩用（例如輕型導彈），所以同一個 def 依發射時設定的角色結算：
    ///   - 對地：帶 <see cref="worldObject"/>，升空結束時加入世界航跡。
    ///   - 攔截：<see cref="isInterceptor"/>，升空結束時釋放火力通道並擲骰。
    /// 升空外觀與尾焰由 def 的 graphicData、<see cref="MissileFlightExtension"/> 與發射特效 comp 決定。
    /// </summary>
    public class Projectile_MissileLaunch : Projectile_MissileBase, IInterceptorShot
    {
        /// <summary>對地發射：離場後加入世界的航跡物件。</summary>
        public WorldObject worldObject;

        /// <summary>攔截發射：目標 <see cref="BVRTarget.id"/>（-1 = 世界層攔截的純視覺升空彈）與命中率。</summary>
        public bool isInterceptor;
        public int targetId = -1;
        public float hitChance = 0.9f;

        public int TargetId => targetId;
        public float HitChance { get => hitChance; set => hitChance = value; }

        private CompEffecterLaunch launchComp;
        private Graphic separatedGraphic;

        /// <summary>助推器分離後改畫不含助推器的貼圖（<see cref="CompProperties_EffecterLaunch.separatedTexPath"/>）。</summary>
        public override Graphic Graphic
        {
            get
            {
                launchComp ??= GetComp<CompEffecterLaunch>();
                if (launchComp != null && !launchComp.Props.separatedTexPath.NullOrEmpty() && launchComp.BoosterSeparated)
                {
                    return separatedGraphic ??= GraphicDatabase.Get<Graphic_Single>(launchComp.Props.separatedTexPath,
                        def.graphic.Shader, def.graphicData.drawSize, def.graphic.Color);
                }
                return base.Graphic;
            }
        }

        protected override void LeaveMap()
        {
            if (isInterceptor) { ResolveIntercept(Map, targetId, hitChance); }
            base.LeaveMap();
            // 出圖不切鏡頭；只有電視制導抵達選點時才跳到目標地圖。
            if (worldObject != null) { Find.WorldObjects.Add(worldObject); }
        }

        /// <summary>中過程攔截結算：釋放火力通道，依命中率乾淨擊毀目標（<paramref name="targetId"/> = -1 為純視覺，不結算）。</summary>
        public static void ResolveIntercept(Map map, int targetId, float hitChance)
        {
            MapComponent_BVRCombat comp = map != null ? map.GetComponent<MapComponent_BVRCombat>() : null;
            if (comp == null || targetId < 0) { return; }
            BVRTarget target = comp.GetTarget(targetId);
            if (target == null) { return; }

            target.NotifyInterceptorResolved();
            if (Rand.Chance(hitChance))
            {
                MapComponent_InterceptEffects.Trigger(map, InterceptEventData.From(target, map, terminal: false, IntVec3.Invalid));
                comp.ResolveMidcourseHit(target, hitChance, hit: true);
                if (Prefs.DevMode) { Log.Message($"[DMSE BVR] 中過程攔截成功 target#{targetId}"); }
            }
            else
            {
                comp.ResolveMidcourseHit(target, hitChance, hit: false);
                if (Prefs.DevMode) { Log.Message($"[DMSE BVR] 中過程攔截失敗 target#{targetId}"); }
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Deep.Look(ref worldObject, "worldObject");
            Scribe_Values.Look(ref isInterceptor, "isInterceptor", false);
            Scribe_Values.Look(ref targetId, "targetId", -1);
            Scribe_Values.Look(ref hitChance, "hitChance", 0.9f);
        }
    }
}
