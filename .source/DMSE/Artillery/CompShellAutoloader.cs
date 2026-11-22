using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.Sound;

namespace DMSE
{
    public class CompProperties_ShellAutoloader : CompProperties
    {
        /// <summary>每隔多久檢查一次連結的砲（ticks）；空膛的砲最多等這麼久就會裝上下一發。</summary>
        public int checkIntervalTicks = 60;

        public CompProperties_ShellAutoloader()
        {
            compClass = typeof(CompShellAutoloader);
        }
    }

    /// <summary>
    /// 自動裝填架：掛在存放迫擊砲彈的 <see cref="Building_Storage"/> 上，以 <see cref="CompFacility"/> 連結原版砲塔。
    /// 連結的砲（gun 上有 <see cref="CompChangeableProjectile"/>）空膛時，從架上取一發該砲「砲彈」設定允許的砲彈裝進去，
    /// 讓不需砲手的砲也能用原版可換彈藥；已裝填的砲彈被砲塔設定排除時先退到架旁（同原版砲手換彈）。
    /// 架上的砲彈由殖民者照一般存放規則搬來；斷電（<see cref="CompFacility.CanBeActive"/>）時停止裝填。
    /// </summary>
    public class CompShellAutoloader : ThingComp
    {
        public CompProperties_ShellAutoloader Props => (CompProperties_ShellAutoloader)props;

        private CompFacility facilityInt;

        private CompFacility Facility => facilityInt ?? (facilityInt = parent.GetComp<CompFacility>());

        public override void CompTick()
        {
            base.CompTick();
            if (parent.IsHashIntervalTick(Props.checkIntervalTicks)) { ServeLinkedTurrets(); }
        }

        public override void CompTickRare()
        {
            base.CompTickRare();
            ServeLinkedTurrets();
        }

        /// <summary>替每一門連結的同陣營砲補彈。</summary>
        public void ServeLinkedTurrets()
        {
            if (!parent.Spawned || !(parent is Building_Storage storage)) { return; }
            CompFacility facility = Facility;
            if (facility == null || !facility.CanBeActive) { return; }

            List<Thing> linked = facility.LinkedBuildings;
            for (int i = 0; i < linked.Count; i++)
            {
                if (!(linked[i] is Building_Turret anyTurret) || !anyTurret.Spawned || anyTurret.Faction != parent.Faction) { continue; }
                // 相容層的彈匣砲（CE：CompAmmoUser）：由相容層從架上取彈裝匣
                if (CombatCompat.Active != null && CombatCompat.Active.TryAutoload(anyTurret, storage)) { continue; }
                if (anyTurret is Building_TurretGun turret)
                {
                    TryLoad(storage, turret);
                }
            }
        }

        private void TryLoad(Building_Storage storage, Building_TurretGun turret)
        {
            CompChangeableProjectile shells = turret.gun?.TryGetComp<CompChangeableProjectile>();
            if (shells == null) { return; }

            if (shells.Loaded)
            {
                if (shells.allowedShellsSettings.AllowedToAccept(shells.LoadedShell)) { return; }
                // 玩家把這種砲彈從砲塔的「砲彈」設定排除了：退出來放到架旁，再裝允許的。
                if (turret.AttackVerb?.state == VerbState.Bursting) { return; }
                GenPlace.TryPlaceThing(shells.RemoveShell(), parent.Position, parent.Map, ThingPlaceMode.Near);
            }

            Thing stack = FindShell(storage, shells.allowedShellsSettings);
            if (stack == null) { return; }

            Thing shell = stack.SplitOff(1);
            shells.LoadShell(shell.def, 1);
            shell.Destroy();
            SoundDefOf.Artillery_ShellLoaded.PlayOneShot(new TargetInfo(turret.Position, turret.Map));
        }

        /// <summary>架上第一發砲塔允許、未被禁用的砲彈（有 projectileWhenLoaded 的物品）。</summary>
        private Thing FindShell(Building_Storage storage, StorageSettings allowed)
        {
            Map map = storage.Map;
            List<IntVec3> cells = storage.AllSlotCellsList();
            for (int c = 0; c < cells.Count; c++)
            {
                List<Thing> things = map.thingGrid.ThingsListAtFast(cells[c]);
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    if (t.def.category != ThingCategory.Item || t.def.projectileWhenLoaded == null) { continue; }
                    if (parent.Faction != null && t.IsForbidden(parent.Faction)) { continue; }
                    if (!allowed.AllowedToAccept(t)) { continue; }
                    return t;
                }
            }
            return null;
        }
    }
}
