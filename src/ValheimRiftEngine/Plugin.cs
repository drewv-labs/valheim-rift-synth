using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using ValheimRiftEngine.Models;
using ValheimRiftEngine.Runtime;

namespace ValheimRiftEngine;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.jimmylab.valheimriftengine";
    public const string PluginName = "ValheimRiftEngine";
    public const string PluginVersion = "0.1.0";

    internal static Plugin? Instance { get; private set; }
    internal static ManualLogSource Log { get; private set; } = null!;
    internal static RiftDirector? Director { get; private set; }
    internal static bool RequireAdminForAbort => _cfgRequireAdminForAbort?.Value ?? false;

    private static ConfigEntry<bool>? _cfgRequireAdminForAbort;
    private static ConfigEntry<bool>? _cfgSeedDefaultManifest;

    private Harmony? _harmony;
    private FileSystemWatcher? _watcher;
    private Coroutine? _hotReloadCoroutine;
    private string _manifestDir = string.Empty;
    private string _manifestFilePath = string.Empty;
    private readonly object _reloadSync = new();
    private bool _watcherTriggeredReload;
    private DateTime _watcherDebounceUntilUtc = DateTime.MinValue;
    private DateTime _lastObservedWriteUtc = DateTime.MinValue;

    private static readonly string[] AuditedPrefabs =
    {
        // Structural & Interactive
        "blackmarble_floor_large", "blackmarble_2x2x1", "blackmarble_column_2",
        "dvergrprops_lantern_standing", "piece_brazierfloor01", "iron_grate",
        "piece_chest_wood", "portal_wood",
        // VFX
        "fx_Lightning", "fx_chainlightning_hit", "vfx_FireWork_SurtlingCore",
        "fx_blobLava_explosion", "vfx_blocked", "vfx_FireballHit", "vfx_offering",
        // Sandbox Kit
        "HelmetCarapace", "ArmorCarapaceChest", "ArmorCarapaceLegs", "CapeFeather",
        "Battleaxe", "ShieldCarapace", "MisthareSupreme", "MeatPlatter",
        "MushroomOmelette", "MeadHealthMajor", "MeadStaminaMedium",
        // 36 Curated Creatures
        "Greydwarf_Elite", "Greydwarf_Shaman", "Skeleton_Poison", "Skeleton_NoArcher", "Ghost", "Troll",
        "Draugr_Elite", "Draugr_Ranged", "BlobElite", "Wraith", "Abomination", "Surtling",
        "Wolf", "Ulv", "Fenring", "Fenring_Cultist", "StoneGolem", "Hatchling",
        "Goblin", "GoblinArcher", "GoblinBrute", "GoblinShaman", "BlobTar", "Lox",
        "Dverger", "DvergerMageFire", "DvergerMageIce", "DvergerMageSupport", "Seeker", "SeekerBrood", "SeekerBrute",
        "Charred_Twitcher", "Charred_Archer", "Charred_Melee", "Charred_Mage", "Morgen",
        // 26 Curated Rewards
        "Coins", "Ruby", "AmberPearl", "Thunderstone", "Bloodbag", "BombOoze", "Bilebag",
        "MeadEitrMinor", "Iron", "Silver", "BlackMetal", "Carapace", "Eitr", "CelestialFeather",
        "SwordSilver", "AtgeirBlackmetal", "SledgeDemolisher", "SwordMistwalker", "THSwordKrom",
        "StaffFireball", "StaffIceShards",
    };

    private void Awake()
    {
        Instance = this;
        Log = Logger;

        _cfgRequireAdminForAbort = Config.Bind(
            "Permissions",
            "RequireAdminForAbort",
            false,
            "If true, only server admins in adminlist.txt may run /rift abort.");

        _cfgSeedDefaultManifest = Config.Bind(
            "Manifest",
            "SeedDefaultManifestIfMissing",
            true,
            "Writes a validated starter active_rift.json if none exists on startup.");

        var affixController = new AffixController(this);
        Director = new RiftDirector(this, affixController);

        SetupManifestDirectoryAndWatcher();

        _harmony = new Harmony(PluginGuid);
        _harmony.PatchAll(typeof(Plugin).Assembly);

        _hotReloadCoroutine = StartCoroutine(HotReloadWatchLoop());

        Log.LogInfo($"Loaded v{PluginVersion}");
    }

    private void OnDestroy()
    {
        if (_hotReloadCoroutine != null)
        {
            StopCoroutine(_hotReloadCoroutine);
            _hotReloadCoroutine = null;
        }

        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        Director?.ShutdownCleanup();
        _harmony?.UnpatchSelf();
    }

    public bool ReloadManifestImmediate(out string summary)
    {
        try
        {
            if (!File.Exists(_manifestFilePath))
            {
                summary = $"Manifest file not found at '{_manifestFilePath}'.";
                Log.LogWarning($"[HotReload] {summary}");
                return false;
            }

            string json = ReadFileWithSharedAccess(_manifestFilePath);
            _lastObservedWriteUtc = File.GetLastWriteTimeUtc(_manifestFilePath);

            if (!RiftManifest.TryParseAndValidate(json, ZNetScene.instance, out var manifest, out var errors) ||
                manifest == null)
            {
                summary = $"Validation failed ({errors.Count} error(s)): {string.Join("; ", errors)}";
                Log.LogError($"[HotReload] Rejected active_rift.json — {summary}");
                return false;
            }

            Director?.SetLoadedManifest(manifest);
            string znetStatus = ZNetScene.instance != null ? "prefabs verified vs ZNetScene" : "pre-ZNetScene";
            summary =
                $"Validated & staged '{manifest.Title}' (id={manifest.RiftId}, +{manifest.KeystoneLevel}, " +
                $"rooms={manifest.Rooms.Count}, Z={manifest.ComputeTotalZSpanMeters():F0}m, " +
                $"affixes=[{string.Join(", ", manifest.GlobalAffixes)}], {znetStatus})";
            Log.LogInfo($"[HotReload] {summary}");
            return true;
        }
        catch (Exception ex)
        {
            summary = $"Exception reading '{_manifestFilePath}': {ex.Message}";
            Log.LogError($"[HotReload] {summary}");
            return false;
        }
    }

    [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
    private static class ZNetSceneReadyPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ZNetScene __instance)
        {
            var missing = new List<string>();
            foreach (string name in AuditedPrefabs)
            {
                if (__instance.GetPrefab(name) == null)
                {
                    missing.Add(name);
                }
            }

            if (missing.Count == 0)
            {
                Log.LogInfo($"[PrefabAudit] Verified {AuditedPrefabs.Length}/{AuditedPrefabs.Length} vanilla structural, VFX, kit, creature & reward prefabs in ZNetScene.");
            }
            else
            {
                Log.LogWarning($"[PrefabAudit] Missing {missing.Count} prefab(s) in ZNetScene: {string.Join(", ", missing)}");
            }

            Instance?.ReloadManifestImmediate(out _);
        }
    }

    private void SetupManifestDirectoryAndWatcher()
    {
        _manifestDir = Path.Combine(Paths.ConfigPath, "ValheimRiftEngine");
        Directory.CreateDirectory(_manifestDir);
        _manifestFilePath = Path.Combine(_manifestDir, "active_rift.json");

        if ((_cfgSeedDefaultManifest?.Value ?? true) && !File.Exists(_manifestFilePath))
        {
            File.WriteAllText(_manifestFilePath, GetDefaultStarterManifestJson());
            Log.LogInfo($"[HotReload] Seeded default starter manifest at '{_manifestFilePath}'.");
        }

        if (File.Exists(_manifestFilePath))
        {
            ReloadManifestImmediate(out _);
        }

        try
        {
            _watcher = new FileSystemWatcher(_manifestDir, "active_rift.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnManifestFsEvent;
            _watcher.Created += OnManifestFsEvent;
            _watcher.Renamed += OnManifestFsEvent;
            Log.LogInfo($"[HotReload] Watching '{_manifestFilePath}' for live Rift updates.");
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[HotReload] FileSystemWatcher fallback to timestamp polling ({ex.Message}).");
        }
    }

    private void OnManifestFsEvent(object sender, FileSystemEventArgs e)
    {
        lock (_reloadSync)
        {
            _watcherTriggeredReload = true;
            _watcherDebounceUntilUtc = DateTime.UtcNow.AddMilliseconds(350);
        }
    }

    private IEnumerator HotReloadWatchLoop()
    {
        var wait = new WaitForSeconds(1.0f);
        while (true)
        {
            yield return wait;

            bool shouldReload = false;
            lock (_reloadSync)
            {
                if (_watcherTriggeredReload && DateTime.UtcNow >= _watcherDebounceUntilUtc)
                {
                    _watcherTriggeredReload = false;
                    shouldReload = true;
                }
            }

            if (!shouldReload && File.Exists(_manifestFilePath))
            {
                DateTime writeUtc = File.GetLastWriteTimeUtc(_manifestFilePath);
                if (writeUtc > _lastObservedWriteUtc.AddMilliseconds(100))
                {
                    shouldReload = true;
                }
            }

            if (shouldReload)
            {
                ReloadManifestImmediate(out _);
            }
        }
    }

    private static string ReadFileWithSharedAccess(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }

    private static string GetDefaultStarterManifestJson() => """
        {
          "rift_id": "dwarven-forge-starter",
          "title": "The Shattered Forge",
          "keystone_level": 3,
          "par_time_seconds": 360,
          "global_affixes": [
            "molten_slag",
            "thunder_blessing"
          ],
          "rooms": [
            {
              "room_index": 0,
              "template": "entry_vestibule",
              "pillar_layout": "none",
              "waves": []
            },
            {
              "room_index": 1,
              "template": "crucible_square",
              "pillar_layout": "four_corners",
              "waves": [
                {
                  "wave_index": 0,
                  "spawns": [
                    {
                      "prefab": "Dverger",
                      "anchor": "north_left",
                      "count": 2,
                      "stars": 0,
                      "is_boss": false
                    },
                    {
                      "prefab": "DvergerMageFire",
                      "anchor": "sniper_perch_ne",
                      "count": 1,
                      "stars": 1,
                      "is_boss": false
                    }
                  ]
                }
              ]
            },
            {
              "room_index": 2,
              "template": "sanctum_oct",
              "pillar_layout": "center_los_blocker",
              "waves": [
                {
                  "wave_index": 0,
                  "spawns": [
                    {
                      "prefab": "SeekerBrute",
                      "anchor": "center",
                      "count": 1,
                      "stars": 1,
                      "is_boss": true
                    },
                    {
                      "prefab": "DvergerMageFire",
                      "anchor": "sniper_perch_nw",
                      "count": 1,
                      "stars": 0,
                      "is_boss": false
                    }
                  ]
                }
              ]
            }
          ],
          "rewards": {
            "base_drops": [
              {
                "prefab": "Coins",
                "amount": 150,
                "quality": 1
              },
              {
                "prefab": "Eitr",
                "amount": 15,
                "quality": 1
              }
            ],
            "timed_bonus_drops": [
              {
                "prefab": "AtgeirBlackmetal",
                "amount": 1,
                "quality": 3
              },
              {
                "prefab": "Thunderstone",
                "amount": 5,
                "quality": 1
              }
            ]
          }
        }
        """;
}
