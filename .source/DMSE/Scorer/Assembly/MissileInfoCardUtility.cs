using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    [DefOf]
    public static class MissileStatCategoryDefOf
    {
        /// <summary>導彈諸元：彈體分級、平台與裝配後的有效數值。</summary>
        public static StatCategoryDef DMSE_MissileProfile;

        /// <summary>導引與落點特效的機制說明。</summary>
        public static StatCategoryDef DMSE_MissileMechanics;

        /// <summary>部件本身：槽位、適用彈體、材料與數值修正。</summary>
        public static StatCategoryDef DMSE_MissilePart;

        static MissileStatCategoryDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(MissileStatCategoryDefOf));
        }
    }

    /// <summary>
    /// 導彈物品、彈體與部件的資訊卡條目。數值一律取自 <see cref="MissileConfig"/> 的有效值計算，
    /// 特效說明由各 <see cref="WarheadEffect"/>／<see cref="PayloadEffect"/>／<see cref="GuidanceType"/> 代入 N 產生，
    /// 所以資訊卡與實際發射行為不會各說各話。
    /// </summary>
    public static class MissileInfoCardUtility
    {
        private static readonly Dictionary<ThingDef, List<ThingDef>> platformsByItem = new Dictionary<ThingDef, List<ThingDef>>();

        private static StatCategoryDef Profile => MissileStatCategoryDefOf.DMSE_MissileProfile;
        private static StatCategoryDef Mechanics => MissileStatCategoryDefOf.DMSE_MissileMechanics;
        private static StatCategoryDef PartCat => MissileStatCategoryDefOf.DMSE_MissilePart;

        // ================================================================== 導彈（物品或彈體）

        /// <summary>
        /// 一枚導彈的資訊卡條目：彈體分級與平台、裝配後的有效數值、導引與特效機制。
        /// <paramref name="item"/> 用來找可裝填的發射平台；彈體卡（無物品型態）傳 null。
        /// </summary>
        public static IEnumerable<StatDrawEntry> MissileStats(MissileConfig config, ThingDef item)
        {
            MissileBodyDef body = config?.body;
            if (body == null) { yield break; }
            float n = config.PayloadCapacity;

            // ---- 分級與平台 ----
            yield return new StatDrawEntry(Profile, "DMSE.Card.Body".Translate(), body.LabelCap, body.description, 5000,
                hyperlinks: BodyLink(body, item));
            yield return new StatDrawEntry(Profile, "DMSE.Card.SizeClass".Translate(), ("DMSE.Bench.Size." + body.sizeClass).Translate(),
                ("DMSE.Card.SizeClassDesc." + body.sizeClass).Translate(), 4990);
            yield return new StatDrawEntry(Profile, "DMSE.Card.LaunchMode".Translate(), ("DMSE.Card.LaunchMode." + body.launchMode).Translate(),
                "DMSE.Card.LaunchModeDesc".Translate(), 4980);
            if (item != null)
            {
                List<ThingDef> platforms = PlatformsFor(item);
                yield return new StatDrawEntry(Profile, "DMSE.Card.Platforms".Translate(),
                    platforms.Count > 0 ? platforms.Count.ToString() : "DMSE.Missile.None".Translate().ToString(),
                    "DMSE.Card.PlatformsDesc".Translate() + (platforms.Count > 0
                        ? "\n\n" + string.Join("\n", platforms.Select(p => "  - " + p.LabelCap))
                        : string.Empty),
                    4970, hyperlinks: platforms.Select(p => new Dialog_InfoCard.Hyperlink(p)));
            }
            yield return new StatDrawEntry(Profile, "DMSE.Card.Customize".Translate(), ("DMSE.Card.Customize." + body.customizeLocation).Translate(),
                ("DMSE.Card.CustomizeDesc." + body.customizeLocation).Translate(), 4960);

            // ---- 已裝部件 ----
            int order = 4900;
            foreach (MissilePartCategory cat in body.slots)
            {
                MissilePartDef part = config.PartFor(cat);
                yield return new StatDrawEntry(Profile, ("DMSE.Missile.Cat." + cat).Translate(),
                    part != null ? part.LabelCap.ToString() : "DMSE.Missile.None".Translate().ToString(),
                    part != null ? part.description : "DMSE.Card.EmptySlotDesc".Translate().ToString(), order--,
                    hyperlinks: part != null ? new[] { new Dialog_InfoCard.Hyperlink(part) } : null);
            }

            // ---- 打擊數值 ----
            yield return new StatDrawEntry(Profile, "DMSE.UI.DamageChange".Translate(),
                config.DamageAmount + (config.DamageDef != null ? " (" + config.DamageDef.label + ")" : string.Empty), "DMSE.Card.DamageDesc".Translate(), 4800);
            yield return new StatDrawEntry(Profile, "DMSE.Bench.Stat.Radius".Translate(),
                config.ExplosionRadius.ToString("0.#"), "DMSE.Card.RadiusDesc".Translate(), 4790);
            yield return new StatDrawEntry(Profile, "DMSE.Bench.Stat.Scatter".Translate(),
                config.Scatter.ToString("0.#"), "DMSE.Card.ScatterDesc".Translate(), 4780);
            yield return new StatDrawEntry(Profile, "DMSE.UI.RangeChange".Translate(),
                RangeText(config.Range), RangeReport(config), 4770);
            yield return new StatDrawEntry(Profile, "DMSE.Bench.Stat.Speed".Translate(),
                config.WorldSpeedFactor.ToStringByStyle(ToStringStyle.FloatMaxTwo, ToStringNumberSense.Factor),
                "DMSE.Card.SpeedDesc".Translate(), 4760);
            yield return new StatDrawEntry(Profile, "DMSE.Card.PayloadCapacity".Translate(),
                n.ToString("0.#"), "DMSE.Card.PayloadCapacityDesc".Translate(), 4750);
            yield return new StatDrawEntry(Profile, "DMSE.Bench.Stat.ThickRoof".Translate(),
                config.PenetratesThickRoof ? "Yes".Translate() : "No".Translate(), "DMSE.Card.ThickRoofDesc".Translate(), 4740);

            // ---- 導引 ----
            yield return new StatDrawEntry(Mechanics, "DMSE.Bench.Stat.Guidance".Translate(), config.TargetingLabel,
                GuidanceReport(config), 4700);
            yield return new StatDrawEntry(Mechanics, "DMSE.Bench.Stat.TerminalUpdates".Translate(),
                config.HasTerminalGuidance ? config.TerminalUpdates.ToString() : "DMSE.Card.NoGuidance".Translate().ToString(),
                "DMSE.Card.TerminalUpdatesDesc".Translate(), 4690);
            yield return new StatDrawEntry(Mechanics, "DMSE.Bench.Stat.Channels".Translate(),
                config.StrikeChannelCost.ToString(), "DMSE.Card.ChannelsDesc".Translate(), 4680);
            if (config.LoiterTicks > 0)
            {
                yield return new StatDrawEntry(Mechanics, "DMSE.Bench.Stat.Loiter".Translate(), config.LoiterTicks.ToStringTicksToPeriod(),
                    "DMSE.Missile.Stat.Loiter".Translate(config.LoiterTicks.ToStringTicksToPeriod()) + "\n\n"
                    + "DMSE.Card.LoiterRetry".Translate(config.LoiterRetryTicks.ToStringTicksToPeriod()), 4670);
            }

            // ---- 防空與生存 ----
            WarheadEffect warhead = config.PartFor(MissilePartCategory.Warhead)?.warheadEffect;
            float interceptBonus = warhead?.InterceptBonus(n) ?? 0f;
            int extraRolls = warhead?.ExtraInterceptRolls(n) ?? 0;
            if (config.InterceptHitFactor != 1f || interceptBonus > 0f || extraRolls > 0)
            {
                StringBuilder sb = new StringBuilder("DMSE.Card.InterceptDesc".Translate());
                if (interceptBonus > 0f) { sb.Append("\n\n").Append("DMSE.Card.InterceptBonus".Translate(interceptBonus.ToStringPercent())); }
                if (extraRolls > 0) { sb.Append("\n").Append("DMSE.Card.InterceptRolls".Translate(extraRolls)); }
                yield return new StatDrawEntry(Mechanics, "DMSE.Card.Intercept".Translate(),
                    config.InterceptHitFactor.ToStringByStyle(ToStringStyle.FloatMaxTwo, ToStringNumberSense.Factor)
                    + (interceptBonus > 0f ? " +" + interceptBonus.ToStringPercent() : string.Empty),
                    sb.ToString(), 4660);
            }
            if (config.SignatureFactor != 1f)
            {
                yield return new StatDrawEntry(Mechanics, "DMSE.Bench.Stat.Signature".Translate(),
                    config.SignatureFactor.ToStringByStyle(ToStringStyle.FloatMaxTwo, ToStringNumberSense.Factor),
                    "DMSE.Card.SignatureDesc".Translate(), 4650);
            }
            if (config.ChaffCharges > 0)
            {
                yield return new StatDrawEntry(Mechanics, "DMSE.Bench.Stat.Chaff".Translate(),
                    config.ChaffCharges + " (" + config.JamStrength.ToStringPercent() + ")",
                    ChaffReport(config.JamStrength), 4640);
            }

            // ---- 落點特效 ----
            MissilePartDef warheadPart = config.PartFor(MissilePartCategory.Warhead);
            string warheadText = warheadPart?.warheadEffect?.EffectDescription(n);
            if (!warheadText.NullOrEmpty())
            {
                yield return new StatDrawEntry(Mechanics, "DMSE.Card.WarheadEffect".Translate(), warheadPart.LabelCap,
                    warheadText + "\n\n" + "DMSE.Card.ScaledByN".Translate(n.ToString("0.#")), 4600,
                    hyperlinks: new[] { new Dialog_InfoCard.Hyperlink(warheadPart) });
            }
            MissilePartDef payloadPart = config.PartFor(MissilePartCategory.Payload);
            string payloadText = PayloadText(payloadPart, n);
            if (!payloadText.NullOrEmpty())
            {
                yield return new StatDrawEntry(Mechanics, "DMSE.Card.PayloadEffect".Translate(), payloadPart.LabelCap,
                    payloadText + "\n\n" + "DMSE.Card.ScaledByN".Translate(n.ToString("0.#")), 4590,
                    hyperlinks: new[] { new Dialog_InfoCard.Hyperlink(payloadPart) });
            }

            // ---- 改裝 ----
            if (body.customizeLocation != MissileCustomizeLocation.None && body.slots.Count > 0)
            {
                yield return new StatDrawEntry(Profile, "DMSE.Card.AssembleWork".Translate(),
                    ((float)body.assembleWorkAmount).ToStringWorkAmount(), "DMSE.Card.AssembleWorkDesc".Translate(), 4500);
                yield return new StatDrawEntry(Profile, "DMSE.Card.Refund".Translate(),
                    body.refundFraction.ToStringPercent(), "DMSE.Card.RefundDesc".Translate(), 4490);
            }
        }

        /// <summary>彈體卡：以預設零件構型顯示，並列出槽位與預設部件。</summary>
        public static IEnumerable<StatDrawEntry> BodyStats(MissileBodyDef body)
        {
            if (body == null) { yield break; }
            ThingDef item = MissileItemUtility.ItemDefFor(body);
            if (body.slots.Count > 0)
            {
                yield return new StatDrawEntry(Profile, "DMSE.Card.Slots".Translate(),
                    string.Join(", ", body.slots.Select(c => ("DMSE.Missile.Cat." + c).Translate().ToString())),
                    "DMSE.Card.SlotsDesc".Translate(), 4950);
            }
            if (body.researchPrerequisites != null && body.researchPrerequisites.Count > 0)
            {
                yield return ResearchEntry(Profile, body.researchPrerequisites, 4940);
            }
            if (item != null)
            {
                yield return new StatDrawEntry(Profile, "DMSE.Card.Item".Translate(), item.LabelCap, item.description, 4930,
                    hyperlinks: new[] { new Dialog_InfoCard.Hyperlink(item) });
            }
            foreach (StatDrawEntry e in MissileStats(MissileConfig.NewWithDefaults(body), item))
            {
                yield return e;
            }
        }

        // ================================================================== 部件

        public static IEnumerable<StatDrawEntry> PartStats(MissilePartDef part)
        {
            if (part == null) { yield break; }
            List<MissileBodyDef> bodies = DefDatabase<MissileBodyDef>.AllDefsListForReading.Where(part.CompatibleWith).ToList();

            yield return new StatDrawEntry(PartCat, "DMSE.Card.PartSlot".Translate(), ("DMSE.Missile.Cat." + part.category).Translate(),
                "DMSE.Card.PartSlotDesc".Translate(), 5000);
            yield return new StatDrawEntry(PartCat, "DMSE.Card.CompatibleBodies".Translate(),
                bodies.Count.ToString(),
                "DMSE.Card.CompatibleBodiesDesc".Translate()
                + (part.requiresLoiteringBody ? "\n" + "DMSE.Card.RequiresLoitering".Translate().ToString() : string.Empty)
                + (bodies.Count > 0 ? "\n\n" + string.Join("\n", bodies.Select(b => "  - " + b.LabelCap)) : string.Empty),
                4990, hyperlinks: bodies.Select(b => BodyLink(b, MissileItemUtility.ItemDefFor(b)).First()));
            if (part.researchPrerequisites != null && part.researchPrerequisites.Count > 0)
            {
                yield return ResearchEntry(PartCat, part.researchPrerequisites, 4980);
            }
            yield return new StatDrawEntry(PartCat, "DMSE.Card.Cost".Translate(),
                part.costList != null && part.costList.Count > 0 ? string.Join(" · ", part.costList.Select(c => c.thingDef.LabelCap + " " + c.count)) : "DMSE.Bench.NoMaterials".Translate().ToString(),
                "DMSE.Card.CostDesc".Translate(), 4970,
                hyperlinks: part.costList?.Select(c => new Dialog_InfoCard.Hyperlink(c.thingDef)));

            // ---- 數值修正（加算在彈體基礎值上）----
            string modDesc = "DMSE.Card.ModifierDesc".Translate();
            if (part.damageDefOverride != null)
            {
                yield return new StatDrawEntry(Profile, "DMSE.Bench.Stat.DamageType".Translate(), part.damageDefOverride.LabelCap,
                    "DMSE.Card.DamageTypeDesc".Translate(), 4900);
            }
            if (part.damageAmountOffset != 0)
            {
                yield return Modifier("DMSE.UI.DamageChange".Translate(), part.damageAmountOffset.ToStringWithSign(), modDesc, 4890);
            }
            if (part.explosionRadiusOffset != 0f)
            {
                yield return Modifier("DMSE.Bench.Stat.Radius".Translate(), part.explosionRadiusOffset.ToStringWithSign("0.#"), modDesc, 4880);
            }
            if (part.scatterOffset != 0f)
            {
                yield return Modifier("DMSE.Bench.Stat.Scatter".Translate(), part.scatterOffset.ToStringWithSign("0.#"),
                    modDesc + "\n\n" + "DMSE.Card.ScatterDesc".Translate(), 4870);
            }
            if (part.worldSpeedFactorOffset != 0f)
            {
                yield return Modifier("DMSE.Bench.Stat.Speed".Translate(), part.worldSpeedFactorOffset.ToStringWithSign("0.##"),
                    modDesc + "\n\n" + "DMSE.Card.SpeedDesc".Translate(), 4860);
            }
            if (part.fuelOffset != 0f)
            {
                yield return Modifier("DMSE.Card.Fuel".Translate(), part.fuelOffset.ToStringWithSign("0.#"),
                    modDesc + "\n\n" + "DMSE.Card.FuelImpulseDesc".Translate(), 4850);
            }
            if (part.specificImpulseOffset != 0f)
            {
                yield return Modifier("DMSE.Card.Impulse".Translate(), part.specificImpulseOffset.ToStringWithSign("0.##"),
                    modDesc + "\n\n" + "DMSE.Card.FuelImpulseDesc".Translate(), 4840);
            }
            if (part.rangeOffset != 0)
            {
                yield return Modifier("DMSE.UI.RangeChange".Translate(), part.rangeOffset.ToStringWithSign(), modDesc, 4830);
            }

            // ---- 導引 ----
            bool isGuidance = part.category == MissilePartCategory.Guidance;
            if (isGuidance || part.guidanceType != null)
            {
                string mech = part.guidanceType?.MechanismDescription;
                yield return new StatDrawEntry(Mechanics, "DMSE.Card.TerminalGuidance".Translate(),
                    part.ProvidesTerminalGuidance ? "Yes".Translate() : "No".Translate(),
                    (mech.NullOrEmpty() ? string.Empty : mech + "\n\n")
                    + (part.ProvidesTerminalGuidance ? "DMSE.Card.TerminalGuidanceYes" : "DMSE.Card.TerminalGuidanceNo").Translate(),
                    4700);
                if (part.guidanceType != null)
                {
                    yield return new StatDrawEntry(Mechanics, "DMSE.Card.TargetSelection".Translate(),
                        (part.guidanceType.PlayerChoosesTarget ? "DMSE.Card.TargetManual" : "DMSE.Card.TargetAuto").Translate(),
                        mech.NullOrEmpty() ? "DMSE.Card.SpreadNote".Translate().ToString() : mech + "\n\n" + "DMSE.Card.SpreadNote".Translate(), 4690);
                    if (part.guidanceType.ExtraTerminalUpdates != 0)
                    {
                        yield return new StatDrawEntry(Mechanics, "DMSE.Bench.Stat.TerminalUpdates".Translate(),
                            part.guidanceType.ExtraTerminalUpdates.ToStringWithSign(), "DMSE.Card.TerminalUpdatesDesc".Translate(), 4680);
                    }
                    if (part.guidanceType is GuidanceType_RadioCommand || part.guidanceType is GuidanceType_FireControl)
                    {
                        yield return new StatDrawEntry(Mechanics, "DMSE.Bench.Stat.Channels".Translate(), "DMSE.Card.ChannelsPerMissile".Translate(),
                            "DMSE.Card.ChannelsDesc".Translate(), 4670);
                    }
                }
            }

            // ---- 飛行行為 ----
            if (part.loiterTicks > 0)
            {
                yield return new StatDrawEntry(Mechanics, "DMSE.Bench.Stat.Loiter".Translate(), part.loiterTicks.ToStringTicksToPeriod(),
                    "DMSE.Missile.Stat.Loiter".Translate(part.loiterTicks.ToStringTicksToPeriod()) + "\n\n"
                    + "DMSE.Card.LoiterRetry".Translate(Mathf.Max(1, part.loiterRetryTicks).ToStringTicksToPeriod()) + "\n"
                    + "DMSE.Card.LoiterOnlyLoitering".Translate(), 4660);
            }
            if (part.signatureFactor != 1f)
            {
                yield return new StatDrawEntry(Mechanics, "DMSE.Bench.Stat.Signature".Translate(),
                    part.signatureFactor.ToStringByStyle(ToStringStyle.FloatMaxTwo, ToStringNumberSense.Factor),
                    "DMSE.Card.SignatureDesc".Translate(), 4650);
            }
            if (part.chaffCharges > 0 || part.jamStrength > 0f)
            {
                yield return new StatDrawEntry(Mechanics, "DMSE.Bench.Stat.Chaff".Translate(),
                    part.chaffCharges + " (" + Mathf.Clamp01(part.jamStrength).ToStringPercent() + ")",
                    ChaffReport(part.jamStrength), 4640);
            }

            // ---- 特效（依各相容彈體的 N 代入）----
            if (part.warheadEffect != null)
            {
                bool roof = part.warheadEffect.PenetratesThickRoof;
                StatDrawEntry e = EffectEntry("DMSE.Card.WarheadEffect".Translate(), bodies, part.warheadEffect.EffectDescription, roof, 4600);
                if (e != null) { yield return e; }
            }
            if (part.payloadEffect != null || part.payloadExplosionRadius > 0f || part.payloadSpawnThing != null)
            {
                bool roof = part.payloadEffect?.PenetratesThickRoof ?? false;
                StatDrawEntry e = EffectEntry("DMSE.Card.PayloadEffect".Translate(), bodies, nv => PayloadText(part, nv), roof, 4590);
                if (e != null) { yield return e; }
            }
        }

        // ================================================================== 共用

        private static StatDrawEntry Modifier(string label, string value, string report, int order)
            => new StatDrawEntry(Profile, label, value, report, order);

        private static StatDrawEntry ResearchEntry(StatCategoryDef cat, List<ResearchProjectDef> research, int order)
            => new StatDrawEntry(cat, "DMSE.Card.Research".Translate(),
                string.Join(", ", research.Select(r => r.LabelCap.ToString())),
                "DMSE.Card.ResearchDesc".Translate(), order,
                hyperlinks: research.Select(r => new Dialog_InfoCard.Hyperlink(r)));

        /// <summary>
        /// 特效條目：值欄顯示 N（多種彈體時為區間），說明欄依不同 N 分組列出代入後的實際效果，
        /// 例如燒夷彈頭在 N=5 的彈體上是 5 格、在 N=8 的彈體上是 8 格。
        /// </summary>
        private static StatDrawEntry EffectEntry(string label, List<MissileBodyDef> bodies, System.Func<float, string> describe, bool roof, int order)
        {
            List<IGrouping<float, MissileBodyDef>> groups = bodies.GroupBy(b => b.payloadCapacity).OrderBy(g => g.Key).ToList();
            StringBuilder sb = new StringBuilder();
            if (groups.Count == 0)
            {
                string text = describe(5f);
                if (text.NullOrEmpty()) { return null; }
                sb.Append(text);
            }
            else if (groups.Count == 1)
            {
                string text = describe(groups[0].Key);
                if (text.NullOrEmpty()) { return null; }
                sb.Append(text).Append("\n\n").Append("DMSE.Card.ScaledByN".Translate(groups[0].Key.ToString("0.#")));
            }
            else
            {
                sb.Append("DMSE.Card.ScaledByNGroups".Translate());
                foreach (IGrouping<float, MissileBodyDef> g in groups)
                {
                    string text = describe(g.Key);
                    if (text.NullOrEmpty()) { continue; }
                    sb.Append("\n\n").Append("DMSE.Card.NGroup".Translate(g.Key.ToString("0.#"),
                        string.Join(", ", g.Select(b => b.LabelCap.ToString())))).Append("\n").Append(text);
                }
            }
            if (roof) { sb.Append("\n\n").Append("DMSE.Card.ThickRoofDesc".Translate()); }
            string value = groups.Count == 0 ? "-"
                : groups.Count == 1 ? "N = " + groups[0].Key.ToString("0.#")
                : "N = " + groups[0].Key.ToString("0.#") + "~" + groups[groups.Count - 1].Key.ToString("0.#");
            return new StatDrawEntry(Mechanics, label, value, sb.ToString(), order);
        }

        /// <summary>酬載說明：效果類別的機制，加上 XML 欄位定義的附加爆炸與填充物。</summary>
        private static string PayloadText(MissilePartDef part, float n)
        {
            if (part == null) { return null; }
            List<string> lines = new List<string>();
            string effect = part.payloadEffect?.EffectDescription(n);
            if (!effect.NullOrEmpty()) { lines.Add(effect); }
            if (part.payloadExplosionRadius > 0f)
            {
                lines.Add("DMSE.Card.Payload.Extra".Translate(part.payloadExplosionRadius.ToString("0.#"),
                    part.payloadDamageAmount, (part.payloadDamageDef ?? DamageDefOf.Bomb).label));
            }
            if (part.payloadSpawnThing != null)
            {
                lines.Add("DMSE.Card.Payload.Spawn".Translate(part.payloadSpawnCount, part.payloadSpawnThing.LabelCap,
                    part.payloadSpawnChance.ToStringPercent()));
            }
            return lines.Count > 0 ? string.Join("\n", lines) : null;
        }

        /// <summary>箔條說明：代入世界層斷鎖機率與地圖層攔截命中懲罰（與 BVR 計算同一組調校值）。</summary>
        private static string ChaffReport(float jam)
        {
            jam = Mathf.Clamp01(jam);
            BVRWorldTuningDef tuning = BVRWorldTuningDef.Current;
            return "DMSE.Card.ChaffDesc".Translate(
                Mathf.Clamp01(tuning.chaffBreakChance * jam).ToStringPercent(),
                Mathf.Clamp01(tuning.chaffMapHitPenalty * jam).ToStringPercent());
        }

        private static string RangeText(int range)
            => range > 0 ? "DMSE.Card.RangeTiles".Translate(range).ToString() : "DMSE.Card.Unlimited".Translate().ToString();

        private static string RangeReport(MissileConfig c)
        {
            float n = c.PayloadCapacity;
            float warheadOffset = c.PartFor(MissilePartCategory.Warhead)?.warheadEffect?.RangeOffset(n) ?? 0f;
            float payloadFactor = c.PartFor(MissilePartCategory.Payload)?.payloadEffect?.RangeFactor(n) ?? 1f;
            int fixedBonus = c.body.baseRange;
            foreach (MissilePartDef p in c.parts) { if (p != null) { fixedBonus += p.rangeOffset; } }
            return "DMSE.Card.RangeDesc".Translate(
                c.Fuel.ToString("0.#"), c.SpecificImpulse.ToString("0.##"), c.body.rangePerFuelImpulse.ToString("0.###"),
                fixedBonus.ToStringWithSign(), warheadOffset.ToStringWithSign("0.#"), payloadFactor.ToStringPercent(),
                RangeText(c.Range));
        }

        private static string GuidanceReport(MissileConfig c)
        {
            List<string> lines = new List<string>();
            string mech = c.TargetingGuidance?.MechanismDescription;
            lines.Add(mech.NullOrEmpty() ? "DMSE.Card.Guidance.Inertial".Translate().ToString() : mech);
            lines.Add(c.HasTerminalGuidance
                ? "DMSE.Missile.Stat.TerminalGuidance".Translate(c.TerminalUpdates).ToString()
                : "DMSE.Missile.Stat.NoTerminalGuidance".Translate().ToString());
            if (c.PartFor(MissilePartCategory.Guidance) == null && c.body.builtInGuidance != null)
            {
                lines.Add("DMSE.Card.BuiltInGuidance".Translate());
            }
            if (c.TracksMobileTargets) { lines.Add("DMSE.Card.TracksMobile".Translate()); }
            return string.Join("\n\n", lines);
        }

        private static IEnumerable<Dialog_InfoCard.Hyperlink> BodyLink(MissileBodyDef body, ThingDef item)
        {
            yield return item != null ? new Dialog_InfoCard.Hyperlink(item) : new Dialog_InfoCard.Hyperlink(body);
        }

        /// <summary>
        /// 玩家可建造、會被裝填工作派工（<see cref="WorkGiver_LoadMissileLauncher.IsLauncher"/> 的同一組 comp）、
        /// 且固定儲存過濾允許此物品的發射平台。與執行期的裝填判定同源。
        /// </summary>
        public static List<ThingDef> PlatformsFor(ThingDef item)
        {
            if (item == null) { return new List<ThingDef>(); }
            if (platformsByItem.TryGetValue(item, out List<ThingDef> cached)) { return cached; }
            List<ThingDef> list = DefDatabase<ThingDef>.AllDefsListForReading
                .Where(d => d.BuildableByPlayer && d.comps != null
                            && d.comps.Any(c => c.compClass != null
                                && (typeof(CompMissileLauncher_Rail).IsAssignableFrom(c.compClass)
                                    || typeof(CompMissileLauncher_Interceptor).IsAssignableFrom(c.compClass)))
                            && (d.building?.fixedStorageSettings?.filter?.Allows(item) ?? false))
                .OrderBy(d => d.label)
                .ToList();
            platformsByItem[item] = list;
            return list;
        }
    }
}
