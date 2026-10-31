using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Xml.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using DMSE;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Grammar;


static partial class Test
{
    // docs/攔截殘骸特效_實作與技術美術計畫.md §3–§4: burst point on the incoming route, debris keeps the incoming momentum.
    static void InterceptDebrisGeometry()
    {
        var p = InterceptDebrisPhysics.Default;
        var size = new IntVec3(250, 1, 250);
        var center = new IntVec3(125, 0, 125);
        var landing = new IntVec3(125, 0, 125);
        Vector3 landingPos = landing.ToVector3Shifted(); landingPos.y = 0f;
        foreach (float heading in new[] { 0f, 45f, 90f, 135f, 180f, 225f, 270f, 315f })
        {
            var missile = new Projectile_MissileIncoming { def = Empty<ThingDef>(), angle = 0f };
            missile.SetEntryHeading(heading);
            Vector3 back = MissileMapEntryUtility.HeadingVector(heading);
            Vector3 forward = -back;
            Vector3 edge = MissileMapEntryUtility.EdgePoint(size, landingPos, heading);

            // Midcourse: virtual burst outside the map; only remote debris fades into view.
            var mid = InterceptEventData.Build(missile, landing, size, center, false, IntVec3.Invalid, 0f, null, 17 + (int)heading, p);
            Check(mid.hasHeading && mid.payloadKind == InterceptPayloadKind.Missile, $"intercept data reads missile heading {heading}");
            Near(mid.heading, heading, $"intercept data heading {heading}");
            Vector3 drawn = mid.BurstDrawPos(p); drawn.y = 0f;
            Check(mid.remote && InterceptGeometry.DrawHidden(mid.burstGround, mid.burstHeight, size, p),
                $"midcourse burst is hidden outside the map for {heading}");
            Check(InterceptGeometry.DistanceOutside(size, drawn) > 0f,
                $"midcourse burst draw position is outside the map for {heading}");
            Near(drawn.z - mid.burstGround.z, mid.burstHeight * p.heightScale, $"midcourse ground lies under the drawn burst for {heading}", 0.01f);
            Check(mid.burstHeight >= p.midcourseHeight.min && mid.burstHeight <= p.midcourseHeight.max, $"midcourse height for {heading}");


            // Terminal: at the CIWS aim cell, jittered only along the route; height follows the approach.
            IntVec3 aim = (landingPos + back * 30f).ToIntVec3();
            var onMap = InterceptEventData.Build(missile, landing, size, center, true, aim, p.terminalRemoteApproach, null, 3, p);
            var near = InterceptEventData.Build(missile, landing, size, center, true, aim, 1f, null, 3, p);
            Vector3 offset = onMap.burstGround - aim.ToVector3Shifted(); offset.y = 0f;
            Check(Mathf.Abs(Vector3.Dot(offset, new Vector3(back.z, 0f, -back.x))) < 0.01f
                && offset.magnitude <= p.terminalJitter + 0.01f, $"terminal burst stays on the CIWS aim line for {heading}");
            Check(!onMap.remote && !near.remote, $"terminal bursts drawn inside the map keep their airburst for {heading}");
            Near(near.burstHeight, p.terminalHeightNear, $"terminal height near {heading}");

            // Far terminal (just entered the window): a virtual burst beyond the entry edge, never drawn, debris fades in.
            var far = InterceptEventData.Build(missile, landing, size, center, true, aim, 0f, null, 3, p);
            Near(far.burstHeight, p.terminalHeightFar, $"terminal height far {heading}");
            Check(far.remote, $"far terminal intercept is remote for {heading}");
            Check(InterceptGeometry.DrawHidden(far.burstGround, far.burstHeight, size, p),
                $"far terminal burst is drawn out of sight for {heading} (south entries pushed further out)");
            Check(Vector3.Dot(far.burstGround - edge, back) >= -0.01f, $"far terminal ground point is at or beyond the entry edge for {heading}");
            Rand.PushState(2000 + (int)heading);
            try
            {
                int visibleLight = 0, visibleHeavy = 0;
                const int tries = 200;
                for (int i = 0; i < tries; i++)
                {
                    DebrisState light = InterceptGeometry.Launch(far.burstGround, far.burstHeight, heading, far.targetSpeed, 0.4f, p);
                    DebrisState heavy = InterceptGeometry.Launch(far.burstGround, far.burstHeight, heading, far.targetSpeed, 2f, p);
                    if (InterceptGeometry.BecomesVisible(light, size, p, Vector3.zero)) { visibleLight++; }
                    if (InterceptGeometry.BecomesVisible(heavy, size, p, Vector3.zero)) { visibleHeavy++; }
                }
                Console.WriteLine($"  remote debris heading {heading}: distance beyond edge {Vector3.Dot(far.burstGround - edge, back):0.#}, "
                    + $"visible light {visibleLight * 100 / tries}% heavy {visibleHeavy * 100 / tries}%");
                Check(visibleHeavy >= tries / 2, $"most heavy chunks from a far intercept reach the screen for {heading} ({visibleHeavy}/{tries})");
                Check(visibleLight > 0, $"some light fragments from a far intercept reach the screen for {heading} ({visibleLight}/{tries})");
            }
            finally { Rand.PopState(); }

            // Debris keeps the incoming momentum: mean horizontal velocity points along the flight direction,
            // landings fall ahead of the burst, heavy chunks carry further than light fragments.
            Rand.PushState(1000 + (int)heading);
            try
            {
                Vector3 meanVelocity = Vector3.zero, meanLight = Vector3.zero, meanHeavy = Vector3.zero;
                int maxTicks = 0;
                const int n = 200;
                for (int i = 0; i < n; i++)
                {
                    DebrisState light = InterceptGeometry.Launch(far.burstGround, far.burstHeight, heading, 1f, 0.2f, p);
                    DebrisState heavy = InterceptGeometry.Launch(far.burstGround, far.burstHeight, heading, 1f, 2.5f, p);
                    meanVelocity += light.velocity + heavy.velocity;
                    meanLight += InterceptGeometry.SimulateLanding(light, p, Vector3.zero, out int t1) - far.burstGround;
                    meanHeavy += InterceptGeometry.SimulateLanding(heavy, p, Vector3.zero, out int t2) - far.burstGround;
                    maxTicks = Mathf.Max(maxTicks, Mathf.Max(t1, t2));
                }
                Check(Vector3.Angle(meanVelocity, forward) < 30f, $"debris velocity follows incoming direction {heading}");
                Check(Vector3.Dot(meanLight / n, forward) > 0f && Vector3.Dot(meanHeavy / n, forward) > 0f,
                    $"debris lands ahead of the burst for {heading}");
                Check(Vector3.Dot(meanHeavy / n, forward) > Vector3.Dot(meanLight / n, forward),
                    $"heavy debris carries further than light fragments for {heading}");
                Check(maxTicks < p.maxLifeTicks, $"all debris lands within the lifetime for {heading} ({maxTicks})");
            }
            finally { Rand.PopState(); }
        }

        // Fall time (P6 tuning, plan §11 "1–2 s"): a heavy chunk from a mid terminal height lands in roughly 1–2 seconds.
        Rand.PushState(11);
        try
        {
            int total = 0;
            for (int i = 0; i < 50; i++)
            {
                InterceptGeometry.SimulateLanding(InterceptGeometry.Launch(Vector3.zero, 14f, 90f, 1f, 2f, p), p, Vector3.zero, out int ticks);
                total += ticks;
            }
            float mean = total / 50f;
            Check(mean >= 55f && mean <= 130f, $"heavy chunk from 14 cells lands in {mean:0} ticks on average (55–130)");
        }
        finally { Rand.PopState(); }

        // A hulk keeps the most momentum and barely spreads.
        Rand.PushState(5);
        try
        {
            DebrisState hulk = InterceptGeometry.Launch(Vector3.zero, 15f, 90f, 1f, 2f, p, hulk: true);
            Check(Vector3.Angle(hulk.velocity, Vector3.left) < 10f, "hulk flies on along the incoming direction");
        }
        finally { Rand.PopState(); }

        // Heading fallbacks: skyfaller angle (drop pods), then map center → landing.
        var pod = InterceptEventData.Build(new Skyfaller { def = Empty<ThingDef>(), angle = 200f }, landing, size, center, true,
            IntVec3.Invalid, 0.5f, null, 9, p);
        Check(pod.payloadKind == InterceptPayloadKind.DropPod && Mathf.Abs(pod.heading - 200f) < 0.001f, "drop pod uses skyfaller angle");
        var none = InterceptEventData.Build(null, new IntVec3(200, 0, 125), size, center, false, IntVec3.Invalid, 0f, null, 9, p);
        Check(!none.hasHeading && Mathf.Abs(Mathf.DeltaAngle(none.heading, 90f)) < 0.5f, "missing payload falls back to center bearing");
        Check(none.remote && none.burstGround.x > size.x,
            "fallback midcourse burst is virtual beyond the east edge");
        Near(InterceptGeometry.AverageSpeed(null), InterceptGeometry.FallbackSpeed, "unknown payload speed fallback");

        // A terminal burst whose height lift carries it past the north edge is no longer pulled back onto the edge:
        // it stays at the aim point and becomes remote (no airburst, debris fades in).
        var north = new Projectile_MissileIncoming { def = Empty<ThingDef>() };
        north.SetEntryHeading(0f);
        var edgeBurst = InterceptEventData.Build(north, new IntVec3(125, 0, 245), size, center, true, new IntVec3(125, 0, 249), 0.5f, null, 4, p);
        Check(edgeBurst.remote && Mathf.Abs(edgeBurst.burstGround.z - 249.5f) <= p.terminalJitter + 0.01f,
            "terminal burst lifted past the north edge stays at the aim point and turns remote");
        Near(edgeBurst.burstHeight, Mathf.Lerp(p.terminalHeightFar, p.terminalHeightNear, 0.5f), "remote edge burst keeps its height");

        // Fade band: transparent within drawEdgeInset of the border (and outside), opaque remoteFadeCells further in.
        Near(InterceptGeometry.EdgeFade(size, new Vector3(-3f, 0f, 125f), p), 0f, "edge fade is 0 off the map");
        Near(InterceptGeometry.EdgeFade(size, new Vector3(125f, 0f, size.z - p.drawEdgeInset + 0.5f), p), 0f, "edge fade is 0 inside the edge band");
        Near(InterceptGeometry.EdgeFade(size, new Vector3(p.drawEdgeInset + p.remoteFadeCells * 0.5f, 0f, 125f), p), 0.5f,
            "edge fade is half way through the fade cells", 0.01f);
        Near(InterceptGeometry.EdgeFade(size, new Vector3(125f, 0f, 125f), p), 1f, "edge fade is 1 well inside the map");
        Check(InterceptGeometry.ForwardTravel(0f, 18f, p) < 0.01f && InterceptGeometry.ForwardTravel(0.3f, 18f, p) < InterceptGeometry.ForwardTravel(1f, 18f, p),
            "representative chunk travel grows with the target speed");

        // Remote pieces in the container start transparent, are not culled while still outside, fade in, and leave normally.
        var fleckDefRemote = new FleckDef { solidTime = 10f, fadeOutTime = 0.5f };
        var remoteFlecks = new InterceptDebrisFlecks(p);
        var westMissile = new Projectile_MissileIncoming { def = Empty<ThingDef>() };
        westMissile.SetEntryHeading(270f);
        var west = InterceptEventData.Build(westMissile, landing, size, center, true, IntVec3.Invalid, 0f, null, 21, p);
        Check(west.remote && west.burstGround.x < 0f, "far terminal from the west puts the virtual burst off the west edge");
        Rand.PushState(78);
        try
        {
            for (int i = 0; i < 80 && remoteFlecks.Count < 30; i++)
            {
                DebrisState s = InterceptGeometry.Launch(west.burstGround, west.burstHeight, west.heading, west.targetSpeed, Rand.Range(0.2f, 0.6f), p);
                if (InterceptGeometry.BecomesVisible(s, size, p, Vector3.zero)) { remoteFlecks.Spawn(fleckDefRemote, s, 0.6f, Color.white, Color.grey, 30, remote: true); }
            }
        }
        finally { Rand.PopState(); }
        int remoteSpawned = remoteFlecks.Count;
        Check(remoteSpawned > 0, $"visible remote fragments are spawned ({remoteSpawned})");
        bool allStartHidden = true;
        for (int i = 0; i < remoteFlecks.Count; i++) { allStartHidden &= remoteFlecks.Pieces[i].fleck.instanceColor.a <= 0f; }
        Check(allStartHidden, "remote fragments start fully transparent");
        float maxAlpha = 0f;
        for (int tick = 0; tick < p.maxLifeTicks + 10 && remoteFlecks.Count > 0; tick++)
        {
            remoteFlecks.Tick(null, size, Vector3.zero);
            for (int i = 0; i < remoteFlecks.Count; i++) { maxAlpha = Mathf.Max(maxAlpha, remoteFlecks.Pieces[i].fleck.instanceColor.a); }
        }
        Check(remoteFlecks.Count == 0 && remoteFlecks.expiredCount == 0
            && remoteFlecks.landedCount + remoteFlecks.culledCount == remoteSpawned, "remote fragments leave by landing or culling");
        Check(maxAlpha >= p.remoteMinFade, $"remote fragments fade in once inside the map ({maxAlpha:0.##})");

        // B-layer debris container (§5.3): every piece leaves by landing or culling, never by the fleck lifetime;
        // pieces from an eastern entry land west of the burst; the drawn height follows the state.
        var fleckDef = new FleckDef { solidTime = 10f, fadeOutTime = 0.5f };
        var flecks = new InterceptDebrisFlecks(p);
        Vector3 burstGround = new Vector3(200f, 0f, 125f);
        Rand.PushState(77);
        try
        {
            for (int i = 0; i < 60; i++)
            {
                flecks.Spawn(fleckDef, InterceptGeometry.Launch(burstGround, 15f, 90f, 1f, Rand.Range(0.2f, 0.6f), p),
                    0.6f, Color.white, Color.grey, 30);
            }
        }
        finally { Rand.PopState(); }
        Check(flecks.Count == 60, "debris container holds every spawned piece");
        Near(flecks.Pieces[0].fleck.position.height, 15f * p.heightScale, "debris fleck height starts at the burst height");
        float sumX = 0f; int landedSeen = 0;
        for (int tick = 0; tick < p.maxLifeTicks + 10 && flecks.Count > 0; tick++)
        {
            for (int i = 0; i < flecks.Count; i++)
            {
                if (flecks.Pieces[i].state.height <= flecks.Pieces[i].state.verticalSpeed * -1.01f) { sumX += flecks.Pieces[i].state.ground.x; landedSeen++; }
            }
            flecks.Tick(null, size, Vector3.zero);
        }
        Check(flecks.Count == 0, "all debris pieces leave the container");
        Check(flecks.landedCount + flecks.culledCount == 60 && flecks.expiredCount == 0,
            $"debris leaves by landing or culling, not lifetime (landed {flecks.landedCount}, culled {flecks.culledCount})");
        Check(landedSeen > 0 && sumX / landedSeen < burstGround.x, "debris from an eastern entry lands west of the burst");

        // Culling: far off-map or too old.
        var cull = new DebrisState { ground = new Vector3(-p.offMapCull - 1f, 0f, 10f) };
        Check(InterceptGeometry.ShouldCull(cull, size, p), "debris far off-map is culled");
        cull.ground = new Vector3(-5f, 0f, 10f);
        Check(!InterceptGeometry.ShouldCull(cull, size, p), "debris just off-map keeps flying");
    }

