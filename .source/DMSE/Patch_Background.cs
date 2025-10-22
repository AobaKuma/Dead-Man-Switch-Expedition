using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Diagnostics;
using UnityEngine.Tilemaps;
using Verse;
using Verse.Sound;
using static UnityEngine.Networking.UnityWebRequest;

namespace DMSE
{
    [HarmonyPatch(typeof(WorldCameraDriver), "ApplyMapPositionToGameObject")]
    public class Patch_Background
    {
        [HarmonyPostfix]
        public static void postfix(WorldCameraDriver __instance)
        {
            if (Find.CurrentMap != null && Patch_Visible.WO.Any() 
                && Patch_Visible.WO.Find(w => w.worldObjec == Find.CurrentMap.Parent)
                is WorldObject_Transfer wo) 
            {
                Camera c = (Camera)AccessTools.Property(typeof(WorldCameraDriver), "MyCamera")
                    .GetValue(__instance);
                Vector3 vector = wo.DrawPos;
                Vector3 vector2 = -vector.normalized;
                vector += -vector2 * wo.Tile.Layer.BackgroundWorldCameraOffset;
                Transform transform = c.transform;
                Quaternion rotation = Quaternion.LookRotation(vector2, Vector3.up);
                transform.rotation = rotation;
                float num = wo.Tile.Layer.BackgroundWorldCameraParallaxDistancePer100Cells;
                if (num == 0f)
                {
                    transform.position = vector;
                    return;
                }
                Vector2 viewSpacePosition = Find.CameraDriver.ViewSpacePosition;
                IntVec3 size = Find.CurrentMap.Size;
                float num2 = 1f;
                float num3 = 1f;
                if (size.x > size.z)
                {
                    num3 = (float)size.z / (float)size.x;
                    num = num * (float)size.x / 100f;
                }
                else if (size.z > size.x)
                {
                    num2 = (float)size.x / (float)size.z;
                    num = num * (float)size.z / 100f;
                }
                Vector3 up = transform.up;
                Vector3 right = transform.right;
                Vector3 b = up * (viewSpacePosition.y * num * num3) - up * num / 2f * num3;
                Vector3 b2 = right * (viewSpacePosition.x * num * num2) - right * num / 2f * num2;
                transform.position = vector + b + b2 + wo.Tile.Layer.Origin;
            }
        }
    }
}