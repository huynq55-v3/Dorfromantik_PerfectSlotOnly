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
        private const string modName = "Perfect Trigger Slot Highlighter & Dynamic Radius Circle";
        private const string modVersion = "5.2.0";

        private readonly Harmony harmony = new Harmony(modGUID);
        private static BepInEx.Logging.ManualLogSource Log;

        private static readonly Dictionary<Tile, GameObject> activeMarkers = new Dictionary<Tile, GameObject>();
        private static readonly HashSet<Tile> currentlyHighlightedTiles = new HashSet<Tile>();

        // Quản lý đường tròn bán kính (Mesh Visualizer)
        private static GameObject radiusCircleObject;
        private static MeshFilter circleMeshFilter;
        private static MeshRenderer circleMeshRenderer;

        private void Awake()
        {
            Log = Logger;
            harmony.PatchAll(typeof(PerfectTriggerSlotBase));
            Log.LogWarning("=================================================");
            Log.LogWarning($"[PerfectTriggerSlot] v{modVersion} (BLUE DYNAMIC CIRCLE) ACTIVE!");
            Log.LogWarning("=================================================");
        }

        // =========================================================================
        // LOGIC VẼ ĐƯỜNG TRÒN VỚI TÂM TỰ ĐỘNG & MÀU BLUE MỜ (DYNAMIC BOUNDING CIRCLE)
        // =========================================================================

        private static void InitRadiusCircle()
        {
            if (radiusCircleObject != null) return;

            radiusCircleObject = new GameObject("MaxRadiusCircleVisualizer");
            circleMeshFilter = radiusCircleObject.AddComponent<MeshFilter>();
            circleMeshRenderer = radiusCircleObject.AddComponent<MeshRenderer>();

            // Dùng Shader Sprites/Default hỗ trợ Alpha Blending
            Material mat = new Material(Shader.Find("Sprites/Default"));
            
            // Màu Xanh Dương (Blue) mờ dịu mắt (R: 0.1, G: 0.5, B: 1.0, Alpha: 0.35)
            mat.color = new Color(0.1f, 0.5f, 1.0f, 0.35f);
            
            // Bật chế độ trong suốt cho Material
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

            // Step 1: Tìm tâm tự động (Centroid / Center of Mass) của toàn bộ các Tile
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

            // Tâm trọng trường trung bình của cụm Tile
            Vector3 dynamicCenter = centerSum / validCount;

            // Step 2: Tìm bán kính R_max tính từ Tâm Tự Động tới Tile xa nhất
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

            // Step 3: Dựng Mesh hình vòng nhẫn mờ xung quanh tâm động (dynamicCenter)
            int segments = 120;
            float thickness = 0.2f; // Đường viền mảnh và tinh tế hơn
            float innerRadius = maxRadius - (thickness / 2f);
            float outerRadius = maxRadius + (thickness / 2f);

            Mesh ringMesh = new Mesh();
            Vector3[] vertices = new Vector3[segments * 2];
            int[] triangles = new int[segments * 6];

            const float TWO_PI = 6.28318530718f;
            float heightY = 0.12f; // Nâng nhẹ để nằm trên mặt đất

            for (int i = 0; i < segments; i++)
            {
                float angle = (float)i / segments * TWO_PI;
                float cos = Mathf.Cos(angle);
                float sin = Mathf.Sin(angle);

                // Tọa độ đỉnh công thêm vị trí Tâm Động (dynamicCenter)
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

        // =========================================================================
        // LOGIC HIGHLIGHT TILE & HEX GRID (GIỮ NGUYÊN)
        // =========================================================================

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

        private static int GetOppositeNeighborDir(Vector2Int fromPos, Vector2Int toPos, int defaultDir)
        {
            try
            {
                var method = typeof(GridCalculator).GetMethod("GetNeighborIndexFromGridPos", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance);
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
                else
                {
                    emptyNeighborDir = i;
                }
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

        public static void RunHighlightScan()
        {
            World world = Object.FindObjectOfType<World>();
            if (world == null) return;

            List<Tile> allPlacedTiles = world.GetAllPlacedTiles();
            if (allPlacedTiles == null) return;

            // 1. Cập nhật đường tròn màu xanh dương với tâm động (Dynamic Center & Blue Circle)
            UpdateMaxRadiusCircle(allPlacedTiles);

            // 2. Quét Highlight các vị trí 5/5 matched
            HashSet<Tile> activeTriggers = new HashSet<Tile>();

            foreach (Tile centerTile in allPlacedTiles)
            {
                if (centerTile == null) continue;

                if (IsTileWaitingFor6thPerfect(centerTile, world, out int emptyDir))
                {
                    activeTriggers.Add(centerTile);
                }
            }

            UpdateTileHighlights(activeTriggers);
        }

        [HarmonyPatch(typeof(TileSlotPreviewer), "UpdateTileSlotValidity")]
        [HarmonyPostfix]
        private static void Postfix_UpdateTileSlotValidity(Tile newTile, ref Dictionary<Vector2Int, TileSlot> ___tileSlots)
        {
            RunHighlightScan();
        }

        [HarmonyPatch(typeof(Dorfromantik.TilePlacementEventBroadcaster), "BroadcastTilePlacedFinalized")]
        [HarmonyPostfix]
        private static void Postfix_BroadcastTilePlacedFinalized(Tile placedTile, bool placedByPlayer)
        {
            RunHighlightScan();
        }
    }
}
