using HarmonyLib;
using RimWorld;
using System.Collections.Generic;
using Verse;

namespace DMSE
{
    public class RKU_FactionComponent : GameComponent
    {
        List<FactionDef> enemyFaction = new List<FactionDef>
        {
            FactionDef.Named("Rakinia_Warlord"),
            FactionDef.Named("Rakinia")
        };

        public RKU_FactionComponent(Game game) { }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            SetPermanentEnemies();
        }

        public override void StartedNewGame()
        {
            base.StartedNewGame();
            SetPermanentEnemies();
        }

        public override void LoadedGame()
        {
            base.LoadedGame();
            SetPermanentEnemies();
        }
        public void SetPermanentEnemies()
        {
            Faction rFaction = Find.FactionManager.FirstFactionOfDef(DMSE_DefOf.DMS_Army);
            if (rFaction == null) return;

            foreach (FactionDef enemy in enemyFaction)
            {
                Faction eFaction = Find.FactionManager.FirstFactionOfDef(enemy);
                eFaction.RelationWith(rFaction).baseGoodwill = -100;
                rFaction.RelationWith(eFaction).baseGoodwill = -100;
                FactionRelationKind oldKind1 = eFaction.RelationWith(rFaction).kind;
                FactionRelationKind oldKind2 = rFaction.RelationWith(eFaction).kind;
                eFaction.RelationWith(rFaction).kind = FactionRelationKind.Hostile;
                rFaction.RelationWith(eFaction).kind = FactionRelationKind.Hostile;
                eFaction.Notify_RelationKindChanged(rFaction, oldKind1, false, "", TargetInfo.Invalid, out var sentLetter1);
                rFaction.Notify_RelationKindChanged(eFaction, oldKind2, false, "", TargetInfo.Invalid, out var sentLetter2);
            }
        }

        private void SetFactionLeaderTitle(Faction faction, string title)
        {
            var leaderTitleField = typeof(Faction).GetField("leaderTitle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (leaderTitleField != null)
            {
                leaderTitleField.SetValue(faction, title);
            }
        }
    }
    [HarmonyPatch(typeof(Faction), "TryAffectGoodwillWith")]
    public class FactionGoodwill_Patch
    {
        private static void Postfix(Faction __instance, Faction other, int goodwillChange, bool __result)
        {
            if (__result && (__instance.IsPlayer || other.IsPlayer) &&
                ((__instance.def.defName == "DMS_Army" && other.IsPlayer) ||
                 (__instance.IsPlayer && other.def.defName == "DMS_Army")))
            {
                var radioComponent = Current.Game.GetComponent<RKU_RadioGameComponent>();
                if (radioComponent != null)
                {
                    Faction targetFaction = Find.FactionManager.FirstFactionOfDef(DMSE_DefOf.DMS_Army);
                    Faction playerFaction = Faction.OfPlayer;

                    if (targetFaction != null && playerFaction != null)
                    {
                        int actualGoodwill = targetFaction.GoodwillWith(playerFaction);
                        radioComponent.ralationshipGrade = actualGoodwill;
                        if (actualGoodwill <= -25)
                        {
                            FactionRelationKind kind2 = targetFaction.RelationWith(Faction.OfPlayer).kind;
                            targetFaction.RelationWith(Faction.OfPlayer).kind = FactionRelationKind.Hostile;
                            Faction.OfPlayer.RelationWith(targetFaction).kind = FactionRelationKind.Hostile;
                            targetFaction.Notify_RelationKindChanged(Faction.OfPlayer, kind2, false, "", TargetInfo.Invalid, out var sentLetter);
                            Faction.OfPlayer.Notify_RelationKindChanged(targetFaction, kind2, false, "", TargetInfo.Invalid, out sentLetter);
                        }
                    }
                }
            }
        }
    }
    // 拦截原版的自然goodwill调整机制，防止RKU与玩家的关系被重置
    [HarmonyPatch(typeof(Faction), "CheckReachNaturalGoodwill")]
    public class Faction_CheckReachNaturalGoodwill_Patch
    {
        private static bool Prefix(Faction __instance)
        {
            if (__instance.def.defName == "DMS_Army")
            {
                Faction playerFaction = Faction.OfPlayer;
                if (playerFaction != null)
                {
                    FactionRelationKind relation = __instance.RelationKindWith(playerFaction);
                    if (relation == FactionRelationKind.Hostile)
                    {
                        Traverse.Create(__instance).Field("naturalGoodwillTimer").SetValue(0);
                        return false;
                    }
                }
            }
            return true;
        }
    }
}