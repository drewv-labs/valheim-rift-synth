using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimRiftEngine.Models;

namespace ValheimRiftEngine.Generation;

public sealed class BuiltRoom
{
    public int RoomIndex { get; init; }
    public string Template { get; init; } = string.Empty;
    public string PillarLayout { get; init; } = "none";
    public Vector3 Center { get; init; }
    public float WidthX { get; init; }
    public float LengthZ { get; init; }
    public Dictionary<string, Vector3> Anchors { get; } = new(StringComparer.Ordinal);
    public List<ZDOID> ExitGateZdos { get; } = new();
    public Vector3 ExitGateCenter { get; set; }
}

public sealed class BuiltRiftInstance
{
    public Vector3 Origin { get; init; }
    public Vector2s Zone { get; init; }
    public Vector3 PlayerStagingPoint { get; init; }
    public Vector3 RewardChestPoint { get; init; }
    public Vector3 ReturnPortalPoint { get; init; }
    public List<BuiltRoom> Rooms { get; } = new();
}

/// <summary>
/// Procedurally constructs a watertight, indestructible single-zone Skybox Rift at Y = 4500f
/// strictly out of verified vanilla Blackmarble, Dvergr lantern, and Iron prefabs.
/// </summary>
public static class SkyboxArenaBuilder
{
    private const string PrefabFloor = "blackmarble_floor_large";
    private const string PrefabWall = "blackmarble_2x2x1";
    private const string PrefabColumn = "blackmarble_column_2";
    private const string PrefabLantern = "dvergrprops_lantern_standing";
    private const string PrefabBrazier = "piece_brazierfloor01";
    private const string PrefabGate = "iron_grate";

    private const float FloorTileSize = 4f;
    private const float WallWidth = 2f;
    private const float WallHeight = 2f;
    private const float SpawnFloorClearanceY = 1.25f;

