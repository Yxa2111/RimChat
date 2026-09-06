using RimWorld;
using Verse;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using RimChat.Dialogue;
using RimChat.Util;
using RimChat.Memory;
using RimChat.WorldState;
using RimChat.Core;

namespace RimChat.DiplomacySystem
{
    public partial class GameComponent_RPGManager : GameComponent
    {
        public static GameComponent_RPGManager Instance;

        private Dictionary<string, int> pawnDialogueCooldownUntilTickById = new Dictionary<string, int>();
        private List<string> cooldownKeysByIdWorkingList;
        private List<int> cooldownValuesByIdWorkingList;
        private Dictionary<string, int> pawnPairDialogueCooldownUntilTickById = new Dictionary<string, int>();
        private List<string> pairCooldownKeysByIdWorkingList;
        private List<int> pairCooldownValuesByIdWorkingList;

        private Dictionary<string, string> pawnPersonaPromptsById = new Dictionary<string, string>();
        private List<string> pawnPersonaPromptKeysByIdWorkingList;
        private List<string> pawnPersonaPromptValuesByIdWorkingList;

        // Legacy fields are loaded once for migration only (read-only on load).
        // These use LookMode.Reference to consume legacy Pawn-keyed XML nodes from old saves.
        // Pawn keys that resolve to null (destroyed/recycled) are safely skipped in MigrateLegacyPawnDictionaries.
        private Dictionary<Pawn, int> legacyPawnDialogueCooldownUntilTick;
        private List<Pawn> legacyCooldownKeysWorkingList;
        private List<int> legacyCooldownValuesWorkingList;
        private Dictionary<Pawn, string> legacyPawnPersonaPrompts;
        private List<Pawn> legacyPawnPersonaPromptKeysWorkingList;
        private List<string> legacyPawnPersonaPromptValuesWorkingList;
        private readonly HashSet<int> pawnPersonaSyncGuards = new HashSet<int>();
        private string persistentRpgSaveSlotId = string.Empty;

        private const float DefaultExitCooldownHours = 2f;
        private const string PersistentRpgSaveSlotPrefix = "slot";

        public GameComponent_RPGManager(Game game)
        {
            Instance = this;
        }

        public override void StartedNewGame()
        {
            base.StartedNewGame();
            Instance = this;
            SaveContextTracker.Reset();
            RimChatTrackedEntityRegistry.Reset();
            ResetPersistentRpgSaveSlotIdForNewGame();
            RpgNpcDialogueArchiveManager.Instance.OnNewGame();
            MarkNpcPersonaBootstrapAsNewGame();
        }

