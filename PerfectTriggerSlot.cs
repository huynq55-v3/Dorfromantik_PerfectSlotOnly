using System;
using System.Collections.Generic;
using System.Linq;
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
        private const string modName = "Perfect Trigger Slot Highlighter";
        private const string modVersion = "18.0.0";

        private readonly Harmony harmony = new Harmony(modGUID);
        private static BepInEx.Logging.ManualLogSource Log;

        private static readonly Dictionary<Tile, GameObject> activeTileMarkers = new Dictionary<Tile, GameObject>();
        private static readonly HashSet<Tile> currentlyHighlightedTiles = new HashSet<Tile>();
        private static readonly Dictionary<Tile, GameObject> activeTilePresetTexts = new Dictionary<Tile, GameObject>();

        private static readonly Dictionary<TileSlot, GameObject> activeSlotMarkers = new Dictionary<TileSlot, GameObject>();

        private static readonly Color ImpossibleGrayColor = new Color(0.4f, 0.4f, 0.4f, 0.85f);

        // Cache cố định 27 màu gốc (Hàm Max) cho Tile đang cầm trên tay
        private static readonly Dictionary<TileSlot, Color> cachedBaseSlotColors = new Dictionary<TileSlot, Color>();
        private static Tile lastHeldTileInstance = null;
        private static bool isBaseColorCacheDirty = true;

        // Cache cho ô bất khả thi tĩnh (trên lưới tile đã đặt vĩnh viễn)
        private static readonly HashSet<TileSlot> cachedStaticImpossibleSlots = new HashSet<TileSlot>();
        private static bool isStaticImpossibleCacheDirty = true;

        private enum MatchStatus { None, BlackMatch, FourMatch, FiveMatch, SixMatch }

        public struct SlotState : IComparable<SlotState>
        {
            public int match;
            public int total;

            public SlotState(int match, int total)
            {
                this.match = match;
                this.total = total;
            }

            public int CompareTo(SlotState other)
            {
                if (this.match != other.match)
                {
                    return this.match.CompareTo(other.match);
                }

                int penaltyA = this.total - this.match;
                int penaltyB = other.total - other.match;
                if (penaltyA != penaltyB)
                {
                    return penaltyB.CompareTo(penaltyA);
                }

                return this.total.CompareTo(other.total);
            }
        }

        // Tối ưu hóa: Mã hóa địa hình thành ID số nguyên (0..15)
        private static readonly Dictionary<GroupType, byte> groupTypeIdMap = new Dictionary<GroupType, byte>();
        private static readonly Dictionary<string, byte> groupTypeIdStringMap = new Dictionary<string, byte>();
        private static byte nextGroupTypeId = 1; // 0 dành cho null (Plain)

        // Bảng tra nhanh Bitmask 32-bit cho các mẫu ô tile trong game
        private static readonly HashSet<uint> validRotatedPatternKeys = new HashSet<uint>();
        private static bool isPatternKeySetBuilt = false;
        private static float lastPatternBuildTime = 0f;

        private static readonly Dictionary<KeyValuePair<int, int>, Color> slotColorMap = new Dictionary<KeyValuePair<int, int>, Color>();

        private void Awake()
        {
            Log = Logger;
            Initialize27DistinctRandomColors();
            harmony.PatchAll(typeof(PerfectTriggerSlotBase));
            Log.LogWarning("=================================================");
            Log.LogWarning($"[PerfectTriggerSlot] v{modVersion} ACTIVE!");
            Log.LogWarning(" - Placed Tiles: Dynamic Red/Yellow/Green/Black updating on Preview Rotation");
            Log.LogWarning(" - Unplaced Slots: 27 Base Colors FROZEN in memory per held tile");
            Log.LogWarning(" - Impossible Perfect Slots: GRAY (Dynamic on Rotate, Restores Frozen Base Color)");
            Log.LogWarning(" - Current Tile Preset: Large Gold Text (Camera Aligned)");
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

        private static void InvalidateCache()
        {
            isStaticImpossibleCacheDirty = true;
            isBaseColorCacheDirty = true;
        }

        private static byte GetTerrainId(GroupType groupType)
        {
            if (groupType == null) return 0;
            if (groupTypeIdMap.TryGetValue(groupType, out byte id)) return id;

            string idKey = groupType.id.ToString();
            if (string.IsNullOrEmpty(idKey) && !string.IsNullOrEmpty(groupType.name))
            {
                idKey = groupType.name;
            }

            if (groupTypeIdStringMap.TryGetValue(idKey, out byte strId))
            {
                groupTypeIdMap[groupType] = strId;
                return strId;
            }

            byte newId = nextGroupTypeId++;
            groupTypeIdMap[groupType] = newId;
            if (!string.IsNullOrEmpty(idKey)) groupTypeIdStringMap[idKey] = newId;
            return newId;
        }

        private static uint PackPatternKey(byte[] edgeIds)
        {
            uint key = 0;
            for (int i = 0; i < 6; i++)
            {
                key |= ((uint)(edgeIds[i] & 0x0F)) << (i * 4);
            }
            return key;
        }

        private static void BuildFastPatternKeySet()
        {
            if (isPatternKeySetBuilt && Time.time - lastPatternBuildTime < 15f) return;

            validRotatedPatternKeys.Clear();
            try
            {
                var genConfigs = Resources.FindObjectsOfTypeAll<TileGenConfiguration>();
                if (genConfigs != null && genConfigs.Length > 0)
                {
                    foreach (var config in genConfigs)
                    {
                        if (config == null || config.allTilePresets == null) continue;
                        foreach (var presetConfig in config.allTilePresets)
                        {
                            if (presetConfig == null || presetConfig.segmentProbabilities == null) continue;
                            AddPresetToPatternKeys(presetConfig.segmentProbabilities);
                        }
                    }
                }

                if (validRotatedPatternKeys.Count == 0)
                {
                    var generators = Resources.FindObjectsOfTypeAll<TileGenerator>();
                    if (generators != null)
                    {
                        foreach (var gen in generators)
                        {
                            if (gen != null && gen.Configuration != null && gen.Configuration.allTilePresets != null)
                            {
                                foreach (var presetConfig in gen.Configuration.allTilePresets)
                                {
                                    if (presetConfig == null || presetConfig.segmentProbabilities == null) continue;
                                    AddPresetToPatternKeys(presetConfig.segmentProbabilities);
                                }
                            }
                        }
                    }
                }

                if (validRotatedPatternKeys.Count == 0)
                {
                    Tile[] instantiatedTiles = Resources.FindObjectsOfTypeAll<Tile>();
                    if (instantiatedTiles != null)
                    {
                        foreach (Tile t in instantiatedTiles)
                        {
                            if (t == null || t.AllElementGroupSegments == null) continue;
                            byte[] edges = new byte[6];
                            for (int i = 0; i < 6; i++)
                            {
                                ElementGroup eg = t.GetElementGroup(i, Space.Self, null);
                                edges[i] = GetTerrainId(eg?.GroupType);
                            }
                            AddRotatedKeys(edges);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (Log != null) Log.LogError($"Error building fast pattern keys: {ex.Message}");
            }

            if (validRotatedPatternKeys.Count > 0)
            {
                isPatternKeySetBuilt = true;
                lastPatternBuildTime = Time.time;
            }
        }

        private static void AddPresetToPatternKeys(List<SegmentPresetInfo> segmentProbabilities)
        {
            List<List<GroupType>> segmentOptions = new List<List<GroupType>>();
            List<List<int>> segmentEdgeIndices = new List<List<int>>();

            foreach (var segInfo in segmentProbabilities)
            {
                if (segInfo == null || segInfo.segmentType == null || segInfo.segmentType.edges == null) continue;

                List<GroupType> options = new List<GroupType>();
                if (segInfo.possibleTypes != null)
                {
                    foreach (var gtConfig in segInfo.possibleTypes)
                    {
                        if (gtConfig != null && gtConfig.groupType != null)
                        {
                            if (!options.Contains(gtConfig.groupType))
                            {
                                options.Add(gtConfig.groupType);
                            }
                        }
                    }
                }
                if (options.Count == 0) options.Add(null);

                segmentOptions.Add(options);
                segmentEdgeIndices.Add(segInfo.segmentType.edges);
            }

            if (segmentOptions.Count == 0) return;

            List<byte[]> combinations = new List<byte[]>();
            GenerateCombinationsRecursive(segmentOptions, segmentEdgeIndices, 0, new byte[6], combinations);

            foreach (var combo in combinations)
            {
                AddRotatedKeys(combo);
            }
        }

        private static void AddRotatedKeys(byte[] baseEdges)
        {
            for (int rot = 0; rot < 6; rot++)
            {
                byte[] rotated = new byte[6];
                for (int i = 0; i < 6; i++)
                {
                    rotated[i] = baseEdges[(i - rot + 600) % 6];
                }
                validRotatedPatternKeys.Add(PackPatternKey(rotated));
            }
        }

        private static void GenerateCombinationsRecursive(
            List<List<GroupType>> segmentOptions,
            List<List<int>> segmentEdgeIndices,
            int currentSegmentIndex,
            byte[] currentEdges,
            List<byte[]> result)
        {
            if (currentSegmentIndex >= segmentOptions.Count)
            {
                byte[] copy = new byte[6];
                Array.Copy(currentEdges, copy, 6);
                result.Add(copy);
                return;
            }

            List<GroupType> options = segmentOptions[currentSegmentIndex];
            List<int> edgeIndices = segmentEdgeIndices[currentSegmentIndex];

            foreach (GroupType option in options)
            {
                byte terrainId = GetTerrainId(option);
                byte[] nextEdges = new byte[6];
                Array.Copy(currentEdges, nextEdges, 6);

                foreach (int edgeIdx in edgeIndices)
                {
                    if (edgeIdx >= 0 && edgeIdx < 6)
                    {
                        nextEdges[edgeIdx] = terrainId;
                    }
                }

                GenerateCombinationsRecursive(segmentOptions, segmentEdgeIndices, currentSegmentIndex + 1, nextEdges, result);
            }
        }

        // Kiểm tra tính khả thi Perfect match cho ô trống (tính cả ô Tile kề đang đặt/xoay ở previewSlot)
        private static bool CanSlotAchievePerfectMatchFast(TileSlot slot, World world, TileSlot previewSlot = null, Tile heldTile = null)
        {
            if (slot == null || world == null) return true;
            BuildFastPatternKeySet();
            if (validRotatedPatternKeys.Count == 0) return true;

            Vector2Int slotPos = slot.GridPos;
            Vector2Int[] neighborPositions = GetNeighborPositions(slotPos);

            byte[] reqTerrainIds = new byte[6];
            bool[] isPlaced = new bool[6];
            int placedCount = 0;

            for (int i = 0; i < 6; i++)
            {
                Vector2Int nPos = neighborPositions[i];
                Tile neighbor = world.GetTile(nPos);
                if (neighbor != null)
                {
                    // Ô kề đã đặt vĩnh viễn trên bản đồ
                    int oppDir = GetOppositeNeighborDir(slotPos, neighbor.GridPos, i);
                    ElementGroup elem = GetWorldElementGroup(neighbor, oppDir, null);
                    reqTerrainIds[i] = GetTerrainId(elem?.GroupType);
                    isPlaced[i] = true;
                    placedCount++;
                }
                else if (previewSlot != null && heldTile != null && previewSlot.GridPos == nPos)
                {
                    // Ô kề chính là ô đang đặt previewTile! Cạnh chĩa sang ô slot phụ thuộc vào góc xoay heldTile.RotationIndex
                    int oppDir = GetOppositeNeighborDir(slotPos, nPos, i);
                    ElementGroup elem = GetWorldElementGroupWithRot(heldTile, oppDir, heldTile.RotationIndex, null);
                    reqTerrainIds[i] = GetTerrainId(elem?.GroupType);
                    isPlaced[i] = true;
                    placedCount++;
                }
            }

            if (placedCount < 2) return true;

            foreach (uint key in validRotatedPatternKeys)
            {
                bool matchAll = true;
                for (int i = 0; i < 6; i++)
                {
                    if (!isPlaced[i]) continue;
                    byte candId = (byte)((key >> (i * 4)) & 0x0F);
                    if (candId != reqTerrainIds[i])
                    {
                        matchAll = false;
                        break;
                    }
                }
                if (matchAll) return true;
            }

            return false;
        }

        private static void Initialize27DistinctRandomColors()
        {
            List<SlotState> allStates = new List<SlotState>();
            for (int t = 1; t <= 6; t++)
            {
                for (int m = 0; m <= t; m++)
                {
                    allStates.Add(new SlotState(m, t));
                }
            }

            System.Random rng = new System.Random(1337);
            const float goldenRatioConjugate = 0.618033988749895f;
            float hue = (float)rng.NextDouble();

            slotColorMap.Clear();
            for (int i = 0; i < allStates.Count; i++)
            {
                var key = new KeyValuePair<int, int>(allStates[i].match, allStates[i].total);

                hue = (hue + goldenRatioConjugate) % 1.0f;
                float saturation = 0.75f + (float)(rng.NextDouble() * 0.25f);
                float value = 0.85f + (float)(rng.NextDouble() * 0.15f);

                Color color = Color.HSVToRGB(hue, saturation, value);
                color.a = 0.85f;

                slotColorMap[key] = color;
            }
        }

        private static Color GetSlotColor(int match, int total)
        {
            var key = new KeyValuePair<int, int>(match, total);
            if (slotColorMap.TryGetValue(key, out Color color))
            {
                return color;
            }
            return new Color(0.5f, 0.5f, 0.5f, 0.5f);
        }

        private static GameObject CreateMarkerObject(string name, Vector3 pos, Color color, Vector3 scale)
        {
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            marker.name = name;

            Collider col = marker.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.Destroy(col);

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

        private static MatchStatus GetTileMatchStatus(Tile centerTile, World world, TileSlot previewSlot = null, Tile heldTile = null)
        {
            if (centerTile == null || world == null) return MatchStatus.None;

            Vector2Int centerPos = centerTile.GridPos;
            Vector2Int[] neighborPositions = GetNeighborPositions(centerPos);
            int filledCount = 0;
            bool allFilledMatched = true;

            for (int i = 0; i < 6; i++)
            {
                Vector2Int nPos = neighborPositions[i];
                Tile neighbor = world.GetTile(nPos);
                if (neighbor != null)
                {
                    int oppositeDir = GetOppositeNeighborDir(centerPos, neighbor.GridPos, i);
                    bool edgeMatch = CheckEdgeMatch(centerTile, i, neighbor, oppositeDir);

                    if (!edgeMatch)
                    {
                        allFilledMatched = false;
                    }
                    filledCount++;
                }
                else if (previewSlot != null && heldTile != null && previewSlot.GridPos == nPos)
                {
                    // Ô kề i chính là previewTile đang cầm trên tay! Tính góc xoay hiện tại heldTile.RotationIndex
                    int oppositeDir = GetOppositeNeighborDir(centerPos, nPos, i);
                    bool edgeMatch = CheckEdgeMatchWithRot(heldTile, heldTile.RotationIndex, oppositeDir, centerTile, i);

                    if (!edgeMatch)
                    {
                        allFilledMatched = false;
                    }
                    filledCount++;
                }
            }

            if (allFilledMatched && filledCount > 0)
            {
                if (filledCount == 6) return MatchStatus.SixMatch;
                if (filledCount == 5) return MatchStatus.FiveMatch;
                if (filledCount == 4) return MatchStatus.FourMatch;
            }
            else if (filledCount > 0)
            {
                return MatchStatus.BlackMatch;
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

        private static TileSlot GetCurrentPreviewSlot()
        {
            if (OverwritingSingleton<IngameUi>.Instance != null && OverwritingSingleton<IngameUi>.Instance.tilePlacer != null)
            {
                return OverwritingSingleton<IngameUi>.Instance.tilePlacer.CurrentTileSlot;
            }
            return null;
        }

        private static bool CalculateSlotMaxState(TileSlot slot, Tile heldTile, World world, out SlotState bestState)
        {
            bestState = new SlotState(0, 1);
            if (slot == null || heldTile == null || world == null) return false;

            Vector2Int slotPos = slot.GridPos;
            Vector2Int[] neighborPositions = GetNeighborPositions(slotPos);

            int filledNeighbors = 0;
            for (int i = 0; i < 6; i++)
            {
                if (world.GetTile(neighborPositions[i]) != null)
                    filledNeighbors++;
            }

            if (filledNeighbors == 0) return false;

            int maxMatch = -1;

            // Duyệt qua cả 6 góc xoay để lấy MAX MATCH cố định cho heldTile
            for (int rot = 0; rot < 6; rot++)
            {
                int matchCount = 0;

                for (int i = 0; i < 6; i++)
                {
                    Tile neighbor = world.GetTile(neighborPositions[i]);
                    if (neighbor != null)
                    {
                        int oppositeDir = GetOppositeNeighborDir(slotPos, neighbor.GridPos, i);
                        if (CheckEdgeMatchWithRot(heldTile, rot, i, neighbor, oppositeDir))
                        {
                            matchCount++;
                        }
                    }
                }

                if (matchCount > maxMatch)
                {
                    maxMatch = matchCount;
                }
            }

            bestState = new SlotState(maxMatch, filledNeighbors);
            return true;
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
                    if (kvp.Value != null) UnityEngine.Object.Destroy(kvp.Value);
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
                    targetColor = new Color(0.0f, 1.0f, 0.2f, 0.85f);
                }
                else if (status == MatchStatus.FiveMatch)
                {
                    targetColor = new Color(1.0f, 0.9f, 0.0f, 0.85f);
                }
                else if (status == MatchStatus.FourMatch)
                {
                    targetColor = new Color(1.0f, 0.1f, 0.1f, 0.85f);
                }
                else
                {
                    targetColor = new Color(0.0f, 0.0f, 0.0f, 0.85f);
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

        private static void UpdateSlotMarkers(Dictionary<TileSlot, Color> slotColors, HashSet<TileSlot> impossibleSlots = null)
        {
            HashSet<TileSlot> allActiveSlots = new HashSet<TileSlot>(slotColors.Keys);
            if (impossibleSlots != null)
            {
                foreach (TileSlot s in impossibleSlots)
                {
                    if (s != null) allActiveSlots.Add(s);
                }
            }

            List<TileSlot> toRemove = new List<TileSlot>();
            foreach (var kvp in activeSlotMarkers)
            {
                if (kvp.Key == null || !allActiveSlots.Contains(kvp.Key))
                {
                    if (kvp.Value != null) UnityEngine.Object.Destroy(kvp.Value);
                    toRemove.Add(kvp.Key);
                }
            }
            foreach (TileSlot s in toRemove) activeSlotMarkers.Remove(s);

            foreach (TileSlot slot in allActiveSlots)
            {
                if (slot == null) continue;

                Color targetColor;
                // Nếu slot bị bất khả thi -> Ưu tiên hiện màu XÁM
                if (impossibleSlots != null && impossibleSlots.Contains(slot))
                {
                    targetColor = ImpossibleGrayColor;
                }
                // Nếu slot không bị bất khả thi -> Trả về đúng MÀU GỐC HÀM MAX BỊ ĐÓNG BĂNG TRONG CACHE của nó
                else if (slotColors.TryGetValue(slot, out Color baseColor))
                {
                    targetColor = baseColor;
                }
                else
                {
                    targetColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);
                }

                if (!activeSlotMarkers.ContainsKey(slot) || activeSlotMarkers[slot] == null)
                {
                    GameObject marker = CreateMarkerObject("SlotStateMarker", slot.transform.position + new Vector3(0f, 0.25f, 0f), targetColor, new Vector3(0.325f, 0.04f, 0.325f));
                    activeSlotMarkers[slot] = marker;
                }
                else
                {
                    Renderer r = activeSlotMarkers[slot].GetComponent<Renderer>();
                    if (r != null && r.material != null)
                    {
                        r.material.color = targetColor;
                    }
                }
            }
        }

        private static string GetGroupTypeLetter(GroupType groupType)
        {
            if (groupType == null) return "";
            try
            {
                string idStr = groupType.id.ToString();
                switch (idStr)
                {
                    case "Village": return "V";
                    case "Forest": return "F";
                    case "Agriculture": return "A";
                    case "TrainTracks": return "T";
                    case "Water": return "W";
                    default:
                        if (!string.IsNullOrEmpty(groupType.name))
                            return groupType.name.Substring(0, 1).ToUpper();
                        return idStr.Length > 0 ? idStr.Substring(0, 1).ToUpper() : "";
                }
            }
            catch
            {
                return "";
            }
        }

        private static string GetSegmentShapeCode(SegmentType segmentType)
        {
            if (segmentType == null) return "";
            try
            {
                string idName = segmentType.id.ToString();
                if (!string.IsNullOrEmpty(idName) && idName.StartsWith("SegmentType"))
                {
                    string code = idName.Substring("SegmentType".Length);
                    if (!string.IsNullOrEmpty(code)) return code;
                }

                string name = segmentType.name ?? "";
                if (name.Length >= 2)
                {
                    string tail = name.Substring(name.Length - 2, 2);
                    if (char.IsDigit(tail[0]) && char.IsLetter(tail[1]))
                    {
                        return tail.ToUpper();
                    }
                }

                int count = segmentType.edges != null ? segmentType.edges.Count : 0;
                if (count > 0) return count.ToString() + "A";
            }
            catch {}
            return "";
        }

        private static string GetTilePresetString(Tile tile)
        {
            if (tile == null || tile.AllElementGroupSegments == null || tile.AllElementGroupSegments.Count == 0)
            {
                return "";
            }

            try
            {
                List<string> parts = new List<string>();
                var validSegments = tile.AllElementGroupSegments
                    .Where(s => s != null && s.SegmentType != null && s.GroupType != null)
                    .OrderByDescending(s => s.SegmentType.edges != null ? s.SegmentType.edges.Count : 0)
                    .ThenBy(s => GetGroupTypeLetter(s.GroupType));

                foreach (var seg in validSegments)
                {
                    string shapeCode = GetSegmentShapeCode(seg.SegmentType);
                    string typeLetter = GetGroupTypeLetter(seg.GroupType);
                    if (!string.IsNullOrEmpty(shapeCode) && !string.IsNullOrEmpty(typeLetter))
                    {
                        parts.Add(shapeCode + typeLetter);
                    }
                }

                return parts.Count > 0 ? string.Join(" ", parts) : "";
            }
            catch
            {
                return "";
            }
        }

        private static void CreateOrUpdatePresetText(Tile tile)
        {
            if (tile == null) return;
            string presetTextStr = GetTilePresetString(tile);
            if (string.IsNullOrEmpty(presetTextStr))
            {
                if (activeTilePresetTexts.TryGetValue(tile, out GameObject oldObj))
                {
                    if (oldObj != null) UnityEngine.Object.Destroy(oldObj);
                    activeTilePresetTexts.Remove(tile);
                }
                return;
            }

            Vector3 textPos = tile.transform.position + new Vector3(0f, 0.50f, 0f);

            Camera mainCam = Camera.main;
            Quaternion targetRotation;
            if (mainCam != null)
            {
                targetRotation = Quaternion.Euler(90f, mainCam.transform.eulerAngles.y, 0f);
            }
            else
            {
                targetRotation = Quaternion.Euler(90f, 0f, 0f);
            }

            if (!activeTilePresetTexts.TryGetValue(tile, out GameObject textObj) || textObj == null)
            {
                textObj = DynamicTextHelper.CreateTextObject("TilePresetText", textPos, targetRotation, presetTextStr);
                activeTilePresetTexts[tile] = textObj;
            }
            else
            {
                DynamicTextHelper.UpdateTextObject(textObj, textPos, targetRotation, presetTextStr);
            }
        }

        private static void UpdateCurrentHeldTilePresetTextOnly()
        {
            Tile heldTile = GetCurrentHeldTile();

            List<Tile> toRemove = new List<Tile>();
            foreach (var kvp in activeTilePresetTexts)
            {
                if (kvp.Key == null || kvp.Key != heldTile)
                {
                    if (kvp.Value != null) UnityEngine.Object.Destroy(kvp.Value);
                    toRemove.Add(kvp.Key);
                }
            }
            foreach (Tile t in toRemove) activeTilePresetTexts.Remove(t);

            if (heldTile != null)
            {
                CreateOrUpdatePresetText(heldTile);
            }
        }

        private void LateUpdate()
        {
            UpdateCurrentHeldTilePresetTextOnly();
        }

        public static void ScanPlacedTilesOnly()
        {
            World world = UnityEngine.Object.FindObjectOfType<World>();
            if (world == null) return;

            List<Tile> allPlacedTiles = world.GetAllPlacedTiles();
            if (allPlacedTiles == null) return;

            Tile heldTile = GetCurrentHeldTile();
            TileSlot previewSlot = GetCurrentPreviewSlot();

            Dictionary<Tile, MatchStatus> tileStatuses = new Dictionary<Tile, MatchStatus>();
            foreach (Tile centerTile in allPlacedTiles)
            {
                if (centerTile == null) continue;
                MatchStatus status = GetTileMatchStatus(centerTile, world, previewSlot, heldTile);
                if (status != MatchStatus.None)
                {
                    tileStatuses[centerTile] = status;
                }
            }
            UpdateTileMarkers(tileStatuses);
            UpdateCurrentHeldTilePresetTextOnly();
        }

        public static void ScanSlotsOnly()
        {
            World world = UnityEngine.Object.FindObjectOfType<World>();
            if (world == null) return;

            HashSet<TileSlot> currentImpossibleSlots = new HashSet<TileSlot>();

            Tile heldTile = GetCurrentHeldTile();
            TileSlot previewSlot = GetCurrentPreviewSlot();
            TileSlotPreviewer slotPreviewer = UnityEngine.Object.FindObjectOfType<TileSlotPreviewer>();

            if (slotPreviewer != null)
            {
                // 1. Cập nhật cache ô bất khả thi tĩnh (khi chốt đặt ô vĩnh viễn)
                if (isStaticImpossibleCacheDirty)
                {
                    cachedStaticImpossibleSlots.Clear();
                    List<TileSlot> allSlots = slotPreviewer.AllTileSlots;
                    if (allSlots != null)
                    {
                        foreach (TileSlot slot in allSlots)
                        {
                            if (slot == null) continue;

                            Vector2Int[] neighborPositions = GetNeighborPositions(slot.GridPos);
                            int filledNeighbors = 0;
                            for (int i = 0; i < 6; i++)
                            {
                                if (world.GetTile(neighborPositions[i]) != null)
                                    filledNeighbors++;
                            }

                            if (filledNeighbors >= 2)
                            {
                                if (!CanSlotAchievePerfectMatchFast(slot, world, null, null))
                                {
                                    cachedStaticImpossibleSlots.Add(slot);
                                }
                            }
                        }
                    }
                    isStaticImpossibleCacheDirty = false;
                }

                // 2. Thêm tất cả các ô tĩnh bất khả thi vào danh sách Xám hiện tại
                foreach (TileSlot s in cachedStaticImpossibleSlots)
                {
                    if (s != null) currentImpossibleSlots.Add(s);
                }

                // 3. ĐÓNG BĂNG TUYỆT ĐỐI MÀU GỐC HÀM MAX CHO TILE ĐANG CẦM (Chỉ tính lại khi đổi Tile hoặc đặt Tile)
                if (heldTile != lastHeldTileInstance || isBaseColorCacheDirty)
                {
                    cachedBaseSlotColors.Clear();
                    List<TileSlot> slotsToCalc = slotPreviewer.AllTileSlots;
                    if (slotsToCalc != null)
                    {
                        foreach (TileSlot slot in slotsToCalc)
                        {
                            if (slot == null) continue;
                            if (heldTile != null)
                            {
                                if (CalculateSlotMaxState(slot, heldTile, world, out SlotState state))
                                {
                                    cachedBaseSlotColors[slot] = GetSlotColor(state.match, state.total);
                                }
                            }
                        }
                    }
                    lastHeldTileInstance = heldTile;
                    isBaseColorCacheDirty = false;
                }

                // 4. Kiểm tra XÁM ĐỘNG ở CÁC Ô TRỐNG KỀ NẰM BÊN CẠNH TILE ĐANG ĐẶT XOAY
                List<TileSlot> allSlotsEval = slotPreviewer.AllTileSlots;
                if (allSlotsEval != null)
                {
                    foreach (TileSlot slot in allSlotsEval)
                    {
                        if (slot == null) continue;

                        // Nếu chưa bị xám tĩnh, kiểm tra các ô trống kề xem việc xoay previewTile ở vị trí hiện tại có làm ô trống kề bị XÁM ĐỘNG không
                        if (!currentImpossibleSlots.Contains(slot))
                        {
                            if (!CanSlotAchievePerfectMatchFast(slot, world, previewSlot, heldTile))
                            {
                                currentImpossibleSlots.Add(slot);
                            }
                        }
                    }
                }
            }

            UpdateSlotMarkers(cachedBaseSlotColors, currentImpossibleSlots);
        }

        public static void RunFullScan()
        {
            ScanPlacedTilesOnly();
            ScanSlotsOnly();
        }

        [HarmonyPatch(typeof(TileSlotPreviewer), "UpdateTileSlotValidity")]
        [HarmonyPostfix]
        private static void Postfix_UpdateTileSlotValidity()
        {
            RunFullScan();
        }

        [HarmonyPatch(typeof(Dorfromantik.TilePlacementEventBroadcaster), "BroadcastTilePlacedFinalized")]
        [HarmonyPostfix]
        private static void Postfix_BroadcastTilePlacedFinalized()
        {
            InvalidateCache();
            RunFullScan();
        }

        [HarmonyPatch(typeof(TilePlacer), "RotatePreviewTile", new System.Type[] { typeof(int), typeof(bool) })]
        [HarmonyPostfix]
        private static void Postfix_RotatePreviewTile()
        {
            RunFullScan();
        }

        [HarmonyPatch(typeof(TilePlacer), "ShowPreviewTileAt")]
        [HarmonyPostfix]
        private static void Postfix_ShowPreviewTileAt()
        {
            RunFullScan();
        }
    }

    internal static class DynamicTextHelper
    {
        private static Type tmpType;
        private static Type textMeshType;
        private static bool searched = false;

        private static readonly Color GoldColor = new Color(1.0f, 0.843f, 0.0f, 1.0f);

        private static void InitTypes()
        {
            if (searched) return;
            searched = true;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (tmpType == null) tmpType = asm.GetType("TMPro.TextMeshPro");
                if (textMeshType == null) textMeshType = asm.GetType("UnityEngine.TextMesh");
            }
        }

        public static GameObject CreateTextObject(string name, Vector3 pos, Quaternion rotation, string textString)
        {
            InitTypes();
            GameObject textObj = new GameObject(name);
            textObj.transform.position = pos;
            textObj.transform.rotation = rotation;

            AddTextComp(textObj, textString);
            return textObj;
        }

        public static void UpdateTextObject(GameObject textObj, Vector3 pos, Quaternion rotation, string textString)
        {
            if (textObj == null) return;
            textObj.transform.position = pos;
            textObj.transform.rotation = rotation;

            UpdateTextComp(textObj, textString);
        }

        private static Component AddTextComp(GameObject go, string text)
        {
            if (tmpType != null)
            {
                Component tmp = go.AddComponent(tmpType);
                SetProp(tmp, "text", text);
                SetProp(tmp, "fontSize", 4.25f);
                SetProp(tmp, "color", GoldColor);
                SetPropEnum(tmp, "alignment", "Center");
                SetPropEnum(tmp, "fontStyle", "Bold");
                return tmp;
            }
            else if (textMeshType != null)
            {
                Component tm = go.AddComponent(textMeshType);
                SetProp(tm, "text", text);
                SetProp(tm, "fontSize", 180);
                SetProp(tm, "characterSize", 0.025f);
                SetProp(tm, "color", GoldColor);
                SetPropEnum(tm, "alignment", "Center");
                SetPropEnum(tm, "anchor", "MiddleCenter");
                SetPropEnum(tm, "fontStyle", "Bold");
                return tm;
            }
            return null;
        }

        private static void UpdateTextComp(GameObject go, string text)
        {
            if (go == null) return;
            Component comp = (tmpType != null ? go.GetComponent(tmpType) : null)
                          ?? (textMeshType != null ? go.GetComponent(textMeshType) : null);
            if (comp != null)
            {
                SetProp(comp, "text", text);
                SetProp(comp, "color", GoldColor);
            }
        }

        private static void SetProp(object target, string propName, object val)
        {
            if (target == null) return;
            try
            {
                var prop = target.GetType().GetProperty(propName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(target, Convert.ChangeType(val, prop.PropertyType), null);
                }
            }
            catch {}
        }

        private static void SetPropEnum(object target, string propName, string enumString)
        {
            if (target == null) return;
            try
            {
                var prop = target.GetType().GetProperty(propName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (prop != null && prop.CanWrite && prop.PropertyType.IsEnum)
                {
                    object enumVal = Enum.Parse(prop.PropertyType, enumString);
                    prop.SetValue(target, enumVal, null);
                }
            }
            catch {}
        }
    }
}