    // docs/導彈發射特效_重製計畫.md §4.2 (L1): drifting smoke puffs stop, rise, drift with the wind, grow, fade, and expire.
    static void FxSmokeMotion()
    {
        var spec = new SmokePuffSpec
        {
            def = new FleckDef { solidTime = 1000f },
            pos = new Vector3(100f, 0f, 100f),
            velocity = new Vector3(0f, 0f, -1f),
            size = 2f, growth = 3f, lifeTicks = 480,
            drag = 0.08f, buoyancy = 0.004f, windFactor = 0.01f,
            hotColor = new Color(1f, 0.7f, 0.4f, 0.9f), color = new Color(1f, 1f, 1f, 0.8f), hotTicks = 10
        };

        // Exhaust push dies out within ~0.5 s; buoyancy then lifts the puff (+z on screen).
        Vector3 pos = spec.pos, vel = spec.velocity;
        for (int i = 0; i < 30; i++) { FxSmokeParticles.Step(ref pos, ref vel, spec, Vector3.zero); }
        Check(Mathf.Abs(vel.z) < 0.15f, $"smoke exhaust push dies out within 30 ticks (vz {vel.z:0.###})");
        float zAt30 = pos.z;
        for (int i = 0; i < 120; i++) { FxSmokeParticles.Step(ref pos, ref vel, spec, Vector3.zero); }
        Check(vel.z > 0f && pos.z > zAt30, "buoyancy lifts the smoke afterwards");

        // Wind drifts it downwind.
        Vector3 wpos = spec.pos, wvel = Vector3.zero;
        for (int i = 0; i < 120; i++) { FxSmokeParticles.Step(ref wpos, ref wvel, spec, new Vector3(1.5f, 0f, 0f)); }
        Check(wpos.x - spec.pos.x > 1f, $"wind drifts the smoke downwind ({wpos.x - spec.pos.x:0.#} cells)");

        // Growth and fade.
        Near(FxSmokeParticles.SizeAt(spec, 0), 2f, "smoke starts at its spawn size");
        Near(FxSmokeParticles.SizeAt(spec, spec.lifeTicks), 2f * (1f + 3f), "smoke ends at size × (1 + growth)");
        Check(FxSmokeParticles.SizeAt(spec, 120) < FxSmokeParticles.SizeAt(spec, 240), "smoke keeps widening");
        Check(FxSmokeParticles.AlphaAt(spec, 0) < 0.5f && Mathf.Abs(FxSmokeParticles.AlphaAt(spec, 50) - 1f) < 0.001f, "smoke fades in");
        Check(FxSmokeParticles.AlphaAt(spec, 300) < 1f && FxSmokeParticles.AlphaAt(spec, 470) < 0.05f, "smoke fades out over the second half of its life");

        // Container: puffs expire at their life; a puff blown off the map is culled.
        var smoke = new FxSmokeParticles();
        var size = new IntVec3(250, 1, 250);
        Rand.PushState(3);
        try
        {
            smoke.Spawn(spec);
            var older = spec; older.ageOffset = 400;
            smoke.Spawn(older);
            var edge = spec; edge.pos = new Vector3(249f, 0f, 100f); edge.velocity = new Vector3(2f, 0f, 0f); edge.drag = 0f;
            smoke.Spawn(edge);
        }
        finally { Rand.PopState(); }
        Check(smoke.Count == 3, "smoke container holds every puff");
        for (int i = 0; i < 100; i++) { smoke.Tick(size, Vector3.zero); }
        Check(smoke.Count == 1 && smoke.expiredCount == 1 && smoke.culledCount == 1,
            $"older puff expired and the off-map puff was culled (left {smoke.Count}, expired {smoke.expiredCount}, culled {smoke.culledCount})");
        for (int i = 0; i < 400; i++) { smoke.Tick(size, Vector3.zero); }
        Check(smoke.Count == 0 && smoke.expiredCount == 2, "the remaining puff expires at its life");

        // Ambient light is multiplied in (alpha untouched); amount 0 = self-lit, 1 = fully lit; no map = white.
        Color dusk = new Color(0.78f, 0.64f, 0.48f, 1f);
        Color lit = FxLighting.Apply(new Color(1f, 1f, 1f, 0.8f), dusk);
        Check(Mathf.Abs(lit.r - 0.78f) < 0.001f && Mathf.Abs(lit.b - 0.48f) < 0.001f && Mathf.Abs(lit.a - 0.8f) < 0.001f, "lighting multiplies the colour, keeps alpha");
        Color selfLit = FxLighting.Apply(new Color(1f, 0.7f, 0.4f, 1f), dusk, 0f);
        Check(Mathf.Abs(selfLit.g - 0.7f) < 0.001f, "self-lit (amount 0) ignores the ambient light");
        Color half = FxLighting.Apply(Color.white, dusk, 0.5f);
        Check(Mathf.Abs(half.b - 0.74f) < 0.001f, "partly self-lit blends toward the ambient light");
        Check(FxLighting.LightAt(null, Vector3.zero) == Color.white, "no map: unlit (white)");

        // Smoke shade is a grey tint on one texture (§0.5), matching the old light/mid/dark texture mix on average.
        Color tint = new Color(1f, 0.9f, 0.8f, 0.4f);
        Near(LaunchFxEmitter.ShadeTint(tint, 0f, 0f).r, 1f, "shade 0: the light texture untinted");
        Near(LaunchFxEmitter.ShadeTint(tint, 1f, 0f).r, 0.32f / 0.95f, "shade 1: as dark as the old dark texture");
        Near(LaunchFxEmitter.ShadeTint(tint, 0.5f, 0f).r, 0.25f + 0.5f * 0.6f / 0.95f + 0.25f * 0.32f / 0.95f, "shade 0.5: the old mix's average");
        Color shaded = LaunchFxEmitter.ShadeTint(tint, 0.5f, 1f);
        Near(shaded.a, 0.4f, "shade keeps alpha");
        Near(shaded.g / shaded.r, 0.9f, "shade keeps the hue");
        Check(LaunchFxEmitter.ShadeTint(tint, 0.5f, 1f).r > LaunchFxEmitter.ShadeTint(tint, 0.5f, -1f).r, "jitter varies the shade");

        // Ground cloud (§0.6): lobes of unequal reach plus a dense core, not an even ring.
        Rand.PushState(1234);
        try
        {
            var sectors = new int[12];
            int core = 0, total = 2000;
            LaunchFxEmitter.GroundLobe[] lobes = LaunchFxEmitter.PlanLobes(180f);
            Check(lobes.Length >= 3 && lobes.Length <= 5, $"3-5 ground lobes ({lobes.Length})");
            for (int i = 0; i < total; i++)
            {
                LaunchFxEmitter.GroundPlacement g = LaunchFxEmitter.GroundCloudPlacement(lobes);
                if (g.speedFactor <= 0.3f && g.sizeFactor >= 1.1f) { core++; continue; }
                sectors[Mathf.FloorToInt(Mathf.Repeat(g.heading, 360f) / 30f) % 12]++;
            }
            int outer = total - core, max = 0, empty = 0;
            foreach (int c in sectors) { max = Mathf.Max(max, c); if (c < outer / 12 / 4) { empty++; } }
            Check(max > 2.5f * outer / 12f, $"dust bunches into lobes (busiest 30° sector {max} vs even {outer / 12})");
            Check(empty >= 3, $"some directions get almost no dust ({empty} sparse sectors)");
            Check(core > total * 0.2f && core < total * 0.4f, $"about 30 % of the dust stays in the core ({core}/{total})");
        }
        finally { Rand.PopState(); }
    }