        public override void LoadedGame()
        {
            base.LoadedGame();
            Instance = this;
            RpgNpcDialogueArchiveManager.Instance.OnLoadedGame();
            ScheduleNpcPersonaBootstrapOnLoad();
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            Instance = this;

            // Check if AI Quest Def is loaded
            var questDef = DefDatabase<QuestScriptDef>.GetNamedSilentFail("RimChat_AIQuest");
            if (questDef == null)
            {
                Log.Warning("[RimChat] Failed to find QuestScriptDef 'RimChat_AIQuest'. AI Quests will not be available.");
            }
            else
            {
                Log.Message("[RimChat] QuestScriptDef 'RimChat_AIQuest' loaded successfully.");
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();

            if (Scribe.mode == LoadSaveMode.Saving)
            {
                EnsurePersistentRpgSaveSlotId();
                RpgNpcDialogueArchiveManager.Instance.OnBeforeGameSave();
            }

            Scribe_Values.Look(ref persistentRpgSaveSlotId, "persistentRpgSaveSlotId", string.Empty);

            Scribe_Collections.Look(
                ref pawnDialogueCooldownUntilTickById,
                "pawnDialogueCooldownUntilTickById",
                LookMode.Value,
                LookMode.Value,
                ref cooldownKeysByIdWorkingList,
                ref cooldownValuesByIdWorkingList);

            Scribe_Collections.Look(
                ref pawnPairDialogueCooldownUntilTickById,
                "pawnPairDialogueCooldownUntilTickById",
                LookMode.Value,
                LookMode.Value,
                ref pairCooldownKeysByIdWorkingList,
                ref pairCooldownValuesByIdWorkingList);

            Scribe_Collections.Look(
                ref pawnPersonaPromptsById,
                "pawnPersonaPromptsById",
                LookMode.Value,
                LookMode.Value,
                ref pawnPersonaPromptKeysByIdWorkingList,
                ref pawnPersonaPromptValuesByIdWorkingList);

            if (Scribe.mode != LoadSaveMode.Saving)
            {
                // Consume legacy Pawn-keyed dictionaries from old saves.
                // LookMode.Reference is required to match the original save format.
                // Pawns that no longer exist resolve to null and are skipped in migration.
                try
                {
                    Scribe_Collections.Look(
                        ref legacyPawnDialogueCooldownUntilTick,
                        "pawnDialogueCooldownUntilTick",
                        LookMode.Reference,
                        LookMode.Value,
                        ref legacyCooldownKeysWorkingList,
                        ref legacyCooldownValuesWorkingList);

                    Scribe_Collections.Look(
                        ref legacyPawnPersonaPrompts,
                        "pawnPersonaPrompts",
                        LookMode.Reference,
                        LookMode.Value,
                        ref legacyPawnPersonaPromptKeysWorkingList,
                        ref legacyPawnPersonaPromptValuesWorkingList);
                }
                catch (Exception ex)
                {
                    Log.Warning($"[RimChat] Failed to load legacy pawn data, clearing for compatibility: {ex.Message}");
                    legacyPawnDialogueCooldownUntilTick = null;
                    legacyPawnPersonaPrompts = null;
                }
            }

            ExposeData_NpcPersonaBootstrap();

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                EnsurePersistentRpgSaveSlotId();
                if (pawnDialogueCooldownUntilTickById == null)
                {
                    pawnDialogueCooldownUntilTickById = new Dictionary<string, int>();
                }

                if (pawnPersonaPromptsById == null)
                {
                    pawnPersonaPromptsById = new Dictionary<string, string>();
                }

                if (pawnPairDialogueCooldownUntilTickById == null)
                {
                    pawnPairDialogueCooldownUntilTickById = new Dictionary<string, int>();
                }

                MigrateLegacyPawnDictionaries();
                int currentTick = Find.TickManager?.TicksGame ?? 0;
                CleanupInvalidRpgDictionaries(currentTick);

                cooldownKeysByIdWorkingList = null;
                cooldownValuesByIdWorkingList = null;
                pairCooldownKeysByIdWorkingList = null;
                pairCooldownValuesByIdWorkingList = null;
                pawnPersonaPromptKeysByIdWorkingList = null;
                pawnPersonaPromptValuesByIdWorkingList = null;
                legacyCooldownKeysWorkingList = null;
                legacyCooldownValuesWorkingList = null;
                legacyPawnPersonaPromptKeysWorkingList = null;
                legacyPawnPersonaPromptValuesWorkingList = null;

                RpgNpcDialogueArchiveManager.Instance.OnAfterGameLoad();
                OnPostLoadInit_NpcPersonaBootstrap();
            }
        }

        public string GetPersistentRpgSaveSlotId()
        {
            EnsurePersistentRpgSaveSlotId();
            return persistentRpgSaveSlotId;
        }

        private void ResetPersistentRpgSaveSlotIdForNewGame()
        {
            persistentRpgSaveSlotId = string.Empty;
            EnsurePersistentRpgSaveSlotId();
        }

        private void EnsurePersistentRpgSaveSlotId()
        {
            if (!string.IsNullOrWhiteSpace(persistentRpgSaveSlotId))
            {
                return;
            }

            persistentRpgSaveSlotId = $"{PersistentRpgSaveSlotPrefix}_{Guid.NewGuid():N}";
        }

        public int GetRpgDialogueExitCooldownTicks()
        {
            return Mathf.RoundToInt(DefaultExitCooldownHours * 2500f);
        }

        public void StartRpgDialogueCooldown(Pawn pawn, int cooldownTicks)
        {
            if (pawn == null || cooldownTicks <= 0)
            {
                return;
            }

            string pawnId = GetPawnStableId(pawn);
            if (string.IsNullOrWhiteSpace(pawnId))
            {
                return;
            }

            int currentTick = Find.TickManager?.TicksGame ?? 0;
            int untilTick = currentTick + cooldownTicks;
            if (pawnDialogueCooldownUntilTickById.TryGetValue(pawnId, out int existing))
            {
                pawnDialogueCooldownUntilTickById[pawnId] = Mathf.Max(existing, untilTick);
                return;
            }

            pawnDialogueCooldownUntilTickById[pawnId] = untilTick;
        }

