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

        internal static ConfigEntry<int> MinimumWeeds;
        internal static ConfigEntry<int> SpawnChance;

        private void Awake()
        {
            logSource = Logger;

            Assembly patches = Assembly.GetExecutingAssembly();
            harmony.PatchAll(patches);
            logSource.LogInfo("Patched SetPlanetsMold");

            MinimumWeeds = Config.Bind("Fox Spawning", "Minimum Weeds", 30, "The minimum amount of weeds required to spawn");
            SpawnChance = Config.Bind("Fox Spawning", "Spawn Chance", -1, new ConfigDescription("What should the spawn chance be? If left as -1 then it will be the same as vanilla (a higher chance the more weeds there are)", new AcceptableValueRange<int>(-1, 100)));

            AssetBundle BushWolfBundle = AssetBundle.LoadFromFile(Path.Combine(Path.GetDirectoryName(Info.Location), "bush_wolf"));
            if (BushWolfBundle == null)
            {
                logSource.LogInfo("Asset bundle not found");
                return;
            }

            BushWolfPrefab = BushWolfBundle.LoadAsset<GameObject>("Assets/LethalCompany/Game/Prefabs/EnemyAI/BushWolfEnemy.prefab");
            if (BushWolfPrefab != null)
            {
                if (!_networkPrefabs.Contains(BushWolfPrefab))
                    _networkPrefabs.Add(BushWolfPrefab);
                logSource.LogInfo("Loaded Bush Wolf prefab");
            }
            else
            {
                logSource.LogInfo("Failed to load Bush Wolf prefab");
            }
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
                if (!__instance.levels[i].canSpawnMold)
                {
                    continue;
                }

                if (__instance.levels[i].moldSpreadIterations > 0)
                {
                    __instance.levels[i].moldSpreadIterations++;
                    Plugin.logSource.LogInfo($"Increasing level #{i} {__instance.levels[i].PlanetName} mold iterations by 1; risen to {__instance.levels[i].moldSpreadIterations}");
                    continue;
                }

                float num = ((__instance.levels[i] == __instance.currentLevel) ? 0.085f : ((terminal.groupCredits < 200 && __instance.levels[i].levelID == 12) ? 0.05f : ((!((float)terminal.groupCredits < 500f) || (__instance.levels[i].levelID != 7 && __instance.levels[i].levelID != 6 && __instance.levels[i].levelID < 10) || (__instance.currentLevel.levelID != 5 && __instance.currentLevel.levelID != 8 && __instance.currentLevel.levelID != 4 && __instance.currentLevel.levelID > 2)) ? 0.04f : 0.02f)));
                if (random.Next(0, 100) <= (int)(num * 100f))
                {
                    __instance.levels[i].moldSpreadIterations += random.Next(1, 3);
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
            EnemyType[] enemyTypes = Resources.FindObjectsOfTypeAll<EnemyType>().Where(x => x.name == "BushWolf").ToArray();
            foreach (EnemyType enemyType in enemyTypes)
            {
                if (enemyType.name == "BushWolf" && enemyType.enemyPrefab != Plugin.BushWolfPrefab)
                {
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
                    rarity = 300,
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
            if (num <= Plugin.MinimumWeeds.Value)
            {
                return;
            }
            // 0 <= 0
            if (Plugin.SpawnChance.Value >= 0 ? WeedEnemySpawnRandom.Next(1, 100) > Plugin.SpawnChance.Value : WeedEnemySpawnRandom.Next(0, 80) > num)
            {
                return;
            }
            int num2 = WeedEnemySpawnRandom.Next(1, 3);
            GameObject[] spawnPoints = GameObject.FindGameObjectsWithTag("OutsideAINode");
            float timeUpToCurrentHour = TimeOfDay.Instance.lengthOfHours * (float)currentHour;

            if (WeedEnemies.Count == 0)
            {
                Plugin.logSource.LogError("Could not find any WeedEnemies");
                return;
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
                    continue;
                }
                int num2 = ((RoundManager.Instance.increasedOutsideEnemySpawnRateIndex == i) ? 100 : ((!enemyType.useNumberSpawnedFalloff) ? ((int)((float)RoundManager.Instance.currentLevel.OutsideEnemies[i].rarity * enemyType.probabilityCurve.Evaluate(timeUpToCurrentHour / RoundManager.Instance.timeScript.totalTime))) : ((int)((float)RoundManager.Instance.currentLevel.OutsideEnemies[i].rarity * (enemyType.probabilityCurve.Evaluate(timeUpToCurrentHour / RoundManager.Instance.timeScript.totalTime) * enemyType.numberSpawnedFalloff.Evaluate((float)enemyType.numberSpawned / 10f))))));
                if (enemyType.spawnFromWeeds)
                {
                    num2 = (int)Mathf.Clamp((float)num2 * ((float)numberOfWeeds / 60f), 0f, 200f);
                }
                RoundManager.Instance.SpawnProbabilities.Add(num2);
                num += num2;
            }
            RoundManager.Instance.firstTimeSpawningWeedEnemies = false;
            if (num <= 20)
            {
                _ = RoundManager.Instance.currentOutsideEnemyPower;
                _ = RoundManager.Instance.currentMaxOutsidePower;
                return false;
            }
            bool result = false;
            int randomWeightedIndex = RoundManager.Instance.GetRandomWeightedIndex(RoundManager.Instance.SpawnProbabilities.ToArray(), WeedEnemySpawnRandom);
            EnemyType enemyType2 = WeedEnemies[randomWeightedIndex].enemyType;
            float num3 = Mathf.Max(enemyType2.spawnInGroupsOf, 1);
            for (int j = 0; (float)j < num3; j++)
            {
                if (enemyType2.PowerLevel > RoundManager.Instance.currentMaxOutsidePower - RoundManager.Instance.currentOutsideEnemyPower)
                {
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
            Plugin.logSource.LogInfo("Spawned weed enemy: " + enemyType2.enemyName);
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
    }
}