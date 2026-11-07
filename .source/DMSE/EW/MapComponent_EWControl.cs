using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    public enum EngagePolicy { SelfDefense, All }
    public enum EngagePriority { FirstCome, LargestFirst }

    [Flags]
    public enum HoldFireClass { None = 0, Loitering = 1, DropPod = 2, Ballistic = 4 }

    public enum EWDeviceGroup { Jammers, SearchRadars, FireControlRadars, All }

    /// <summary>
    /// 一張地圖的電子戰狀態（docs/電子戰建築_實作計畫.md §2）：交戰限制、遙控快照與裝置註冊表。
    /// 同一份規則給地圖層與世界層讀，多座終端共用。規則只在本圖有玩家的戰情終端時生效（NPC 陣地的終端只提供守軍加成）；
    /// 沒有終端就維持舊行為。遙控只動本圖、玩家陣營的裝置。
    /// </summary>
    public class MapComponent_EWControl : MapComponent
    {
        public const int ChannelCapUnlimited = -1;
        private const int ReapplyIntervalTicks = 60;
        private const int CommsMessageCooldownTicks = 2500;
        private static readonly AccessTools.FieldRef<CompFlickable, bool> WantSwitchOn
            = AccessTools.FieldRefAccess<CompFlickable, bool>("wantSwitchOn");

        // 註冊表（不存檔，由 comp 生成時重建）。
        public readonly HashSet<CompWarRoomConsole> consoles = new HashSet<CompWarRoomConsole>();
        public readonly HashSet<CompJammer> jammers = new HashSet<CompJammer>();
        public readonly HashSet<CompPowerThrottle> throttles = new HashSet<CompPowerThrottle>();

        // ---- 交戰限制 ----
        public bool autoEngage = true;
        public EngagePolicy engagePolicy = EngagePolicy.SelfDefense;
        public bool engageNeutral;
        public int channelCap = ChannelCapUnlimited;
        public EngagePriority priority = EngagePriority.FirstCome;
        public HoldFireClass holdFireClasses = HoldFireClass.None;

        // ---- 一鍵靜默 ----
        private List<int> silencedIds = new List<int>();
        public bool Silenced => silencedIds.Count > 0;

        private bool commsBlockedLast;
        private int lastCommsMessageTick = -99999;

        public MapComponent_EWControl(Map map) : base(map) { }

        public bool RoeActive
        {
            get
            {
                foreach (CompWarRoomConsole console in consoles)
                {
                    if (console.parent.Faction == Faction.OfPlayer) { return true; }
                }
                return false;
            }
        }

        public static MapComponent_EWControl For(Map map) => map?.GetComponent<MapComponent_EWControl>();

        /// <summary>取得玩家節點要遵守的規則；不是玩家或本圖沒有終端時為 null（走舊規則）。</summary>
        public static MapComponent_EWControl ActiveRoe(Map map, Faction defender)
        {
            if (defender == null || !defender.IsPlayer) { return null; }
            MapComponent_EWControl control = For(map);
            return control != null && control.RoeActive ? control : null;
        }

        // ====================================================================
        //  交戰限制判定
        // ====================================================================

        public bool AllowsEngagement(Faction attacker, Faction targetFaction, HoldFireClass targetClass)
        {
            if (!autoEngage || (holdFireClasses & targetClass) != 0) { return false; }
            Faction player = Faction.OfPlayer;
            if (attacker != null && AirDefenseAlertPolicy.IsFriendly(player, attacker)) { return false; }
            bool targetsUs = targetFaction != null && AirDefenseAlertPolicy.IsFriendly(player, targetFaction);
            if (!player.HostileTo(attacker))
            {
                // 未知來源只在確認攻擊己方時接戰；中立陣營要另外開放。
                if (attacker == null ? !targetsUs : !engageNeutral) { return false; }
            }
            return engagePolicy == EngagePolicy.All || targetsUs;
        }

        public bool AllowsTrack(IBVRTrack track) => AllowsEngagement(track.Attacker,
            WorldComponent_FactionAirDefenseAlert.TargetSite(track)?.Faction, EWControlUtility.ClassOf(track));

        /// <summary>地圖層目標：已經落在本圖，一定算指向本方。</summary>
        public bool AllowsMapTarget(BVRTarget target)
        {
            IIncomingMissile missile = target.Missile;
            return AllowsEngagement(missile?.Attacker, Faction.OfPlayer,
                EWControlUtility.ClassOf(missile?.Config, missile == null));
        }

        /// <summary>依 <see cref="channelCap"/>，防空還能再佔幾條通道（砲擊與打擊不計入，這正是保留的目的）。</summary>
        public int RoeChannelsLeft(Faction faction)
        {
            if (channelCap < 0) { return int.MaxValue; }
            BVRChannelLedger ledger = map.GetComponent<MapComponent_BVRCombat>()?.Channels;
            return ledger == null ? channelCap : Mathf.Max(0, channelCap - ledger.Local(faction) - ledger.World(faction));
        }

        // ====================================================================
        //  遙控
        // ====================================================================

        public IEnumerable<ThingWithComps> Devices(EWDeviceGroup group)
        {
            MapComponent_BVRCombat bvr = map.GetComponent<MapComponent_BVRCombat>();
            IEnumerable<ThingWithComps> search = bvr.searchRadars.Select(r => r.parent);
            IEnumerable<ThingWithComps> fireControl = bvr.fireControlRadars.Select(r => r.parent);
            IEnumerable<ThingWithComps> jam = jammers.Select(j => j.parent);
            IEnumerable<ThingWithComps> devices;
            switch (group)
            {
                case EWDeviceGroup.Jammers: devices = jam; break;
                case EWDeviceGroup.SearchRadars: devices = search; break;
                case EWDeviceGroup.FireControlRadars: devices = fireControl; break;
                default: devices = search.Concat(fireControl).Concat(jam); break;
            }
            return devices.Where(t => t.Faction == Faction.OfPlayer).Distinct();
        }

        /// <summary>直接切換開關，連同 wantSwitchOn 與待辦的切換工作一起更新（否則 Gizmo 會顯示反的）。</summary>
        private static void SetSwitch(CompFlickable flick, bool on)
        {
            WantSwitchOn(flick) = on;
            flick.SwitchIsOn = on;
            FlickUtility.UpdateFlickDesignation(flick.parent);
        }

        public void SetPower(EWDeviceGroup group, bool on)
        {
            foreach (ThingWithComps device in Devices(group).ToList())
            {
                CompFlickable flick = device.GetComp<CompFlickable>();
                if (flick != null) { SetSwitch(flick, on); }
            }
            PruneSilenced();
        }

        /// <summary>靜默期間手動重開或已拆除的裝置不再算靜默，否則警告會一直掛著、還原時也會動到它們。</summary>
        private void PruneSilenced()
        {
            if (silencedIds.Count == 0) { return; }
            List<ThingWithComps> devices = Devices(EWDeviceGroup.All).ToList();
            silencedIds.RemoveAll(id =>
            {
                ThingWithComps device = devices.FirstOrDefault(d => d.thingIDNumber == id);
                return device == null || (device.GetComp<CompFlickable>()?.SwitchIsOn ?? true);
            });
        }

        public void SetThrottle(EWDeviceGroup group, int step)
        {
            foreach (ThingWithComps device in Devices(group).ToList())
            {
                CompPowerThrottle throttle = device.GetComp<CompPowerThrottle>();
                if (throttle != null) { throttle.Step = Mathf.Min(step, throttle.Steps); }
            }
        }

        /// <summary>一鍵靜默：記下目前開著的裝置再關掉；恢復時只還原快照內的裝置。</summary>
        public void Silence()
        {
            foreach (ThingWithComps device in Devices(EWDeviceGroup.All).ToList())
            {
                CompFlickable flick = device.GetComp<CompFlickable>();
                if (flick == null || !flick.SwitchIsOn) { continue; }
                if (!silencedIds.Contains(device.thingIDNumber)) { silencedIds.Add(device.thingIDNumber); }
                SetSwitch(flick, false);
            }
        }

        public void Restore()
        {
            List<ThingWithComps> devices = Devices(EWDeviceGroup.All).ToList();
            silencedIds.RemoveAll(id => !devices.Any(d => d.thingIDNumber == id));
            foreach (ThingWithComps device in devices)
            {
                if (!silencedIds.Contains(device.thingIDNumber)) { continue; }
                CompFlickable flick = device.GetComp<CompFlickable>();
                if (flick != null) { SetSwitch(flick, true); }
            }
            silencedIds.Clear();
        }

        // ====================================================================
        //  操作員
        // ====================================================================

        /// <summary>
        /// 該陣營在本圖所有終端的操作員此技能等級總和，上限 <see cref="EWControlUtility.OperatorLevelCap"/>；沒有人操作時 -1。
        /// </summary>
        public int OperatorLevelTotal(SkillDef skill, Faction faction)
        {
            int total = -1;
            foreach (CompWarRoomConsole console in consoles)
            {
                if (console.parent.Faction != faction) { continue; }
                SkillRecord record = console.Operator?.skills?.GetSkill(skill);
                if (record != null) { total = Mathf.Max(total, 0) + record.Level; }
            }
            return Mathf.Min(total, EWControlUtility.OperatorLevelCap);
        }

        // ====================================================================
        //  通訊封鎖
        // ====================================================================

        public bool CommsJammed
        {
            get
            {
                foreach (CompJammer jammer in jammers)
                {
                    if (jammer.BlocksCommsNow) { return true; }
                }
                return false;
            }
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            int now = Find.TickManager.TicksGame;
            if (now % ReapplyIntervalTicks != 0) { return; }
            foreach (CompPowerThrottle throttle in throttles) { throttle.ApplyPowerDraw(); }
            PruneSilenced();

            bool blocked = CommsJammed;
            if (blocked && !commsBlockedLast && map.IsPlayerHome && now - lastCommsMessageTick >= CommsMessageCooldownTicks)
            {
                lastCommsMessageTick = now;
                CompJammer source = jammers.FirstOrDefault(j => j.BlocksCommsNow);
                Messages.Message("DMSE.EW.Jammer.CommsBlockedMessage".Translate(), source?.parent, MessageTypeDefOf.CautionInput, historical: false);
            }
            commsBlockedLast = blocked;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref autoEngage, "autoEngage", true);
            Scribe_Values.Look(ref engagePolicy, "engagePolicy", EngagePolicy.SelfDefense);
            Scribe_Values.Look(ref engageNeutral, "engageNeutral", false);
            Scribe_Values.Look(ref channelCap, "channelCap", ChannelCapUnlimited);
            Scribe_Values.Look(ref priority, "priority", EngagePriority.FirstCome);
            Scribe_Values.Look(ref holdFireClasses, "holdFireClasses", HoldFireClass.None);
            Scribe_Collections.Look(ref silencedIds, "silencedIds", LookMode.Value);
            Scribe_Values.Look(ref commsBlockedLast, "commsBlockedLast", false);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) { silencedIds ??= new List<int>(); }
        }
    }

    /// <summary>操作員技能加成與目標分類（§2.4）。</summary>
    public static class EWControlUtility
    {
        public const int NeutralSkillLevel = 8;

        /// <summary>多座終端的操作員等級疊加後的總和上限。</summary>
        public const int OperatorLevelCap = 40;

        /// <summary>
        /// 通道上限的 -/+ 順序：0 → … → 火控容量 → 不限。「不限」視為最大值，
        /// 否則從不限按 + 會直接變成 0，等於完全不攔截。
        /// </summary>
        public static int NextChannelCap(int current, int capacity, int delta)
        {
            int unlimited = MapComponent_EWControl.ChannelCapUnlimited;
            if (delta < 0)
            {
                if (current < 0) { return Mathf.Max(capacity, 0); }
                return Mathf.Max(current - 1, 0);
            }
            if (current < 0 || current >= capacity) { return unlimited; }
            return current + 1;
        }

        /// <summary>智識等級總和 → 雷達搜索距離係數（下限 0.8，等級 40 時 ×1.64）。</summary>
        public static float IntellectualRangeFactor(int level)
            => Mathf.Max(1f + 0.02f * (Mathf.Min(level, OperatorLevelCap) - NeutralSkillLevel), 0.8f);

        /// <summary>射擊等級總和 → 發射冷卻倍率（上限 1.25，等級 40 時 ×0.51）。</summary>
        public static float ShootingCooldownFactor(int level)
            => Mathf.Min(1f / (1f + 0.03f * (Mathf.Min(level, OperatorLevelCap) - NeutralSkillLevel)), 1.25f);

        /// <summary>同陣營終端操作員對這座裝置的技能等級總和；沒有陣營、未生成或沒人操作時 -1。</summary>
        private static int LevelFor(ThingWithComps thing, SkillDef skill)
        {
            if (thing == null || !thing.Spawned || thing.Faction == null) { return -1; }
            return MapComponent_EWControl.For(thing.Map)?.OperatorLevelTotal(skill, thing.Faction) ?? -1;
        }

        /// <summary>本圖同陣營操作員對這座雷達的距離係數；沒人操作時回 1。</summary>
        public static float RadarRangeFactor(ThingWithComps radar)
        {
            int level = LevelFor(radar, SkillDefOf.Intellectual);
            return level < 0 ? 1f : IntellectualRangeFactor(level);
        }

        /// <summary>本圖同陣營操作員對這座發射平台的冷卻倍率；沒人操作時回 1。</summary>
        public static float LauncherCooldownFactor(ThingWithComps launcher)
        {
            int level = LevelFor(launcher, SkillDefOf.Shooting);
            return level < 0 ? 1f : ShootingCooldownFactor(level);
        }

        public static HoldFireClass ClassOf(MissileConfig config, bool pod)
        {
            if (pod) { return HoldFireClass.DropPod; }
            switch (config?.body?.sizeClass)
            {
                case MissileSizeClass.Loitering: return HoldFireClass.Loitering;
                case MissileSizeClass.Ballistic: return HoldFireClass.Ballistic;
                default: return HoldFireClass.None;
            }
        }

        public static HoldFireClass ClassOf(IBVRTrack track) => ClassOf(track.Config, track.Self is WorldObject_DropPodTrack);
    }
}
