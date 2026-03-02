# Missile System - Quick Reference

## All-in-One Cheat Sheet

### DefOf References
```csharp
// Guidance Systems
DMSE_DefOf.MissileGuidance_Ballistic
DMSE_DefOf.MissileGuidance_Proportional
DMSE_DefOf.MissileGuidance_Radar
DMSE_DefOf.MissileGuidance_Inertial
DMSE_DefOf.MissileGuidance_GravityGradient

// Warheads
DMSE_DefOf.MissileWarhead_HighExplosive
DMSE_DefOf.MissileWarhead_Incendiary
DMSE_DefOf.MissileWarhead_EMP
DMSE_DefOf.MissileWarhead_Fragmentation
```

### Key Classes

#### MissileWarheadDef
```
WarheadType: Explosive | Incendiary | EMP | Fragmentation | Custom
float blastRadius, blastDamage
DamageDef damageType
EffecterDef detonationEffecter
SoundDef detonationSound
FleckDef explosionFleck
float fireExplosionChance
float empDisableChance
int empDisableDuration
int fragmentCount
```

#### MissileGuidanceDef
```
GuidanceMethod: Ballistic | Proportional | Inertial | GravityGradient | Radar
float maxTurnRate
float accuracy (0-1)
float radarRange
float maxSpeed, minSpeed
float accelerationRate
float gravityFactor
float airResistance
float weight, cost, powerConsumption
```

#### CompMissileLoader
```
Methods:
- StartLoadingMissile(index, guidance, warhead)
- LaunchMissile(index, targetCell)
- UnloadMissile(index)
- GetReadyMissileCount()
- GetLoadingProgress()
- GetCurrentLoadingPhaseLabel()
- GetMissileLoadout(index)
- GetAllMissiles()

Properties:
- maxMissiles
- maxAmmo
- ticksToLoadMissile
- ticksToLoadWarhead
- ticksToLoadFuel
- requiresPowerToLoad
- availableGuidances (List)
- availableWarheads (List)
```

#### MissileGuidanceState
```
Properties:
- guidanceDef
- currentVelocity (Vector3)
- targetPosition (Vector3)
- currentSpeed
- currentAccuracy
- guidanceTicks
- isActive
- targetVelocity

Methods:
- UpdateGuidance(Vector3 currentPos, Thing target)
- GetNextPosition(Vector3 currentPos)
- IsTargetReached(Vector3 currentPos, float radius)
- GetGuidanceInfo()
```

#### MissileWarheadData
```
Properties:
- warheadDef
- loadedTicks

Methods:
- Detonate(IntVec3 targetPos, Map map)
```

### XML Template - Minimal Launcher
```xml
<li Class="DMSE.CompProperties_MissileLoader">
  <compClass>DMSE.CompMissileLoader</compClass>
  <maxMissiles>4</maxMissiles>
  <requiresPowerToLoad>true</requiresPowerToLoad>
  <availableGuidances>
    <li>MissileGuidance_Ballistic</li>
    <li>MissileGuidance_Radar</li>
  </availableGuidances>
  <availableWarheads>
    <li>MissileWarhead_HighExplosive</li>
    <li>MissileWarhead_Incendiary</li>
  </availableWarheads>
</li>
```

### Code Patterns

#### Load Missile
```csharp
var loader = building.GetComp<CompMissileLoader>();
loader.StartLoadingMissile(0, 
    DMSE_DefOf.MissileGuidance_Radar,
    DMSE_DefOf.MissileWarhead_HighExplosive);
```

#### Fire Missile
```csharp
var loaded = loader.GetMissileLoadout(0);
if (loaded?.isReadyToLaunch == true)
{
    launcher.FireMissile(loaded, targetCell);
    loader.LaunchMissile(0, targetCell);
}
```

#### Apply Warhead
```csharp
public class MyMissile : Projectile
{
    public MissileWarheadData warhead;
    
    protected override void Impact(Thing hitThing, IntVec3? hitCell)
    {
        base.Impact(hitThing, hitCell);
        if (warhead != null && hitCell.HasValue)
            warhead.Detonate(hitCell.Value, Map);
        Destroy();
    }
}
```

#### Apply Guidance
```csharp
public class MyGuidedMissile : Projectile
{
    public MissileGuidanceState guidance;
    
    public override void Tick()
    {
        base.Tick();
        if (guidance != null && guidance.isActive)
        {
            guidance.UpdateGuidance(DrawPos);
            SetExactPosition(guidance.GetNextPosition(DrawPos).ToIntVec3());
            if (guidance.IsTargetReached(DrawPos))
                Impact(null, Position);
        }
    }
}
```

