# Quick Integration Guide - Missile System

This guide shows how to integrate the new missile system into your launcher buildings and projectiles.

## 1. Add Loader to Launcher Building

### XML Configuration

In your launcher building `ThingDef`:

```xml
<ThingDef ParentName="BuildingBase">
  <defName>MissileLauncher_Advanced</defName>
  <label>Advanced Missile Launcher</label>
  <!-- ... other properties ... -->
  
  <comps>
    <!-- ... existing comps ... -->
    
    <li Class="DMSE.CompProperties_MissileLoader">
      <compClass>DMSE.CompMissileLoader</compClass>
      <maxMissiles>4</maxMissiles>
      <maxAmmo>100</maxAmmo>
      <ticksToLoadMissile>1200</ticksToLoadMissile>
      <ticksToLoadWarhead>600</ticksToLoadWarhead>
      <ticksToLoadFuel>2400</ticksToLoadFuel>
      <loaderLabel>Missile Loader</loaderLabel>
      <showLoaderUI>true</showLoaderUI>
      <requiresPowerToLoad>true</requiresPowerToLoad>
      
      <availableGuidances>
        <li>MissileGuidance_Ballistic</li>
        <li>MissileGuidance_Proportional</li>
        <li>MissileGuidance_Radar</li>
      </availableGuidances>
      
      <availableWarheads>
        <li>MissileWarhead_HighExplosive</li>
        <li>MissileWarhead_Incendiary</li>
        <li>MissileWarhead_EMP</li>
      </availableWarheads>
    </li>
  </comps>
</ThingDef>
```

## 2. Use Warheads in Missiles

### C# Code Example

```csharp
public class CustomMissile : Projectile
{
    public MissileWarheadData warhead;
    
    protected override void Impact(Thing hitThing, IntVec3? hitCell)
    {
        base.Impact(hitThing, hitCell);
        
        // Detonate warhead at impact
        if (warhead != null && hitCell.HasValue)
        {
            warhead.Detonate(hitCell.Value, this.Map);
        }
        else
        {
            // Default explosion if no warhead
            GenExplosion.DoExplosion(Position, Map, 10, DamageDefOf.Bomb, launcher: null);
        }
        
        Destroy();
    }
    
    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Deep.Look(ref warhead, "warhead");
    }
}
```

## 3. Apply Guidance to Flying Missiles

### C# Code Example

```csharp
public class GuidedMissile : Projectile
{
    public MissileGuidanceState guidance;
    private Thing targetThing;
    
    public override void Tick()
    {
        base.Tick();
        
        if (guidance != null && guidance.isActive)
        {
            // Update guidance
            guidance.UpdateGuidance(this.DrawPos, targetThing);
            
            // Move missile
            Vector3 nextPos = guidance.GetNextPosition(this.DrawPos);
            SetExactPosition(nextPos.ToIntVec3());
            
            // Check if reached target
            if (guidance.IsTargetReached(this.DrawPos))
            {
                // Detonate or execute impact logic
            }
        }
    }
    
    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Deep.Look(ref guidance, "guidance");
        Scribe_References.Look(ref targetThing, "targetThing");
    }
}
```

## 4. Load Missiles Through Launcher Component

### C# Code Example

```csharp
public class MissileLauncherBuilding : Building
{
    public void LaunchMissileAtTarget(IntVec3 target)
    {
        var loader = this.GetComp<CompMissileLoader>();
        if (loader == null) return;
        
        // Find first ready missile
        var readyMissile = loader.GetAllMissiles().FirstOrDefault(m => m.isReadyToLaunch);
        if (readyMissile == null) return;
        
        // Create missile projectile
        var missile = (GuidedMissile)GenSpawn.Spawn(
            ThingDefOf.Projectile_Missile, 
            this.Position, 
            this.Map);
        
        // Apply guidance and warhead
        missile.guidance = readyMissile.guidanceState;
        missile.warhead = readyMissile.warheadData;
        missile.targetThing = target.GetFirstThing(this.Map, ThingCategory.Pawn);
        
        // Launch
        missile.Launch(this, new LocalTargetInfo(target), 
            new LocalTargetInfo(target), ProjectileHitFlags.All);
        
        // Remove from loader
        loader.LaunchMissile(readyMissile.missileIndex, target);
    }
}
```

