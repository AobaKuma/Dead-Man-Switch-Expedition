using System.Collections.Generic;
using LudeonTK;
using RimWorld;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 攔截特效調參用的開發指令（docs/攔截殘骸特效_實作與技術美術計畫.md §9 P2）：
    /// 點選落點 → 選來襲方位 → 選攔截型態，直接觸發 <see cref="MapComponent_InterceptEffects"/>，不需要真的有來襲物。
    /// </summary>
    public static class DebugActions_InterceptFx
    {
        private const string Category = "DMSE Intercept FX";

        private static readonly float[] Headings = { 0f, 45f, 90f, 135f, 180f, 225f, 270f, 315f };

        [DebugAction(Category, "Intercept FX at landing cell...", actionType = DebugActionType.ToolMap,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void AtCell()
        {
            Map map = Find.CurrentMap;
            IntVec3 landing = UI.MouseCell();
            if (map == null || !landing.InBounds(map)) { return; }

            var options = new List<FloatMenuOption>();
            foreach (float h in Headings)
            {
                float heading = h;
                options.Add(new FloatMenuOption($"from {heading:0}° ({MissileMapEntryUtility.EdgeRot(heading).ToStringHuman()})",
                    () => ChooseKind(map, landing, new[] { heading })));
            }
            options.Add(new FloatMenuOption("all 8 headings", () => ChooseKind(map, landing, Headings)));
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private static void ChooseKind(Map map, IntVec3 landing, float[] headings)
        {
            var options = new List<FloatMenuOption>
            {
                new FloatMenuOption("terminal, far: virtual point beyond the edge, no burst (approach 0)", () => Fire(map, landing, headings, true, 0f, InterceptPayloadKind.Missile)),
                new FloatMenuOption("terminal, nearing the edge (approach 0.15)", () => Fire(map, landing, headings, true, 0.15f, InterceptPayloadKind.Missile)),
                new FloatMenuOption("terminal, inside the map (approach 0.5)", () => Fire(map, landing, headings, true, 0.5f, InterceptPayloadKind.Missile)),
                new FloatMenuOption("terminal, about to land (approach 1)", () => Fire(map, landing, headings, true, 1f, InterceptPayloadKind.Missile)),
                new FloatMenuOption("midcourse (beyond entry edge)", () => Fire(map, landing, headings, false, 0f, InterceptPayloadKind.Missile)),
                new FloatMenuOption("terminal drop pod (no fireball)", () => Fire(map, landing, headings, true, 0.5f, InterceptPayloadKind.DropPod))
            };
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private static void Fire(Map map, IntVec3 landing, float[] headings, bool terminal, float approach, InterceptPayloadKind kind)
        {
            int tick = Find.TickManager.TicksGame;
            for (int i = 0; i < headings.Length; i++)
            {
                InterceptEventData data = InterceptEventData.Build(null, landing, map.Size, map.Center, terminal, IntVec3.Invalid,
                    approach, null, Gen.HashCombineInt(tick, i), InterceptDebrisPhysics.Default, headings[i]);
                data.payloadKind = kind;
                MapComponent_InterceptEffects.Trigger(map, data);
                Log.Message($"[DMSE InterceptFX] heading={data.heading:0} terminal={terminal} remote={data.remote} approach={approach:0.##} "
                    + $"burstGround=({data.burstGround.x:0.#}, {data.burstGround.z:0.#}) height={data.burstHeight:0.#} "
                    + $"offMap={InterceptGeometry.DistanceOutside(map.Size, data.burstGround):0.#}");
            }
        }
    }
}
