# RimWorld DMSE Missile System Implementation

This document describes the new missile system features implemented for the Dead-Man-Switch-Expedition mod.

## Overview

Three major features have been implemented to create a comprehensive missile system:

1. **Missile Warhead System** (??系?) - Multiple warhead types with custom effects
2. **Missile Guidance System** (制?系?) - Multiple guidance methods with different accuracy/range profiles  
3. **Missile Loader Component** (??系?) - Building component to load missiles on launcher buildings

---

## 1. Missile Warhead System (`MissileWarhead.cs`)

### Warhead Types

The system supports 5 different warhead types:

#### a) **Explosive** (爆炸??)
- Standard high-explosive warhead
- Causes blast damage in a radius
- Creates explosion effects
- Properties: `blastRadius`, `blastDamage`, `damageType`

#### b) **Incendiary** (燃???)
- Spreads fire across impact area
- Lower blast damage but high fire chance
- Effective against buildings and vegetation
- Properties: `fireExplosionChance`

#### c) **EMP** (?磁????)
- Disables electronics in area
- No physical damage
- Useful against mechanoid threats
- Properties: `empDisableChance`, `empDisableDuration`

#### d) **Fragmentation** (破片??)
- Splits into multiple projectiles on impact
- Initial explosion plus fragment scatter
- Good anti-personnel weapon
- Properties: `fragmentCount`, `fragmentDamage`, `fragmentProjectile`

#### e) **Custom** (自定?)
- Framework for modders to add custom warhead effects via ModExtensions

### Core Classes

- **`MissileWarheadDef`**: Def class that defines warhead properties
- **`MissileWarheadData`**: Runtime data instance for loaded warheads
- **`Detonate()`**: Main method that handles warhead detonation at target location

### Usage Example

```csharp
// Get a warhead definition
var warheadDef = DMSE_DefOf.MissileWarhead_HighExplosive;

// Create warhead data
var warheadData = new MissileWarheadData(warheadDef);

// Detonate at target position
warheadData.Detonate(targetCell, currentMap);
```

---

## 2. Missile Guidance System (`MissileGuidance.cs`)

### Guidance Methods

#### a) **Ballistic** (?道)
- Simple gravity-based trajectory
- Lowest cost, no active guidance
- Good for stationary targets
- Accuracy: 85%

#### b) **Proportional Navigation** (比例?航)
- Advanced guidance tracking moving targets
- Uses line-of-sight rate calculation
- Better for dynamic combat
- Accuracy: 92%, Radar Range: 80 tiles

#### c) **Inertial** (?性)
- Self-contained system, no external tracking
- Maintains constant direction/speed
- Independent of external systems
- Accuracy: 88%

#### d) **Gravity Gradient** (重力梯度)
- Uses gravity field gradients for guidance
- Specialized system
- Medium performance
- Accuracy: ~90%

#### e) **Active Radar** (主?雷?)
- Sophisticated radar-guided system
- Excellent against moving targets
- Highest accuracy
- Accuracy: 98%, Radar Range: 150 tiles

### Core Classes

- **`MissileGuidanceDef`**: Defines guidance system properties
- **`MissileGuidanceState`**: Runtime state of active guidance
- **`UpdateGuidance()`**: Main update method called each tick
- **`MissileGuidanceUtility`**: Helper functions for guidance calculations

### Key Properties

Each guidance system has:
- `maxTurnRate`: How quickly missile can change direction
- `accuracy`: Accuracy percentage (0-1)
- `radarRange`: Detection range for radar-guided systems
- `maxSpeed` / `minSpeed`: Velocity constraints
- `gravityFactor`: How much gravity affects trajectory
- `airResistance`: Drag coefficient

### Guidance Info

```csharp
var guidance = new MissileGuidanceState(guidanceDef);
guidance.targetPosition = targetCell.ToVector3();

// Update each tick
guidance.UpdateGuidance(currentPosition);

// Get next missile position
Vector3 nextPos = guidance.GetNextPosition(currentPos);

// Check if reached target
if (guidance.IsTargetReached(currentPos))
{
    // Detonate warhead
}
```

---

## 3. Missile Launcher Loader Component (`CompMissileLoader.cs`)

### Purpose

Adds ammunition/missile loading capability to launcher buildings. Manages:
- Missile storage slots
- Loading/preparation phases
- Fuel management
- Ready-to-launch status

### Loading Phases

Missiles go through 3 phases:

1. **LoadingMissile** - Install missile body
   - Duration: `ticksToLoadMissile` (default: 1200 ticks = 20 seconds)
   
2. **LoadingWarhead** - Attach warhead
   - Duration: `ticksToLoadWarhead` (default: 600 ticks = 10 seconds)
   
3. **LoadingFuel** - Fill fuel tanks
   - Duration: `ticksToLoadFuel` (default: 2400 ticks = 40 seconds)