        public bool IsRpgDialogueOnCooldown(Pawn pawn, out int remainingTicks)
        {
            remainingTicks = 0;
            if (pawn == null || pawnDialogueCooldownUntilTickById == null)
            {
                return false;
            }

            string pawnId = GetPawnStableId(pawn);
            if (string.IsNullOrWhiteSpace(pawnId) ||
                !pawnDialogueCooldownUntilTickById.TryGetValue(pawnId, out int untilTick))
            {
                return false;
            }

            int currentTick = Find.TickManager?.TicksGame ?? 0;
            remainingTicks = untilTick - currentTick;
            if (remainingTicks > 0)
            {
                return true;
            }

            pawnDialogueCooldownUntilTickById.Remove(pawnId);
            remainingTicks = 0;
            return false;
        }

        public int StartRandomRpgDialoguePairCooldown(Pawn first, Pawn second)
        {
            float minHours = Mathf.Clamp(RimChatMod.Settings?.RpgPairCooldownMinHours ?? 12f, 1f, 72f);
            float maxHours = Mathf.Clamp(RimChatMod.Settings?.RpgPairCooldownMaxHours ?? 24f, minHours, 72f);
            int ticks = Mathf.RoundToInt(Rand.Range(minHours, maxHours) * 2500f);
            StartRpgDialoguePairCooldown(first, second, ticks);
            return ticks;
        }

        public void StartRpgDialoguePairCooldown(Pawn first, Pawn second, int cooldownTicks)
        {
            if (first == null || second == null || cooldownTicks <= 0)
            {
                return;
            }

            string pairKey = GetPawnPairStableId(first, second);
            if (string.IsNullOrWhiteSpace(pairKey))
            {
                return;
            }

            int untilTick = (Find.TickManager?.TicksGame ?? 0) + cooldownTicks;
            if (pawnPairDialogueCooldownUntilTickById.TryGetValue(pairKey, out int existing))
            {
                pawnPairDialogueCooldownUntilTickById[pairKey] = Mathf.Max(existing, untilTick);
            }
            else
            {
                pawnPairDialogueCooldownUntilTickById[pairKey] = untilTick;
            }
        }

        public bool IsRpgDialoguePairOnCooldown(Pawn first, Pawn second, out int remainingTicks)
        {
            remainingTicks = 0;
            string pairKey = GetPawnPairStableId(first, second);
            if (string.IsNullOrWhiteSpace(pairKey) || pawnPairDialogueCooldownUntilTickById == null ||
                !pawnPairDialogueCooldownUntilTickById.TryGetValue(pairKey, out int untilTick))
            {
                return false;
            }

            remainingTicks = untilTick - (Find.TickManager?.TicksGame ?? 0);
            if (remainingTicks > 0)
            {
                return true;
            }

            pawnPairDialogueCooldownUntilTickById.Remove(pairKey);
            remainingTicks = 0;
            return false;
        }

        private string GetPawnPairStableId(Pawn first, Pawn second)
        {
            string firstId = GetPawnStableId(first);
            string secondId = GetPawnStableId(second);
            if (string.IsNullOrWhiteSpace(firstId) || string.IsNullOrWhiteSpace(secondId))
            {
                return string.Empty;
            }

            return string.CompareOrdinal(firstId, secondId) <= 0
                ? firstId + "|" + secondId
                : secondId + "|" + firstId;
        }

        public int GetDialogueCooldownUntilTick(Pawn pawn)
        {
            if (pawn == null || pawnDialogueCooldownUntilTickById == null)
            {
                return 0;
            }

            string pawnId = GetPawnStableId(pawn);
            if (string.IsNullOrWhiteSpace(pawnId))
            {
                return 0;
            }

            return pawnDialogueCooldownUntilTickById.TryGetValue(pawnId, out int untilTick) ? untilTick : 0;
        }