### Warhead Effects Quick Guide

| Type | Damage | Fire | Area | Effect |
|------|--------|------|------|--------|
| Explosive | High | Low | Large | Blast |
| Incendiary | Medium | High | Large | Fire |
| EMP | Low | None | Large | Disable |
| Fragment | Medium | Low | Medium | Scatter |

### Guidance Performance Quick Guide

| Method | Accuracy | Speed | Cost | Best For |
|--------|----------|-------|------|----------|
| Ballistic | 85% | High | Low | Stationary targets |
| Proportional | 92% | High | Medium | Moving targets |
| Inertial | 88% | High | Medium | Independent systems |
| GravityGradient | 90% | High | Medium | Specialized |
| Radar | 98% | High | High | All targets |

### Loader Configuration Quick Guide

| Setting | Fast | Balanced | Slow |
|---------|------|----------|------|
| ticksToLoadMissile | 800 | 1200 | 1600 |
| ticksToLoadWarhead | 400 | 600 | 800 |
| ticksToLoadFuel | 1600 | 2400 | 3200 |
| Total time | ~47s | ~67s | ~87s |

### Loading Phases
1. **LoadingMissile** - Install body
2. **LoadingWarhead** - Attach warhead  
3. **LoadingFuel** - Fill tanks
4. **Complete** - Ready to fire

### UI Integration
```csharp
// Show loading progress
float progress = loader.GetLoadingProgress(); // 0-1
string phase = loader.GetCurrentLoadingPhaseLabel();

// Show ready count
int ready = loader.GetReadyMissileCount();
int max = loader.Props.maxMissiles;

// Show missile info
var missiles = loader.GetAllMissiles();
foreach (var missile in missiles)
{
    string info = missile.GetLoadoutInfo();
}
```

### Save/Load
All classes support Scribe:
```csharp
// Automatic via IExposable/CompProperties
Scribe_Deep.Look(ref warhead, "warhead");
Scribe_Collections.Look(ref loadouts, "loadouts", LookMode.Deep);
```

### Performance Notes
- ? Loading: O(1) state machine
- ? Guidance: O(1) per tick (when active)
- ? Warhead: O(n) where n = affected objects
- ? Memory: Only active missiles consume memory
- ? No background overhead when idle

### Common Mistakes to Avoid
- ? Don't call UpdateGuidance() every frame in Tick() without time management
- ? Don't forget to check `isReadyToLaunch` before firing
- ? Don't forget to null-check warhead/guidance
- ? Don't forget to call Destroy() after Impact()
- ? Don't forget to add definitions to DefOf.cs
- ? Don't forget to implement ExposeData() for save/load

### Localization Keys
```
DMSE.Missile.LoadingMissile
DMSE.Missile.LoadingWarhead
DMSE.Missile.LoadingFuel
DMSE.Missile.Idle
DMSE.Missile.OpenLoaderUI
DMSE.Missile.OpenLoaderUIDesc
DMSE.Missile.MissileLoader
```

### File Locations
```
Core Classes:
- DMSE\Scorer\MissileWarhead.cs
- DMSE\Scorer\MissileGuidance.cs
- DMSE\Scorer\CompMissileLoader.cs

Definitions:
- DMSE\Defs\MissileSystemDefs.xml
- DMSE\Defs\MissileLauncherExamples.xml

Static References:
- DMSE\DefOf.cs (7 new references)
```

### Resources Needed
Minimal - only what you define in XML:
- Steel (for construction)
- Plasteel (high-tech)
- Advanced (for guided systems)
- Component (for electronics)

### Power Consumption
- Basic Loader: 150-300 W
- Advanced Loader: 400-500 W
- Guidance Systems: 1-5 W (active only)
- Radar: 100-200 W (when armed)

### Integration Checklist
- [ ] Copy 3 C# files to Scorer/
- [ ] Add XML defs to your def files
- [ ] Update DefOf.cs
- [ ] Create missile projectile class
- [ ] Add to launcher building
- [ ] Add UI for weapon selection
- [ ] Test loading sequence
- [ ] Test firing sequence
- [ ] Test save/load
- [ ] Add localization strings
- [ ] Balance for gameplay
- [ ] Document for players

---

This reference covers 90% of common use cases. For detailed information, see INTEGRATION_GUIDE.md or MISSILE_SYSTEM_README.md.
