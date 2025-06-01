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

        internal static ManualLogSource logSource;

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

        private void Awake()
        {
            logSource = Logger;

            Assembly patches = Assembly.GetExecutingAssembly();
            harmony.PatchAll(patches);
            logSource.LogInfo("Patches Loaded");

            AssetBundle BushWolfBundle = AssetBundle.LoadFromFile(Path.Combine(Path.GetDirectoryName(Info.Location), "bush_wolf"));
            if (BushWolfBundle == null)
            {
                logSource.LogError("[AssetBundle] Failed to load asset bundle: bush_wolf");
                return;
            }
            BushWolfAddonPrefab = BushWolfBundle.LoadAsset<GameObject>("Assets/LethalCompany/Game/Prefabs/EnemyAI/BushWolfEnemy.prefab");
            if (BushWolfAddonPrefab != null)
            {
                if (!_networkPrefabs.Contains(BushWolfAddonPrefab))
                    _networkPrefabs.Add(BushWolfAddonPrefab);
                logSource.LogInfo("[AssetBundle] Successfully loaded prefab: BushWolfEnemy");
            }
            else
            {
                logSource.LogError("[AssetBundle] Failed to load prefab: BushWolfEnemy");
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

        [HarmonyPatch(typeof(TimeOfDay), "OnDayChanged")]
        [HarmonyPostfix]
        public static void OnDayChanged(TimeOfDay __instance)
        {
            if (!__instance.IsOwner || StartOfRound.Instance.isChallengeFile) return;
            
            System.Random random = new System.Random(StartOfRound.Instance.randomMapSeed + 32);
            Terminal terminal = Object.FindAnyObjectByType<Terminal>();
            for (int i = 0; i < StartOfRound.Instance.levels.Length; i++)
            {
                if (i == 3 || (!StartOfRound.Instance.levels[i].canSpawnMold && !Plugin.Shroud_AllMoons.Value))
                {
                    Plugin.logSource.LogInfo($"Skipping level #{i} {StartOfRound.Instance.levels[i].PlanetName} mold iterations");
                    continue;
                }

                if (StartOfRound.Instance.levels[i].moldSpreadIterations > 0)
                {
                    if (StartOfRound.Instance.levels[i].moldSpreadIterations < Plugin.Shroud_MaximumIterations.Value)
                    {
                        float chance = (StartOfRound.Instance.levels[i] == StartOfRound.Instance.currentLevel ? Plugin.Shroud_GrowChance_SameMoon.Value : Plugin.Shroud_GrowChance_OtherMoons.Value) / 100f;
                        if (random.NextDouble() <= chance)
                        {
                            StartOfRound.Instance.levels[i].moldSpreadIterations++;
                            Plugin.logSource.LogInfo($"Increasing level #{i} {StartOfRound.Instance.levels[i].PlanetName} mold iterations by 1; risen to {StartOfRound.Instance.levels[i].moldSpreadIterations}");
                        }
                    }
                    continue;
                }

                float num;
                if (StartOfRound.Instance.levels[i] == StartOfRound.Instance.currentLevel)
                {
                    num = Plugin.Shroud_SpawnChance_SameMoon.Value / 100f;
                }
                else
                {
                    num = Plugin.Shroud_SpawnChance_OtherMoons.Value / 100f;
                    if (terminal.groupCredits < 200 && StartOfRound.Instance.levels[i].levelID == 12)
                    {
                        num *= 1.25f; // 0.04 -> 0.05 (vanilla)
                    }
                    else if (terminal.groupCredits < 500 && (StartOfRound.Instance.levels[i].levelID == 7 || StartOfRound.Instance.levels[i].levelID == 6 || StartOfRound.Instance.levels[i].levelID >= 10) && (StartOfRound.Instance.currentLevel.levelID == 5 || StartOfRound.Instance.currentLevel.levelID == 8 || StartOfRound.Instance.currentLevel.levelID == 4 || StartOfRound.Instance.currentLevel.levelID <= 2))
                    {
                        num *= 0.5f; // 0.04 -> 0.02 (vanilla)
                    }
                }
                if (random.NextDouble() <= num)
                {
                    StartOfRound.Instance.levels[i].moldSpreadIterations += random.Next(1, 3);
                    Plugin.logSource.LogInfo($"Increasing level #{i} {StartOfRound.Instance.levels[i].PlanetName} mold iterations for the first time; risen to {StartOfRound.Instance.levels[i].moldSpreadIterations}");
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

            if (!__instance.currentLevel.canSpawnMold && !Plugin.Shroud_AllMoons.Value)
            {
                __instance.currentLevel.moldSpreadIterations = 0;
                __instance.currentLevel.moldStartPosition = -1;
                return;
            }

            // retroactively apply iteration cap to old save files
            if (__instance.currentLevel.moldSpreadIterations > Plugin.Shroud_MaximumIterations.Value)
            {
                __instance.currentLevel.moldSpreadIterations = Plugin.Shroud_MaximumIterations.Value;
            }

            GameObject[] outsideAINodes = GameObject.FindGameObjectsWithTag("OutsideAINode");
            if (outsideAINodes == null || outsideAINodes.Length < 1)
                return;

            Vector3 shipPos = new(1.27146339f, 0.278438568f, -7.5f); // StartOfRound.elevatorTransform position, when fully landed

            outsideAINodes = [.. outsideAINodes.OrderBy(x => Vector3.Distance(x.transform.position, shipPos))];

            // starting point has already been chosen
            if (__instance.currentLevel.moldStartPosition >= 0 && __instance.currentLevel.moldStartPosition < outsideAINodes.Length)
            {
                float shipDist = Vector3.Distance(outsideAINodes[__instance.currentLevel.moldStartPosition].transform.position, shipPos);

                // spot chosen is already an acceptable distance from the ship
                if (shipDist >= 30f)
                    return;

                Plugin.logSource.LogInfo($"Mold growth is starting from node #{__instance.currentLevel.moldStartPosition} which is too close to the ship ({shipDist} < 30)");
            }

            // starting point has not been chosen, or was invalid

            System.Random random = new System.Random(__instance.randomMapSeed + 2017);
            int temp = __instance.currentLevel.moldSpreadIterations; // preserve
            for (int i = 0; i < outsideAINodes.Length; i++)
            {
                float shipDist = Vector3.Distance(outsideAINodes[i].transform.position, shipPos);
                // greater than 40 units and selected randomly
                if (shipDist >= Plugin.Shroud_MinimumDistance.Value && (random.Next(100) < 13 || outsideAINodes.Length - i < 20))
                {
                    Plugin.logSource.LogDebug($"Mold growth: outsideAINodes[{i}] is candidate (ship dist: {shipDist} > {Plugin.Shroud_MinimumDistance.Value})");
                    // furthest distance in vanilla is on Artifice (7.88802)
                    if (Physics.Raycast(outsideAINodes[i].transform.position, Vector3.down, out RaycastHit hit, 8f, StartOfRound.Instance.collidersAndRoomMaskAndDefault, QueryTriggerInteraction.Ignore))
                    {
                        // final test... can the fox spawn here?

                        // must simulate weed growth, there aren't any shortcuts for this
                        MoldSpreadManager.GenerateMold(outsideAINodes[i].transform.position, Plugin.Shroud_MaximumIterations.Value);
                        int amt = MoldSpreadManager.generatedMold.Count;
                        MoldSpreadManager.RemoveAllMold();

                        if (amt >= Plugin.Fox_MinimumWeeds.Value)
                        {
                            __instance.currentLevel.moldStartPosition = i;
                            __instance.currentLevel.moldSpreadIterations = temp;
                            Plugin.logSource.LogInfo($"Mold growth: Selected outsideAINodes[{i}]: coords {outsideAINodes[i].transform.position}, dist {shipDist}");
                            return;
                        }
                        else
                            Plugin.logSource.LogDebug($"Mold growth: outsideAINodes[{i}] rejected (max weeds: {amt} < {Plugin.Fox_MinimumWeeds.Value})");
                    }
                    else
                        Plugin.logSource.LogDebug($"Mold growth: outsideAINodes[{i}] rejected (no ground)");
                }
            }

            __instance.currentLevel.moldSpreadIterations = 0;
            __instance.currentLevel.moldStartPosition = -1;
            Plugin.logSource.LogInfo($"Level \"{__instance.currentLevel.PlanetName}\" has no valid AI nodes");
        }

        // v64
        public static System.Random WeedEnemySpawnRandom;
        public static List<SpawnableEnemyWithRarity> WeedEnemies = new List<SpawnableEnemyWithRarity>();

        [HarmonyPatch(typeof(StartOfRound), "Start")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void StartOfRound_Start()
        {
            GenerateWeedEnemiesList();
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
                            Plugin.logSource.LogInfo("[GenerateWeedEnemiesList] BushWolf: Replaced addon EnemyAI enemyType");
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
                        Plugin.logSource.LogInfo("[GenerateWeedEnemiesList] BushWolf: Replaced original EnemyType prefab");
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
                Plugin.logSource.LogError(e);
            }
        }

        public static void SpawnWeedEnemies(int currentHour)
        {
            int num = 0;
            if (MoldSpreadManager != null)
            {
                num = MoldSpreadManager.generatedMold.Count(x => x != null && x.activeSelf);
            }
            if (num < Plugin.Fox_MinimumWeeds.Value)
            {
                Plugin.logSource.LogDebug($"Weed enemies attempted to spawn but were denied. Reason: WeedCount | Amount: {num}");
                return;
            }
            if (Plugin.Fox_SpawnChance.Value >= 0)
            {
                int spawnChance = WeedEnemySpawnRandom.Next(1, 100);
                if (spawnChance > Plugin.Fox_SpawnChance.Value)
                {
                    Plugin.logSource.LogDebug($"Weed enemies attempted to spawn but were denied. Reason: SpawnChance | Amount: {spawnChance}");
                    return;
                }
            }
            else
            {
                int spawnChance = WeedEnemySpawnRandom.Next(0, 80);
                if (spawnChance > num)
                {
                    Plugin.logSource.LogDebug($"Weed enemies attempted to spawn but were denied. Reason: SpawnChance | Amount: {spawnChance}");
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
                    Plugin.logSource.LogError($"Weed enemies attempted to spawn but were denied. Reason: ListEmpty");
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
                    Plugin.logSource.LogDebug($"A weed enemy attempted to spawn but was denied. Reason: Probability | Amount: 0");
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
                Plugin.logSource.LogDebug($"A weed enemy attempted to spawn but was denied. Reason: SpawnRate | Amount: {num}");
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
                    Plugin.logSource.LogDebug($"A weed enemy attempted to spawn but was denied. Reason: PowerLevel | Amount: {currentOutsideEnemyPower}");
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
            Plugin.logSource.LogDebug($"{enemyType2.enemyName} attempted to spawn and was allowed");
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
                SpawnWeedEnemies(__instance.currentHour + __instance.hourTimeBetweenEnemySpawnBatches);
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
                Plugin.logSource.LogInfo($"Mold growth on \"{StartOfRound.Instance.currentLevel.PlanetName}\" erroneously reset from {iterations} iterations");
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
                        Plugin.logSource.LogDebug($"Use cached VehicleController in Kidnapper Fox AI");
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
                        Plugin.logSource.LogDebug($"Use cached MoldSpreadManager in {__originalMethod.DeclaringType}.{__originalMethod.Name}");
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

                        Plugin.logSource.LogDebug($"Use cached MoldAttractionPoint in {__originalMethod.DeclaringType}.{__originalMethod.Name}");
                    }
                    else if (methodInfo == FIND_GAME_OBJECTS_WITH_TAG && codes[i - 1].opcode == OpCodes.Ldstr && (string)codes[i - 1].operand == "MoldSpore")
                    {
                        codes[i].operand = FIND_MOLD_SPORES;

                        codes[i - 1].opcode = OpCodes.Nop;
                        //codes[i - 1].operand = null;

                        Plugin.logSource.LogDebug($"Use cached MoldSpore game objects in {__originalMethod.DeclaringType}.{__originalMethod.Name}");
                    }
                }
            }

            //Plugin.Logger.LogWarning($"{__originalMethod.Name} transpiler failed");
            return codes;
        }
    }
}