        public void SetDialogueCooldownUntilTick(Pawn pawn, int untilTick)
        {
            if (pawn == null)
            {
                return;
            }

            string pawnId = GetPawnStableId(pawn);
            if (string.IsNullOrWhiteSpace(pawnId))
            {
                return;
            }

            if (pawnDialogueCooldownUntilTickById == null)
            {
                pawnDialogueCooldownUntilTickById = new Dictionary<string, int>();
            }

            int currentTick = Find.TickManager?.TicksGame ?? 0;
            if (untilTick <= currentTick)
            {
                pawnDialogueCooldownUntilTickById.Remove(pawnId);
                return;
            }

            pawnDialogueCooldownUntilTickById[pawnId] = untilTick;
        }

        public string GetPawnPersonaPrompt(Pawn pawn)
        {
            if (pawn == null || pawnPersonaPromptsById == null)
            {
                return string.Empty;
            }

            string pawnId = GetPawnStableId(pawn);
            if (string.IsNullOrWhiteSpace(pawnId))
            {
                return string.Empty;
            }

            bool found = pawnPersonaPromptsById.TryGetValue(pawnId, out string prompt);
            return found ? prompt ?? string.Empty : string.Empty;
        }

        public string ResolveEffectivePawnPersonalityPrompt(Pawn pawn, bool allowGenerateFallback = true)
        {
            if (pawn == null)
            {
                return string.Empty;
            }

            if (IsPawnPersonaSyncInProgress(pawn))
            {
                return GetPawnPersonaPrompt(pawn)?.Trim() ?? string.Empty;
            }

            TrySyncPawnPersonaFromRimTalkSafely(pawn);

            string existing = GetPawnPersonaPrompt(pawn)?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }

            return allowGenerateFallback ? BuildAndPersistFallbackPawnPersonaPrompt(pawn) : string.Empty;
        }

        private void TrySyncPawnPersonaFromRimTalkSafely(Pawn pawn)
        {
            if (pawn == null ||
                pawn.Faction != Faction.OfPlayer ||
                pawn.Dead ||
                pawn.Destroyed)
            {
                return;
            }

            if (!PawnDialogueRoutingPolicy.IsRimTalkPersonaSyncEligible(pawn))
            {
                DebugLogger.Debug(
                    $"Skip RimTalk persona sync: pawn '{pawn.LabelShortCap}' lacks persona sync capability.");
                return;
            }

            if (!TryBeginPawnPersonaSync(pawn))
            {
                return;
            }

            try
            {
                if (CanCopyPawnPersonaFromRimTalk(pawn))
                {
                    TrySyncPawnPersonaFromRimTalk(pawn);
                }
            }

            catch (Exception ex)
            {
                Log.Warning($"[RimChat] Failed to resolve RimTalk personality for '{pawn.LabelShortCap}': {ex.Message}");
            }
            finally
            {
                EndPawnPersonaSync(pawn);
            }
        }

        private bool IsPawnPersonaSyncInProgress(Pawn pawn)
        {
            return pawn != null &&
                pawn.thingIDNumber > 0 &&
                pawnPersonaSyncGuards.Contains(pawn.thingIDNumber);
        }

        private bool TryBeginPawnPersonaSync(Pawn pawn)
        {
            if (pawn == null || pawn.thingIDNumber <= 0)
            {
                return false;
            }

            return pawnPersonaSyncGuards.Add(pawn.thingIDNumber);
        }

        private void EndPawnPersonaSync(Pawn pawn)
        {
            if (pawn == null || pawn.thingIDNumber <= 0)
            {
                return;
            }

            pawnPersonaSyncGuards.Remove(pawn.thingIDNumber);
        }

        private string BuildAndPersistFallbackPawnPersonaPrompt(Pawn pawn)
        {
            if (!IsEligibleNpcPersonaTarget(pawn))
            {
                return string.Empty;
            }

            string generated = BuildFallbackPersonaPrompt(pawn)?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(generated))
            {
                return string.Empty;
            }

