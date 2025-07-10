using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace YesFox
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    internal class Plugin : BaseUnityPlugin
    {
        private readonly Harmony harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);

        internal static ManualLogSource StaticLogger { get; private set; }
        internal static ConfigFile StaticConfig { get; private set; }

        internal static List<GameObject> _networkPrefabs = new List<GameObject>();
        public static GameObject BushWolfAddonPrefab { get; internal set; }

        internal static ConfigEntry<bool> Shroud_AllMoons;
        internal static ConfigEntry<float> Shroud_SpawnChance_SameMoon;
        internal static ConfigEntry<float> Shroud_SpawnChance_OtherMoons;
        internal static ConfigEntry<float> Shroud_GrowChance_SameMoon;
        internal static ConfigEntry<float> Shroud_GrowChance_OtherMoons;
        internal static ConfigEntry<int> Shroud_MaximumIterations;
        internal static ConfigEntry<float> Shroud_MinimumDistance;
        internal static ConfigEntry<int> Fox_MinimumWeeds;
        internal static ConfigEntry<int> Fox_SpawnChance;
        internal static Dictionary<string, ConfigEntry<bool>> Shroud_MoonToggles = [];
        internal static Dictionary<string, ConfigEntry<bool>> WeedOverridesEnabled = [];
        internal static Dictionary<string, ConfigEntry<bool>> FoxOverridesEnabled = [];
        internal static Dictionary<string, ConfigEntry<float>> Shroud_MoonSpawnChanceSameMoonOverrides = [];
        internal static Dictionary<string, ConfigEntry<float>> Shroud_MoonSpawnChanceOtherMoonsOverrides = [];
        internal static Dictionary<string, ConfigEntry<float>> Shroud_MoonGrowChanceSameMoonOverrides = [];
        internal static Dictionary<string, ConfigEntry<float>> Shroud_MoonGrowChanceOtherMoonsOverrides = [];
        internal static Dictionary<string, ConfigEntry<int>> Shroud_MoonMaxIterationOverrides = [];
        internal static Dictionary<string, ConfigEntry<float>> Shroud_MoonMinDistanceOverrides = [];
        internal static Dictionary<string, ConfigEntry<int>> Fox_MinimumWeedsOverrides = [];
        internal static Dictionary<string, ConfigEntry<int>> Fox_SpawnChanceOverrides = [];

        private void Awake()
        {
            StaticLogger = Logger;
            StaticConfig = Config;

            Assembly patches = Assembly.GetExecutingAssembly();
            harmony.PatchAll(patches);
            StaticLogger.LogInfo("Patches Loaded");

            AssetBundle BushWolfBundle = AssetBundle.LoadFromFile(Path.Combine(Path.GetDirectoryName(Info.Location), "bush_wolf"));
            if (BushWolfBundle == null)
            {
                StaticLogger.LogError("[AssetBundle] Failed to load asset bundle: bush_wolf");
                return;
            }
            BushWolfAddonPrefab = BushWolfBundle.LoadAsset<GameObject>("Assets/LethalCompany/Game/Prefabs/EnemyAI/BushWolfEnemy.prefab");
            if (BushWolfAddonPrefab != null)
            {
                if (!_networkPrefabs.Contains(BushWolfAddonPrefab))
                    _networkPrefabs.Add(BushWolfAddonPrefab);
                StaticLogger.LogInfo("[AssetBundle] Successfully loaded prefab: BushWolfEnemy");
            }
            else
            {
                StaticLogger.LogError("[AssetBundle] Failed to load prefab: BushWolfEnemy");
            }

            Shroud_AllMoons = Config.Bind("Weed Spawning", "All Moons", false, "Should weeds be able to spawn on all moons excluding gordion?");
            Shroud_SpawnChance_SameMoon = Config.Bind("Weed Spawning", "Spawn Chance (Current Moon)", 8.5f, new ConfigDescription("What should the chance for them to initially spawn the moon you are routed to be? Weeds attempt to spawn on all moons when you go into orbit after each day.", new AcceptableValueRange<float>(0, 100)));
            Shroud_SpawnChance_OtherMoons = Config.Bind("Weed Spawning", "Spawn Chance (Other Moons)", 4f, new ConfigDescription("What should the chance for them to initially spawn on other moons be? Weeds attempt to spawn on all moons when you go into orbit after each day.", new AcceptableValueRange<float>(0, 100)));
            Shroud_GrowChance_SameMoon = Config.Bind("Weed Spawning", "Growth Chance (Current Moon)", 100f, new ConfigDescription("What is the chance that weeds should grow another \"step\" after leaving a moon? This applies at the end of the day, only once the spawn chance has succeeded for the first time.", new AcceptableValueRange<float>(0, 100)));
            Shroud_GrowChance_OtherMoons = Config.Bind("Weed Spawning", "Growth Chance (Other Moons)", 100f, new ConfigDescription("What is the chance that weeds should grow another \"step\" for all other moons? This applies at the end of the day, only once the spawn chance has succeeded for the first time.", new AcceptableValueRange<float>(0, 100)));
            Shroud_MaximumIterations = Config.Bind("Weed Spawning", "Maximum Iterations", 20, new ConfigDescription("How many days in a row are additional weeds allowed to grow on the same moon?", new AcceptableValueRange<int>(1, 20)));
            // absolute upper limit is 70.4927 (Experimentation's furthest valid distance at default settings)
            Shroud_MinimumDistance = Config.Bind("Weed Spawning", "Minimum Distance", 40f, new ConfigDescription("How many units away from the ship must the starting points for weed growth be?", new AcceptableValueRange<float>(30f, 70f)));

            Fox_MinimumWeeds = Config.Bind("Fox Spawning", "Minimum Weeds", 31, "The minimum amount of weeds required to spawn");
            Fox_SpawnChance = Config.Bind("Fox Spawning", "Spawn Chance", -1, new ConfigDescription("What should the spawn chance be? If left as -1 then it will be the same as vanilla (a higher chance the more weeds there are)", new AcceptableValueRange<int>(-1, 100)));
        }
    }

    [HarmonyPatch]
    internal static class HarmonyPatches
    {
        private static bool perMoonConfigsGenerated = false;

        [HarmonyPatch(typeof(GameNetworkManager), "Start")]
        [HarmonyPostfix]
        private static void GameNetworkManager_Start(GameNetworkManager __instance)
        {
            if (__instance.gameVersionNum >= 64)
            {
                foreach (GameObject obj in Plugin._networkPrefabs)
                {
                    if (!NetworkManager.Singleton.NetworkConfig.Prefabs.Contains(obj))
                        NetworkManager.Singleton.AddNetworkPrefab(obj);
                }
            }
            else
            {
                foreach (GameObject obj in Plugin._networkPrefabs)
                {
                    if (NetworkManager.Singleton.NetworkConfig.Prefabs.Contains(obj))
                        NetworkManager.Singleton.RemoveNetworkPrefab(obj);
                }
            }

            if (Chainloader.PluginInfos.ContainsKey("BMX.LobbyCompatibility"))
            {
                Compatibility.LobbyCompatibility.Init(__instance.gameVersionNum);
            }
        }

        static MoldSpreadManager _moldSpreadManager;
        static MoldSpreadManager MoldSpreadManager
        {
            get
            {
                if (_moldSpreadManager == null)
                    _moldSpreadManager = Object.FindAnyObjectByType<MoldSpreadManager>();

                return _moldSpreadManager;
            }
        }

        private static T GetValue<T>(string planetName, Dictionary<string, ConfigEntry<bool>> overrideEnabledDict, Dictionary<string, ConfigEntry<T>> overrideValueDict, ConfigEntry<T> globalValue)
        {
            bool overridesEnabled = overrideEnabledDict.TryGetValue(planetName, out var enabledConfig) && enabledConfig.Value;
            if (overridesEnabled && overrideValueDict.TryGetValue(planetName, out var valueConfig))
            {
                return valueConfig.Value;
            }
            return globalValue.Value;
        }

        [HarmonyPatch(typeof(TimeOfDay), "OnDayChanged")]
        [HarmonyPostfix]
        public static void OnDayChanged(TimeOfDay __instance)
        {
            if (!__instance.IsOwner || StartOfRound.Instance.isChallengeFile) return;
            
            System.Random random = new System.Random(StartOfRound.Instance.randomMapSeed + 32);
            Terminal terminal = Object.FindAnyObjectByType<Terminal>();
            for (int i = 0; i < StartOfRound.Instance.levels.Length; i++)
            {
                SelectableLevel level = StartOfRound.Instance.levels[i];
                string planetName = level.PlanetName;
                
                bool canSpawnOnMoon = Plugin.Shroud_MoonToggles.TryGetValue(planetName, out var toggleConfig) ? toggleConfig.Value : level.canSpawnMold;

                if (i == 3 || (!canSpawnOnMoon && !Plugin.Shroud_AllMoons.Value))
                {
                    Plugin.StaticLogger.LogInfo($"Skipping level #{i} {level.PlanetName} mold iterations");
                    continue;
                }

                int maxIterations = GetValue(planetName, Plugin.WeedOverridesEnabled, Plugin.Shroud_MoonMaxIterationOverrides, Plugin.Shroud_MaximumIterations);

                if (level.moldSpreadIterations > 0)
                {
                    if (level.moldSpreadIterations < maxIterations)
                    {
                        bool isCurrentMoon = level == StartOfRound.Instance.currentLevel;
                        float chance = isCurrentMoon
                            ? GetValue(planetName, Plugin.WeedOverridesEnabled, Plugin.Shroud_MoonGrowChanceSameMoonOverrides, Plugin.Shroud_GrowChance_SameMoon)
                            : GetValue(planetName, Plugin.WeedOverridesEnabled, Plugin.Shroud_MoonGrowChanceOtherMoonsOverrides, Plugin.Shroud_GrowChance_OtherMoons);
                            
                        if (random.NextDouble() <= chance / 100f)
                        {
                            level.moldSpreadIterations++;
                            Plugin.StaticLogger.LogInfo($"Increasing level #{i} {level.PlanetName} mold iterations by 1; risen to {level.moldSpreadIterations}");
                        }
                    }
                    continue;
                }

                float num;
                bool isCurrentMoonForSpawn = level == StartOfRound.Instance.currentLevel;
                bool weedOverridesEnabled = Plugin.WeedOverridesEnabled.TryGetValue(planetName, out var enabledConfig) && enabledConfig.Value;

                if (isCurrentMoonForSpawn)
                {
                    num = GetValue(planetName, Plugin.WeedOverridesEnabled, Plugin.Shroud_MoonSpawnChanceSameMoonOverrides, Plugin.Shroud_SpawnChance_SameMoon) / 100f;
                }
                else
                {
                    num = GetValue(planetName, Plugin.WeedOverridesEnabled, Plugin.Shroud_MoonSpawnChanceOtherMoonsOverrides, Plugin.Shroud_SpawnChance_OtherMoons) / 100f;
                    if (!weedOverridesEnabled) // Only apply vanilla logic if not using overrides
                    {
                        if (terminal.groupCredits < 200 && level.levelID == 12)
                        {
                            num *= 1.25f; // 0.04 -> 0.05 (vanilla)
                        }
                        else if (terminal.groupCredits < 500 && (level.levelID == 7 || level.levelID == 6 || level.levelID >= 10) && (StartOfRound.Instance.currentLevel.levelID == 5 || StartOfRound.Instance.currentLevel.levelID == 8 || StartOfRound.Instance.currentLevel.levelID == 4 || StartOfRound.Instance.currentLevel.levelID <= 2))
                        {
                            num *= 0.5f; // 0.04 -> 0.02 (vanilla)
                        }
                    }
                }

                if (random.NextDouble() <= num)
                {
                    level.moldSpreadIterations += random.Next(1, 3);
                    Plugin.StaticLogger.LogInfo($"Increasing level #{i} {level.PlanetName} mold iterations for the first time; risen to {level.moldSpreadIterations}");
                }
            }
        }

        // called after the scene (and AI nodes) load, but before LoadNewLevelWait selects a start position
        [HarmonyPatch(typeof(StartOfRound), "PlayerLoadedServerRpc")]
        [HarmonyPostfix]
        static void PlayerLoadedServerRpc(StartOfRound __instance)
        {
            if (!__instance.IsServer || __instance.currentLevel.moldSpreadIterations < 1)
                return;

            string planetName = __instance.currentLevel.PlanetName;
            bool canSpawnOnMoon = Plugin.Shroud_MoonToggles.TryGetValue(planetName, out var toggleConfig) ? toggleConfig.Value : __instance.currentLevel.canSpawnMold;

            if (!canSpawnOnMoon && !Plugin.Shroud_AllMoons.Value)
            {
                __instance.currentLevel.moldSpreadIterations = 0;
                __instance.currentLevel.moldStartPosition = -1;
                return;
            }

            int maxIterations = GetValue(planetName, Plugin.WeedOverridesEnabled, Plugin.Shroud_MoonMaxIterationOverrides, Plugin.Shroud_MaximumIterations);

            // retroactively apply iteration cap to old save files
            if (__instance.currentLevel.moldSpreadIterations > maxIterations)
            {
                __instance.currentLevel.moldSpreadIterations = maxIterations;
            }

            GameObject[] outsideAINodes = GameObject.FindGameObjectsWithTag("OutsideAINode");
            if (outsideAINodes == null || outsideAINodes.Length < 1)
                return;

            Vector3 shipPos = StartOfRound.Instance.shipLandingPosition.position; // StartOfRound.elevatorTransform position, when fully landed

            outsideAINodes = [.. outsideAINodes.OrderBy(x => Vector3.Distance(x.transform.position, shipPos))];

            // starting point has already been chosen
            if (__instance.currentLevel.moldStartPosition >= 0 && __instance.currentLevel.moldStartPosition < outsideAINodes.Length)
            {
                float shipDist = Vector3.Distance(outsideAINodes[__instance.currentLevel.moldStartPosition].transform.position, shipPos);

                // spot chosen is already an acceptable distance from the ship
                if (shipDist >= 30f)
                    return;

                Plugin.StaticLogger.LogInfo($"Mold growth is starting from node #{__instance.currentLevel.moldStartPosition} which is too close to the ship ({shipDist} < 30)");
            }

            // starting point has not been chosen, or was invalid

            float minDistance = GetValue(planetName, Plugin.WeedOverridesEnabled, Plugin.Shroud_MoonMinDistanceOverrides, Plugin.Shroud_MinimumDistance);
            int minWeeds = GetValue(planetName, Plugin.FoxOverridesEnabled, Plugin.Fox_MinimumWeedsOverrides, Plugin.Fox_MinimumWeeds);

            System.Random random = new System.Random(__instance.randomMapSeed + 2017);
            int temp = __instance.currentLevel.moldSpreadIterations; // preserve
            for (int i = 0; i < outsideAINodes.Length; i++)
            {
                float shipDist = Vector3.Distance(outsideAINodes[i].transform.position, shipPos);
                // greater than 40 units and selected randomly
                if (shipDist >= minDistance && (random.Next(100) < 13 || outsideAINodes.Length - i < 20))
                {
                    Plugin.StaticLogger.LogDebug($"Mold growth: outsideAINodes[{i}] is candidate (ship dist: {shipDist} > {minDistance})");
                    // furthest distance in vanilla is on Artifice (7.88802)
                    if (Physics.Raycast(outsideAINodes[i].transform.position, Vector3.down, out RaycastHit hit, 8f, StartOfRound.Instance.collidersAndRoomMaskAndDefault, QueryTriggerInteraction.Ignore))
                    {
                        // final test... can the fox spawn here?

                        // must simulate weed growth, there aren't any shortcuts for this
                        MoldSpreadManager.GenerateMold(outsideAINodes[i].transform.position, maxIterations);
                        int amt = MoldSpreadManager.generatedMold.Count;
                        MoldSpreadManager.RemoveAllMold();

                        if (amt >= minWeeds)
                        {
                            __instance.currentLevel.moldStartPosition = i;
                            __instance.currentLevel.moldSpreadIterations = temp;
                            Plugin.StaticLogger.LogInfo($"Mold growth: Selected outsideAINodes[{i}]: coords {outsideAINodes[i].transform.position}, dist {shipDist}");
                            return;
                        }
                        else
                            Plugin.StaticLogger.LogDebug($"Mold growth: outsideAINodes[{i}] rejected (max weeds: {amt} < {minWeeds})");
                    }
                    else
                        Plugin.StaticLogger.LogDebug($"Mold growth: outsideAINodes[{i}] rejected (no ground)");
                }
            }

            __instance.currentLevel.moldSpreadIterations = 0;
            __instance.currentLevel.moldStartPosition = -1;
            Plugin.StaticLogger.LogInfo($"Level \"{__instance.currentLevel.PlanetName}\" has no valid AI nodes");
        }

        // v64
        public static System.Random WeedEnemySpawnRandom;
        public static List<SpawnableEnemyWithRarity> WeedEnemies = new List<SpawnableEnemyWithRarity>();

        [HarmonyPatch(typeof(StartOfRound), "Start")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void StartOfRound_Start(StartOfRound __instance)
        {
            GeneratePerMoonConfigsIfNeeded(__instance);
            GenerateWeedEnemiesList();
        }

        private static void GeneratePerMoonConfigsIfNeeded(StartOfRound startOfRoundInstance)
        {
            if (!perMoonConfigsGenerated && Plugin.StaticConfig != null)
            {
                Plugin.StaticLogger.LogInfo("Generating per-moon fox & weed configs...");
                foreach (SelectableLevel level in startOfRoundInstance.levels)
                {
                    if (level.levelID == 3 || !level.spawnEnemiesAndScrap) continue;

                    string planetName = level.PlanetName;
                    string sanitizedName = new string(planetName.Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c)).ToArray()).Trim();
                    string overrideSection = $"Moon Overrides.{sanitizedName}";

                    Plugin.Shroud_MoonToggles.TryAdd(planetName, Plugin.StaticConfig.Bind(
                        overrideSection, "Enable Weed Spawning", level.canSpawnMold,
                        $"Controls whether weeds can spawn on {planetName} at all. This is the master switch for this moon."
                    ));

                    Plugin.WeedOverridesEnabled.TryAdd(planetName, Plugin.StaticConfig.Bind(
                        overrideSection, "Enable Weed Overrides", false,
                        $"Enable per-moon overrides for weed settings (spawn/growth chance, etc). If this is false, the global settings will be used."
                    ));

                    Plugin.Shroud_MoonSpawnChanceSameMoonOverrides.TryAdd(planetName, Plugin.StaticConfig.Bind(
                        overrideSection, "Spawn Chance (While Routed)", Plugin.Shroud_SpawnChance_SameMoon.Value,
                        new ConfigDescription($"Override the weed spawn chance for {planetName} when you are currently routed to it.", new AcceptableValueRange<float>(0f, 100f))
                    ));
                    Plugin.Shroud_MoonSpawnChanceOtherMoonsOverrides.TryAdd(planetName, Plugin.StaticConfig.Bind(
                        overrideSection, "Spawn Chance (While Not Routed)", Plugin.Shroud_SpawnChance_OtherMoons.Value,
                        new ConfigDescription($"Override the weed spawn chance for {planetName} when you are routed to another moon.", new AcceptableValueRange<float>(0f, 100f))
                    ));
                    Plugin.Shroud_MoonGrowChanceSameMoonOverrides.TryAdd(planetName, Plugin.StaticConfig.Bind(
                        overrideSection, "Growth Chance (While Routed)", Plugin.Shroud_GrowChance_SameMoon.Value,
                        new ConfigDescription($"Override the weed growth chance for {planetName} when you are currently routed to it.", new AcceptableValueRange<float>(0f, 100f))
                    ));
                    Plugin.Shroud_MoonGrowChanceOtherMoonsOverrides.TryAdd(planetName, Plugin.StaticConfig.Bind(
                        overrideSection, "Growth Chance (While Not Routed)", Plugin.Shroud_GrowChance_OtherMoons.Value,
                        new ConfigDescription($"Override the weed growth chance for {planetName} when you are routed to another moon.", new AcceptableValueRange<float>(0f, 100f))
                    ));
                    Plugin.Shroud_MoonMaxIterationOverrides.TryAdd(planetName, Plugin.StaticConfig.Bind(
                        overrideSection, "Maximum Iterations", Plugin.Shroud_MaximumIterations.Value,
                        new ConfigDescription($"Override the global maximum weed iterations for {planetName}.", new AcceptableValueRange<int>(1, 20))
                    ));
                    Plugin.Shroud_MoonMinDistanceOverrides.TryAdd(planetName, Plugin.StaticConfig.Bind(
                        overrideSection, "Minimum Distance", Plugin.Shroud_MinimumDistance.Value,
                        new ConfigDescription($"Override the global minimum distance for weed growth on {planetName}.", new AcceptableValueRange<float>(30f, 70f))
                    ));
                    Plugin.FoxOverridesEnabled.TryAdd(planetName, Plugin.StaticConfig.Bind(
                        overrideSection, "Enable Fox Overrides", false,
                        $"Enable per-moon overrides for fox settings (minimum weeds, spawn chance). If this is false, the global settings will be used."
                    ));
                    Plugin.Fox_MinimumWeedsOverrides.TryAdd(planetName, Plugin.StaticConfig.Bind(
                        overrideSection, "Fox Minimum Weeds", Plugin.Fox_MinimumWeeds.Value,
                        new ConfigDescription($"Override the minimum weeds required for a fox to spawn on {planetName}.")
                    ));
                    Plugin.Fox_SpawnChanceOverrides.TryAdd(planetName, Plugin.StaticConfig.Bind(
                        overrideSection, "Fox Spawn Chance", Plugin.Fox_SpawnChance.Value,
                        new ConfigDescription($"Override the spawn chance for a fox on {planetName}. Set to -1 to use the vanilla spawn logic.", new AcceptableValueRange<int>(-1, 100))
                    ));
                }
                perMoonConfigsGenerated = true;
            }
        }

        public static void GenerateWeedEnemiesList()
        {
            WeedEnemies.Clear();

            try
            {
                EnemyType bushWolfTypeOrig = Object.FindAnyObjectByType<QuickMenuManager>()?.testAllEnemiesLevel?.OutsideEnemies.FirstOrDefault(x => x.enemyType.name == "BushWolf" && x.enemyType.enemyPrefab?.GetComponentsInChildren<SkinnedMeshRenderer>()?.Length > 0)?.enemyType;
                if (bushWolfTypeOrig != null)
                {
                    if (GameNetworkManager.Instance.gameVersionNum >= 64 && bushWolfTypeOrig.enemyPrefab != Plugin.BushWolfAddonPrefab && Plugin.BushWolfAddonPrefab != null)
                    {
                        if (Plugin.BushWolfAddonPrefab.GetComponent<EnemyAI>() != null)
                        {
                            Plugin.BushWolfAddonPrefab.GetComponent<EnemyAI>().enemyType = bushWolfTypeOrig;
                            Plugin.StaticLogger.LogInfo("[GenerateWeedEnemiesList] BushWolf: Replaced addon EnemyAI enemyType");
                        }

                        SkinnedMeshRenderer rendererOrig = bushWolfTypeOrig.enemyPrefab?.GetComponentsInChildren<SkinnedMeshRenderer>().FirstOrDefault(rend => rend.sharedMaterials.Length > 1);
                        SkinnedMeshRenderer[] renderersNew = Plugin.BushWolfAddonPrefab.GetComponentsInChildren<SkinnedMeshRenderer>();
                        if (rendererOrig != null)
                        {
                            foreach (SkinnedMeshRenderer renderer in renderersNew)
                            {
                                Material[] mats = new Material[renderer.name == "BodyLOD2" ? 1 : 2];
                                for (int i = 0; i < mats.Length; i++)
                                {
                                    mats[i] = rendererOrig.materials[i];
                                }
                                renderer.materials = mats;
                            }
                        }

                        bushWolfTypeOrig.enemyPrefab = Plugin.BushWolfAddonPrefab;
                        Plugin.StaticLogger.LogInfo("[GenerateWeedEnemiesList] BushWolf: Replaced original EnemyType prefab");
                    }

                    WeedEnemies.Add(new SpawnableEnemyWithRarity()
                    {
                        enemyType = bushWolfTypeOrig,
                        rarity = 100,
                    });
                }
            }
            catch (Exception e)
            {
                Plugin.StaticLogger.LogError(e);
            }
        }

        public static void SpawnWeedEnemies(int currentHour, SelectableLevel currentLevel)
        {
            int num = 0;
            if (MoldSpreadManager != null)
            {
                num = MoldSpreadManager.generatedMold.Count(x => x != null && x.activeSelf);
            }

            string planetName = currentLevel.PlanetName;
            int minWeeds = GetValue(planetName, Plugin.FoxOverridesEnabled, Plugin.Fox_MinimumWeedsOverrides, Plugin.Fox_MinimumWeeds);

            if (num < minWeeds)
            {
                Plugin.StaticLogger.LogDebug($"Weed enemies attempted to spawn but were denied. Reason: WeedCount | Amount: {num}");
                return;
            }

            int foxSpawnChance = GetValue(planetName, Plugin.FoxOverridesEnabled, Plugin.Fox_SpawnChanceOverrides, Plugin.Fox_SpawnChance);

            if (foxSpawnChance >= 0)
            {
                int spawnChance = WeedEnemySpawnRandom.Next(1, 100);
                if (spawnChance > foxSpawnChance)
                {
                    Plugin.StaticLogger.LogDebug($"Weed enemies attempted to spawn but were denied. Reason: SpawnChance | Amount: {spawnChance}");
                    return;
                }
            }
            else
            {
                int spawnChance = WeedEnemySpawnRandom.Next(0, 80);
                if (spawnChance > num)
                {
                    Plugin.StaticLogger.LogDebug($"Weed enemies attempted to spawn but were denied. Reason: SpawnChance | Amount: {spawnChance}");
                    return;
                }
            }
            int num2 = WeedEnemySpawnRandom.Next(1, 3);
            GameObject[] spawnPoints = GameObject.FindGameObjectsWithTag("OutsideAINode");
            float timeUpToCurrentHour = TimeOfDay.Instance.lengthOfHours * (float)currentHour;

            if (WeedEnemies.Count == 0)
            {
                GenerateWeedEnemiesList();
                if (WeedEnemies.Count == 0)
                {
                    Plugin.StaticLogger.LogError($"Weed enemies attempted to spawn but were denied. Reason: ListEmpty");
                    return;
                }
            }

            for (int i = 0; i < num2; i++)
            {
                if (!SpawnRandomWeedEnemy(spawnPoints, timeUpToCurrentHour, num))
                {
                    break;
                }
            }
        }

        public static bool SpawnRandomWeedEnemy(GameObject[] spawnPoints, float timeUpToCurrentHour, int numberOfWeeds)
        {
            RoundManager.Instance.SpawnProbabilities.Clear();
            int num = 0;
            for (int i = 0; i < WeedEnemies.Count; i++)
            {
                EnemyType enemyType = WeedEnemies[i].enemyType;
                if (RoundManager.Instance.firstTimeSpawningWeedEnemies)
                {
                    enemyType.numberSpawned = 0;
                }
                if (enemyType.PowerLevel > RoundManager.Instance.currentMaxOutsidePower - RoundManager.Instance.currentOutsideEnemyPower || enemyType.numberSpawned >= enemyType.MaxCount || enemyType.spawningDisabled)
                {
                    RoundManager.Instance.SpawnProbabilities.Add(0);
                    Plugin.StaticLogger.LogDebug($"A weed enemy attempted to spawn but was denied. Reason: Probability | Amount: 0");
                    continue;
                }
                int num2 = ((RoundManager.Instance.increasedOutsideEnemySpawnRateIndex == i) ? 100 : ((!enemyType.useNumberSpawnedFalloff) ? ((int)((float)WeedEnemies[i].rarity * enemyType.probabilityCurve.Evaluate(timeUpToCurrentHour / RoundManager.Instance.timeScript.totalTime))) : ((int)((float)WeedEnemies[i].rarity * (enemyType.probabilityCurve.Evaluate(timeUpToCurrentHour / RoundManager.Instance.timeScript.totalTime) * enemyType.numberSpawnedFalloff.Evaluate((float)enemyType.numberSpawned / 10f))))));
                if (enemyType.spawnFromWeeds)
                {
                    num2 = (int)Mathf.Clamp((float)num2 * ((float)numberOfWeeds / 60f), 0f, 200f);
                }
                RoundManager.Instance.SpawnProbabilities.Add(num2);
                num += num2;
            }
            RoundManager.Instance.firstTimeSpawningWeedEnemies = false;
            if (num <= 0)
            {
                Plugin.StaticLogger.LogDebug($"A weed enemy attempted to spawn but was denied. Reason: SpawnRate | Amount: {num}");
                return false;
            }
            bool result = false;
            int randomWeightedIndex = RoundManager.Instance.GetRandomWeightedIndex(RoundManager.Instance.SpawnProbabilities.ToArray(), WeedEnemySpawnRandom);
            EnemyType enemyType2 = WeedEnemies[randomWeightedIndex].enemyType;
            float num3 = Mathf.Max(enemyType2.spawnInGroupsOf, 1);
            for (int j = 0; (float)j < num3; j++)
            {
                float currentOutsideEnemyPower = RoundManager.Instance.currentMaxOutsidePower - RoundManager.Instance.currentOutsideEnemyPower;
                if (enemyType2.PowerLevel > currentOutsideEnemyPower)
                {
                    Plugin.StaticLogger.LogDebug($"A weed enemy attempted to spawn but was denied. Reason: PowerLevel | Amount: {currentOutsideEnemyPower}");
                    break;
                }
                RoundManager.Instance.currentOutsideEnemyPower += enemyType2.PowerLevel;
                Vector3 position = spawnPoints[RoundManager.Instance.AnomalyRandom.Next(0, spawnPoints.Length)].transform.position;
                position = RoundManager.Instance.GetRandomNavMeshPositionInBoxPredictable(position, 10f, default(NavMeshHit), RoundManager.Instance.AnomalyRandom, RoundManager.Instance.GetLayermaskForEnemySizeLimit(enemyType2));
                position = RoundManager.Instance.PositionWithDenialPointsChecked(position, spawnPoints, enemyType2);
                GameObject gameObject = Object.Instantiate(enemyType2.enemyPrefab, position, Quaternion.Euler(Vector3.zero));
                gameObject.GetComponentInChildren<NetworkObject>().Spawn(destroyWithScene: true);
                RoundManager.Instance.SpawnedEnemies.Add(gameObject.GetComponent<EnemyAI>());
                gameObject.GetComponent<EnemyAI>().enemyType.numberSpawned++;
                result = true;
            }
            Plugin.StaticLogger.LogDebug($"{enemyType2.enemyName} attempted to spawn and was allowed");
            return result;
        }

        [HarmonyPatch(typeof(RoundManager), "InitializeRandomNumberGenerators")]
        [HarmonyPostfix]
        public static void InitializeRandomNumberGenerators(RoundManager __instance)
        {
            WeedEnemySpawnRandom = new System.Random(__instance.playersManager.randomMapSeed + 42);
        }

        [HarmonyPatch(typeof(RoundManager), "AdvanceHourAndSpawnNewBatchOfEnemies")]
        [HarmonyPrefix]
        public static void AdvanceHourAndSpawnNewBatchOfEnemies(RoundManager __instance)
        {
            if (GameNetworkManager.Instance.gameVersionNum >= 64)
            {
                SpawnWeedEnemies(__instance.currentHour + __instance.hourTimeBetweenEnemySpawnBatches, StartOfRound.Instance.currentLevel);
            }
        }

        [HarmonyPatch(typeof(BushWolfEnemy), "GetBiggestWeedPatch")]
        [HarmonyPrefix]
        public static void Pre_GetBiggestWeedPatch(ref Collider[] ___nearbyColliders)
        {
            if (___nearbyColliders != null && ___nearbyColliders.Length > 10)
                return;

            if (MoldSpreadManager?.generatedMold == null)
                return;

            if (___nearbyColliders == null || MoldSpreadManager.generatedMold.Count > ___nearbyColliders.Length)
            {
                ___nearbyColliders = new Collider[MoldSpreadManager.generatedMold.Count];
            }
        }

        static GameObject _moldAttractionPoint;
        static GameObject MoldAttractionPoint
        {
            get
            {
                if (_moldAttractionPoint == null)
                    _moldAttractionPoint = GameObject.FindGameObjectWithTag("MoldAttractionPoint");

                return _moldAttractionPoint;
            }
        }

        // Fixed aggressivePosition not being set if there isn't a MoldAttractionPoint
        [HarmonyPatch(typeof(BushWolfEnemy), "GetBiggestWeedPatch")]
        [HarmonyPostfix]
        public static void Post_GetBiggestWeedPatch(BushWolfEnemy __instance, bool __result)
        {
            if (__result && MoldAttractionPoint == null)
            {
                __instance.aggressivePosition = __instance.mostHiddenPosition;
            }
        }

        // Fixes weeds resetting when they naturally fail to spawn
        [HarmonyPatch(typeof(MoldSpreadManager), "GenerateMold")]
        [HarmonyPrefix]
        static void Pre_GenerateMold(MoldSpreadManager __instance, ref int __state)
        {
            __state = StartOfRound.Instance.currentLevel.moldStartPosition;
        }
        [HarmonyPatch(typeof(MoldSpreadManager), "GenerateMold")]
        [HarmonyPostfix]
        static void Post_GenerateMold(MoldSpreadManager __instance, int __state, int iterations)
        {
            if (__instance.iterationsThisDay < 1 && iterations > 0)
            {
                Plugin.StaticLogger.LogInfo($"Mold growth on \"{StartOfRound.Instance.currentLevel.PlanetName}\" erroneously reset from {iterations} iterations");
                StartOfRound.Instance.currentLevel.moldSpreadIterations = iterations;
                StartOfRound.Instance.currentLevel.moldStartPosition = __state;
            }
        }

        // caching
        [HarmonyPatch(typeof(MoldSpreadManager), "Start")]
        [HarmonyPostfix]
        static void MoldSpreadManager_Start(MoldSpreadManager __instance)
        {
            if (_moldSpreadManager == null)
                _moldSpreadManager = __instance;
        }

        static VehicleController vehicleController;
        [HarmonyPatch(typeof(VehicleController), "Awake")]
        [HarmonyPostfix]
        static void VehicleController_Awake(VehicleController __instance)
        {
            if (vehicleController == null)
                vehicleController = __instance;
        }

        [HarmonyPatch(typeof(BushWolfEnemy), nameof(BushWolfEnemy.Update))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> CacheVehicleController(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = instructions.ToList();

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Call)
                {
                    string methodName = codes[i].operand.ToString();
                    if (methodName.Contains("FindObjectOfType") && methodName.Contains("VehicleController"))
                    {
                        codes[i].opcode = OpCodes.Ldsfld;
                        codes[i].operand = AccessTools.Field(typeof(HarmonyPatches), nameof(vehicleController));
                        Plugin.StaticLogger.LogDebug($"Use cached VehicleController in Kidnapper Fox AI");
                        break;
                    }
                }
            }

            return codes;
        }

        static readonly MethodInfo MOLD_SPREAD_MANAGER_INSTANCE = AccessTools.DeclaredPropertyGetter(typeof(HarmonyPatches), nameof(MoldSpreadManager));
        [HarmonyPatch(typeof(GameNetworkManager), nameof(GameNetworkManager.SaveGameValues))]
        [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.LoadNewLevelWait), MethodType.Enumerator)]
        [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.GenerateNewLevelClientRpc))]
        [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.SpawnRandomOutsideEnemy))]
        [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.EndOfGame), MethodType.Enumerator)]
        [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.ResetMoldStates))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> CacheMoldSpreadManager(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            List<CodeInstruction> codes = instructions.ToList();

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Call)
                {
                    string methodName = codes[i].operand.ToString();
                    if (methodName.Contains("FindObjectOfType") && methodName.Contains("MoldSpreadManager"))
                    {
                        codes[i].operand = MOLD_SPREAD_MANAGER_INSTANCE;
                        Plugin.StaticLogger.LogDebug($"Use cached MoldSpreadManager in {__originalMethod.DeclaringType}.{__originalMethod.Name}");
                    }
                }
            }

            //Plugin.Logger.LogWarning($"{__originalMethod.Name} transpiler failed");
            return codes;
        }

        public static GameObject[] FindMoldSpores()
        {
            if (MoldSpreadManager?.generatedMold == null)
                return GameObject.FindGameObjectsWithTag("MoldSpore");

            return MoldSpreadManager.generatedMold.Where(x => x != null && x.activeSelf).ToArray();
        }

        static readonly MethodInfo MOLD_ATTRACTION_POINT = AccessTools.DeclaredPropertyGetter(typeof(HarmonyPatches), nameof(MoldAttractionPoint));
        static readonly MethodInfo FIND_MOLD_SPORES = AccessTools.Method(typeof(HarmonyPatches), nameof(FindMoldSpores));
        static readonly MethodInfo FIND_GAME_OBJECT_WITH_TAG = AccessTools.Method(typeof(GameObject), nameof(GameObject.FindGameObjectWithTag));
        static readonly MethodInfo FIND_GAME_OBJECTS_WITH_TAG = AccessTools.Method(typeof(GameObject), nameof(GameObject.FindGameObjectsWithTag));
        [HarmonyPatch(typeof(MoldSpreadManager), "GenerateMold")]
        [HarmonyPatch(typeof(BushWolfEnemy), nameof(BushWolfEnemy.GetBiggestWeedPatch))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> CacheMoldGameObjects(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            List<CodeInstruction> codes = instructions.ToList();

            for (int i = 1; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Call)
                {
                    MethodInfo methodInfo = codes[i].operand as MethodInfo; // constructors apparently cause problems if you just cast with (MethodInfo)
                    if (methodInfo == FIND_GAME_OBJECT_WITH_TAG && codes[i - 1].opcode == OpCodes.Ldstr && (string)codes[i - 1].operand == "MoldAttractionPoint")
                    {
                        codes[i].operand = MOLD_ATTRACTION_POINT;

                        // would remove instead, but that breaks labels in GetBiggestWeedPatch, and probably isn't worth fixing
                        codes[i - 1].opcode = OpCodes.Nop;
                        //codes[i - 1].operand = null;

                        Plugin.StaticLogger.LogDebug($"Use cached MoldAttractionPoint in {__originalMethod.DeclaringType}.{__originalMethod.Name}");
                    }
                    else if (methodInfo == FIND_GAME_OBJECTS_WITH_TAG && codes[i - 1].opcode == OpCodes.Ldstr && (string)codes[i - 1].operand == "MoldSpore")
                    {
                        codes[i].operand = FIND_MOLD_SPORES;

                        codes[i - 1].opcode = OpCodes.Nop;
                        //codes[i - 1].operand = null;

                        Plugin.StaticLogger.LogDebug($"Use cached MoldSpore game objects in {__originalMethod.DeclaringType}.{__originalMethod.Name}");
                    }
                }
            }

            //Plugin.Logger.LogWarning($"{__originalMethod.Name} transpiler failed");
            return codes;
        }
    }
}