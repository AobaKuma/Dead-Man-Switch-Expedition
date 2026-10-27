using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 彈體專屬的發射 Projectile（<see cref="MissileBodyDef.launchProjectile"/>），取代平台共用的
    /// <see cref="ScorerProjectile"/>（對地離場）與 <see cref="InterceptProjectile"/>（攔截）。
    /// 同一彈體可能兩用（例如輕型導彈），所以同一個 def 依發射時設定的角色結算：
    ///   - 對地：帶 <see cref="worldObject"/>，升空結束時加入世界航跡（同舊 ScorerProjectile.LeaveMap）。
    ///   - 攔截：<see cref="isInterceptor"/>，升空結束時釋放火力通道並擲骰（同舊 InterceptProjectile.LeaveMap）。
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

        protected override void LeaveMap()
        {
            if (isInterceptor) { ResolveIntercept(); }
            base.LeaveMap();
            if (worldObject != null)
            {
                Find.WorldObjects.Add(worldObject);
                // 只有玩家自己的發射才跳到世界地圖（NPC 導彈陣地的升空彈不搶鏡頭）
                if (worldObject.Faction == Faction.OfPlayer) { CameraJumper.TryJump(worldObject); }
            }
        }

        /// <summary>中過程攔截結算（與舊 <see cref="InterceptProjectile"/> 相同）：釋放火力通道，依命中率乾淨擊毀目標。</summary>
        private void ResolveIntercept()
        {
            MapComponent_BVRCombat comp = Map != null ? Map.GetComponent<MapComponent_BVRCombat>() : null;
            if (comp == null || targetId < 0) { return; }
            BVRTarget target = comp.GetTarget(targetId);
            if (target == null) { return; }

            target.midcourseEngagedUntil = -1;
            if (Rand.Chance(hitChance))
            {
                MapComponent_InterceptEffects.Trigger(Map, InterceptEventData.From(target, Map, terminal: false, IntVec3.Invalid));
                comp.RemoveTarget(targetId);
                if (Prefs.DevMode) { Log.Message($"[DMSE BVR] 中過程攔截成功 target#{targetId}"); }
            }
            else if (Prefs.DevMode)
            {
                Log.Message($"[DMSE BVR] 中過程攔截失敗 target#{targetId}");
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