## 5. Start Loading Missiles

### UI Button Handler

```csharp
private void StartLoadingMissile_Click()
{
    var loader = building.GetComp<CompMissileLoader>();
    if (loader == null) return;
    
    // Find first empty slot
    var emptySlot = loader.GetAllMissiles()
        .FirstOrDefault(m => !m.isLoaded);
    
    if (emptySlot != null)
    {
        // Start loading with selected guidance and warhead
        loader.StartLoadingMissile(
            emptySlot.missileIndex,
            selectedGuidance,
            selectedWarhead);
    }
}
```

## 6. Monitor Loading Progress

### UI Display Code

```csharp
public void DrawLoaderStatus()
{
    var loader = building.GetComp<CompMissileLoader>();
    if (loader == null) return;
    
    // Show ready missile count
    var readyCount = loader.GetReadyMissileCount();
    GUI.Label(rect, $"Ready Missiles: {readyCount}/{loader.Props.maxMissiles}");
    
    // Show loading progress
    var progress = loader.GetLoadingProgress();
    if (progress > 0 && progress < 1)
    {
        var phase = loader.GetCurrentLoadingPhaseLabel();
        GUI.Label(rect, $"Loading: {phase} ({progress * 100:F0}%)");
    }
}
```

## 7. Warhead Selection Dialog

### Simple Implementation

```csharp
public class Dialog_SelectWarhead : Window
{
    private CompMissileLoader loader;
    private List<MissileWarheadDef> availableWarheads;
    
    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(inRect, "Select Warhead Type");
        
        inRect.yMin += 40;
        Text.Font = GameFont.Small;
        
        float y = inRect.y;
        foreach (var warhead in availableWarheads)
        {
            var rect = new Rect(inRect.x, y, inRect.width, 30);
            if (Widgets.ButtonText(rect, warhead.label))
            {
                this.selectedWarhead = warhead;
                this.Close();
            }
            
            // Show warhead info
            var infoRect = new Rect(inRect.x + 200, y, inRect.width - 200, 30);
            Widgets.Label(infoRect, $"Blast: {warhead.blastRadius}");
            
            y += 35;
        }
    }
}
```

## 8. DefOf Access

```csharp
// Use DefOf references
var ballistic = DMSE_DefOf.MissileGuidance_Ballistic;
var highExplosive = DMSE_DefOf.MissileWarhead_HighExplosive;
var radarGuidance = DMSE_DefOf.MissileGuidance_Radar;
```

## 9. Tips for Integration

### Performance

- Only update guidance for active missiles
- Reuse GuidanceState instances when possible
- Cache DefOf references
- Batch warhead detonation calculations

### Gameplay Balance

- Adjust `ticksToLoadMissile` for difficulty
- Balance warhead damage with player weapons
- Set appropriate guidance accuracy for weapon tier
- Consider missile cost vs effectiveness

### Localization

Add these keys to your language files:

```xml
<DMSE.Missile.LoadingMissile>Loading Missile</DMSE.Missile.LoadingMissile>
<DMSE.Missile.LoadingWarhead>Installing Warhead</DMSE.Missile.LoadingWarhead>
<DMSE.Missile.LoadingFuel>Fueling Missile</DMSE.Missile.LoadingFuel>
<DMSE.Missile.OpenLoaderUI>Missile Loader</DMSE.Missile.OpenLoaderUI>
```

## 10. Common Patterns

### Creating a Custom Guidance

```csharp
public class MissileGuidanceDef : Def
{
    public GuidanceMethod guidanceMethod = GuidanceMethod.Radar;
    // ... configure properties
}
```

### Creating a Custom Warhead

```csharp
public class MissileWarheadDef : Def
{
    public WarheadType warheadType = WarheadType.Custom;
    // ... configure properties
}
```

### Extending Loader Component

```csharp
public class CompMissileLoader_Advanced : CompMissileLoader
{
    // Override methods for custom behavior
    public override void CompTick()
    {
        // Custom tick logic
        base.CompTick();
    }
}
```

---

That's it! Your missile system is now integrated. Test thoroughly and adjust parameters for desired gameplay balance.