            SetPawnPersonaPrompt(pawn, generated);
            return generated;
        }

        public void SetPawnPersonaPrompt(Pawn pawn, string prompt)
        {
            if (pawn == null)
            {
                return;
            }

            string pawnId = GetPawnStableId(pawn);
            if (string.IsNullOrWhiteSpace(pawnId))
            {
                return;
            }

            if (pawnPersonaPromptsById == null)
            {
                pawnPersonaPromptsById = new Dictionary<string, string>();
            }

            string normalized = prompt?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(normalized))
            {
                pawnPersonaPromptsById.Remove(pawnId);
                return;
            }

            pawnPersonaPromptsById[pawnId] = normalized;
        }

        private void MigrateLegacyPawnDictionaries()
        {
            if (legacyPawnDialogueCooldownUntilTick != null)
            {
                foreach (KeyValuePair<Pawn, int> entry in legacyPawnDialogueCooldownUntilTick)
                {
                    string pawnId = GetPawnStableId(entry.Key);
                    if (string.IsNullOrWhiteSpace(pawnId))
                    {
                        continue;
                    }

                    if (pawnDialogueCooldownUntilTickById.TryGetValue(pawnId, out int existing))
                    {
                        pawnDialogueCooldownUntilTickById[pawnId] = Mathf.Max(existing, entry.Value);
                    }
                    else
                    {
                        pawnDialogueCooldownUntilTickById[pawnId] = entry.Value;
                    }
                }
            }

            if (legacyPawnPersonaPrompts != null)
            {
                foreach (KeyValuePair<Pawn, string> entry in legacyPawnPersonaPrompts)
                {
                    string pawnId = GetPawnStableId(entry.Key);
                    if (string.IsNullOrWhiteSpace(pawnId))
                    {
                        continue;
                    }

                    string normalized = entry.Value?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(normalized))
                    {
                        continue;
                    }

                    pawnPersonaPromptsById[pawnId] = normalized;
                }
            }

            legacyPawnDialogueCooldownUntilTick = null;
            legacyPawnPersonaPrompts = null;
        }

        private void CleanupInvalidRpgDictionaries(int currentTick)
        {
            if (pawnDialogueCooldownUntilTickById == null)
            {
                pawnDialogueCooldownUntilTickById = new Dictionary<string, int>();
            }
            else
            {
                List<string> invalidCooldownIds = pawnDialogueCooldownUntilTickById
                    .Where(entry => entry.Value <= currentTick || !TryResolvePawnByStableId(entry.Key, out _))
                    .Select(entry => entry.Key)
                    .ToList();
                foreach (string id in invalidCooldownIds)
                {
                    pawnDialogueCooldownUntilTickById.Remove(id);
                }
            }

            if (pawnPersonaPromptsById == null)
            {
                pawnPersonaPromptsById = new Dictionary<string, string>();
            }
            else
            {
                List<string> invalidPersonaIds = pawnPersonaPromptsById
                    .Where(entry =>
                    {
                        if (string.IsNullOrWhiteSpace(entry.Value))
                        {
                            return true;
                        }

                        if (!TryResolvePawnByStableId(entry.Key, out Pawn pawn))
                        {
                            return true;
                        }

                        return !PawnDialogueRoutingPolicy.IsRpgDialogueEligibleRace(pawn);
                    })
                    .Select(entry => entry.Key)
                    .ToList();
                foreach (string id in invalidPersonaIds)
                {
                    pawnPersonaPromptsById.Remove(id);
                }
            }

            if (pawnPairDialogueCooldownUntilTickById == null)
            {
                pawnPairDialogueCooldownUntilTickById = new Dictionary<string, int>();
            }
            else
            {
                List<string> invalidPairIds = pawnPairDialogueCooldownUntilTickById
                    .Where(entry => entry.Value <= currentTick || !CanResolveCooldownPair(entry.Key))
                    .Select(entry => entry.Key)
                    .ToList();
                foreach (string id in invalidPairIds)
                {
                    pawnPairDialogueCooldownUntilTickById.Remove(id);
                }
            }
        }

        private static bool TryResolvePawnByStableId(string pawnId, out Pawn pawn)
        {
            if (string.IsNullOrWhiteSpace(pawnId))
            {
                pawn = null;
                return false;
            }

            return DialogueContextResolver.TryResolvePawn(pawnId, out pawn);
        }

        private static bool CanResolveCooldownPair(string pairId)
        {
            if (string.IsNullOrWhiteSpace(pairId)) return false;
            string[] ids = pairId.Split('|');
            return ids.Length == 2 &&
                TryResolvePawnByStableId(ids[0], out _) &&
                TryResolvePawnByStableId(ids[1], out _);
        }

        private static string GetPawnStableId(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed || pawn.Dead)
            {
                return string.Empty;
            }

            return pawn.GetUniqueLoadID() ?? string.Empty;
        }
    }
}
