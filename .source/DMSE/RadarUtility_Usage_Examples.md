# RadarUtility 使用示例 (Usage Examples)

## 示例1: 在雷??件中?算目?覆? (Calculate Target Coverage in Radar)

```csharp
// 在 CompRadar.cs 中的??
private float CalculateTargetCoverage(WorldObject_Transfer target)
{
    if (target == null || parent.Map == null)
        return 0f;

    int distanceTiles = Find.WorldGrid.TraversalDistanceBetween(parent.Map.Tile, target.Tile);
    
    // 有效面?系? = 反?形等? + 照射?度
    float h = Props.antiStealthLevel + Props.irradiationPower;
    
    // 覆??度 = 有效面? - 距离（距离越?，覆?越弱）
    float w = Mathf.Max(0, h - distanceTiles);
    
    // ?算雷?交叉面???
    return RadarUtility.CalculateRadarCrossSection(h, w);
}

// 使用方式
float targetCoverage = radar.GetTargetCoverage(enemyTarget);
if (targetCoverage > minCoverageThreshold)
{
    radar.LockTarget(enemyTarget);
}
```

## 示例2: 在?截器中?算???截?? (Calculate Critical Interception Time)

```csharp
// 在 MapComponent_InterceptSkyfaller.cs 中的??
private float CalculateCriticalInterceptionTime(DroppodData pod)
{
    // 收集所有炮塔的雷?覆?信息
    List<float> radarCoverage = new List<float>();
    List<float> distances = new List<float>();

    foreach (var turret in turrets)
    {
        if (turret?.parent == null) continue;

        // 每?炮塔的覆??度 = 生命值 / 最大生命值
        radarCoverage.Add(turret.parent.HitPoints / (float)turret.parent.MaxHitPoints);
        
        // ?算?炮塔到目?的距离
        if (pod.pods.Count > 0)
        {
            distances.Add(turret.parent.Position.DistanceTo(pod.pods[0].position));
        }
    }

    // ??定?
    float h = 10f;        // 有效面?系?
    float velocity = 20f; // ??速度
    float vht = 0.5f;     // ?截?值

    // ?算????
    return RadarUtility.CalculateCriticalInterceptionTime(
        radarCoverage.ToArray(),
        distances.ToArray(),
        velocity,
        h,
        vht
    );
}

// 使用方式
int timeToSpawn = pod.tickToSpawn - Find.TickManager.TicksGame;
float criticalTime = CalculateCriticalInterceptionTime(pod);

// 如果剩余??小于????，???最后?刺?截
if (timeToSpawn <= criticalTime)
{
    FireInterceptorMissiles(pod);
}
```

## 示例3: 在炮塔中?算?截窗口 (Calculate Interception Window in Turret)

```csharp
// 在 SkyfallerTurretComp.cs 中的??
public float CalculateFireWindow(float remainingTime)
{
    // ????射到到?目?的??
    float totalDistance = Props.effectiveCrossSection * Props.guidanceVelocity;
    
    // ??到?目?的??
    float arrivalTime = RadarUtility.CalculateArrivalTime(
        totalDistance,
        0,
        Props.guidanceVelocity
    );

    // ?算?截??窗口 (??在到目?到?的?? - ????)
    return RadarUtility.CalculateInterceptionWindow(
        totalDistance,
        Props.guidanceVelocity,
        remainingTime
    );
}

// 使用方式
float timeWindow = CalculateFireWindow(timeToTargetArrival);
if (timeWindow > minimumInterceptWindow && cooldownReady)
{
    FireMissile(target);
}
```

## 示例4: ?算多???覆? (Calculate Total Coverage from Multiple Missiles)

```csharp
// 收集所有已?射??的到???和覆?值
float[] arrivalTimes = new float[activeMissiles.Count];
float[] coverageValues = new float[activeMissiles.Count];

for (int i = 0; i < activeMissiles.Count; i++)
{
    arrivalTimes[i] = activeMissiles[i].TimeToArrival;
    coverageValues[i] = activeMissiles[i].CoverageStrength;
}

// ??
float h = 10f; // 有效面?系?
float currentTime = Find.TickManager.TicksGame * 0.016f; // ???秒

// ?算?覆?
float totalCoverage = RadarUtility.CalculateTotalProjectileCoverage(
    currentTime,
    arrivalTimes,
    coverageValues,
    h
);

// 根据?覆??估?截成功概率
float interceptProbability = totalCoverage / requiredCoverageForSuccess;
```

## 示例5: 在自定???中使用 (Custom Logic Usage)

```csharp
// ?算????的覆???
float projectileCoverage = RadarUtility.CalculateProjectileCoverage(
    currentTime: 10f,           // ?前??（秒）
    arrivalTime: 5f,            // ??到???（秒）
    radarCoverage: 8f,          // 雷?覆??度
    h: 10f                      // 有效面?系?
);

// ?算炮塔的覆???
float turretCoverage = CalculateCoverageContribution(distance: 50f);

// ?合所有覆?源?行?策
if (projectileCoverage + turretCoverage > targetThreshold)
{
    AttemptInterception();
}
```

## 性能考? (Performance Considerations)

### ?存策略
```csharp
// 在 CompRadar 中已???存以避免重复?算
private Dictionary<WorldObject_Transfer, float> targetCoverageCache 
    = new Dictionary<WorldObject_Transfer, float>();

// ?存命中?直接返回
if (targetCoverageCache.TryGetValue(target, out float coverage))
    return coverage;

// ?存未命中??算并存?
coverage = CalculateTargetCoverage(target);
targetCoverageCache[target] = coverage;
```

### ?算?率优化
```csharp
// 不需要每tick都?算，可以用?隔?算
if (Find.TickManager.TicksGame % 30 == 0)
{
    RecalculateRadarCoverage();
}
```

## ??建? (Debugging Tips)

```csharp
// 在 DevMode 下打印??信息
if (Prefs.DevMode)
{
    Log.Message($"Target Coverage: {coverage:F2}");
    Log.Message($"Critical Time: {criticalTime:F2} seconds");
    Log.Message($"Intercept Window: {window:F2} seconds");
}
```

---
**提示**: 所有??可以通? XML Defs ?行配置，?需修改代?即可?整游?平衡