    public static BuiltRiftInstance Build(
        RiftManifest manifest,
        Vector3 callerWorldPos,
        InstanceCleaner cleaner)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer())
        {
            throw new InvalidOperationException("SkyboxArenaBuilder.Build must only execute on the authoritative server.");
        }
        if (ZNetScene.instance == null)
        {
            throw new InvalidOperationException("ZNetScene.instance is null.");
        }

        Vector2s zone = ZoneSystem.GetZone(callerWorldPos);
        Vector3 origin = new(zone.x * 64f, RiftConstants.SkyboxAltitudeY, zone.y * 64f);

        float totalZ = manifest.ComputeTotalZSpanMeters();
        float cursorZ = origin.z - (totalZ * 0.5f);

        var built = new BuiltRiftInstance
        {
            Origin = origin,
            Zone = zone,
            PlayerStagingPoint = new Vector3(origin.x, origin.y + SpawnFloorClearanceY, cursorZ + 2.5f),
            RewardChestPoint = new Vector3(origin.x - 2.0f, origin.y + SpawnFloorClearanceY * 0.6f, origin.z + (totalZ * 0.5f) - 4.0f),
            ReturnPortalPoint = new Vector3(origin.x + 2.5f, origin.y + SpawnFloorClearanceY * 0.6f, origin.z + (totalZ * 0.5f) - 4.0f),
        };

        for (int i = 0; i < manifest.Rooms.Count; i++)
        {
            var spec = manifest.Rooms[i];
            var (widthX, lengthZ) = RiftConstants.GetTemplateDimensions(spec.Template);
            float roomMinZ = cursorZ;
            float roomMaxZ = cursorZ + lengthZ;
            float roomCenterZ = (roomMinZ + roomMaxZ) * 0.5f;
            Vector3 roomCenter = new(origin.x, origin.y, roomCenterZ);

            bool hasSouthDoor = i > 0;
            bool hasNorthDoor = i < manifest.Rooms.Count - 1;
            float ceilingHeight = spec.Template == "sanctum_oct" ? 10f : 8f;

            var builtRoom = new BuiltRoom
            {
                RoomIndex = i,
                Template = spec.Template,
                PillarLayout = spec.PillarLayout,
                Center = roomCenter,
                WidthX = widthX,
                LengthZ = lengthZ,
            };

            BuildRoomShell(origin, roomMinZ, roomMaxZ, widthX, ceilingHeight, hasSouthDoor, hasNorthDoor, spec.Template, cleaner);
            BuildTemplateFeatures(spec, builtRoom, origin.y, cleaner);
            PopulateAnchors(spec, builtRoom, origin.y);

            if (hasNorthDoor)
            {
                float corridorMinZ = roomMaxZ;
                float corridorMaxZ = roomMaxZ + RiftConstants.CorridorLengthMeters;
                BuildCorridorAndGate(origin, corridorMinZ, corridorMaxZ, builtRoom, cleaner);
                cursorZ = corridorMaxZ;
            }
            else
            {
                cursorZ = roomMaxZ;
            }

            built.Rooms.Add(builtRoom);
        }

        Plugin.Log.LogInfo(
            $"[SkyboxArenaBuilder] Built '{manifest.RiftId}' ({manifest.Rooms.Count} rooms, {totalZ:F0}m Z-span) " +
            $"at zone ({zone.x},{zone.y}) origin {origin} ({cleaner.TrackedCount} indestructible ZDOs).");

        return built;
    }

    private static void BuildRoomShell(
        Vector3 origin,
        float minZ,
        float maxZ,
        float widthX,
        float ceilingHeight,
        bool hasSouthDoor,
        bool hasNorthDoor,
        string template,
        InstanceCleaner cleaner)
    {
        float baseY = origin.y;
        float shellWidthX = template == "gauntlet_bridge" ? 12f : widthX;
        float minX = origin.x - (shellWidthX * 0.5f);
        float maxX = origin.x + (shellWidthX * 0.5f);

        int floorTilesX = Mathf.RoundToInt(shellWidthX / FloorTileSize);
        int floorTilesZ = Mathf.RoundToInt((maxZ - minZ) / FloorTileSize);
        int wallSegmentsZ = Mathf.RoundToInt((maxZ - minZ) / WallWidth);
        int wallCourses = Mathf.RoundToInt(ceilingHeight / WallHeight) + 1;

        if (template == "gauntlet_bridge")
        {
            for (int ix = 0; ix < floorTilesX; ix++)
            {
                float cx = minX + (ix + 0.5f) * FloorTileSize;
                for (int iz = 0; iz < floorTilesZ; iz++)
                {
                    float cz = minZ + (iz + 0.5f) * FloorTileSize;
                    SpawnIndestructible(PrefabFloor, new Vector3(cx, baseY - 2f, cz), Quaternion.identity, cleaner);
                    SpawnIndestructible(PrefabFloor, new Vector3(cx, baseY + ceilingHeight, cz), Quaternion.identity, cleaner);
                }
            }

            for (int iz = 0; iz < floorTilesZ; iz++)
            {
                float cz = minZ + (iz + 0.5f) * FloorTileSize;
                SpawnIndestructible(PrefabFloor, new Vector3(origin.x - 2f, baseY, cz), Quaternion.identity, cleaner);
                SpawnIndestructible(PrefabFloor, new Vector3(origin.x + 2f, baseY, cz), Quaternion.identity, cleaner);

                SpawnIndestructible(PrefabBrazier, new Vector3(origin.x - 5.0f, baseY - 1.5f, cz), Quaternion.identity, cleaner);
                SpawnIndestructible(PrefabBrazier, new Vector3(origin.x + 5.0f, baseY - 1.5f, cz), Quaternion.identity, cleaner);
            }
        }
        else
        {
            for (int ix = 0; ix < floorTilesX; ix++)
            {
                float cx = minX + (ix + 0.5f) * FloorTileSize;
                for (int iz = 0; iz < floorTilesZ; iz++)
                {
                    float cz = minZ + (iz + 0.5f) * FloorTileSize;
                    SpawnIndestructible(PrefabFloor, new Vector3(cx, baseY, cz), Quaternion.identity, cleaner);
                    SpawnIndestructible(PrefabFloor, new Vector3(cx, baseY + ceilingHeight, cz), Quaternion.identity, cleaner);
                }
            }
        }

        float wallBottomY = template == "gauntlet_bridge" ? baseY - 2f : baseY;
        int totalWallCourses = template == "gauntlet_bridge" ? wallCourses + 1 : wallCourses;

        Quaternion rotSideWall = Quaternion.Euler(0f, 90f, 0f);
        for (int iz = 0; iz < wallSegmentsZ; iz++)
        {
            float cz = minZ + (iz + 0.5f) * WallWidth;
            for (int iy = 0; iy < totalWallCourses; iy++)
            {
                float wy = wallBottomY + (iy * WallHeight);
                SpawnIndestructible(PrefabWall, new Vector3(minX, wy, cz), rotSideWall, cleaner);
                SpawnIndestructible(PrefabWall, new Vector3(maxX, wy, cz), rotSideWall, cleaner);
            }
        }

        BuildEndWall(origin.x, minZ, minX, shellWidthX, wallBottomY, baseY, totalWallCourses, hasSouthDoor, cleaner);
        BuildEndWall(origin.x, maxZ, minX, shellWidthX, wallBottomY, baseY, totalWallCourses, hasNorthDoor, cleaner);
    }

    private static void BuildEndWall(
        float originX,
        float wallZ,
        float minX,
        float shellWidthX,
        float wallBottomY,
        float floorY,
        int totalWallCourses,
        bool hasCenteredDoorway,
        InstanceCleaner cleaner)
    {
        int wallSegmentsX = Mathf.RoundToInt(shellWidthX / WallWidth);
        for (int iy = 0; iy < totalWallCourses; iy++)
        {
            float wy = wallBottomY + (iy * WallHeight);
            bool inDoorwayVerticalOpening = hasCenteredDoorway && wy >= floorY - 0.1f && wy < floorY + 3.9f;

            for (int ix = 0; ix < wallSegmentsX; ix++)
            {
                float cx = minX + (ix + 0.5f) * WallWidth;
                if (inDoorwayVerticalOpening && Mathf.Abs(cx - originX) < 1.9f)
                {
                    continue;
                }
                SpawnIndestructible(PrefabWall, new Vector3(cx, wy, wallZ), Quaternion.identity, cleaner);
            }
        }

        if (hasCenteredDoorway)
        {
            SpawnIndestructible(PrefabColumn, new Vector3(originX - 2.1f, floorY, wallZ), Quaternion.identity, cleaner);
            SpawnIndestructible(PrefabColumn, new Vector3(originX - 2.1f, floorY + 2f, wallZ), Quaternion.identity, cleaner);
            SpawnIndestructible(PrefabColumn, new Vector3(originX + 2.1f, floorY, wallZ), Quaternion.identity, cleaner);
            SpawnIndestructible(PrefabColumn, new Vector3(originX + 2.1f, floorY + 2f, wallZ), Quaternion.identity, cleaner);
        }
    }

    private static void BuildTemplateFeatures(
        RoomSpec spec,
        BuiltRoom room,
        float baseY,
        InstanceCleaner cleaner)
    {
        Vector3 c = room.Center;
        float hw = room.WidthX * 0.5f;
        float hl = room.LengthZ * 0.5f;

        // Eternal fuel-free Dvergr standing lanterns illuminate room corners
        float insetX = Mathf.Max(2.5f, hw - 1.5f);
        float insetZ = Mathf.Max(2.5f, hl - 1.5f);
        SpawnIndestructible(PrefabLantern, new Vector3(c.x - insetX, baseY + 0.5f, c.z - insetZ), Quaternion.identity, cleaner);
        SpawnIndestructible(PrefabLantern, new Vector3(c.x + insetX, baseY + 0.5f, c.z - insetZ), Quaternion.identity, cleaner);
        SpawnIndestructible(PrefabLantern, new Vector3(c.x - insetX, baseY + 0.5f, c.z + insetZ), Quaternion.identity, cleaner);
        SpawnIndestructible(PrefabLantern, new Vector3(c.x + insetX, baseY + 0.5f, c.z + insetZ), Quaternion.identity, cleaner);

        if (spec.Template == "entry_vestibule")
        {
            SpawnIndestructible(PrefabBrazier, new Vector3(c.x - 1.8f, baseY + 0.5f, c.z + 1.5f), Quaternion.identity, cleaner);
            SpawnIndestructible(PrefabBrazier, new Vector3(c.x + 1.8f, baseY + 0.5f, c.z + 1.5f), Quaternion.identity, cleaner);
            return;
        }

        float perchX = spec.Template == "gauntlet_bridge" ? 4.0f : room.WidthX * 0.30f;
        float perchZ = c.z + (room.LengthZ * 0.28f);
        BuildSniperPerch(new Vector3(c.x - perchX, baseY, perchZ), cleaner);
        BuildSniperPerch(new Vector3(c.x + perchX, baseY, perchZ), cleaner);

        if (spec.Template == "sanctum_oct")
        {
            float chamferOffset = 7.2f;
            Quaternion rot45 = Quaternion.Euler(0f, 45f, 0f);
            Quaternion rotNeg45 = Quaternion.Euler(0f, -45f, 0f);
            for (int iy = 0; iy < 5; iy++)
            {
                float wy = baseY + (iy * WallHeight);
                SpawnIndestructible(PrefabWall, new Vector3(c.x - chamferOffset, wy, c.z - chamferOffset), rot45, cleaner);
                SpawnIndestructible(PrefabWall, new Vector3(c.x + chamferOffset, wy, c.z + chamferOffset), rot45, cleaner);
                SpawnIndestructible(PrefabWall, new Vector3(c.x + chamferOffset, wy, c.z - chamferOffset), rotNeg45, cleaner);
                SpawnIndestructible(PrefabWall, new Vector3(c.x - chamferOffset, wy, c.z + chamferOffset), rotNeg45, cleaner);
            }

            SpawnIndestructible(PrefabLantern, new Vector3(c.x - 4.5f, baseY + 0.5f, c.z), Quaternion.identity, cleaner);
            SpawnIndestructible(PrefabLantern, new Vector3(c.x + 4.5f, baseY + 0.5f, c.z), Quaternion.identity, cleaner);
        }

        switch (spec.PillarLayout)
        {
            case "four_corners":
            {
                float px = spec.Template == "gauntlet_bridge" ? 2.6f : room.WidthX * 0.24f;
                float pz = room.LengthZ * 0.24f;
                BuildColumnStack(new Vector3(c.x - px, baseY, c.z - pz), 4, cleaner);
                BuildColumnStack(new Vector3(c.x + px, baseY, c.z - pz), 4, cleaner);
                BuildColumnStack(new Vector3(c.x - px, baseY, c.z + pz), 4, cleaner);
                BuildColumnStack(new Vector3(c.x + px, baseY, c.z + pz), 4, cleaner);
                break;
            }
            case "center_los_blocker":
            {
                BuildColumnStack(new Vector3(c.x, baseY, c.z), 4, cleaner);
                SpawnIndestructible(PrefabWall, new Vector3(c.x, baseY, c.z), Quaternion.identity, cleaner);
                SpawnIndestructible(PrefabWall, new Vector3(c.x, baseY + 2f, c.z), Quaternion.identity, cleaner);
                SpawnIndestructible(PrefabWall, new Vector3(c.x, baseY, c.z), Quaternion.Euler(0f, 90f, 0f), cleaner);
                SpawnIndestructible(PrefabWall, new Vector3(c.x, baseY + 2f, c.z), Quaternion.Euler(0f, 90f, 0f), cleaner);
                break;
            }
        }
    }

    private static void BuildSniperPerch(Vector3 basePos, InstanceCleaner cleaner)
    {
        SpawnIndestructible(PrefabColumn, basePos, Quaternion.identity, cleaner);
        SpawnIndestructible(PrefabFloor, new Vector3(basePos.x, basePos.y + 2.0f, basePos.z), Quaternion.identity, cleaner);
    }

    private static void BuildColumnStack(Vector3 basePos, int segments, InstanceCleaner cleaner)
    {
        for (int i = 0; i < segments; i++)
        {
            SpawnIndestructible(PrefabColumn, new Vector3(basePos.x, basePos.y + (i * 2f), basePos.z), Quaternion.identity, cleaner);
        }
    }

    private static void BuildCorridorAndGate(
        Vector3 origin,
        float minZ,
        float maxZ,
        BuiltRoom sourceRoom,
        InstanceCleaner cleaner)
    {
        float baseY = origin.y;
        float centerZ = (minZ + maxZ) * 0.5f;
        Quaternion rotSide = Quaternion.Euler(0f, 90f, 0f);

        SpawnIndestructible(PrefabFloor, new Vector3(origin.x, baseY, centerZ), Quaternion.identity, cleaner);
        SpawnIndestructible(PrefabFloor, new Vector3(origin.x, baseY + 4f, centerZ), Quaternion.identity, cleaner);

        float[] zOffsets = { centerZ - 1.0f, centerZ + 1.0f };
        foreach (float sz in zOffsets)
        {
            for (int iy = 0; iy < 3; iy++)
            {
                float wy = baseY + (iy * WallHeight);
                SpawnIndestructible(PrefabWall, new Vector3(origin.x - 2f, wy, sz), rotSide, cleaner);
                SpawnIndestructible(PrefabWall, new Vector3(origin.x + 2f, wy, sz), rotSide, cleaner);
            }
        }

        SpawnIndestructible(PrefabLantern, new Vector3(origin.x - 1.5f, baseY + 0.5f, centerZ - 1.2f), Quaternion.identity, cleaner);
        SpawnIndestructible(PrefabLantern, new Vector3(origin.x + 1.5f, baseY + 0.5f, centerZ - 1.2f), Quaternion.identity, cleaner);

        sourceRoom.ExitGateCenter = new Vector3(origin.x, baseY + 1.5f, centerZ);
        float[] gateXs = { origin.x - 1.0f, origin.x + 1.0f };
        float[] gateYs = { baseY + 0.5f, baseY + 2.5f };

        foreach (float gx in gateXs)
        {
            foreach (float gy in gateYs)
            {
                ZDOID gateId = SpawnIndestructible(PrefabGate, new Vector3(gx, gy, centerZ), Quaternion.identity, cleaner);
                if (!gateId.IsNone())
                {
                    sourceRoom.ExitGateZdos.Add(gateId);
                }
            }
        }
    }

    private static void PopulateAnchors(RoomSpec spec, BuiltRoom room, float baseY)
    {
        Vector3 c = room.Center;
        float floorSpawnY = baseY + SpawnFloorClearanceY;
        float perchSpawnY = baseY + 2.0f + SpawnFloorClearanceY;

        float lateralX = spec.Template == "gauntlet_bridge" ? 1.8f : room.WidthX * 0.26f;
        float depthZ = room.LengthZ * 0.26f;
        float perchX = spec.Template == "gauntlet_bridge" ? 4.0f : room.WidthX * 0.30f;
        float perchZ = room.LengthZ * 0.28f;

        Vector3 centerPos = spec.PillarLayout == "center_los_blocker"
            ? new Vector3(c.x, floorSpawnY, c.z + 2.8f)
            : new Vector3(c.x, floorSpawnY, c.z);

        room.Anchors["center"] = centerPos;
        room.Anchors["north_left"] = new Vector3(c.x - lateralX, floorSpawnY, c.z + depthZ);
        room.Anchors["north_right"] = new Vector3(c.x + lateralX, floorSpawnY, c.z + depthZ);
        room.Anchors["south_left"] = new Vector3(c.x - lateralX, floorSpawnY, c.z - depthZ);
        room.Anchors["south_right"] = new Vector3(c.x + lateralX, floorSpawnY, c.z - depthZ);
        room.Anchors["sniper_perch_nw"] = new Vector3(c.x - perchX, perchSpawnY, c.z + perchZ);
        room.Anchors["sniper_perch_ne"] = new Vector3(c.x + perchX, perchSpawnY, c.z + perchZ);
    }

    public static ZDOID SpawnIndestructible(
        string prefabName,
        Vector3 pos,
        Quaternion rot,
        InstanceCleaner cleaner)
    {
        GameObject? prefab = ZNetScene.instance.GetPrefab(prefabName);
        if (prefab == null)
        {
            Plugin.Log.LogWarning($"[SkyboxArenaBuilder] Missing structural prefab '{prefabName}'.");
            return ZDOID.None;
        }

        GameObject go = UnityEngine.Object.Instantiate(prefab, pos, rot);
        var netView = go.GetComponent<ZNetView>();
        if (netView == null || netView.GetZDO() == null)
        {
            return ZDOID.None;
        }

        ZDO zdo = netView.GetZDO();
        var wear = go.GetComponent<WearNTear>();
        if (wear != null)
        {
            wear.m_noRoofWear = true;
            wear.m_noSupportWear = true;
        }

        zdo.Set(ZDOVars.s_health, 1e9f);
        zdo.Set(ZDOVars.s_support, 1e9f);

        cleaner.Track(zdo.m_uid, indestructibleStructure: true);
        return zdo.m_uid;
    }
}
