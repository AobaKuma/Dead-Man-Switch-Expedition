using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>階段三：末端攔截砲塔（CIWS）。在末端窗口內自主運作，無需火控導引。</summary>
    public class CompProperties_TerminalCIWS : CompProperties
    {
        /// <summary>末端攔截彈（視覺用；實際是否擊落由 interceptChance 決定）。</summary>
        public ThingDef projectile;

        /// <summary>末端攔截窗口（ticks，約 30 秒 = 1800）。剩餘時間進入此區間才啟動。</summary>
        public int terminalWindowTicks = 1800;

        /// <summary>兩次攔截嘗試之間的冷卻（ticks）。</summary>
        public int cooldownTicks = 60;

        /// <summary>單次攔截嘗試最多發射的攔截彈數。</summary>
        public int shotsPerBurst = 5;

        /// <summary>每發攔截彈擊落目標的機率。</summary>
        public float interceptChance = 0.5f;

        /// <summary>擊落成功後生成殘骸（碎片）的機率。碎片僅在末端攔截中可能生成。</summary>
        public float debrisChance = 0.3f;

        public CompProperties_TerminalCIWS()
        {
            compClass = typeof(CompTerminalCIWS);
        }
    }

    public class CompTerminalCIWS : CompBVRDevice
    {
        public CompProperties_TerminalCIWS Props => (CompProperties_TerminalCIWS)props;

        public int cooldownUntil;

        public void TryEngage(BVRWave wave, int now)
        {
            if (!Active || now < cooldownUntil) { return; }

            int shots = Props.shotsPerBurst;
            while (shots > 0 && wave.targets.Count > 0)
            {
                BVRTarget target = wave.targets[0];
                FireVisual(target);
                shots--;

                if (Rand.Chance(Props.interceptChance))
                {
                    if (Rand.Chance(Props.debrisChance))
                    {
                        target.SpawnDebris(parent.Map);
                    }
                    else
                    {
                        target.Discard();
                    }
                    wave.targets.Remove(target);
                }
            }

            cooldownUntil = now + Props.cooldownTicks;
        }

        /// <summary>發射一發視覺攔截彈朝向目標預測落點（命中與否不由此決定）。</summary>
        private void FireVisual(BVRTarget target)
        {
            if (Props.projectile == null || parent.Map == null) { return; }

            try
            {
                Projectile pro = (Projectile)GenSpawn.Spawn(Props.projectile, parent.Position, parent.Map, WipeMode.Vanish);
                LocalTargetInfo t = new LocalTargetInfo(target.position);
                pro.Launch(parent, t, t, ProjectileHitFlags.All);
            }
            catch
            {
                // 視覺效果失敗不影響攔截判定。
            }
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            MapComponent_BVRCombat m = Manager;
            if (m != null) { m.ciwsTurrets.Add(this); }
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            MapComponent_BVRCombat m = map != null ? map.GetComponent<MapComponent_BVRCombat>() : null;
            if (m != null) { m.ciwsTurrets.Remove(this); }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref cooldownUntil, "cooldownUntil", 0);
        }
    }
}