Total time per missile: ~70 seconds

### Core Classes

- **`CompProperties_MissileLoader`**: Configuration properties
- **`CompMissileLoader`**: Main component class
- **`MissileLoadout`**: Data structure for individual missile slots
- **`Dialog_MissileLoaderUI`**: UI window for managing missiles

### Configuration Properties

```csharp
public int maxMissiles = 4;              // Number of missile slots
public int maxAmmo = 100;                // Ammunition units
public int ticksToLoadMissile = 1200;   // Load time
public int ticksToLoadWarhead = 600;    // Warhead installation time
public int ticksToLoadFuel = 2400;      // Fuel loading time
public bool requiresPowerToLoad = true;  // Needs power to load
```

### Usage Example

```csharp
// Get loader component
var loader = building.GetComp<CompMissileLoader>();

// Start loading missile with specific guidance and warhead
loader.StartLoadingMissile(0, 
    DMSE_DefOf.MissileGuidance_Radar, 
    DMSE_DefOf.MissileWarhead_HighExplosive);

// Check ready missiles count
int readyCount = loader.GetReadyMissileCount();

// Launch ready missile
loader.LaunchMissile(0, targetCell);

// Get loading progress
float progress = loader.GetLoadingProgress(); // 0-1
```

### UI Integration

- Shows all missile slots
- Displays loading progress
- Shows guidance and warhead info
- Indicates "Ready to Launch" status
- Can unload missiles

---

## Integration with Existing Systems

### RadarUtility Integration

The guidance system integrates with existing `RadarUtility` for:
- Arrival time calculations
- Coverage calculations
- Interception window determination

### Existing Missile Components

The new systems complement existing systems:
- `CompScorer`: Missile launcher
- `ScorerProjectile`: Physical missile projectile
- `ScorerProjectile_WorldObject`: World-level missile tracking

---

## DefOf References

New static DefOf references added to `DMSE_DefOf.cs`:

```csharp
// Guidance systems
public static MissileGuidanceDef MissileGuidance_Ballistic;
public static MissileGuidanceDef MissileGuidance_Radar;
public static MissileGuidanceDef MissileGuidance_Proportional;

// Warheads
public static MissileWarheadDef MissileWarhead_HighExplosive;
public static MissileWarheadDef MissileWarhead_Incendiary;
public static MissileWarheadDef MissileWarhead_EMP;
public static MissileWarheadDef MissileWarhead_Fragmentation;
```

---

## XML Definitions

All systems are defined in `Defs/MissileSystemDefs.xml`:

- 4 Guidance system definitions
- 4 Warhead definitions
- All with localizable labels and descriptions
- Configurable parameters

---

## Advanced Features

### Guidance Accuracy Degradation

Accuracy decreases with distance:
```csharp
currentAccuracy = Mathf.Clamp01(accuracy * (1f - distance / 1000f));
```

### Intercept Point Calculation

For guided missiles against moving targets:
```csharp
Vector3 interceptPoint = MissileGuidanceUtility.CalculateInterceptPoint(
    launchPos, targetPos, targetVelocity, missileSpeed);
```

### Deviation Calculation

Applies aiming error based on accuracy:
```csharp
float deviation = MissileGuidanceUtility.CalculateDeviation(
    currentPos, targetPos, accuracy, distance);
```

---

## Save/Load Support

All systems support save/load:
- `MissileGuidanceState.ExposeData()`
- `MissileWarheadData.ExposeData()`
- `MissileLoadout.ExposeData()`
- `CompMissileLoader.PostExposeData()`

---

## Localization Keys

UI strings use these translation keys:
- `DMSE.Missile.LoadingMissile`
- `DMSE.Missile.LoadingWarhead`
- `DMSE.Missile.LoadingFuel`
- `DMSE.Missile.Idle`
- `DMSE.Missile.OpenLoaderUI`
- `DMSE.Missile.OpenLoaderUIDesc`
- `DMSE.Missile.MissileLoader`

Add these to your language file for localization.

---

## Future Expansion Points

The system is designed for modder extensions:

1. **Custom Warheads**: Create warhead types with ModExtensions
2. **New Guidance Methods**: Add new GuidanceMethod enum values
3. **Custom Effects**: Override `Detonate()` method
4. **Loading Mechanics**: Extend `CompMissileLoader` for special loading
5. **Visual Effects**: Add custom EffecterDef for each system

---

## Performance Considerations

- Guidance updates only run during active flight
- Fragmentation warheads spawn projectiles efficiently
- Loading uses simple state machine (low overhead)
- Radar calculations integrated with existing utility

---

## Known Limitations

1. Fragmentation projectiles use simplified Launch mechanics
2. EMP warhead currently targets power systems only
3. Accuracy decay is linear (not realistic curve)
4. No missile-to-missile interception support

These can be enhanced in future versions based on gameplay feedback.