    // docs/導彈發射特效_重製計畫.md §4.1, §7: thrust timeline and smoke budget pressure.
    static void LaunchFxTimeline()
    {
        var solid = new LaunchFxProfileDef { rampTicks = 4 };
        Near(LaunchFxEmitter.ThrustAt(solid, -1, 0, 0.1f), 0f, "no thrust before ignition");
        Near(LaunchFxEmitter.ThrustAt(solid, -1, 2, 0.1f), 0.5f, "thrust ramps up over rampTicks");
        Near(LaunchFxEmitter.ThrustAt(solid, -1, 200, 0.5f), 1f, "full thrust while burning");
        var cruise = new LaunchFxProfileDef { rampTicks = 4, boosterTicks = 45, sustainThrust = 0.12f };
        Near(LaunchFxEmitter.ThrustAt(cruise, -1, 45, 0.2f), 1f, "booster at full thrust until burnout");
        Near(LaunchFxEmitter.ThrustAt(cruise, -1, 60, 0.3f), 0.12f, "after burnout thrust settles at the sustainer", 0.001f);
        Near(LaunchFxEmitter.ThrustAt(solid, 30, 30, 0.1f), 1f, "a burn-limited motor burns to its burn duration");
        Near(LaunchFxEmitter.ThrustAt(solid, 30, 38, 0.1f), 0f, "and is out 8 ticks later", 0.001f);
        Near(LaunchFxEmitter.ThrustAt(solid, 0, 10, 0.1f), 0f, "burnDurationTicks 0 disables the flame");
        var curved = new LaunchFxProfileDef { rampTicks = 1, thrustCurve = new SimpleCurve { new CurvePoint(0f, 1f), new CurvePoint(1f, 0.5f) } };
        Near(LaunchFxEmitter.ThrustAt(curved, -1, 10, 1f), 0.5f, "thrust curve scales by launch progress");

        int cap = LaunchFxBudget.SmokeCap;
        Check(LaunchFxBudget.Pressure(0) == 0 && LaunchFxBudget.Pressure((int)(cap * 0.75f)) == 1 && LaunchFxBudget.Pressure(cap) == 2,
            "smoke budget pressure: normal, over 70 %, at the cap");
        Check(LaunchFxBudget.BurstCount(16, cap) < LaunchFxBudget.BurstCount(16, 0), "bursts shrink at the smoke cap");
    }

