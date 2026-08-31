using System;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using Dorfromantik;

namespace PerfectTriggerSlot
{
    [BepInPlugin(modGUID, modName, modVersion)]
    public class ScoreInjectorBase : BaseUnityPlugin
    {
        private const string modGUID = "JG.PerfectTriggerSlot";
        private const string modName = "Leaderboard Score Injector";
        private const string modVersion = "1.0.0";

        // Mức điểm mục tiêu muốn post lên Leaderboard
        public const int TARGET_SCORE = 88888888;

        private readonly Harmony harmony = new Harmony(modGUID);
        private static BepInEx.Logging.ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            harmony.PatchAll(typeof(ScoreInjectorBase));

            Log.LogWarning("=================================================");
            Log.LogWarning($"[{modName}] v{modVersion} ACTIVE!");
            Log.LogWarning($" - Target Score: {TARGET_SCORE:N0} pts 🎯");
            Log.LogWarning(" - Auto-post on Game Start & on Every Placed Tile");
            Log.LogWarning(" - All Client Validations (Basic + Custom) Bypassed ⚡️");
            Log.LogWarning("=================================================");
        }

        // =========================================================================
        // HÀM POST ĐIỂM TRỰC TIẾP LÊN STEAM LEADERBOARD
        // =========================================================================
        public static void PostArbitraryScore(int scoreToPost)
        {
            try
            {
                var steamLeaderboardManager = UnityEngine.Object.FindObjectOfType<SteamLeaderboardManager>();
                if (steamLeaderboardManager == null)
                {
                    return;
                }

                // 1. Reset cooldown 60s của Steam Leaderboard để cho phép upload ngay tức thì
                var lastUploadField = typeof(SteamLeaderboardManager).GetField("lastUploadTime", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                lastUploadField?.SetValue(steamLeaderboardManager, float.NegativeInfinity);

                // 2. Cập nhật điểm hiển thị trong RewardSystem
                var rsField = typeof(SteamLeaderboardManager).GetField("rewardSystem", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var rewardSystem = rsField?.GetValue(steamLeaderboardManager) as RewardSystem;
                if (rewardSystem != null && rewardSystem.Score != scoreToPost)
                {
                    rewardSystem.ResetScore(scoreToPost);
                }

                // 3. Lấy LeaderboardType của ván đấu hiện tại (Classic / Monthly / Hard / etc.)
                LeaderboardType currentLeaderboard = null;
                if (OverwritingSingleton<GameSession>.Instance != null && OverwritingSingleton<GameSession>.Instance.GameMode != null)
                {
                    currentLeaderboard = OverwritingSingleton<GameSession>.Instance.GameMode.GetLeaderboard();
                }

                if (currentLeaderboard == null)
                {
                    var lmField = typeof(SteamLeaderboardManager).GetField("leaderboardManager", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    var lm = lmField?.GetValue(steamLeaderboardManager) as LeaderboardManager;
                    if (lm != null)
                    {
                        currentLeaderboard = lm.GetCurrentLeaderboard(false);
                    }
                }

                if (currentLeaderboard == null)
                {
                    return;
                }

                string leaderboardId = currentLeaderboard.GetLeaderboardId();
                Log?.LogWarning($"[ScoreInjector] >>> POST ĐIỂM {scoreToPost:N0} LÊN LEADERBOARD `{leaderboardId}` <<<");

                // 4. Kích hoạt method SetHighscore(LeaderboardType, int, bool) của SteamLeaderboardManager
                MethodInfo setHighscoreMethod = typeof(SteamLeaderboardManager).GetMethod(
                    "SetHighscore",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
                );

                if (setHighscoreMethod != null)
                {
                    setHighscoreMethod.Invoke(steamLeaderboardManager, new object[] { currentLeaderboard, scoreToPost, true });
                    Log?.LogWarning($"[ScoreInjector] Gọi SteamLeaderboardManager.SetHighscore({scoreToPost:N0}, forceUpdate: true) thành công! 🚀");
                }
                else
                {
                    Log?.LogError("[ScoreInjector] Không tìm thấy method SetHighscore trên SteamLeaderboardManager!");
                }
            }
            catch (Exception ex)
            {
                Log?.LogError($"[ScoreInjector] Lỗi ngoại lệ khi post điểm: {ex}");
            }
        }

        // =========================================================================
        // HARMONY HOOKS (KÍCH HOẠT TỰ ĐỘNG & BYPASS VALIDATION)
        // =========================================================================

        // Hook 1: Ngay khi load vào ván chơi -> Tự động nạp và gửi điểm
        [HarmonyPatch(typeof(GameSceneInitializer), "Start")]
        [HarmonyPostfix]
        private static void Postfix_GameSceneInitializer_Start()
        {
            Log?.LogWarning($"[ScoreInjector] Ván chơi bắt đầu! Tự động nạp điểm {TARGET_SCORE:N0}...");
            PostArbitraryScore(TARGET_SCORE);
        }

        // Hook 2: Mỗi khi đặt 1 tile bất kỳ -> Tự động gửi lại điểm
        [HarmonyPatch(typeof(TilePlacementEventBroadcaster), "BroadcastTilePlacedFinalized")]
        [HarmonyPostfix]
        private static void Postfix_BroadcastTilePlacedFinalized()
        {
            PostArbitraryScore(TARGET_SCORE);
        }

        // Hook 3: Chặn hàm SetHighscore của game, ép điểm gửi đi luôn là TARGET_SCORE
        [HarmonyPatch(typeof(SteamLeaderboardManager), "SetHighscore")]
        [HarmonyPrefix]
        private static void Prefix_SteamLeaderboardManager_SetHighscore(SteamLeaderboardManager __instance, ref int score, ref bool forceUpdate)
        {
            score = TARGET_SCORE;
            forceUpdate = true;

            var lastUploadField = typeof(SteamLeaderboardManager).GetField("lastUploadTime", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            lastUploadField?.SetValue(__instance, float.NegativeInfinity);

            Log?.LogWarning($"[ScoreInjector] SetHighscore Intercepted -> Ép điểm = {score:N0} & Bypass Cooldown!");
        }

        // Hook 4: Chặn UpdateHighscore của RewardSystem, ép điểm nội bộ = TARGET_SCORE
        [HarmonyPatch(typeof(RewardSystem), "UpdateHighscore")]
        [HarmonyPrefix]
        private static void Prefix_RewardSystem_UpdateHighscore(RewardSystem __instance, ref bool forceUpdate)
        {
            if (__instance.Score != TARGET_SCORE)
            {
                __instance.ResetScore(TARGET_SCORE);
            }
            forceUpdate = true;
        }

        // Hook 5: Bypass kiểm tra trần điểm lý thuyết (Basic Validator)
        [HarmonyPatch(typeof(BasicSteamLeaderboardValidator), "IsScoreValid")]
        [HarmonyPrefix]
        private static bool Prefix_BasicSteamLeaderboardValidator_IsScoreValid(ref bool __result, ref int scorePercentage)
        {
            scorePercentage = 100;
            __result = true;
            return false; // Bỏ qua hàm gốc
        }

        // Hook 6: Bypass kiểm tra luật Custom / Monthly Mode
        [HarmonyPatch(typeof(CustomModeConfiguration), "IsScoreValid")]
        [HarmonyPrefix]
        private static bool Prefix_CustomModeConfiguration_IsScoreValid(ref bool __result)
        {
            __result = true;
            return false; // Bỏ qua hàm gốc
        }
    }
}
