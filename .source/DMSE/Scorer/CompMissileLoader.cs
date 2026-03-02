using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DMSE
{
    /// <summary>
    /// ???射器???件?性 (Missile launcher loader component properties)
    /// </summary>
    public class CompProperties_MissileLoader : CompProperties
    {
        public CompProperties_MissileLoader()
        {
            this.compClass = typeof(CompMissileLoader);
        }

        // ??容量
        public int maxMissiles = 4;             // 最大???量 (Max missiles)
        public int maxAmmo = 100;               // 最大???位 (Max ammo units)
        
        // ??/准???
        public int ticksToLoadMissile = 1200;  // ???枚??需要的ticks (Ticks to load one missile)
        public int ticksToLoadWarhead = 600;   // ????需要的ticks (Ticks to load one warhead)
        public int ticksToLoadFuel = 2400;     // ??燃料需要的ticks (Ticks to load fuel)
        
        // 可用的制?方法和??
        public List<MissileGuidanceDef> availableGuidances = new List<MissileGuidanceDef>();
        public List<MissileWarheadDef> availableWarheads = new List<MissileWarheadDef>();
        
        // UI和交互
        public string loaderLabel = "MissileLoader";
        public bool showLoaderUI = true;
        public bool requiresPowerToLoad = true;
    }

    /// <summary>
    /// ?????据?构 (Missile loading data structure)
    /// </summary>
    public class MissileLoadout : IExposable
    {
        public int missileIndex;
        public MissileGuidanceState guidanceState;
        public MissileWarheadData warheadData;
        public bool isLoaded = false;
        public bool isReadyToLaunch = false;
        
        public MissileLoadout() { }
        
        public MissileLoadout(int index)
        {
            missileIndex = index;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref missileIndex, "missileIndex", 0);
            Scribe_Deep.Look(ref guidanceState, "guidanceState");
            Scribe_Deep.Look(ref warheadData, "warheadData");
            Scribe_Values.Look(ref isLoaded, "isLoaded", false);
            Scribe_Values.Look(ref isReadyToLaunch, "isReadyToLaunch", false);
        }

        public string GetLoadoutInfo()
        {
            string info = $"Missile #{missileIndex}\n";
            
            if (guidanceState?.guidanceDef != null)
            {
                info += $"Guidance: {guidanceState.guidanceDef.label}\n";
            }
            else
            {
                info += "Guidance: None\n";
            }

            if (warheadData?.warheadDef != null)
            {
                info += $"Warhead: {warheadData.warheadDef.label}\n";
            }
            else
            {
                info += "Warhead: None\n";
            }

            info += $"Status: {(isReadyToLaunch ? "Ready" : isLoaded ? "Loaded" : "Empty")}\n";
            return info;
        }
    }

    /// <summary>
    /// ???射器???件 (Missile launcher loader component)
    /// </summary>
    public class CompMissileLoader : ThingComp
    {
        public CompProperties_MissileLoader Props => (CompProperties_MissileLoader)this.props;

        private List<MissileLoadout> missileLoadouts = new List<MissileLoadout>();
        private int currentLoadingMissileIndex = -1;
        private int loadingProgressTicks = 0;
        private LoadingPhase currentLoadingPhase = LoadingPhase.Idle;
        
        private int ammoCount = 0;
        private int fuelCount = 100;

        private CompPowerTrader powerComp;
        private CompRefuelable refuelComp;

        private enum LoadingPhase
        {
            Idle,
            LoadingMissile,
            LoadingWarhead,
            LoadingFuel,
            Complete
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            
            powerComp = parent.GetComp<CompPowerTrader>();
            refuelComp = parent.GetComp<CompRefuelable>();

            // 初始化??插槽
            for (int i = 0; i < Props.maxMissiles; i++)
            {
                missileLoadouts.Add(new MissileLoadout(i));
            }
        }

        public override void CompTick()
        {
            base.CompTick();

            // 如果需要?力且?有?力，停止??
            if (Props.requiresPowerToLoad && powerComp != null && !powerComp.PowerOn)
            {
                return;
            }

            // ?理???度
            if (currentLoadingPhase != LoadingPhase.Idle && currentLoadingMissileIndex >= 0)
            {
                loadingProgressTicks++;

                int requiredTicks = GetRequiredTicksForPhase();
                
                if (loadingProgressTicks >= requiredTicks)
                {
                    AdvanceLoadingPhase();
                }
            }
        }

        /// <summary>
        /// ?始???? (Start loading missile)
        /// </summary>
        public void StartLoadingMissile(int missileIndex, MissileGuidanceDef guidance, 
            MissileWarheadDef warhead)
        {
            if (missileIndex < 0 || missileIndex >= missileLoadouts.Count)
                return;

            if (missileLoadouts[missileIndex].isLoaded)
                return;

            currentLoadingMissileIndex = missileIndex;
            currentLoadingPhase = LoadingPhase.LoadingMissile;
            loadingProgressTicks = 0;

            // 初始化???据
            missileLoadouts[missileIndex].guidanceState = new MissileGuidanceState(guidance);
            missileLoadouts[missileIndex].warheadData = new MissileWarheadData(warhead);
        }

        private void AdvanceLoadingPhase()
        {
            if (currentLoadingMissileIndex < 0)
                return;

            var loadout = missileLoadouts[currentLoadingMissileIndex];
            loadingProgressTicks = 0;

            switch (currentLoadingPhase)
            {
                case LoadingPhase.LoadingMissile:
                    currentLoadingPhase = LoadingPhase.LoadingWarhead;
                    break;
                    
                case LoadingPhase.LoadingWarhead:
                    currentLoadingPhase = LoadingPhase.LoadingFuel;
                    break;
                    
                case LoadingPhase.LoadingFuel:
                    currentLoadingPhase = LoadingPhase.Complete;
                    loadout.isLoaded = true;
                    loadout.isReadyToLaunch = true;
                    currentLoadingPhase = LoadingPhase.Idle;
                    currentLoadingMissileIndex = -1;
                    break;
            }
        }

        private int GetRequiredTicksForPhase()
        {
            switch (currentLoadingPhase)
            {
                case LoadingPhase.LoadingMissile:
                    return Props.ticksToLoadMissile;
                case LoadingPhase.LoadingWarhead:
                    return Props.ticksToLoadWarhead;
                case LoadingPhase.LoadingFuel:
                    return Props.ticksToLoadFuel;
                default:
                    return 0;
            }
        }

        /// <summary>
        /// ?取???度百分比 (Get loading progress percentage)
        /// </summary>
        public float GetLoadingProgress()
        {
            if (currentLoadingPhase == LoadingPhase.Idle)
                return 0f;

            int required = GetRequiredTicksForPhase();
            if (required <= 0)
                return 0f;

            return Mathf.Clamp01((float)loadingProgressTicks / required);
        }

        /// <summary>
        /// ?取?前???段的?? (Get current loading phase label)
        /// </summary>
        public string GetCurrentLoadingPhaseLabel()
        {
            switch (currentLoadingPhase)
            {
                case LoadingPhase.LoadingMissile:
                    return "DMSE.Missile.LoadingMissile".Translate();
                case LoadingPhase.LoadingWarhead:
                    return "DMSE.Missile.LoadingWarhead".Translate();
                case LoadingPhase.LoadingFuel:
                    return "DMSE.Missile.LoadingFuel".Translate();
                default:
                    return "DMSE.Missile.Idle".Translate();
            }
        }

        /// <summary>
        /// ?取准?就?的???量 (Get count of ready-to-launch missiles)
        /// </summary>
        public int GetReadyMissileCount()
        {
            return missileLoadouts.Count(m => m.isReadyToLaunch);
        }

        /// <summary>
        /// ?取指定的?????? (Get specific missile loadout)
        /// </summary>
        public MissileLoadout GetMissileLoadout(int index)
        {
            if (index >= 0 && index < missileLoadouts.Count)
                return missileLoadouts[index];
            return null;
        }

        /// <summary>
        /// ?取所有?? (Get all missiles)
        /// </summary>
        public List<MissileLoadout> GetAllMissiles()
        {
            return new List<MissileLoadout>(missileLoadouts);
        }

        /// <summary>
        /// ?射?? (Launch missile)
        /// </summary>
        public bool LaunchMissile(int missileIndex, IntVec3 targetCell)
        {
            if (missileIndex < 0 || missileIndex >= missileLoadouts.Count)
                return false;

            var loadout = missileLoadouts[missileIndex];
            if (!loadout.isReadyToLaunch)
                return false;

            // ?建???例并?射 - ??的????在具体??中定?
            // ?里只是重置?槽位
            missileLoadouts[missileIndex] = new MissileLoadout(missileIndex);

            return true;
        }

        /// <summary>
        /// 卸??? (Unload missile)
        /// </summary>
        public void UnloadMissile(int missileIndex)
        {
            if (missileIndex >= 0 && missileIndex < missileLoadouts.Count)
            {
                missileLoadouts[missileIndex] = new MissileLoadout(missileIndex);
                currentLoadingPhase = LoadingPhase.Idle;
                currentLoadingMissileIndex = -1;
            }
        }

        /// <summary>
        /// ?取Gizmo命令 (Get gizmo commands)
        /// </summary>
        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (Props.showLoaderUI)
            {
                yield return new Command_Action()
                {
                    defaultLabel = "DMSE.Missile.OpenLoaderUI".Translate(),
                    defaultDesc = "DMSE.Missile.OpenLoaderUIDesc".Translate(),
                    icon = ContentFinder<Texture2D>.Get("UI/Commands/LaunchReport", true),
                    action = () => Find.WindowStack.Add(new Dialog_MissileLoaderUI(this))
                };
            }

            yield break;
        }

        /// <summary>
        /// ?取更多信息 (Get more information)
        /// </summary>
        public override string CompInspectStringExtra()
        {
            string info = base.CompInspectStringExtra();
            
            info += $"\n{Props.loaderLabel.Translate()}\n";
            info += $"Ready Missiles: {GetReadyMissileCount()}/{Props.maxMissiles}\n";
            info += $"Ammo: {ammoCount}/{Props.maxAmmo}\n";
            info += $"Fuel: {fuelCount}/{Props.maxAmmo}\n";
            
            if (currentLoadingPhase != LoadingPhase.Idle)
            {
                info += $"Loading: {GetCurrentLoadingPhaseLabel()} ({(GetLoadingProgress() * 100):F1}%)\n";
            }

            return info;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Collections.Look(ref missileLoadouts, "missileLoadouts", LookMode.Deep);
            Scribe_Values.Look(ref currentLoadingMissileIndex, "currentLoadingMissileIndex", -1);
            Scribe_Values.Look(ref loadingProgressTicks, "loadingProgressTicks", 0);
            Scribe_Values.Look(ref currentLoadingPhase, "currentLoadingPhase", LoadingPhase.Idle);
            Scribe_Values.Look(ref ammoCount, "ammoCount", 0);
            Scribe_Values.Look(ref fuelCount, "fuelCount", 100);
        }
    }

    /// <summary>
    /// ????UI??框 (Missile loader UI dialog)
    /// </summary>
    public class Dialog_MissileLoaderUI : Window
    {
        private CompMissileLoader loader;
        private Vector2 scrollPosition = Vector2.zero;

        public Dialog_MissileLoaderUI(CompMissileLoader loader)
        {
            this.loader = loader;
            this.doCloseButton = true;
            this.doCloseX = true;
            this.absorbInputAroundWindow = false;
            this.windowRect = new Rect(100, 100, 400, 600);
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(inRect, "DMSE.Missile.MissileLoader".Translate());
            
            inRect.yMin += 30;
            Text.Font = GameFont.Small;

            Rect listRect = new Rect(inRect.x, inRect.y, inRect.width, inRect.height - 60);
            Rect viewRect = new Rect(0, 0, inRect.width - 20, 
                loader.GetAllMissiles().Count * 100);

            Widgets.BeginScrollView(listRect, ref scrollPosition, viewRect);

            float y = 0;
            foreach (var missile in loader.GetAllMissiles())
            {
                Rect itemRect = new Rect(0, y, viewRect.width, 100);
                
                // ?制??信息
                Widgets.DrawBoxSolid(itemRect, new Color(0.3f, 0.3f, 0.3f));
                Widgets.Label(new Rect(itemRect.x + 5, itemRect.y + 5, 200, 20), 
                    missile.GetLoadoutInfo());

                // ?制??按?等
                if (missile.isReadyToLaunch)
                {
                    GUI.color = Color.green;
                    Widgets.Label(new Rect(itemRect.x + 200, itemRect.y + 5, 150, 20), 
                        "Ready to Launch");
                    GUI.color = Color.white;
                }

                y += 105;
            }

            Widgets.EndScrollView();
        }
    }
}
