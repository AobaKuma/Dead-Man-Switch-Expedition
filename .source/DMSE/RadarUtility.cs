using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace DMSE
{
    public static class RadarUtility
    {
        /// <summary>
        /// 单个雷达对导弹有效面积贡献 (Single hit effective radar cross section contribution)
        /// G(h, w_i) = h * ((1 + (h - w_i) / h) / 2 + w_i * ((1 - (h - w_i) / h) / 2))
        /// </summary>
        /// <param name="h">导弹红面积系数 (Effective cross section coefficient)</param>
        /// <param name="w">第i个雷达的蓝面积强度 (Radar coverage strength)</param>
        /// <returns>有效面积贡献 (Effective contribution)</returns>
        public static float CalculateRadarCrossSection(float h, float w)
        {
            if (h <= 0)
                return 0f;

            float ratio = (h - w) / h;
            float term1 = (1f + ratio) / 2f;
            float term2 = w * (1f - ratio) / 2f;
            return h * (term1 + term2);
        }

        /// <summary>
        /// 导弹到达目标的时间 (Time for projectile to reach target)
        /// t_i = (L - d_i) / v
        /// </summary>
        /// <param name="totalDistance">导弹与目标的总距离 (Total distance between projectile and target)</param>
        /// <param name="currentDistance">第i个雷达到目标的距离 (Current distance to target)</param>
        /// <param name="velocity">导弹速度 (Projectile velocity)</param>
        /// <returns>到达时间 (Time to arrival)</returns>
        public static float CalculateArrivalTime(float totalDistance, float currentDistance, float velocity)
        {
            if (velocity <= 0)
                return float.MaxValue;

            return (totalDistance - currentDistance) / velocity;
        }

        /// <summary>
        /// 总蓝面积 (Total radar coverage area)
        /// A = t * Σ G(h, w_i) - Σ ((L - d_i) / v) * G(h, w_i)
        /// </summary>
        /// <param name="time">当前时间 (Current time)</param>
        /// <param name="radarCoverage">各雷达的覆盖值数组 (Array of radar coverage values)</param>
        /// <param name="distances">各雷达到目标的距离数组 (Array of distances to target)</param>
        /// <param name="velocity">导弹速度 (Projectile velocity)</param>
        /// <param name="h">有效面积系数 (Effective cross section coefficient)</param>
        /// <returns>总蓝面积 (Total coverage area)</returns>
        public static float CalculateTotalCoverage(float time, float[] radarCoverage, float[] distances, float velocity, float h)
        {
            if (radarCoverage == null || distances == null || radarCoverage.Length != distances.Length)
                return 0f;

            float sumG = 0f;
            float sumWeightedG = 0f;

            for (int i = 0; i < radarCoverage.Length; i++)
            {
                float g = CalculateRadarCrossSection(h, radarCoverage[i]);
                sumG += g;

                if (velocity > 0)
                {
                    float weight = (h - distances[i]) / velocity;
                    sumWeightedG += weight * g;
                }
            }

            return time * sumG - sumWeightedG;
        }

        /// <summary>
        /// 拦截成功所需的关键时间 (Critical time for successful interception)
        /// t_g = Σ ((L - d_i) / v) * G(h, w_i) / (Σ G(h, w_i) - vht)
        /// </summary>
        /// <param name="radarCoverage">各雷达的覆盖值数组 (Array of radar coverage values)</param>
        /// <param name="distances">各雷达到目标的距离数组 (Array of distances to target)</param>
        /// <param name="velocity">导弹速度 (Projectile velocity)</param>
        /// <param name="h">有效面积系数 (Effective cross section coefficient)</param>
        /// <param name="vht">拦截阈值 (Interception threshold)</param>
        /// <returns>关键时间，小于此值拦截失败 (Critical time for interception)</returns>
        public static float CalculateCriticalInterceptionTime(float[] radarCoverage, float[] distances, float velocity, float h, float vht)
        {
            if (radarCoverage == null || distances == null || radarCoverage.Length != distances.Length)
                return float.MaxValue;

            float numerator = 0f;
            float denominator = 0f;

            for (int i = 0; i < radarCoverage.Length; i++)
            {
                float g = CalculateRadarCrossSection(h, radarCoverage[i]);
                
                if (velocity > 0)
                {
                    float weight = (h - distances[i]) / velocity;
                    numerator += weight * g;
                }

                denominator += g;
            }

            denominator -= velocity * h * vht;

            if (denominator <= 0)
                return float.MaxValue;

            return numerator / denominator;
        }

        /// <summary>
        /// 拦截窗口时间 (Interception window time)
        /// t_w = t_max - t_g = L/v - t_g
        /// </summary>
        /// <param name="totalDistance">导弹与目标的总距离 (Total distance)</param>
        /// <param name="velocity">导弹速度 (Projectile velocity)</param>
        /// <param name="criticalTime">关键时间 (Critical time)</param>
        /// <returns>拦截窗口 (Interception window)</returns>
        public static float CalculateInterceptionWindow(float totalDistance, float velocity, float criticalTime)
        {
            if (velocity <= 0)
                return 0f;

            float maxTime = totalDistance / velocity;
            return maxTime - criticalTime;
        }

        /// <summary>
        /// 单个导弹对目标的蓝面积贡献 (Single projectile coverage contribution)
        /// A_i = (t - t_i) * G(h, w_i)
        /// </summary>
        /// <param name="time">当前时间 (Current time)</param>
        /// <param name="arrivalTime">到达时间 (Arrival time)</param>
        /// <param name="radarCoverage">第i个雷达的蓝面积强度 (Radar coverage strength)</param>
        /// <param name="h">有效面积系数 (Effective cross section coefficient)</param>
        /// <returns>蓝面积贡献 (Coverage contribution)</returns>
        public static float CalculateProjectileCoverage(float time, float arrivalTime, float radarCoverage, float h)
        {
            if (time < arrivalTime)
                return 0f;

            float g = CalculateRadarCrossSection(h, radarCoverage);
            return (time - arrivalTime) * g;
        }

        /// <summary>
        /// n个导弹的总蓝面积 (Total coverage from n projectiles)
        /// A = Σ (t - t_i) * G(h, w_i) for i=1 to n
        /// </summary>
        /// <param name="time">当前时间 (Current time)</param>
        /// <param name="arrivalTimes">各导弹的到达时间数组 (Array of arrival times)</param>
        /// <param name="radarCoverage">各导弹的雷达覆盖值数组 (Array of radar coverage values)</param>
        /// <param name="h">有效面积系数 (Effective cross section coefficient)</param>
        /// <returns>总蓝面积 (Total coverage area)</returns>
        public static float CalculateTotalProjectileCoverage(float time, float[] arrivalTimes, float[] radarCoverage, float h)
        {
            if (arrivalTimes == null || radarCoverage == null || arrivalTimes.Length != radarCoverage.Length)
                return 0f;

            float totalCoverage = 0f;

            for (int i = 0; i < arrivalTimes.Length; i++)
            {
                totalCoverage += CalculateProjectileCoverage(time, arrivalTimes[i], radarCoverage[i], h);
            }

            return totalCoverage;
        }
    }
}