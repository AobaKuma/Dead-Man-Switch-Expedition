using RimWorld;
using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// ??制?方法 (Missile guidance method)
    /// </summary>
    public enum GuidanceMethod
    {
        Ballistic,      // ?道 (Ballistic trajectory)
        Proportional,   // 比例?航 (Proportional navigation)
        GravityGradient,// 重力梯度 (Gravity gradient)
        Inertial,       // ?性 (Inertial guidance)
        Radar,          // 雷?制? (Radar guided)
        Manual          // 手?制? (Manual guidance)
    }

    /// <summary>
    /// ??制??定? (Missile guidance head definition)
    /// </summary>
    public class MissileGuidanceDef : Def
    {
        // 制?方法
        public GuidanceMethod guidanceMethod = GuidanceMethod.Ballistic;
        
        // 制?性能
        public float maxTurnRate = 10f;         // 最大?向率 (degrees/tick) (Max turn rate)
        public float accuracy = 0.95f;          // 精度 (0-1) (Accuracy)
        public float radarRange = 100f;         // 雷?有效范? (Radar effective range)
        
        // ??性能??
        public float maxSpeed = 50f;            // 最大速度 (Max speed in tiles/second)
        public float minSpeed = 5f;             // 最小速度 (Min speed)
        public float accelerationRate = 2f;     // 加速度 (Acceleration)
        
        // ?道??
        public float gravityFactor = 1f;        // 重力影?系? (Gravity influence factor)
        public float airResistance = 0.1f;      // 空气阻力系? (Air resistance)
        
        // 系?成本
        public float weight = 30f;              // 制??重量 (Guidance head weight)
        public float cost = 150f;               // 制造成本 (Manufacturing cost)
        public float powerConsumption = 5f;     // 能耗 (Power consumption)
    }

    /// <summary>
    /// ??制??? (Missile guidance state)
    /// </summary>
    public class MissileGuidanceState : IExposable
    {
        public MissileGuidanceDef guidanceDef;
        public Vector3 currentVelocity = Vector3.zero;
        public Vector3 targetPosition = Vector3.zero;
        public IntVec3 targetCell = IntVec3.Invalid;
        public float currentSpeed = 0f;
        public float currentAccuracy = 1f;
        public int guidanceTicks = 0;
        public bool isActive = true;
        
        // 制???
        public float navigationConstant = 3f;   // 比例?航常? (Proportional navigation constant)
        public Vector3 targetVelocity = Vector3.zero;

        public MissileGuidanceState() { }

        public MissileGuidanceState(MissileGuidanceDef def)
        {
            guidanceDef = def;
            currentSpeed = def.minSpeed;
            currentAccuracy = def.accuracy;
        }

        public void ExposeData()
        {
            Scribe_Defs.Look(ref guidanceDef, "guidanceDef");
            Scribe_Values.Look(ref currentVelocity, "currentVelocity", Vector3.zero);
            Scribe_Values.Look(ref targetPosition, "targetPosition", Vector3.zero);
            Scribe_Values.Look(ref targetCell, "targetCell", IntVec3.Invalid);
            Scribe_Values.Look(ref currentSpeed, "currentSpeed", 0f);
            Scribe_Values.Look(ref currentAccuracy, "currentAccuracy", 1f);
            Scribe_Values.Look(ref guidanceTicks, "guidanceTicks", 0);
            Scribe_Values.Look(ref isActive, "isActive", true);
            Scribe_Values.Look(ref navigationConstant, "navigationConstant", 3f);
            Scribe_Values.Look(ref targetVelocity, "targetVelocity", Vector3.zero);
        }

        /// <summary>
        /// 更新制??? (Update guidance state)
        /// </summary>
        public void UpdateGuidance(Vector3 currentPos, Thing target = null)
        {
            if (!isActive || guidanceDef == null)
                return;

            guidanceTicks++;

            // 更新目?位置
            if (target != null && target.Spawned)
            {
                targetVelocity = (target.DrawPos - targetPosition) / Time.deltaTime;
                targetPosition = target.DrawPos;
            }

            // 根据制?方法?算速度和方向
            switch (guidanceDef.guidanceMethod)
            {
                case GuidanceMethod.Ballistic:
                    UpdateBallisticGuidance(currentPos);
                    break;
                case GuidanceMethod.Proportional:
                    UpdateProportionalGuidance(currentPos);
                    break;
                case GuidanceMethod.GravityGradient:
                    UpdateGravityGradientGuidance(currentPos);
                    break;
                case GuidanceMethod.Inertial:
                    UpdateInertialGuidance(currentPos);
                    break;
                case GuidanceMethod.Radar:
                    UpdateRadarGuidance(currentPos);
                    break;
            }

            // ?用空气阻力
            currentSpeed *= (1f - guidanceDef.airResistance * 0.01f);
            currentSpeed = Mathf.Clamp(currentSpeed, guidanceDef.minSpeed, guidanceDef.maxSpeed);

            // 精度衰?（?距离?降低精度）
            float distance = Vector3.Distance(currentPos, targetPosition);
            currentAccuracy = Mathf.Clamp01(guidanceDef.accuracy * (1f - distance / 1000f));
        }

        private void UpdateBallisticGuidance(Vector3 currentPos)
        {
            Vector3 direction = (targetPosition - currentPos).normalized;
            
            // ?用重力
            Vector3 gravity = new Vector3(0, -guidanceDef.gravityFactor * 9.81f * Time.deltaTime, 0);
            
            currentVelocity = direction * currentSpeed + gravity;
        }

        private void UpdateProportionalGuidance(Vector3 currentPos)
        {
            Vector3 toTarget = targetPosition - currentPos;
            float distance = toTarget.magnitude;

            if (distance < 0.1f)
            {
                isActive = false;
                return;
            }

            Vector3 dirNormalized = currentVelocity.normalized;
            if (dirNormalized.magnitude < 0.001f)
            {
                dirNormalized = toTarget.normalized;
            }
            Vector3 lineOfSightRate = (toTarget.normalized - dirNormalized) / Time.deltaTime;
            Vector3 guidance = navigationConstant * currentSpeed * lineOfSightRate;

            currentVelocity = (toTarget.normalized * currentSpeed) + guidance;
        }

        private void UpdateGravityGradientGuidance(Vector3 currentPos)
        {
            // 重力梯度制? - 使用位置的梯度
            float dx = 0.1f;
            float gravityAtPos = 9.81f * (1f - (currentPos.y / 100f));
            float gravityAtPos2 = 9.81f * (1f - ((currentPos.y + dx) / 100f));
            
            Vector3 gravityGradient = new Vector3(0, (gravityAtPos2 - gravityAtPos) / dx, 0);
            
            Vector3 direction = (targetPosition - currentPos).normalized;
            currentVelocity = direction * currentSpeed + gravityGradient * guidanceDef.gravityFactor;
        }

        private void UpdateInertialGuidance(Vector3 currentPos)
        {
            // ?性制? - 保持恒定方向和速度
            if (currentVelocity.magnitude < 0.1f)
            {
                currentVelocity = (targetPosition - currentPos).normalized * currentSpeed;
            }
            // 速度??保持
            currentSpeed = Mathf.Clamp(currentSpeed + guidanceDef.accelerationRate * Time.deltaTime, 
                guidanceDef.minSpeed, guidanceDef.maxSpeed);
        }

        private void UpdateRadarGuidance(Vector3 currentPos)
        {
            // 雷?制? - 主?跟?
            Vector3 toTarget = targetPosition - currentPos;
            float distance = toTarget.magnitude;

            if (distance < guidanceDef.radarRange)
            {
                Vector3 direction = toTarget.normalized;
                float turnAmount = guidanceDef.maxTurnRate * Time.deltaTime;
                
                Vector3 newDirection = Vector3.RotateTowards(currentVelocity.normalized, 
                    direction, turnAmount * Mathf.Deg2Rad, 0f);
                
                currentVelocity = newDirection * currentSpeed;
            }
            else
            {
                // 超出雷?范?，???道?航
                UpdateBallisticGuidance(currentPos);
            }
        }

        /// <summary>
        /// ?取下一?位置 (Get next position)
        /// </summary>
        public Vector3 GetNextPosition(Vector3 currentPos)
        {
            return currentPos + currentVelocity * Time.deltaTime;
        }

        /// <summary>
        /// ?查是否到?目? (Check if target is reached)
        /// </summary>
        public bool IsTargetReached(Vector3 currentPos, float targetRadius = 1f)
        {
            return Vector3.Distance(currentPos, targetPosition) <= targetRadius;
        }

        /// <summary>
        /// ?取制?信息 (Get guidance information)
        /// </summary>
        public string GetGuidanceInfo()
        {
            return $"Method: {guidanceDef.guidanceMethod}\n" +
                   $"Speed: {currentSpeed:F2}\n" +
                   $"Accuracy: {(currentAccuracy * 100):F1}%\n" +
                   $"Distance to Target: {Vector3.Distance(Vector3.zero, targetPosition):F1}";
        }
    }

    /// <summary>
    /// 制??算工具? (Guidance calculation utility)
    /// </summary>
    public static class MissileGuidanceUtility
    {
        /// <summary>
        /// ?算?截? (Calculate intercept point)
        /// </summary>
        public static Vector3 CalculateInterceptPoint(Vector3 launchPos, Vector3 targetPos, 
            Vector3 targetVelocity, float missileSpeed)
        {
            // 使用二次方程求解?截?
            Vector3 relativePos = targetPos - launchPos;
            float a = Vector3.Dot(targetVelocity, targetVelocity) - missileSpeed * missileSpeed;
            float b = 2 * Vector3.Dot(relativePos, targetVelocity);
            float c = Vector3.Dot(relativePos, relativePos);

            float discriminant = b * b - 4 * a * c;
            
            if (discriminant < 0)
            {
                // ?法?截，返回目?位置
                return targetPos;
            }

            float t = (-b + Mathf.Sqrt(discriminant)) / (2 * a);
            t = Mathf.Max(0, t);

            return targetPos + targetVelocity * t;
        }

        /// <summary>
        /// ?算制?偏差 (Calculate guidance deviation)
        /// </summary>
        public static float CalculateDeviation(Vector3 currentPos, Vector3 targetPos, 
            float accuracy, float distance)
        {
            // 精度越低或距离越?，偏差越大
            float maxDeviation = (1f - accuracy) * distance * 0.5f;
            return Rand.Range(-maxDeviation, maxDeviation);
        }

        /// <summary>
        /// ?算??耗? (Calculate missile time to target)
        /// </summary>
        public static float CalculateTimeToTarget(Vector3 from, Vector3 to, float speed)
        {
            float distance = Vector3.Distance(from, to);
            if (speed <= 0)
                return float.MaxValue;
            return distance / speed;
        }
    }
}
