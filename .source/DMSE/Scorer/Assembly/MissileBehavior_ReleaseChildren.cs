using System.Collections.Generic;
using Fortified;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 飛行中分出子彈（分導彈頭、誘餌）。依賴裝配系統的彈體與部件，所以跟 <see cref="MissileBodyDef"/> 一起遷移。
    /// <c>&lt;li Class="DMSE.MissileBehaviorProps_ReleaseChildren"&gt;</c>
    /// </summary>
    public class MissileBehaviorProps_ReleaseChildren : MissileBehaviorProps
    {
        /// <summary>在整條航線的哪個進度釋放（0–1）。</summary>
        public float atProgress = 0.5f;
        public int count = 1;

        /// <summary>子彈彈體；null = 與母彈相同（此時不繼承母彈的部件以外設定）。</summary>
        public MissileBodyDef childBody;

        /// <summary>子彈部件；null = 使用子彈彈體的預設部件。</summary>
        public List<MissilePartDef> childParts;

        /// <summary>釋放後銷毀母彈（分導彈頭）；false = 母彈繼續飛（誘餌）。</summary>
        public bool retireParent;

        public MissileBehaviorProps_ReleaseChildren() { behaviorClass = typeof(MissileBehavior_ReleaseChildren); }

        public MissileConfig MakeChildConfig(MissileBodyDef fallbackBody)
        {
            MissileBodyDef body = childBody ?? fallbackBody;
            if (body == null) { return null; }
            if (childParts == null) { return MissileConfig.NewWithDefaults(body); }
            MissileConfig config = new MissileConfig(body);
            config.parts.AddRange(childParts);
            return config;
        }

        public override IEnumerable<string> ConfigErrors(Def parent)
        {
            foreach (string error in base.ConfigErrors(parent)) { yield return error; }
            if (count < 1) { yield return $"{parent.defName}: release count must be at least 1"; }
            if (atProgress <= 0f || atProgress >= 1f) { yield return $"{parent.defName}: atProgress must be between 0 and 1"; }
            if (childBody == null && parent is MissileBodyDef) { yield return $"{parent.defName}: a body releasing children of its own body would recurse; set childBody"; }
        }
    }

    public class MissileBehavior_ReleaseChildren : MissileBehavior
    {
        private bool released;

        public override void FlightTick(int elapsedTicks)
        {
            var settings = (MissileBehaviorProps_ReleaseChildren)props;
            if (released || track.RouteProgress < settings.atProgress) { return; }
            released = true;
            if (!(track is MissileWorldObject parent)) { return; }

            bool any = false;
            for (int i = 0; i < settings.count; i++)
            {
                // 母彈銷毀時由第一枚子彈接手戰果回報，任務統計不會多算也不會懸空。
                bool inherits = settings.retireParent && !any;
                MissileConfig config = settings.MakeChildConfig(parent.config?.body);
                if (parent.SpawnChild(config, inherits) != null) { any = true; }
            }
            if (any && settings.retireParent) { parent.Destroy(); }
        }

        public override void ExposeData() => Scribe_Values.Look(ref released, "released", false);
    }
}
