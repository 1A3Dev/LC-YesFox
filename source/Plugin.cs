using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace YesFox
{
    [BepInPlugin(modGUID, "YesFox", modVersion)]
    internal class Plugin : BaseUnityPlugin
    {
        internal const string modGUID = "Dev1A3.YesFox";
        internal const string modVersion = "1.0.0";

        private readonly Harmony harmony = new Harmony(modGUID);

        internal static ManualLogSource logSource;

        internal static List<GameObject> _networkPrefabs = new List<GameObject>();
        public static GameObject BushWolfPrefab { get; internal set; }

        internal static ConfigEntry<bool> Shroud_AllMoons;
        internal static ConfigEntry<float> Shroud_SpawnChance_SameMoon;
        internal static ConfigEntry<float> Shroud_SpawnChance_OtherMoons;
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
            BushWolfPrefab = BushWolfBundle.LoadAsset<GameObject>("Assets/LethalCompany/Game/Prefabs/EnemyAI/BushWolfEnemy.prefab");
            if (BushWolfPrefab != null)
            {
                if (!_networkPrefabs.Contains(BushWolfPrefab))
                    _networkPrefabs.Add(BushWolfPrefab);
                logSource.LogInfo("[AssetBundle] Successfully loaded prefab: BushWolfEnemy");
            }
            else
            {
                logSource.LogError("[AssetBundle] Failed to load prefab: BushWolfEnemy");
            }

            Shroud_AllMoons = Config.Bind("Weed Spawning", "All Moons", false, "Should weeds be able to spawn on all moons excluding gordion?");
            Shroud_SpawnChance_SameMoon = Config.Bind("Weed Spawning", "Spawn Chance (Current Moon)", 8.5f, new ConfigDescription("What should the chance for them to initially spawn the moon you are routed to be? Weeds attempt to spawn on all moons when you go into orbit after each day.", new AcceptableValueRange<float>(0, 100)));
            Shroud_SpawnChance_OtherMoons = Config.Bind("Weed Spawning", "Spawn Chance (Other Moons)", 4f, new ConfigDescription("What should the chance for them to initially spawn on other moons be? Weeds attempt to spawn on all moons when you go into orbit after each day.", new AcceptableValueRange<float>(0, 100)));

            Fox_MinimumWeeds = Config.Bind("Fox Spawning", "Minimum Weeds", 30, "The minimum amount of weeds required to spawn");
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

        [HarmonyPatch(typeof(StartOfRound), "SetPlanetsMold")]
        [HarmonyPrefix]
        public static bool SetPlanetsMold(StartOfRound __instance)
        {
            if (!__instance.IsOwner) return false;

            System.Random random = new System.Random(__instance.randomMapSeed + 32);
            Terminal terminal = Object.FindFirstObjectByType<Terminal>();
            for (int i = 0; i < __instance.levels.Length; i++)
            {
                if (i == 3 || (!__instance.levels[i].canSpawnMold && !Plugin.Shroud_AllMoons.Value))
                {
                    continue;
                }

                if (__instance.levels[i].moldSpreadIterations > 0)
                {
                    __instance.levels[i].moldSpreadIterations++;
                    Plugin.logSource.LogInfo($"Increasing level #{i} {__instance.levels[i].PlanetName} mold iterations by 1; risen to {__instance.levels[i].moldSpreadIterations}");
                    continue;
                }

                float num;
                if (__instance.levels[i] == __instance.currentLevel)
                {
                    num = Plugin.Shroud_SpawnChance_SameMoon.Value / 100f;
                }
                else if (terminal.groupCredits < 200 && __instance.levels[i].levelID == 12)
                {
                    num = 0.05f;
                }
                else if ((float)terminal.groupCredits < 500f && (__instance.levels[i].levelID == 7 || __instance.levels[i].levelID == 6 || __instance.levels[i].levelID >= 10) && (__instance.currentLevel.levelID == 5 || __instance.currentLevel.levelID == 8 || __instance.currentLevel.levelID == 4 || __instance.currentLevel.levelID <= 2))
                {
                    num = 0.02f;
                }
                else
                {
                    num = Plugin.Shroud_SpawnChance_OtherMoons.Value / 100f;
                }
                if (random.Next(0, 100) <= (int)(num * 100f))
                {
                    __instance.levels[i].moldSpreadIterations += random.Next(1, 3);
                    Plugin.logSource.LogInfo($"Increasing level #{i} {__instance.levels[i].PlanetName} mold iterations for the first time; risen to {__instance.levels[i].moldSpreadIterations}");
                }
            }

            return false;
        }

        // v64
        public static System.Random WeedEnemySpawnRandom;
        public static List<SpawnableEnemyWithRarity> WeedEnemies = new List<SpawnableEnemyWithRarity>();

        [HarmonyPatch(typeof(StartOfRound), "Start")]
        [HarmonyPostfix]
        private static void StartOfRound_Start()
        {
            WeedEnemies.Clear();
            GenerateWeedEnemiesList();
        }

        public static void GenerateWeedEnemiesList()
        {
            if (WeedEnemies.Count > 0) return;

            EnemyType[] enemyTypes = Resources.FindObjectsOfTypeAll<EnemyType>().Where(x => x.name == "BushWolf").ToArray();
            foreach (EnemyType enemyType in enemyTypes)
            {
                if (enemyType.name == "BushWolf")
                {
                    if (enemyType.enemyPrefab == Plugin.BushWolfPrefab)
                    {
                        continue;
                    }

                    if (GameNetworkManager.Instance.gameVersionNum >= 64)
                    {
                        SkinnedMeshRenderer[] renderersOrig = enemyType.enemyPrefab?.GetComponentsInChildren<SkinnedMeshRenderer>();
                        SkinnedMeshRenderer[] renderersNew = Plugin.BushWolfPrefab?.GetComponentsInChildren<SkinnedMeshRenderer>();
                        foreach (SkinnedMeshRenderer renderer in renderersNew)
                        {
                            renderer.material = renderersOrig[0].material;
                            renderer.materials = renderersOrig[0].materials;
                        }
                        enemyType.enemyPrefab = Plugin.BushWolfPrefab;
                        Plugin.logSource.LogInfo("Replaced Bush Wolf Prefab");
                    }
                    else
                    {
                        Plugin.BushWolfPrefab = enemyType.enemyPrefab;
                    }
                }

                WeedEnemies.Add(new SpawnableEnemyWithRarity()
                {
                    enemyType = enemyType,
                    rarity = 100,
                });
            }
        }

        public static void SpawnWeedEnemies(int currentHour)
        {
            MoldSpreadManager moldSpreadManager = Object.FindFirstObjectByType<MoldSpreadManager>();
            int num = 0;
            if (moldSpreadManager != null)
            {
                num = moldSpreadManager.generatedMold.Count;
            }
            if (num <= Plugin.Fox_MinimumWeeds.Value)
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
                enemyType2.numberSpawned++;
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

        // Fixed aggressivePosition not being set if there isn't a MoldAttractionPoint
        [HarmonyPatch(typeof(BushWolfEnemy), "GetBiggestWeedPatch")]
        [HarmonyPostfix]
        public static void GetBiggestWeedPatch(BushWolfEnemy __instance, bool __result)
        {
            if (__result && !GameObject.FindGameObjectWithTag("MoldAttractionPoint"))
            {
                __instance.aggressivePosition = __instance.mostHiddenPosition;
            }
        }
    }
}