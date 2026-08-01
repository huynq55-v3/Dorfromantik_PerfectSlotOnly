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
        private const string modVersion = "10.1.0";

        private readonly Harmony harmony = new Harmony(modGUID);
        private static BepInEx.Logging.ManualLogSource Log;

        private static readonly Dictionary<Tile, GameObject> activeTileMarkers = new Dictionary<Tile, GameObject>();
        private static readonly HashSet<Tile> currentlyHighlightedTiles = new HashSet<Tile>();
        private static readonly Dictionary<Tile, GameObject> activeTilePresetTexts = new Dictionary<Tile, GameObject>();

        private static readonly Dictionary<TileSlot, GameObject> activeSlotMarkers = new Dictionary<TileSlot, GameObject>();

        // Thêm trạng thái BlackMatch cho các ô đã đặt bị hủy khả năng Perfect
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

        private static readonly Dictionary<KeyValuePair<int, int>, Color> slotColorMap = new Dictionary<KeyValuePair<int, int>, Color>();

        private void Awake()
        {
            Log = Logger;
            Initialize27DistinctRandomColors();
            harmony.PatchAll(typeof(PerfectTriggerSlotBase));
            Log.LogWarning("=================================================");
            Log.LogWarning($"[PerfectTriggerSlot] v{modVersion} ACTIVE!");
            Log.LogWarning(" - Placed Tiles: Red (4/4), Yellow (5/5), Green (6/6), Black (Imperfect)");
            Log.LogWarning(" - Unplaced Slots: 27 Unique High-Contrast Deterministic Colors");
            Log.LogWarning("=================================================");
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
                if (filledCount == 6) return MatchStatus.SixMatch;  // 6/6
                if (filledCount == 5) return MatchStatus.FiveMatch; // 5/5
                if (filledCount == 4) return MatchStatus.FourMatch; // 4/4
            }
            else if (filledCount > 0)
            {
                // Có ít nhất 1 hàng xóm nhưng có cạnh không trùng khớp -> không thể đạt Perfect
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

        private static bool CalculateSlotMaxState(TileSlot slot, Tile heldTile, World world, out SlotState bestState)
        {
            bestState = new SlotState(0, 1);
            if (slot == null || heldTile == null || world == null || !slot.IsValid) return false;

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
                    targetColor = new Color(0.0f, 1.0f, 0.2f, 0.85f);  // LIME GREEN (6/6 Placed)
                }
                else if (status == MatchStatus.FiveMatch)
                {
                    targetColor = new Color(1.0f, 0.9f, 0.0f, 0.85f);  // YELLOW (5/5 Placed)
                }
                else if (status == MatchStatus.FourMatch)
                {
                    targetColor = new Color(1.0f, 0.1f, 0.1f, 0.85f);  // RED (4/4 Placed)
                }
                else
                {
                    targetColor = new Color(0.0f, 0.0f, 0.0f, 0.85f);  // BLACK (Cạnh lệch, hỏng Perfect)
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

        private static void UpdateSlotMarkers(Dictionary<TileSlot, SlotState> slotStates)
        {
            List<TileSlot> toRemove = new List<TileSlot>();
            foreach (var kvp in activeSlotMarkers)
            {
                if (kvp.Key == null || !slotStates.ContainsKey(kvp.Key))
                {
                    if (kvp.Value != null) UnityEngine.Object.Destroy(kvp.Value);
                    toRemove.Add(kvp.Key);
                }
            }
            foreach (TileSlot s in toRemove) activeSlotMarkers.Remove(s);

            foreach (var kvp in slotStates)
            {
                TileSlot slot = kvp.Key;
                SlotState state = kvp.Value;
                if (slot == null) continue;

                Color targetColor = GetSlotColor(state.match, state.total);

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

            Vector3 textPos = tile.transform.position + new Vector3(0f, 0.42f, 0f);

            if (!activeTilePresetTexts.TryGetValue(tile, out GameObject textObj) || textObj == null)
            {
                textObj = DynamicTextHelper.CreateTextObject("TilePresetText", textPos, presetTextStr);
                activeTilePresetTexts[tile] = textObj;
            }
            else
            {
                DynamicTextHelper.UpdateTextObject(textObj, textPos, presetTextStr);
            }
        }

        private static void UpdateAllTilePresetTexts(List<Tile> allPlacedTiles)
        {
            HashSet<Tile> activeTiles = new HashSet<Tile>();
            if (allPlacedTiles != null)
            {
                foreach (Tile t in allPlacedTiles)
                {
                    if (t != null) activeTiles.Add(t);
                }
            }
            Tile heldTile = GetCurrentHeldTile();
            if (heldTile != null)
            {
                activeTiles.Add(heldTile);
            }

            List<Tile> toRemove = new List<Tile>();
            foreach (var kvp in activeTilePresetTexts)
            {
                if (kvp.Key == null || !activeTiles.Contains(kvp.Key))
                {
                    if (kvp.Value != null) UnityEngine.Object.Destroy(kvp.Value);
                    toRemove.Add(kvp.Key);
                }
            }
            foreach (Tile t in toRemove) activeTilePresetTexts.Remove(t);

            foreach (Tile tile in activeTiles)
            {
                CreateOrUpdatePresetText(tile);
            }
        }

        private void LateUpdate()
        {
            Tile heldTile = GetCurrentHeldTile();
            if (heldTile != null)
            {
                CreateOrUpdatePresetText(heldTile);
            }
        }

        public static void ScanPlacedTilesOnly()
        {
            World world = UnityEngine.Object.FindObjectOfType<World>();
            if (world == null) return;

            List<Tile> allPlacedTiles = world.GetAllPlacedTiles();
            if (allPlacedTiles == null) return;

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
            UpdateAllTilePresetTexts(allPlacedTiles);
        }

        public static void ScanSlotsOnly()
        {
            World world = UnityEngine.Object.FindObjectOfType<World>();
            if (world == null) return;

            Dictionary<TileSlot, SlotState> slotStates = new Dictionary<TileSlot, SlotState>();
            Tile heldTile = GetCurrentHeldTile();

            if (heldTile != null)
            {
                TileSlotPreviewer slotPreviewer = UnityEngine.Object.FindObjectOfType<TileSlotPreviewer>();
                if (slotPreviewer != null)
                {
                    List<TileSlot> validSlots = slotPreviewer.AllValidTileSlots;
                    if (validSlots != null)
                    {
                        foreach (TileSlot slot in validSlots)
                        {
                            if (CalculateSlotMaxState(slot, heldTile, world, out SlotState state))
                            {
                                slotStates[slot] = state;
                            }
                        }
                    }
                }
            }
            UpdateSlotMarkers(slotStates);
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
            RunFullScan();
        }

        [HarmonyPatch(typeof(TilePlacer), "RotatePreviewTile", new System.Type[] { typeof(int), typeof(bool) })]
        [HarmonyPostfix]
        private static void Postfix_RotatePreviewTile()
        {
            ScanPlacedTilesOnly();
        }

        [HarmonyPatch(typeof(TilePlacer), "ShowPreviewTileAt")]
        [HarmonyPostfix]
        private static void Postfix_ShowPreviewTileAt()
        {
            ScanPlacedTilesOnly();
        }
    }

    internal static class DynamicTextHelper
    {
        private static Type tmpType;
        private static Type textMeshType;
        private static bool searched = false;

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

        public static GameObject CreateTextObject(string name, Vector3 pos, string textString)
        {
            InitTypes();
            GameObject textObj = new GameObject(name);
            textObj.transform.position = pos;
            textObj.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            AddTextComp(textObj, textString, Color.white);
            return textObj;
        }

        public static void UpdateTextObject(GameObject textObj, Vector3 pos, string textString)
        {
            if (textObj == null) return;
            textObj.transform.position = pos;
            textObj.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            UpdateTextComp(textObj, textString);
        }

        private static Component AddTextComp(GameObject go, string text, Color color)
        {
            if (tmpType != null)
            {
                Component tmp = go.AddComponent(tmpType);
                SetProp(tmp, "text", text);
                SetProp(tmp, "fontSize", 0.85f);
                SetProp(tmp, "color", Color.white);
                SetPropEnum(tmp, "alignment", "Center");
                SetPropEnum(tmp, "fontStyle", "Bold");
                return tmp;
            }
            else if (textMeshType != null)
            {
                Component tm = go.AddComponent(textMeshType);
                SetProp(tm, "text", text);
                SetProp(tm, "fontSize", 36);
                SetProp(tm, "characterSize", 0.005f);
                SetProp(tm, "color", Color.white);
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
