using System.Collections.Generic;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using Dorfromantik;

namespace PerfectTriggerSlot
{
    [BepInPlugin(modGUID, modName, modVersion)]
    public class PerfectTriggerSlotBase : BaseUnityPlugin
    {
        private const string modGUID = "JG.PerfectTriggerSlot";
        private const string modName = "Perfect Trigger Slot Highlighter & Dynamic Radius Circle";
        private const string modVersion = "6.2.0";

        private readonly Harmony harmony = new Harmony(modGUID);
        private static BepInEx.Logging.ManualLogSource Log;

        private static readonly Dictionary<Tile, GameObject> activeTileMarkers = new Dictionary<Tile, GameObject>();
        private static readonly HashSet<Tile> currentlyHighlightedTiles = new HashSet<Tile>();

        private static readonly Dictionary<TileSlot, GameObject> activeSlotMarkers = new Dictionary<TileSlot, GameObject>();

        private static GameObject radiusCircleObject;
        private static MeshFilter circleMeshFilter;
        private static MeshRenderer circleMeshRenderer;

        private enum MatchStatus { None, FourMatch, FiveMatch, SixMatch }

        private void Awake()
        {
            Log = Logger;
            harmony.PatchAll(typeof(PerfectTriggerSlotBase));
            Log.LogWarning("=================================================");
            Log.LogWarning($"[PerfectTriggerSlot] v{modVersion} ACTIVE!");
            Log.LogWarning(" - Green Marker: 6/6 Matched Edges (Perfect)");
            Log.LogWarning(" - Yellow Marker: 5/5 Matched Edges");
            Log.LogWarning(" - Red Marker: 4/4 Matched Edges");
            Log.LogWarning(" - Purple Marker: Clean Slot for Current Tile");
            Log.LogWarning("=================================================");
        }

        private static GameObject CreateMarkerObject(string name, Vector3 pos, Color color, Vector3 scale)
        {
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            marker.name = name;

            Collider col = marker.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);

            Renderer r = marker.GetComponent<Renderer>();
            if (r != null)
            {
                Material mat = new Material(Shader.Find("Sprites/Default"));
                mat.color = color;
                r.material = mat;
            }