    static void CiwsAimDirections()
    {
        var props = new CompProperties_TerminalCIWS { shotsPerBurst = 5, aimLeadCells = 4, aimSweepCells = 4 };
        var ciws = new CompTerminalCIWS { props = props };
        var size = new IntVec3(250, 1, 250);
        var origin = new IntVec3(125, 0, 125);
        foreach (float heading in new[] { 0f, 45f, 90f, 135f, 180f, 225f, 270f, 315f })
        {
            var missile = new Projectile_MissileIncoming { def = Empty<ThingDef>(), angle = 0f };
            missile.SetEntryHeading(heading); // Deliberately differs from angle: held missiles retain their entry heading.
            var target = new BVRTarget(missile, origin, null, 1);
            IntVec3 far = ciws.CalculateAimCell(target, origin, size, 0f, 2);
            IntVec3 near = ciws.CalculateAimCell(target, origin, size, 1f, 2);
            Near(Mathf.Abs(Mathf.DeltaAngle(MissileMapEntryUtility.HeadingOf((far - origin).ToVector3()), heading)),
                0f, $"CIWS aims into incoming heading {heading}", 1f);
            Check((near - origin).LengthHorizontalSquared < (far - origin).LengthHorizontalSquared,
                $"CIWS follows approach toward landing from {heading}");
            IntVec3 first = ciws.CalculateAimCell(target, origin, size, 0.5f, 0);
            IntVec3 last = ciws.CalculateAimCell(target, origin, size, 0.5f, 4);
            Vector3 sweep = (last - first).ToVector3();
            Check(sweep.magnitude >= 7f, $"CIWS sweeps across heading {heading}");
            Check(Mathf.Abs(Vector3.Dot(sweep, MissileMapEntryUtility.HeadingVector(heading))) < 1.5f,
                $"CIWS sweep is perpendicular to heading {heading}");
        }
        // The aim follows the missile's landing-to-entry line, including a turret away from that line.
        var east = new Projectile_MissileIncoming { def = Empty<ThingDef>() };
        east.SetEntryHeading(90f);
        var offset = new BVRTarget(east, new IntVec3(70, 0, 40), null, 2);
        IntVec3 aim = ciws.CalculateAimCell(offset, origin, size, 0f, 2);
        Check(aim.x > origin.x && aim.z == 40, "CIWS uses entry trajectory rather than turret-to-north edge");
        props.shotsPerBurst = 1;
        foreach (var landing in new[] { new IntVec3(0, 0, 0), new IntVec3(249, 0, 249) })
        {
            offset.position = landing;
            aim = ciws.CalculateAimCell(offset, origin, size, 0f, 0);
            Check(aim.x >= 0 && aim.x < size.x && aim.z >= 0 && aim.z < size.z,
                "CIWS single-shot aim remains inside map at corners");
        }
        var noPayload = new BVRTarget { position = new IntVec3(20, 0, 125) };
        aim = ciws.CalculateAimCell(noPayload, origin, size, 0f, 0);
        Check(aim.x < origin.x && aim.z == origin.z, "CIWS missing-payload fallback uses target bearing");
        var legacy = new BVRTarget(new Skyfaller { def = Empty<ThingDef>(), angle = 270f }, origin, null, 3);
        aim = ciws.CalculateAimCell(legacy, origin, size, 0f, 0);
        Check(aim.x < origin.x && aim.z == origin.z, "CIWS follows skyfaller entry angle");
        var noHeading = new BVRTarget(new Projectile_MissileIncoming { def = Empty<ThingDef>(), angle = 180f }, origin, null, 4);
        aim = ciws.CalculateAimCell(noHeading, origin, size, 0f, 0);
        Check(aim.z < origin.z && aim.x == origin.x, "CIWS older missiles use flight angle when entry heading is absent");
    }
}
