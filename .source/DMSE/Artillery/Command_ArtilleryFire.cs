using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 超視距火砲的「開火」按鈕（<see cref="CompArtilleryStrike"/>）。
    /// 多選時原版會對群組裡每個按鈕各執行一次動作：每門砲各開一次世界選靶，後開的蓋掉先開的，
    /// 結果只有一門砲拿到目標。這裡只開一次選靶，選定後套用到群組裡所有可開火的砲。
    /// </summary>
    public class Command_ArtilleryFire : Command
    {
        public readonly CompArtilleryStrike comp;

        public Command_ArtilleryFire(CompArtilleryStrike comp)
        {
            this.comp = comp;
        }

        public override bool GroupsWith(Gizmo other) => other is Command_ArtilleryFire && base.GroupsWith(other);

        /// <summary>群組裡其他按鈕不各自執行（各開一次選靶會互相覆蓋），改在 <see cref="ProcessGroupInput"/> 一起處理。</summary>
        public override bool InheritInteractionsFrom(Gizmo other) => false;

        public override void ProcessInput(Event ev)
        {
            base.ProcessInput(ev);
            CompArtilleryStrike.BeginWorldTargeting(new List<CompArtilleryStrike> { comp });
        }

        /// <summary>原版在 <see cref="ProcessInput"/> 之後呼叫，帶整個按鈕群組：有其他可開火的砲就改成一起選靶。</summary>
        public override void ProcessGroupInput(Event ev, List<Gizmo> group)
        {
            base.ProcessGroupInput(ev, group);
            if (group == null) { return; }

            List<CompArtilleryStrike> guns = new List<CompArtilleryStrike> { comp };
            for (int i = 0; i < group.Count; i++)
            {
                if (group[i] is Command_ArtilleryFire other && other != this && !other.Disabled && !guns.Contains(other.comp))
                {
                    guns.Add(other.comp);
                }
            }
            if (guns.Count > 1)
            {
                CompArtilleryStrike.BeginWorldTargeting(guns);
            }
        }
    }
}