            marker.transform.position = pos;
            marker.transform.localScale = scale;
            return marker;
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
                    new Vector2Int(0, 1),  new Vector2Int(1, 1),  new Vector2Int(1, 0),
                    new Vector2Int(0, -1), new Vector2Int(-1, 0), new Vector2Int(-1, 1)
                };
            }
            else
            {
                offsets = new Vector2Int[] {
                    new Vector2Int(0, 1),   new Vector2Int(1, 0),  new Vector2Int(1, -1),
                    new Vector2Int(0, -1),  new Vector2Int(-1, -1),new Vector2Int(-1, 0)
                };
            }

            Vector2Int[] neighborPositions = new Vector2Int[6];
            for (int i = 0; i < 6; i++)
            {
                neighborPositions[i] = gridPos + offsets[i];
            }
            return neighborPositions;
        }

        private static ElementGroup GetWorldElementGroup(Tile tile, int worldDir, GroupType targetType = null)
        {
            if (tile == null) return null;
            int rot = tile.RotationIndex;
            int localEdge = (worldDir - rot + 600) % 6;
            return tile.GetElementGroup(localEdge, Space.Self, targetType);
        }

        private static ElementGroup GetWorldElementGroupWithRot(Tile tile, int worldDir, int customRot, GroupType targetType = null)
        {
            if (tile == null) return null;
            int localEdge = (worldDir - customRot + 600) % 6;
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
                if (groupA == groupB || groupA.id == groupB.id || groupA.name == groupB.name) return true;

                ElementGroup matchA = GetWorldElementGroup(tileB, dirB, groupA);
                if (matchA != null && matchA.GroupType != null && (matchA.GroupType == groupA || matchA.GroupType.id == groupA.id))
                    return true;

                ElementGroup matchB = GetWorldElementGroup(tileA, dirA, groupB);
                if (matchB != null && matchB.GroupType != null && (matchB.GroupType == groupB || matchB.GroupType.id == groupB.id))
                    return true;
            }

            if ((GetWorldHybridEdgeCount(tileA, dirA) > 0 && groupB == null) ||
                (GetWorldHybridEdgeCount(tileB, dirB) > 0 && groupA == null))
            {
                return true;
            }

            return false;
        }

        private static bool CheckEdgeMatchWithRot(Tile tileA, int rotA, int dirA, Tile tileB, int dirB)
        {
            if (tileA == null || tileB == null) return false;

            ElementGroup elemA = GetWorldElementGroupWithRot(tileA, dirA, rotA, null);
            GroupType groupA = elemA?.GroupType;

            ElementGroup elemB = GetWorldElementGroup(tileB, dirB, null);
            GroupType groupB = elemB?.GroupType;

            if (groupA == groupB) return true;

            if (groupA != null && groupB != null)
            {
                if (groupA.id == groupB.id || groupA.name == groupB.name) return true;

                ElementGroup matchA = GetWorldElementGroup(tileB, dirB, groupA);
                if (matchA != null && matchA.GroupType != null && (matchA.GroupType == groupA || matchA.GroupType.id == groupA.id))
                    return true;

                ElementGroup matchB = GetWorldElementGroupWithRot(tileA, dirA, rotA, groupB);
                if (matchB != null && matchB.GroupType != null && (matchB.GroupType == groupB || matchB.GroupType.id == groupB.id))
                    return true;
            }

            int rotA_local = (dirA - rotA + 600) % 6;
            var listA = tileA.GetHybridEdges(rotA_local, Space.Self);
            int hybridA = listA != null ? listA.Count : 0;

            if ((hybridA > 0 && groupB == null) || (GetWorldHybridEdgeCount(tileB, dirB) > 0 && groupA == null))
            {
                return true;
            }

            return false;
        }

        private static int GetOppositeNeighborDir(Vector2Int fromPos, Vector2Int toPos, int defaultDir)
        {
            try
            {
                var method = typeof(GridCalculator).GetMethod("GetNeighborIndexFromGridPos", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance);
                if (method != null)
                {
                    object res = method.Invoke(null, new object[] { toPos, fromPos });
                    if (res != null && ((int?)res).HasValue) return ((int?)res).Value;
                }
            }
            catch {}

            return (defaultDir + 3) % 6;
        }

        private static MatchStatus GetTileMatchStatus(Tile centerTile, World world)
        {
            if (centerTile == null || world == null) return MatchStatus.None;

            Vector2Int[] neighborPositions = GetNeighborPositions(centerTile.GridPos);
            int filledCount = 0;
            bool allFilledMatched = true;

            for (int i = 0; i < 6; i++)
            {
                Tile neighbor = world.GetTile(neighborPositions[i]);
                if (neighbor != null)
                {
                    int oppositeDir = GetOppositeNeighborDir(centerTile.GridPos, neighbor.GridPos, i);
                    bool edgeMatch = CheckEdgeMatch(centerTile, i, neighbor, oppositeDir);

                    if (!edgeMatch)
                    {
                        allFilledMatched = false;
                    }
                    filledCount++;
                }
            }

            if (allFilledMatched)
            {
                if (filledCount == 6) return MatchStatus.SixMatch;  // 6/6 -> XANH LÁ CÂY
                if (filledCount == 5) return MatchStatus.FiveMatch; // 5/5 -> VÀNG
                if (filledCount == 4) return MatchStatus.FourMatch; // 4/4 -> ĐỎ
            }

            return MatchStatus.None;
        }

        private static Tile GetCurrentHeldTile()
        {
            if (OverwritingSingleton<IngameUi>.Instance != null && OverwritingSingleton<IngameUi>.Instance.tilePlacer != null)
            {
                return OverwritingSingleton<IngameUi>.Instance.tilePlacer.CurrentTile;
            }
            return null;
        }

        private static bool CanTileFitCleanlyInSlot(TileSlot slot, Tile heldTile, World world)
        {
            if (slot == null || heldTile == null || world == null) return false;
            if (!slot.IsValid) return false;

            Vector2Int slotPos = slot.GridPos;
            Vector2Int[] neighborPositions = GetNeighborPositions(slotPos);

            int filledNeighbors = 0;
            for (int i = 0; i < 6; i++)
            {
                if (world.GetTile(neighborPositions[i]) != null)
                    filledNeighbors++;
            }

            if (filledNeighbors == 0) return false;

            for (int rot = 0; rot < 6; rot++)
            {
                bool allMatched = true;

                for (int i = 0; i < 6; i++)
                {
                    Tile neighbor = world.GetTile(neighborPositions[i]);
                    if (neighbor != null)
                    {
                        int oppositeDir = GetOppositeNeighborDir(slotPos, neighbor.GridPos, i);
                        bool match = CheckEdgeMatchWithRot(heldTile, rot, i, neighbor, oppositeDir);

                        if (!match)
                        {
                            allMatched = false;
                            break;
                        }
                    }
                }

                if (allMatched) return true;
            }

            return false;
        }

        private static void SetTileMeshHighlight(Tile tile, bool highlight)
        {
            if (tile == null) return;
            Renderer[] renderers = tile.GetComponentsInChildren<Renderer>();
            foreach (Renderer r in renderers)
            {
                if (r == null || r.material == null) continue;
                if (r.material.HasProperty("_Highlight"))
                {
                    r.material.SetFloat("_Highlight", highlight ? 1.0f : 0.0f);
                }
            }
        }

        private static void UpdateTileMarkers(Dictionary<Tile, MatchStatus> tileStatuses)
        {
            foreach (Tile tile in currentlyHighlightedTiles)
            {
                if (!tileStatuses.ContainsKey(tile))
                {
                    SetTileMeshHighlight(tile, false);
                }
            }
            currentlyHighlightedTiles.Clear();

            List<Tile> toRemove = new List<Tile>();
            foreach (var kvp in activeTileMarkers)
            {
                if (kvp.Key == null || !tileStatuses.ContainsKey(kvp.Key))
                {
                    if (kvp.Value != null) Object.Destroy(kvp.Value);
                    toRemove.Add(kvp.Key);
                }
            }
            foreach (Tile t in toRemove) activeTileMarkers.Remove(t);

            foreach (var kvp in tileStatuses)
            {
                Tile tile = kvp.Key;
                MatchStatus status = kvp.Value;

                Color targetColor;
                if (status == MatchStatus.SixMatch)
                {
                    targetColor = new Color(0.0f, 1.0f, 0.2f, 0.85f);  // XANH LÁ CÂY (6/6)
                }
                else if (status == MatchStatus.FiveMatch)
                {
                    targetColor = new Color(1.0f, 0.9f, 0.0f, 0.85f);  // VÀNG (5/5)
                }
                else
                {
                    targetColor = new Color(1.0f, 0.1f, 0.1f, 0.85f);  // ĐỎ (4/4)
                }

                currentlyHighlightedTiles.Add(tile);
                SetTileMeshHighlight(tile, true);

                if (!activeTileMarkers.ContainsKey(tile) || activeTileMarkers[tile] == null)
                {
                    GameObject marker = CreateMarkerObject("TileMatchMarker", tile.transform.position + new Vector3(0f, 0.35f, 0f), targetColor, new Vector3(0.35f, 0.05f, 0.35f));
                    activeTileMarkers[tile] = marker;
                }
                else
                {
                    Renderer r = activeTileMarkers[tile].GetComponent<Renderer>();
                    if (r != null && r.material != null)
                    {
                        r.material.color = targetColor;
                    }
                }
            }
        }

        private static void UpdateSlotMarkers(HashSet<TileSlot> cleanSlots)
        {
            List<TileSlot> toRemove = new List<TileSlot>();
            foreach (var kvp in activeSlotMarkers)
            {
                if (kvp.Key == null || !cleanSlots.Contains(kvp.Key))
                {
                    if (kvp.Value != null) Object.Destroy(kvp.Value);
                    toRemove.Add(kvp.Key);
                }
            }
            foreach (TileSlot s in toRemove) activeSlotMarkers.Remove(s);

            Color purpleColor = new Color(0.75f, 0.15f, 0.95f, 0.85f); // TÍM

            foreach (TileSlot slot in cleanSlots)
            {
                if (slot == null) continue;

                if (!activeSlotMarkers.ContainsKey(slot) || activeSlotMarkers[slot] == null)
                {
                    GameObject marker = CreateMarkerObject("CleanSlotMarker", slot.transform.position + new Vector3(0f, 0.25f, 0f), purpleColor, new Vector3(0.325f, 0.04f, 0.325f));
                    activeSlotMarkers[slot] = marker;
                }
            }
        }

        private static void InitRadiusCircle()
        {
            if (radiusCircleObject != null) return;

            radiusCircleObject = new GameObject("MaxRadiusCircleVisualizer");
            circleMeshFilter = radiusCircleObject.AddComponent<MeshFilter>();
            circleMeshRenderer = radiusCircleObject.AddComponent<MeshRenderer>();

            Material mat = new Material(Shader.Find("Sprites/Default"));
            mat.color = new Color(0.1f, 0.5f, 1.0f, 0.35f);

            mat.SetFloat("_Mode", 2);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = 3000;

            circleMeshRenderer.material = mat;
        }

        private static void UpdateMaxRadiusCircle(List<Tile> allPlacedTiles)
        {
            InitRadiusCircle();

            if (allPlacedTiles == null || allPlacedTiles.Count == 0)
            {
                if (radiusCircleObject != null) radiusCircleObject.SetActive(false);
                return;
            }

            Vector3 centerSum = Vector3.zero;
            int validCount = 0;

            foreach (Tile tile in allPlacedTiles)
            {
                if (tile == null) continue;
                Vector3 pos = tile.transform.position;
                centerSum += new Vector3(pos.x, 0f, pos.z);
                validCount++;
            }

            if (validCount == 0)
            {
                if (radiusCircleObject != null) radiusCircleObject.SetActive(false);
                return;
            }

            Vector3 dynamicCenter = centerSum / validCount;

            float maxRadius = 0f;
            foreach (Tile tile in allPlacedTiles)
            {
                if (tile == null) continue;
                Vector3 tilePos = tile.transform.position;
                Vector3 posFlat = new Vector3(tilePos.x, 0f, tilePos.z);

                float dist = Vector3.Distance(dynamicCenter, posFlat);
                if (dist > maxRadius)
                {
                    maxRadius = dist;
                }
            }

            if (maxRadius <= 0.01f)
            {
                if (radiusCircleObject != null) radiusCircleObject.SetActive(false);
                return;
            }

            radiusCircleObject.SetActive(true);

            int segments = 120;
            float thickness = 0.2f;
            float innerRadius = maxRadius - (thickness / 2f);
            float outerRadius = maxRadius + (thickness / 2f);

            Mesh ringMesh = new Mesh();
            Vector3[] vertices = new Vector3[segments * 2];
            int[] triangles = new int[segments * 6];

            const float TWO_PI = 6.28318530718f;
            float heightY = 0.12f;

            for (int i = 0; i < segments; i++)
            {
                float angle = (float)i / segments * TWO_PI;
                float cos = Mathf.Cos(angle);
                float sin = Mathf.Sin(angle);

                vertices[i * 2] = new Vector3(dynamicCenter.x + cos * innerRadius, heightY, dynamicCenter.z + sin * innerRadius);
                vertices[i * 2 + 1] = new Vector3(dynamicCenter.x + cos * outerRadius, heightY, dynamicCenter.z + sin * outerRadius);

                int nextIndex = (i + 1) % segments;
                triangles[i * 6] = i * 2;
                triangles[i * 6 + 1] = i * 2 + 1;
                triangles[i * 6 + 2] = nextIndex * 2;

                triangles[i * 6 + 3] = nextIndex * 2;
                triangles[i * 6 + 4] = i * 2 + 1;
                triangles[i * 6 + 5] = nextIndex * 2 + 1;
            }

            ringMesh.vertices = vertices;
            ringMesh.triangles = triangles;
            ringMesh.RecalculateNormals();

            circleMeshFilter.mesh = ringMesh;
        }

        public static void RunHighlightScan()
        {
            World world = Object.FindObjectOfType<World>();
            if (world == null) return;

            List<Tile> allPlacedTiles = world.GetAllPlacedTiles();
            if (allPlacedTiles == null) return;

            // 1. Cập nhật đường tròn
            UpdateMaxRadiusCircle(allPlacedTiles);

            // 2. Quét ô Đỏ (4/4), Vàng (5/5) và Xanh lá (6/6)
            Dictionary<Tile, MatchStatus> tileStatuses = new Dictionary<Tile, MatchStatus>();
            foreach (Tile centerTile in allPlacedTiles)
            {
                if (centerTile == null) continue;
                MatchStatus status = GetTileMatchStatus(centerTile, world);
                if (status != MatchStatus.None)
                {
                    tileStatuses[centerTile] = status;
                }
            }
            UpdateTileMarkers(tileStatuses);

            // 3. Quét ô Tím (Slot đặt Tile hiện tại không bị lệch)
            HashSet<TileSlot> cleanSlots = new HashSet<TileSlot>();
            Tile heldTile = GetCurrentHeldTile();

            if (heldTile != null)
            {
                TileSlotPreviewer slotPreviewer = Object.FindObjectOfType<TileSlotPreviewer>();
                if (slotPreviewer != null)
                {
                    List<TileSlot> validSlots = slotPreviewer.AllValidTileSlots;
                    if (validSlots != null)
                    {
                        foreach (TileSlot slot in validSlots)
                        {
                            if (CanTileFitCleanlyInSlot(slot, heldTile, world))
                            {
                                cleanSlots.Add(slot);
                            }
                        }
                    }
                }
            }
            UpdateSlotMarkers(cleanSlots);
        }

        [HarmonyPatch(typeof(TileSlotPreviewer), "UpdateTileSlotValidity")]
        [HarmonyPostfix]
        private static void Postfix_UpdateTileSlotValidity()
        {
            RunHighlightScan();
        }

        [HarmonyPatch(typeof(Dorfromantik.TilePlacementEventBroadcaster), "BroadcastTilePlacedFinalized")]
        [HarmonyPostfix]
        private static void Postfix_BroadcastTilePlacedFinalized()
        {
            RunHighlightScan();
        }

        [HarmonyPatch(typeof(TilePlacer), "RotatePreviewTile", new System.Type[] { typeof(int), typeof(bool) })]
        [HarmonyPostfix]
        private static void Postfix_RotatePreviewTile()
        {
            RunHighlightScan();
        }

        [HarmonyPatch(typeof(TilePlacer), "ShowPreviewTileAt")]
        [HarmonyPostfix]
        private static void Postfix_ShowPreviewTileAt()
        {
            RunHighlightScan();
        }
    }
}
