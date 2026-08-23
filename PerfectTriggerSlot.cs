using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        private const string modName = "Perfect Trigger Slot Highlighter & Deck Match Counter";
        private const string modVersion = "20.0.0";

        private readonly Harmony harmony = new Harmony(modGUID);
        private static BepInEx.Logging.ManualLogSource Log;

        // Quản lý hiển thị Tile đã đặt (Highlight đỏ/vàng/xanh/đen)
        private static readonly Dictionary<Tile, GameObject> activeTileMarkers = new Dictionary<Tile, GameObject>();
        private static readonly HashSet<Tile> currentlyHighlightedTiles = new HashSet<Tile>();
        private static readonly Dictionary<Tile, GameObject> activeTilePresetTexts = new Dictionary<Tile, GameObject>();
        private static int currentPerfectTileCount = 0;

        // Quản lý hiển thị Ô trống (Marker 27 màu + Số đếm Tile khớp từ List)
        private static readonly Dictionary<TileSlot, GameObject> activeSlotMarkers = new Dictionary<TileSlot, GameObject>();
        private static readonly Dictionary<TileSlot, GameObject> activeSlotCountTexts = new Dictionary<TileSlot, GameObject>();

        // Cache 27 màu gốc (Hàm Max) cho Tile đang cầm trên tay
        private static readonly Dictionary<TileSlot, Color> cachedBaseSlotColors = new Dictionary<TileSlot, Color>();
        private static Tile lastHeldTileInstance = null;
        private static bool isBaseColorCacheDirty = true;

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

        // Bảng tra màu 27 trạng thái
        private static readonly Dictionary<KeyValuePair<int, int>, Color> slotColorMap = new Dictionary<KeyValuePair<int, int>, Color>();

        // Field reflection cho Undo vô hạn
        private static readonly FieldInfo maxUndoTurnsField = typeof(UndoTracker).GetField("maxUndoTurns", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        // =========================================================================
        // DANH SÁCH HARDCODE CÁC TILE CỦA BẠN
        // =========================================================================
        private static readonly string[] hardcodedTileList = new string[]
        {
            "3AA 1AV", "2CW", "2CW", "2CW", "2AA", "2BW 3AF 1AF", "4AW 2AF", "3AW 3AF", "4AA 2AF", "4AW 2AF",
            "6AW", "2CW 2AF 2AA", "4AW 2AF", "6AV", "4AW 2AF", "1AF", "3AF", "6AF", "4AF", "2AA",
            "3AW 3AF", "2BW 3AF 1AF", "6AW", "2CW 2AF 1AA", "6AW", "2CW 2AF 2AA", "6AW", "2CW", "6AW", "2CW 2AV 1AV",
            "6AW", "4AA 2AF", "6AW", "3AW 3AF", "4BA 1AF 1AF", "6AA", "2BW 3AF 1AF", "2CW 2AF 2AA", "4AW 2AF", "2CW 2AF 2AA",
            "6AW", "5AV 1AF", "3AW 3AF", "6AW", "6AW", "4AW 2AF", "2CW 2AF 2AA", "6AW", "3AW 3AF", "4AW 2AF",
            "2CW", "4AW 2AF", "3AW 3AF", "4AW 2AF", "2CW 2AF 1AA", "2BW 3AF 1AF", "2BW 3AF 1AF", "6AA", "4AF", "2CW 2AF 2AA",
            "2CW 2AF 2AA", "1AF 2AW", "6AW", "2AA 4AF", "6AW", "6AA", "4AA 2AF", "6AA", "6AW", "3AV 3AF",
            "3AW 3AF", "4AA 2AF", "3AW 3AF", "2CW 2AF 2AA", "3AW 3AF", "2CW 2AF 1AA", "4AF", "2BW 3AF 1AF", "3AW 3AF", "3AW 3AF",
            "2AA 4AF", "2CW", "2CW 2AV 1AV", "3AW 3AF", "6AF", "6AF", "6AW", "2CW 2AV 1AV", "2BW 3AF 1AF", "6AW",
            "6AW", "2CW 2AF 2AA", "3AW 3AF", "3AW 3AF", "3AW 3AF", "6AF", "2CW 2AV 1AV", "2CW 2AF 2AA", "5AV 1AF", "6AV",
            "4BF", "2BW", "2BW", "4AW", "2AW 2AW", "6AW", "3BW 1AF", "2BW 2AF", "4AW 2AF", "1AV 1AF",
            "3BA", "2AW", "2AF 1AV 1AA", "2BW", "4AW", "2BW 2AA 1AA", "3CW 2AF 1AV", "2BW 1AV 1AF", "6AW", "2AV 2AF 1AA",
            "2AW 1AF 1AV 1AV", "3AW 2BA", "5AW", "Plain", "3CW", "2AW 1AV", "3CV", "3CA 1AV", "2AW", "3CW 2AF",
            "2BF 2AW", "4AW 2AV", "3BW 1AV", "1AV 1AV 1AF 1AA", "2CW", "4BA 1AF", "2CW 1AV", "1AA 1AF 1AA", "3BF 1AV", "1AA 1AF",
            "3BW 1AV", "1AF 1AV", "3AV 3AW", "2AW", "4CV", "2CW 1AF", "3CV", "2AF 1AA", "2BW 2AV", "6AW",
            "2CW 2AV", "1AV", "4CW 1AV 1AA", "2BW", "4CV", "3BW", "3AW 1AF", "4BW", "2BW", "3AW 3AF",
            "2AW 2AW", "2BW 2AA", "2BW", "4BW", "2BW 2AF", "4BW", "2BV 2AW 1AF 1AF", "4BF 1AV 1AA", "3AW 1AF 1AA", "2AW",
            "3CF", "4CF", "2CW 2AF 2AA", "3CV 1AA", "2AW 1AV", "3CW 1AA 1AF", "3DW", "2AW 2AW", "3CW 2AF", "2AV",
            "4AV", "3DW", "1AV 1AF", "2BW 1AV", "4BV 1AA", "3AW 1AF", "3BV 1AA 1AA", "3AW 1AA", "1AV 1AF 1AF", "3DA 1AF 1AF",
            "2AW", "3CW 2AF", "1AA 1AF 1AV 1AA", "3DW", "2AF 2AW 2AA", "4BW", "1AF 1AV", "3BF 1AV", "4AW 2AA", "2CW 2AV"
        };

        // Danh sách tile sau khi giải mã ra 6 cạnh
        private struct CandidateTile
        {
            public byte[] edges; // 6 cạnh (0..5)
        }
        private static readonly List<CandidateTile> parsedTileDeck = new List<CandidateTile>();

        private void Awake()
        {
            Log = Logger;
            Initialize27DistinctRandomColors();
            ParseHardcodedDeck();
            harmony.PatchAll(typeof(PerfectTriggerSlotBase));
            Log.LogWarning("=================================================");
            Log.LogWarning($"[PerfectTriggerSlot] v{modVersion} ACTIVE!");
            Log.LogWarning($" - Deck Parsed: {parsedTileDeck.Count} tiles loaded for Match Counting");
            Log.LogWarning(" - Unplaced Slots: 27 Base Colors FROZEN + Real-time Deck Match Count Numbers");
            Log.LogWarning(" - Placed Tiles: Dynamic Red/Yellow/Green/Black updating on Preview Rotation");
            Log.LogWarning(" - Infinite Undo: ACTIVE 🔄♾️");
            Log.LogWarning("=================================================");
        }

        // =========================================================================
        // PARSER CHUỖI TILE SANG 6 CẠNH LỤC GIÁC
        // =========================================================================
        private static byte TerrainLetterToId(char letter)
        {
            switch (char.ToUpper(letter))
            {
                case 'V': return 1; // Village
                case 'F': return 2; // Forest
                case 'A': return 3; // Agriculture
                case 'T': return 4; // TrainTracks
                case 'W': return 5; // Water
                default: return 0;  // Plain
            }
        }

        private static int[] GetShapeBaseEdges(string shapeCode)
        {
            switch (shapeCode.ToUpper())
            {
                case "1A": return new int[] { 0 };
                case "2A": return new int[] { 0, 1 };
                case "2B": return new int[] { 0, 2 };
                case "2C": return new int[] { 0, 3 };
                case "3A": return new int[] { 0, 1, 2 };
                case "3B": return new int[] { 0, 1, 3 };
                case "3C": return new int[] { 0, 1, 4 };
                case "3D": return new int[] { 0, 2, 4 };
                case "4A": return new int[] { 0, 1, 2, 3 };
                case "4B": return new int[] { 0, 1, 2, 4 };
                case "4C": return new int[] { 0, 1, 3, 4 };
                case "5A": return new int[] { 0, 1, 2, 3, 4 };
                case "6A": return new int[] { 0, 1, 2, 3, 4, 5 };
                default: return new int[] { 0 };
            }
        }

        private static bool TryAssignSegments(List<KeyValuePair<int[], byte>> segments, int segIdx, byte[] currentEdges, out byte[] finalEdges)
        {
            if (segIdx >= segments.Count)
            {
                finalEdges = (byte[])currentEdges.Clone();
                return true;
            }

            var seg = segments[segIdx];
            for (int rot = 0; rot < 6; rot++)
            {
                bool fits = true;
                for (int i = 0; i < seg.Key.Length; i++)
                {
                    int edgePos = (seg.Key[i] + rot) % 6;
                    if (currentEdges[edgePos] != 0)
                    {
                        fits = false;
                        break;
                    }
                }

                if (fits)
                {
                    byte[] nextEdges = (byte[])currentEdges.Clone();
                    for (int i = 0; i < seg.Key.Length; i++)
                    {
                        int edgePos = (seg.Key[i] + rot) % 6;
                        nextEdges[edgePos] = seg.Value;
                    }

                    if (TryAssignSegments(segments, segIdx + 1, nextEdges, out finalEdges))
                    {
                        return true;
                    }
                }
            }

            finalEdges = null;
            return false;
        }

        private static byte[] ParseTileString(string tileStr)
        {
            byte[] edges = new byte[6];
            if (string.IsNullOrEmpty(tileStr) || tileStr.Trim().Equals("Plain", StringComparison.OrdinalIgnoreCase))
            {
                return edges; // 6 cạnh 0 (Plain)
            }

            string[] parts = tileStr.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            List<KeyValuePair<int[], byte>> segments = new List<KeyValuePair<int[], byte>>();

            foreach (string part in parts)
            {
                if (part.Length < 3) continue;
                string shapeCode = part.Substring(0, part.Length - 1);
                char typeLetter = part[part.Length - 1];
                int[] baseEdges = GetShapeBaseEdges(shapeCode);
                byte terrainId = TerrainLetterToId(typeLetter);
                segments.Add(new KeyValuePair<int[], byte>(baseEdges, terrainId));
            }

            if (segments.Count == 0) return edges;

            if (TryAssignSegments(segments, 0, new byte[6], out byte[] result))
            {
                return result;
            }

            return edges;
        }

        private static void ParseHardcodedDeck()
        {
            parsedTileDeck.Clear();
            foreach (string tileStr in hardcodedTileList)
            {
                byte[] edges = ParseTileString(tileStr);
                parsedTileDeck.Add(new CandidateTile { edges = edges });
            }
        }

        // =========================================================================
        // BỘ ĐẾM MATCH 100% TỪ DANH SÁCH DECK
        // =========================================================================
        private static int CountMatchingTilesFromDeck(TileSlot slot, World world, TileSlot previewSlot = null, Tile heldTile = null)
        {
            if (slot == null || world == null || parsedTileDeck.Count == 0) return 0;

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
                    // Cạnh từ ô tile đã đặt vĩnh viễn
                    int oppDir = GetOppositeNeighborDir(slotPos, neighbor.GridPos, i);
                    ElementGroup elem = GetWorldElementGroup(neighbor, oppDir, null);
                    reqTerrainIds[i] = GetTerrainId(elem?.GroupType);
                    isPlaced[i] = true;
                    placedCount++;
                }
                else if (previewSlot != null && heldTile != null && previewSlot.GridPos == nPos)
                {
                    // Cạnh từ ô previewTile đang cầm xoay ở previewSlot
                    int oppDir = GetOppositeNeighborDir(slotPos, nPos, i);
                    ElementGroup elem = GetWorldElementGroupWithRot(heldTile, oppDir, heldTile.RotationIndex, null);
                    reqTerrainIds[i] = GetTerrainId(elem?.GroupType);
                    isPlaced[i] = true;
                    placedCount++;
                }
            }

            if (placedCount == 0) return parsedTileDeck.Count;

            int matchCount = 0;

            foreach (var cand in parsedTileDeck)
            {
                bool tileCanMatch = false;

                // Thử qua 6 góc xoay của candidate tile
                for (int rot = 0; rot < 6; rot++)
                {
                    bool allEdgesMatch = true;
                    for (int i = 0; i < 6; i++)
                    {
                        if (!isPlaced[i]) continue;
                        byte candEdge = cand.edges[(i - rot + 600) % 6];
                        if (candEdge != reqTerrainIds[i])
                        {
                            allEdgesMatch = false;
                            break;
                        }
                    }

                    if (allEdgesMatch)
                    {
                        tileCanMatch = true;
                        break;
                    }
                }

                if (tileCanMatch)
                {
                    matchCount++;
                }
            }

            return matchCount;
        }

        // =========================================================================
        // UNDO VÔ HẠN
        // =========================================================================
        private static void SetInfiniteUndo(UndoTracker tracker)
        {
            if (tracker == null) return;
            try
            {
                if (maxUndoTurnsField != null)
                {
                    maxUndoTurnsField.SetValue(tracker, -1);
                }
                else
                {
                    Traverse.Create(tracker).Field("maxUndoTurns").SetValue(-1);
                }
            }
            catch (Exception ex)
            {
                if (Log != null) Log.LogError($"Failed to apply infinite undo: {ex.Message}");
            }
        }

        private static Vector2Int[] GetNeighborPositions(Vector2Int gridPos)
        {
            try
            {
                var method = typeof(GridCalculator).GetMethod("GetNeighborGridPositions", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
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
            isBaseColorCacheDirty = true;
        }

        private static byte GetTerrainId(GroupType groupType)
        {
            if (groupType == null) return 0;
            string name = groupType.name ?? groupType.id.ToString();
            if (name.IndexOf("Village", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            if (name.IndexOf("Forest", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            if (name.IndexOf("Agriculture", StringComparison.OrdinalIgnoreCase) >= 0) return 3;
            if (name.IndexOf("Train", StringComparison.OrdinalIgnoreCase) >= 0) return 4;
            if (name.IndexOf("Water", StringComparison.OrdinalIgnoreCase) >= 0) return 5;
            return 0;
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
                var method = typeof(GridCalculator).GetMethod("GetNeighborIndexFromGridPos", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
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

        private static void UpdateSlotMarkersAndCounts(Dictionary<TileSlot, Color> slotColors, Dictionary<TileSlot, int> slotMatchCounts)
        {
            HashSet<TileSlot> allActiveSlots = new HashSet<TileSlot>(slotColors.Keys);

            // Xóa marker thừa
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

            // Xóa text thừa
            List<TileSlot> textToRemove = new List<TileSlot>();
            foreach (var kvp in activeSlotCountTexts)
            {
                if (kvp.Key == null || !allActiveSlots.Contains(kvp.Key))
                {
                    if (kvp.Value != null) UnityEngine.Object.Destroy(kvp.Value);
                    textToRemove.Add(kvp.Key);
                }
            }
            foreach (TileSlot s in textToRemove) activeSlotCountTexts.Remove(s);

            Camera mainCam = Camera.main;
            Quaternion targetRotation = (mainCam != null)
                ? Quaternion.Euler(90f, mainCam.transform.eulerAngles.y, 0f)
                : Quaternion.Euler(90f, 0f, 0f);

            foreach (TileSlot slot in allActiveSlots)
            {
                if (slot == null) continue;

                Color targetColor = slotColors.TryGetValue(slot, out Color baseColor) ? baseColor : new Color(0.5f, 0.5f, 0.5f, 0.5f);
                int count = slotMatchCounts.TryGetValue(slot, out int c) ? c : 0;

                // 1. Cập nhật / tạo hình trụ Marker 27 màu gốc
                Vector3 markerPos = slot.transform.position + new Vector3(0f, 0.25f, 0f);
                if (!activeSlotMarkers.ContainsKey(slot) || activeSlotMarkers[slot] == null)
                {
                    GameObject marker = CreateMarkerObject("SlotStateMarker", markerPos, targetColor, new Vector3(0.325f, 0.04f, 0.325f));
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

                // 2. Cập nhật / tạo Text 3D đếm số tile khớp đặt ngay trên mặt marker
                Vector3 textPos = slot.transform.position + new Vector3(0f, 0.30f, 0f);
                string countStr = count.ToString();

                if (!activeSlotCountTexts.TryGetValue(slot, out GameObject countObj) || countObj == null)
                {
                    countObj = DynamicTextHelper.CreateTextObject("SlotCountText", textPos, targetRotation, countStr, 3.0f, Color.white);
                    activeSlotCountTexts[slot] = countObj;
                }
                else
                {
                    DynamicTextHelper.UpdateTextObject(countObj, textPos, targetRotation, countStr, Color.white);
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

            string fullText = presetTextStr + "\nPerfect: " + currentPerfectTileCount;
            Vector3 textPos = tile.transform.position + new Vector3(0f, 0.50f, 0f);

            Camera mainCam = Camera.main;
            Quaternion targetRotation = (mainCam != null)
                ? Quaternion.Euler(90f, mainCam.transform.eulerAngles.y, 0f)
                : Quaternion.Euler(90f, 0f, 0f);

            if (!activeTilePresetTexts.TryGetValue(tile, out GameObject textObj) || textObj == null)
            {
                textObj = DynamicTextHelper.CreateTextObject("TilePresetText", textPos, targetRotation, fullText, 4.25f, new Color(1.0f, 0.843f, 0.0f, 1.0f));
                activeTilePresetTexts[tile] = textObj;
            }
            else
            {
                DynamicTextHelper.UpdateTextObject(textObj, textPos, targetRotation, fullText, new Color(1.0f, 0.843f, 0.0f, 1.0f));
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

            // Cập nhật hướng xoay của text số đếm ô theo Camera
            Camera mainCam = Camera.main;
            if (mainCam != null)
            {
                Quaternion rot = Quaternion.Euler(90f, mainCam.transform.eulerAngles.y, 0f);
                foreach (var kvp in activeSlotCountTexts)
                {
                    if (kvp.Value != null)
                    {
                        kvp.Value.transform.rotation = rot;
                    }
                }
            }
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
            int perfectCount = 0;
            foreach (Tile centerTile in allPlacedTiles)
            {
                if (centerTile == null) continue;
                MatchStatus status = GetTileMatchStatus(centerTile, world, previewSlot, heldTile);
                if (status != MatchStatus.None)
                {
                    tileStatuses[centerTile] = status;
                    if (status == MatchStatus.SixMatch)
                    {
                        perfectCount++;
                    }
                }
            }
            currentPerfectTileCount = perfectCount;
            UpdateTileMarkers(tileStatuses);
            UpdateCurrentHeldTilePresetTextOnly();
        }

        public static void ScanSlotsOnly()
        {
            World world = UnityEngine.Object.FindObjectOfType<World>();
            if (world == null) return;

            Tile heldTile = GetCurrentHeldTile();
            TileSlot previewSlot = GetCurrentPreviewSlot();
            TileSlotPreviewer slotPreviewer = UnityEngine.Object.FindObjectOfType<TileSlotPreviewer>();

            Dictionary<TileSlot, int> currentSlotMatchCounts = new Dictionary<TileSlot, int>();

            if (slotPreviewer != null)
            {
                // 1. Đóng băng màu 27 trạng thái của ô cho Tile đang cầm
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

                // 2. Tính toán số lượng Tile từ Deck khớp 100% cho TẤT CẢ các ô trống
                List<TileSlot> allSlotsEval = slotPreviewer.AllTileSlots;
                if (allSlotsEval != null)
                {
                    foreach (TileSlot slot in allSlotsEval)
                    {
                        if (slot == null) continue;
                        // Tự động xử lý cả ô tĩnh lẫn ô động quanh previewSlot đang xoay
                        int count = CountMatchingTilesFromDeck(slot, world, previewSlot, heldTile);
                        currentSlotMatchCounts[slot] = count;
                    }
                }
            }

            UpdateSlotMarkersAndCounts(cachedBaseSlotColors, currentSlotMatchCounts);
        }

        public static void RunFullScan()
        {
            ScanPlacedTilesOnly();
            ScanSlotsOnly();
        }

        // ==========================================
        // Harmony Patches
        // ==========================================

        [HarmonyPatch(typeof(UndoTracker), "Awake")]
        [HarmonyPostfix]
        private static void Postfix_UndoTracker_Awake(UndoTracker __instance)
        {
            SetInfiniteUndo(__instance);
        }

        [HarmonyPatch(typeof(UndoTracker), "StoreTurn")]
        [HarmonyPrefix]
        private static void Prefix_UndoTracker_StoreTurn(UndoTracker __instance)
        {
            SetInfiniteUndo(__instance);
        }

        [HarmonyPatch(typeof(TilePlacementEventBroadcaster), "BroadcastTurnUndone")]
        [HarmonyPostfix]
        private static void Postfix_BroadcastTurnUndone()
        {
            InvalidateCache();
            RunFullScan();
        }

        [HarmonyPatch(typeof(TileSlotPreviewer), "UpdateTileSlotValidity")]
        [HarmonyPostfix]
        private static void Postfix_UpdateTileSlotValidity()
        {
            RunFullScan();
        }

        [HarmonyPatch(typeof(TilePlacementEventBroadcaster), "BroadcastTilePlacedFinalized")]
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

        public static GameObject CreateTextObject(string name, Vector3 pos, Quaternion rotation, string textString, float fontSize = 4.25f, Color? textColor = null)
        {
            InitTypes();
            GameObject textObj = new GameObject(name);
            textObj.transform.position = pos;
            textObj.transform.rotation = rotation;

            AddTextComp(textObj, textString, fontSize, textColor ?? Color.white);
            return textObj;
        }

        public static void UpdateTextObject(GameObject textObj, Vector3 pos, Quaternion rotation, string textString, Color? textColor = null)
        {
            if (textObj == null) return;
            textObj.transform.position = pos;
            textObj.transform.rotation = rotation;

            UpdateTextComp(textObj, textString, textColor);
        }

        private static Component AddTextComp(GameObject go, string text, float fontSize, Color color)
        {
            if (tmpType != null)
            {
                Component tmp = go.AddComponent(tmpType);
                SetProp(tmp, "text", text);
                SetProp(tmp, "fontSize", fontSize);
                SetProp(tmp, "color", color);
                SetPropEnum(tmp, "alignment", "Center");
                SetPropEnum(tmp, "fontStyle", "Bold");
                return tmp;
            }
            else if (textMeshType != null)
            {
                Component tm = go.AddComponent(textMeshType);
                SetProp(tm, "text", text);
                SetProp(tm, "fontSize", (int)(fontSize * 40));
                SetProp(tm, "characterSize", 0.02f);
                SetProp(tm, "color", color);
                SetPropEnum(tm, "alignment", "Center");
                SetPropEnum(tm, "anchor", "MiddleCenter");
                SetPropEnum(tm, "fontStyle", "Bold");
                return tm;
            }
            return null;
        }

        private static void UpdateTextComp(GameObject go, string text, Color? color = null)
        {
            if (go == null) return;
            Component comp = (tmpType != null ? go.GetComponent(tmpType) : null)
                          ?? (textMeshType != null ? go.GetComponent(textMeshType) : null);
            if (comp != null)
            {
                SetProp(comp, "text", text);
                if (color.HasValue)
                {
                    SetProp(comp, "color", color.Value);
                }
            }
        }

        private static void SetProp(object target, string propName, object val)
        {
            if (target == null) return;
            try
            {
                var prop = target.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
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
                var prop = target.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
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
