using System.Collections.Generic;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace PerfectTriggerSlot
{
    [BepInPlugin(modGUID, modName, modVersion)]
    public class PerfectTriggerSlotBase : BaseUnityPlugin
    {
        private const string modGUID = "JG.PerfectTriggerSlot";
        private const string modName = "Perfect Trigger Slot Highlighter";
        private const string modVersion = "4.4.0";

        private readonly Harmony harmony = new Harmony(modGUID);
        private static BepInEx.Logging.ManualLogSource Log;

        private static readonly Dictionary<Tile, GameObject> activeMarkers = new Dictionary<Tile, GameObject>();
        private static readonly HashSet<Tile> currentlyHighlightedTiles = new HashSet<Tile>();

        private void Awake()
        {
            Log = Logger;
            harmony.PatchAll(typeof(PerfectTriggerSlotBase));
            Log.LogWarning("=================================================");
            Log.LogWarning($"[PerfectTriggerSlot] v{modVersion} (CORRECT EDGE MAPPING RESOLVED) ACTIVE!");
            Log.LogWarning("=================================================");
        }

        private static Vector2Int[] GetNeighborPositions(Vector2Int gridPos)
        {
            try
            {
                var method = typeof(GridCalculator).GetMethod("GetNeighborGridPositions", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance);
                if (method != null)
                {
                    Vector2Int[] result = (Vector2Int[])method.Invoke(null, new object[] { gridPos });
                    if (result != null && result.Length == 6) return result;
                }
            }
            catch {}

            Vector2Int[] offsets;
            if (gridPos.y % 2 == 0)
            {
                offsets = new Vector2Int[] {
                    new Vector2Int(0, 1),
                    new Vector2Int(1, 1),
                    new Vector2Int(1, 0),
                    new Vector2Int(0, -1),
                    new Vector2Int(-1, 0),
                    new Vector2Int(-1, 1)
                };
            }
            else
            {
                offsets = new Vector2Int[] {
                    new Vector2Int(0, 1),
                    new Vector2Int(1, 0),
                    new Vector2Int(1, -1),
                    new Vector2Int(0, -1),
                    new Vector2Int(-1, -1),
                    new Vector2Int(-1, 0)
                };
            }

            Vector2Int[] neighborPositions = new Vector2Int[6];
            for (int i = 0; i < 6; i++)
            {
                neighborPositions[i] = gridPos + offsets[i];
            }
            return neighborPositions;
        }

        // Authoritative Direct Hex Rotation Formula: LocalEdge = (WorldDir - RotationIndex + 600) mod 6
        private static ElementGroup GetWorldElementGroup(Tile tile, int worldDir, GroupType targetType = null)
        {
            if (tile == null) return null;
            int rot = tile.RotationIndex;
            int localEdge = (worldDir - rot + 600) % 6;
            return tile.GetElementGroup(localEdge, Space.Self, targetType);
        }

        private static int GetWorldHybridEdgeCount(Tile tile, int worldDir)
        {
            if (tile == null) return 0;
            int rot = tile.RotationIndex;
            int localEdge = (worldDir - rot + 600) % 6;
            var list = tile.GetHybridEdges(localEdge, Space.Self);
            return list != null ? list.Count : 0;
        }

        private static ElementGroup GetHeldTileWorldElementGroup(Tile heldTile, int worldDir, int rot, GroupType targetType = null)
        {
            if (heldTile == null) return null;
            int localEdge = (worldDir - rot + 600) % 6;
            return heldTile.GetElementGroup(localEdge, Space.Self, targetType);
        }

        private static int GetHeldTileHybridEdgeCount(Tile heldTile, int worldDir, int rot)
        {
            if (heldTile == null) return 0;
            int localEdge = (worldDir - rot + 600) % 6;
            var list = heldTile.GetHybridEdges(localEdge, Space.Self);
            return list != null ? list.Count : 0;
        }

        private static bool CheckEdgeMatch(Tile tileA, int dirA, Tile tileB, int dirB)
        {
            if (tileA == null || tileB == null) return false;

            ElementGroup elemA = GetWorldElementGroup(tileA, dirA, null);
            GroupType groupA = elemA?.GroupType;

            ElementGroup elemB = GetWorldElementGroup(tileB, dirB, null);
            GroupType groupB = elemB?.GroupType;

            if (groupA == groupB) return true;

            if (groupA != null && groupB != null)
            {
                if (groupA == groupB || groupA.id == groupB.id || groupA.name == groupB.name)
                {
                    return true;
                }

                ElementGroup matchA = GetWorldElementGroup(tileB, dirB, groupA);
                if (matchA != null && matchA.GroupType != null)
                {
                    if (matchA.GroupType == groupA || matchA.GroupType.id == groupA.id)
                        return true;
                }

                ElementGroup matchB = GetWorldElementGroup(tileA, dirA, groupB);
                if (matchB != null && matchB.GroupType != null)
                {
                    if (matchB.GroupType == groupB || matchB.GroupType.id == groupB.id)
                        return true;
                }
            }

            if ((GetWorldHybridEdgeCount(tileA, dirA) > 0 && groupB == null) ||
                (GetWorldHybridEdgeCount(tileB, dirB) > 0 && groupA == null))
            {
                return true;
            }

            return false;
        }

        private static bool CheckHeldTileEdgeMatch(Tile heldTile, int dirA, int rot, Tile tileB, int dirB)
        {
            if (heldTile == null || tileB == null) return false;

            ElementGroup elemA = GetHeldTileWorldElementGroup(heldTile, dirA, rot, null);
            GroupType groupA = elemA?.GroupType;

            ElementGroup elemB = GetWorldElementGroup(tileB, dirB, null);
            GroupType groupB = elemB?.GroupType;

            if (groupA == groupB) return true;

            if (groupA != null && groupB != null)
            {
                if (groupA == groupB || groupA.id == groupB.id || groupA.name == groupB.name)
                {
                    return true;
                }

                ElementGroup matchA = GetWorldElementGroup(tileB, dirB, groupA);
                if (matchA != null && matchA.GroupType != null)
                {
                    if (matchA.GroupType == groupA || matchA.GroupType.id == groupA.id)
                        return true;
                }

                ElementGroup matchB = GetHeldTileWorldElementGroup(heldTile, dirA, rot, groupB);
                if (matchB != null && matchB.GroupType != null)
                {
                    if (matchB.GroupType == groupB || matchB.GroupType.id == groupB.id)
                        return true;
                }
            }

            if ((GetHeldTileHybridEdgeCount(heldTile, dirA, rot) > 0 && groupB == null) ||
                (GetWorldHybridEdgeCount(tileB, dirB) > 0 && groupA == null))
            {
                return true;
            }

            return false;
        }

        private static int GetOppositeNeighborDir(Vector2Int fromPos, Vector2Int toPos, int defaultDir)
        {
            try
            {
                var method = typeof(GridCalculator).GetMethod("GetNeighborIndexFromGridPos", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);
                if (method != null)
                {
                    object instance = null;
                    object res = method.Invoke(instance, new object[] { toPos, fromPos });
                    if (res != null)
                    {
                        int? idx = (int?)res;
                        if (idx.HasValue) return idx.Value;
                    }
                }
            }
            catch {}

            return (defaultDir + 3) % 6;
        }

        private static bool IsTileWaitingFor6thPerfect(Tile centerTile, World world, out int emptyNeighborDir)
        {
            emptyNeighborDir = -1;
            if (centerTile == null || world == null) return false;

            Vector2Int[] neighborPositions = GetNeighborPositions(centerTile.GridPos);
            int filledCount = 0;
            bool allFilledMatched = true;

            List<string> edgeDetails = new List<string>();

            for (int i = 0; i < 6; i++)
            {
                Tile neighbor = world.GetTile(neighborPositions[i]);
                if (neighbor != null)
                {
                    int oppositeDir = GetOppositeNeighborDir(centerTile.GridPos, neighbor.GridPos, i);
                    bool edgeMatch = CheckEdgeMatch(centerTile, i, neighbor, oppositeDir);

                    ElementGroup g1 = GetWorldElementGroup(centerTile, i);
                    ElementGroup g2 = GetWorldElementGroup(neighbor, oppositeDir);

                    edgeDetails.Add($"Dir{i}[{neighbor.GridPos}:{neighbor.name}]:{edgeMatch}({g1?.GroupType?.name ?? "Grass"} vs {g2?.GroupType?.name ?? "Grass"})");

                    if (!edgeMatch)
                    {
                        allFilledMatched = false;
                    }
                    filledCount++;
                }
                else
                {
                    emptyNeighborDir = i;
                }
            }

            if (filledCount >= 4 || centerTile.GridPos == new Vector2Int(0, -1))
            {
                Log.LogWarning($"[Audit Tile {centerTile.GridPos}] ({centerTile.name}, rot={centerTile.RotationIndex}): Filled={filledCount}, AllMatched={allFilledMatched}, MissingDir={emptyNeighborDir}. Edges: {string.Join("; ", edgeDetails)}");
            }

            return filledCount == 5 && allFilledMatched;
        }

        private static void SetTileMeshHighlight(Tile tile, bool highlight)
        {
            if (tile == null) return;
            Renderer[] renderers = tile.GetComponentsInChildren<Renderer>();
            foreach (Renderer r in renderers)
            {
                if (r == null || r.material == null) continue;
                if (highlight)
                {
                    if (r.material.HasProperty("_Highlight")) r.material.SetFloat("_Highlight", 1.0f);
                }
                else
                {
                    if (r.material.HasProperty("_Highlight")) r.material.SetFloat("_Highlight", 0.0f);
                }
            }
        }

        private static void UpdateTileHighlights(HashSet<Tile> activeTriggers)
        {
            foreach (Tile tile in currentlyHighlightedTiles)
            {
                if (!activeTriggers.Contains(tile))
                {
                    SetTileMeshHighlight(tile, false);
                }
            }
            currentlyHighlightedTiles.Clear();

            List<Tile> toRemove = new List<Tile>();
            foreach (var kvp in activeMarkers)
            {
                if (!activeTriggers.Contains(kvp.Key) || kvp.Key == null)
                {
                    if (kvp.Value != null) Object.Destroy(kvp.Value);
                    toRemove.Add(kvp.Key);
                }
            }
            foreach (Tile t in toRemove) activeMarkers.Remove(t);

            foreach (Tile tile in activeTriggers)
            {
                if (tile == null) continue;
                currentlyHighlightedTiles.Add(tile);
                SetTileMeshHighlight(tile, true);

                if (!activeMarkers.ContainsKey(tile) || activeMarkers[tile] == null)
                {
                    GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    marker.name = "PerfectTargetTileMarker";

                    Collider col = marker.GetComponent<Collider>();
                    if (col != null) Object.Destroy(col);

                    Renderer r = marker.GetComponent<Renderer>();
                    if (r != null)
                    {
                        Material mat = new Material(Shader.Find("Sprites/Default"));
                        mat.color = new Color(0.0f, 1.0f, 0.2f, 0.85f);
                        r.material = mat;
                    }

                    marker.transform.position = tile.transform.position + new Vector3(0f, 0.35f, 0f);
                    marker.transform.localScale = new Vector3(0.7f, 0.08f, 0.7f);
                    activeMarkers[tile] = marker;
                }
            }
        }

        public static void RunHighlightScan(Tile heldTile)
        {
            if (heldTile == null)
            {
                TilePlacer placer = Object.FindObjectOfType<TilePlacer>();
                if (placer != null) heldTile = placer.CurrentTile;
            }

            World world = Object.FindObjectOfType<World>();
            if (world == null || heldTile == null) return;

            List<Tile> allPlacedTiles = world.GetAllPlacedTiles();
            if (allPlacedTiles == null) return;

            HashSet<Tile> activeTriggers = new HashSet<Tile>();

            foreach (Tile centerTile in allPlacedTiles)
            {
                if (centerTile == null) continue;

                if (IsTileWaitingFor6thPerfect(centerTile, world, out int emptyDir))
                {
                    Vector2Int[] neighbors = GetNeighborPositions(centerTile.GridPos);
                    Vector2Int emptySlotPos = neighbors[emptyDir];

                    int dirFromSlotToTile = GetOppositeNeighborDir(emptySlotPos, centerTile.GridPos, (emptyDir + 3) % 6);

                    for (int rot = 0; rot < 6; rot++)
                    {
                        if (CheckHeldTileEdgeMatch(heldTile, dirFromSlotToTile, rot, centerTile, emptyDir))
                        {
                            activeTriggers.Add(centerTile);
                            Log.LogWarning($"[PERFECT THIS TURN] Tile {centerTile.GridPos} CAN BECOME PERFECT IN THIS TURN with held tile placed at slot {emptySlotPos}!");
                            break;
                        }
                    }
                }
            }

            UpdateTileHighlights(activeTriggers);
        }

        [HarmonyPatch(typeof(TileSlotPreviewer), "UpdateTileSlotValidity")]
        [HarmonyPostfix]
        private static void Postfix_UpdateTileSlotValidity(Tile newTile, ref Dictionary<Vector2Int, TileSlot> ___tileSlots)
        {
            RunHighlightScan(newTile);
        }

        [HarmonyPatch(typeof(Dorfromantik.TilePlacementEventBroadcaster), "BroadcastTilePlacedFinalized")]
        [HarmonyPostfix]
        private static void Postfix_BroadcastTilePlacedFinalized(Tile placedTile, bool placedByPlayer)
        {
            RunHighlightScan(null);
        }
    }
}