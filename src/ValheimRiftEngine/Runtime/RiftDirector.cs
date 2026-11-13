using System;
using System.Collections;
using System.Collections.Generic;
using Splatform;
using UnityEngine;
using ValheimRiftEngine.Generation;
using ValheimRiftEngine.Models;

namespace ValheimRiftEngine.Runtime;

public enum RiftRunState
{
    Idle = 0,
    Countdown = 1,
    Active = 2,
    VictoryStaging = 3,
}

public sealed class SavedPlayerOrigin
{
    public long PeerUid { get; set; }
    public string PlayerName { get; init; } = string.Empty;
    public Vector3 ReturnPosition { get; init; }
    public Quaternion ReturnRotation { get; init; }
}

/// <summary>
/// Server-authoritative Mythic+ Rift state machine, wave gate controller, Keystone timer,
/// loot chest generator, and sandbox kit dispenser.
/// </summary>
public sealed class RiftDirector
{
    private const float MonitorTickSeconds = 0.5f;
    private const float PartyGatherRadiusMeters = 20.0f;
    private const int CountdownSeconds = 5;

    private readonly MonoBehaviour _coroutineHost;
    private readonly InstanceCleaner _cleaner = new();
    private readonly Dictionary<long, SavedPlayerOrigin> _participants = new();
    private readonly Dictionary<string, SavedPlayerOrigin> _participantsByName = new(StringComparer.OrdinalIgnoreCase);

    private RiftManifest? _loadedManifest;
    private RiftManifest? _activeManifest;
    private BuiltRiftInstance? _builtRift;
    private Coroutine? _runCoroutine;
    private Vector3 _previousServerRefPos = Vector3.zero;
    private bool _previousServerPublicRefPos;

    private int _currentRoomIndex;
    private int _currentWaveIndex;
    private int _totalKills;
    private DateTime _keystoneStartUtc;
    private double _finalElapsedSeconds;

    public RiftDirector(MonoBehaviour coroutineHost, AffixController affixController)
    {
        _coroutineHost = coroutineHost;
        Affixes = affixController;
    }

    public AffixController Affixes { get; }

    public RiftRunState State { get; private set; } = RiftRunState.Idle;

    public RiftManifest? LoadedManifest => _loadedManifest;

    public RiftManifest? ActiveManifest => _activeManifest;

    public void SetLoadedManifest(RiftManifest manifest)
    {
        _loadedManifest = manifest;
    }

    public string GetStatusSummary()
    {
        string loadedInfo = _loadedManifest == null
            ? "Loaded Manifest: <none>"
            : $"Loaded: '{_loadedManifest.Title}' [{_loadedManifest.RiftId}] (+{_loadedManifest.KeystoneLevel}, par {_loadedManifest.ParTimeSeconds}s, rooms={_loadedManifest.Rooms.Count}, Z={_loadedManifest.ComputeTotalZSpanMeters():F0}m, affixes={string.Join(",", _loadedManifest.GlobalAffixes)})";

        if (State == RiftRunState.Idle || _activeManifest == null)
        {
            return $"[Rift] State: IDLE | {loadedInfo}";
        }

        double elapsed = State == RiftRunState.VictoryStaging
            ? _finalElapsedSeconds
            : Math.Max(0.0, (DateTime.UtcNow - _keystoneStartUtc).TotalSeconds);

        return $"[Rift] State: {State} | Active: '{_activeManifest.Title}' (+{_activeManifest.KeystoneLevel}) | " +
               $"Room {_currentRoomIndex + 1}/{_activeManifest.Rooms.Count} Wave {_currentWaveIndex + 1} | " +
               $"Mobs Alive: {Affixes.ActiveMobs.Count} | Kills: {_totalKills} | " +
               $"Timer: {FormatClock(elapsed)} / {FormatClock(_activeManifest.ParTimeSeconds)}";
    }

    public bool TryStartRift(long callerPeerUid, out string message)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer())
        {
            message = "Rifts can only be started on the authoritative server.";
            return false;
        }

        if (_loadedManifest == null)
        {
            message = "No valid active_rift.json manifest is loaded. Run rift-synth or /rift reload first.";
            return false;
        }

        if (State == RiftRunState.Countdown || State == RiftRunState.Active)
        {
            message = $"A Rift ('{_activeManifest?.Title}') is already in progress. Use /rift abort first.";
            return false;
        }

        if (State == RiftRunState.VictoryStaging)
        {
            CleanupInstance(teleportRemainingParticipantsBack: true);
        }

        if (ZNetScene.instance != null)
        {
            foreach (var room in _loadedManifest.Rooms)
            {
                foreach (var wave in room.Waves)
                {
                    foreach (var sp in wave.Spawns)
                    {
                        if (ZNetScene.instance.GetPrefab(sp.Prefab) == null)
                        {
                            message = $"Manifest references unknown creature prefab '{sp.Prefab}'.";
                            return false;
                        }
                    }
                }
            }
        }

        if (!TryResolvePeerTransform(callerPeerUid, out string callerName, out Vector3 callerPos, out Quaternion callerRot))
        {
            message = "Could not locate caller player ZDO in world.";
            return false;
        }

        GatherNearbyParticipants(callerPeerUid, callerName, callerPos, callerRot);

        _activeManifest = _loadedManifest;
        _currentRoomIndex = 0;
        _currentWaveIndex = 0;
        _totalKills = 0;
        _finalElapsedSeconds = 0.0;

        Affixes.InitializeRun(_activeManifest, _cleaner);
        _builtRift = SkyboxArenaBuilder.Build(_activeManifest, callerPos, _cleaner);

        _previousServerRefPos = ZNet.instance.GetReferencePosition();
        _previousServerPublicRefPos = ZNet.instance.IsReferencePositionPublic();
        ZNet.instance.SetReferencePosition(_builtRift.Origin);
        ZNet.instance.SetPublicReferencePosition(false);

        int index = 0;
        foreach (var participant in _participants.Values)
        {
            float spreadX = ((index % 3) - 1) * 1.4f;
            Vector3 spawnPos = _builtRift.PlayerStagingPoint + new Vector3(spreadX, 0f, 0f);
            TeleportPeer(participant.PeerUid, spawnPos, Quaternion.identity);
            index++;
        }

        State = RiftRunState.Countdown;
        _runCoroutine = _coroutineHost.StartCoroutine(RunRiftLifecycleCoroutine());

        message = $"Teleported {_participants.Count} Viking(s) to '{_activeManifest.Title}' (+{_activeManifest.KeystoneLevel}) at Y={RiftConstants.SkyboxAltitudeY:F0}m!";
        Plugin.Log.LogInfo($"[RiftDirector] {message}");
        return true;
    }

    public bool AbortRift(out string message)
    {
        if (State == RiftRunState.Idle)
        {
            message = "No active Rift to abort.";
            return false;
        }

        string title = _activeManifest?.Title ?? "Rift";
        BroadcastCenterToParticipants($"{title} ABORTED — Returning to Midgard...");
        int count = CleanupInstance(teleportRemainingParticipantsBack: true);
        message = $"Aborted '{title}' and cleaned up {count} ZDOs.";
        Plugin.Log.LogInfo($"[RiftDirector] {message}");
        return true;
    }

    public bool TryLeaveRift(long callerPeerUid, string callerName, out string message)
    {
        SavedPlayerOrigin? saved = null;
        if (_participants.TryGetValue(callerPeerUid, out var byUid))
        {
            saved = byUid;
        }
        else if (!string.IsNullOrWhiteSpace(callerName) && _participantsByName.TryGetValue(callerName, out var byName))
        {
            saved = byName;
            saved.PeerUid = callerPeerUid;
        }

        if (saved == null)
        {
            message = "You do not have a saved pre-Rift origin.";
            return false;
        }

        TeleportPeer(callerPeerUid, saved.ReturnPosition + Vector3.up * 0.5f, saved.ReturnRotation);
        _participants.Remove(callerPeerUid);
        if (!string.IsNullOrWhiteSpace(saved.PlayerName))
        {
            _participantsByName.Remove(saved.PlayerName);
        }

        if (State == RiftRunState.VictoryStaging && _participants.Count == 0)
        {
            CleanupInstance(teleportRemainingParticipantsBack: false);
        }

        message = "Teleported back to your pre-Rift origin.";
        return true;
    }

    public bool GrantDwarfKit(long callerPeerUid, out string message)
    {
        if (ZNetScene.instance == null)
        {
            message = "ZNetScene is not ready.";
            return false;
        }

        if (!TryResolvePeerTransform(callerPeerUid, out string playerName, out Vector3 playerPos, out _))
        {
            message = "Could not locate player position to spawn Dwarf Kit.";
            return false;
        }

        (string Prefab, int Amount, int Quality)[] dwarfKit =
        {
            ("HelmetCarapace", 1, 4),
            ("ArmorCarapaceChest", 1, 4),
            ("ArmorCarapaceLegs", 1, 4),
            ("CapeFeather", 1, 4),
            ("Battleaxe", 1, 4),
            ("ShieldCarapace", 1, 4),
            ("MisthareSupreme", 10, 1),
            ("MeatPlatter", 10, 1),
            ("MushroomOmelette", 10, 1),
            ("MeadHealthMajor", 10, 1),
            ("MeadStaminaMedium", 10, 1),
        };

        int spawned = 0;
        Vector3 dropOrigin = playerPos + Vector3.up * 0.9f;
        for (int i = 0; i < dwarfKit.Length; i++)
        {
            var entry = dwarfKit[i];
            GameObject? prefab = ObjectDB.instance != null
                ? ObjectDB.instance.GetItemPrefab(entry.Prefab)
                : ZNetScene.instance.GetPrefab(entry.Prefab);
            if (prefab == null)
            {
                prefab = ZNetScene.instance.GetPrefab(entry.Prefab);
            }
            if (prefab == null)
            {
                Plugin.Log.LogWarning($"[RiftDirector] Kit prefab '{entry.Prefab}' not found.");
                continue;
            }

            float angle = (i / (float)dwarfKit.Length) * Mathf.PI * 2f;
            Vector3 offset = new(Mathf.Cos(angle) * 0.6f, 0f, Mathf.Sin(angle) * 0.6f);
            GameObject itemObj = UnityEngine.Object.Instantiate(prefab, dropOrigin + offset, Quaternion.identity);
            var itemDrop = itemObj.GetComponent<ItemDrop>();
            if (itemDrop != null)
            {
                int maxQ = Mathf.Max(1, itemDrop.m_itemData.m_shared.m_maxQuality);
                int q = Mathf.Clamp(entry.Quality, 1, maxQ);
                itemDrop.SetQuality(q);
                itemDrop.SetStack(entry.Amount);
                itemDrop.m_itemData.m_durability = itemDrop.m_itemData.GetMaxDurability(q);
                itemDrop.Save();
            }
            spawned++;
        }

        SendCenterMessageToPeer(callerPeerUid, "Dwarven Vanguard Kit Granted!");
        message = $"Granted Dwarven Vanguard Kit ({spawned} maxed Carapace/Battleaxe/Food stacks) to {playerName}.";
        Plugin.Log.LogInfo($"[RiftDirector] {message}");
        return true;
    }

    public void ShutdownCleanup()
    {
        if (State != RiftRunState.Idle)
        {
            CleanupInstance(teleportRemainingParticipantsBack: true);
        }
    }

    private IEnumerator RunRiftLifecycleCoroutine()
    {
        if (_activeManifest == null || _builtRift == null)
        {
            yield break;
        }

        BroadcastCenterToParticipants($"{_activeManifest.Title}\nKeystone +{_activeManifest.KeystoneLevel}");
        BroadcastChatToParticipants(
            $"<color=#FFD700>[Rift] {_activeManifest.Title}</color> (+{_activeManifest.KeystoneLevel}) | " +
            $"Par Time: {FormatClock(_activeManifest.ParTimeSeconds)} | Affixes: {string.Join(", ", _activeManifest.GlobalAffixes)}");

        yield return new WaitForSeconds(2.0f);

        for (int sec = CountdownSeconds; sec >= 1; sec--)
        {
            BroadcastCenterToParticipants($"Keystone starts in {sec}...");
            yield return new WaitForSeconds(1.0f);
        }

        DropRoomExitGate(0);
        _keystoneStartUtc = DateTime.UtcNow;
        State = RiftRunState.Active;
        BroadcastCenterToParticipants("RIFT OPEN — ENGAGE!");

        _currentRoomIndex = 1;
        _currentWaveIndex = 0;
        var pendingOverflow = new Queue<SpawnSpec>();
        SpawnRoomWave(_currentRoomIndex, _currentWaveIndex, pendingOverflow);

        var waitTick = new WaitForSeconds(MonitorTickSeconds);
        while (State == RiftRunState.Active && _activeManifest != null && _builtRift != null)
        {
            yield return waitTick;

            _cleaner.RefreshIndestructibleStructures();
            PollActiveWaveCreatures();

            while (pendingOverflow.Count > 0 && Affixes.ActiveMobs.Count < RiftConstants.MaxActiveAiCap)
            {
                var nextSpawn = pendingOverflow.Dequeue();
                SpawnSingleSpecInRoom(_builtRift.Rooms[_currentRoomIndex], nextSpawn);
            }

            Affixes.TickCombat(MonitorTickSeconds, _participants.Keys, BroadcastCenterToParticipants);

            if (Affixes.ActiveMobs.Count > 0 || pendingOverflow.Count > 0)
            {
                continue;
            }

            var currentRoomSpec = _activeManifest.Rooms[_currentRoomIndex];
            if (_currentWaveIndex + 1 < currentRoomSpec.Waves.Count)
            {
                _currentWaveIndex++;
                BroadcastCenterToParticipants(
                    $"Wave {_currentWaveIndex + 1}/{currentRoomSpec.Waves.Count} Incoming!");
                SpawnRoomWave(_currentRoomIndex, _currentWaveIndex, pendingOverflow);
            }
            else if (_currentRoomIndex + 1 < _activeManifest.Rooms.Count)
            {
                DropRoomExitGate(_currentRoomIndex);
                _currentRoomIndex++;
                _currentWaveIndex = 0;
                BroadcastCenterToParticipants(
                    $"Gate Shattered! Advancing to Room {_currentRoomIndex + 1}/{_activeManifest.Rooms.Count}");
                SpawnRoomWave(_currentRoomIndex, _currentWaveIndex, pendingOverflow);
            }
            else
            {
                CompleteRiftVictory();
                yield break;
            }
        }
    }

    private void PollActiveWaveCreatures()
    {
        if (ZDOMan.instance == null || Affixes.ActiveMobs.Count == 0)
        {
            return;
        }

        var deadList = new List<ActiveRiftMobState>();
        foreach (var mob in Affixes.ActiveMobs.Values)
        {
            var zdo = ZDOMan.instance.GetZDO(mob.ZdoId);
            if (zdo == null)
            {
                deadList.Add(mob);
                continue;
            }

            mob.LastPosition = zdo.GetPosition();
            float hp = zdo.GetFloat(ZDOVars.s_health, 1f);
            if (hp <= 0f)
            {
                deadList.Add(mob);
            }
        }

        foreach (var dead in deadList)
        {
            _totalKills++;
            _cleaner.Untrack(dead.ZdoId);
            Affixes.OnRiftMobDied(dead, BroadcastCenterToParticipants);
        }
    }

    private void SpawnRoomWave(int roomIndex, int waveIndex, Queue<SpawnSpec> overflowQueue)
    {
        if (_activeManifest == null || _builtRift == null || ZNetScene.instance == null)
        {
            return;
        }

        var roomSpec = _activeManifest.Rooms[roomIndex];
        if (waveIndex >= roomSpec.Waves.Count)
        {
            return;
        }

        var builtRoom = _builtRift.Rooms[roomIndex];
        var waveSpec = roomSpec.Waves[waveIndex];
        var spawnedMobs = new List<ActiveRiftMobState>();

        foreach (var spec in waveSpec.Spawns)
        {
            for (int c = 0; c < spec.Count; c++)
            {
                if (Affixes.ActiveMobs.Count + spawnedMobs.Count >= RiftConstants.MaxActiveAiCap)
                {
                    overflowQueue.Enqueue(new SpawnSpec
                    {
                        Prefab = spec.Prefab,
                        Anchor = spec.Anchor,
                        Count = 1,
                        Stars = spec.Stars,
                        IsBoss = spec.IsBoss,
                    });
                    continue;
                }

                var mobState = InstantiateRiftCreature(builtRoom, spec, c);
                if (mobState != null)
                {
                    spawnedMobs.Add(mobState);
                }
            }
        }

        Affixes.RegisterWaveMobs(spawnedMobs);
        Plugin.Log.LogInfo(
            $"[RiftDirector] Spawned Room {roomIndex} Wave {waveIndex}: {spawnedMobs.Count} active creature(s) " +
            $"(overflow queued: {overflowQueue.Count}).");
    }

    private void SpawnSingleSpecInRoom(BuiltRoom builtRoom, SpawnSpec spec)
    {
        var mob = InstantiateRiftCreature(builtRoom, spec, 0);
        if (mob != null)
        {
            Affixes.RegisterWaveMobs(new List<ActiveRiftMobState> { mob });
        }
    }

    private ActiveRiftMobState? InstantiateRiftCreature(BuiltRoom builtRoom, SpawnSpec spec, int indexInPack)
    {
        if (_activeManifest == null || ZNetScene.instance == null)
        {
            return null;
        }

        GameObject? prefab = ZNetScene.instance.GetPrefab(spec.Prefab);
        if (prefab == null)
        {
            Plugin.Log.LogWarning($"[RiftDirector] Missing creature prefab '{spec.Prefab}'.");
            return null;
        }

        if (!builtRoom.Anchors.TryGetValue(spec.Anchor, out Vector3 anchorPos))
        {
            anchorPos = builtRoom.Center + Vector3.up * 1.25f;
        }

        float jitterAngle = indexInPack * 1.57f;
        float jitterRadius = indexInPack == 0 ? 0f : 1.15f;
        Vector3 spawnPos = anchorPos + new Vector3(Mathf.Cos(jitterAngle) * jitterRadius, 0f, Mathf.Sin(jitterAngle) * jitterRadius);
        Quaternion spawnRot = Quaternion.Euler(0f, 180f, 0f);

        GameObject go = UnityEngine.Object.Instantiate(prefab, spawnPos, spawnRot);
        var netView = go.GetComponent<ZNetView>();
        var character = go.GetComponent<Character>();
        if (netView == null || netView.GetZDO() == null || character == null)
        {
            UnityEngine.Object.Destroy(go);
            return null;
        }

        ZDO zdo = netView.GetZDO();
        int characterLevel = Mathf.Clamp(spec.Stars, 0, 2) + 1;
        character.SetLevel(characterLevel);

        float baseMaxHp = Mathf.Max(10f, character.GetMaxHealth());
        float keystoneHp = baseMaxHp * _activeManifest.HealthMultiplier * (spec.IsBoss ? 1.35f : 1.0f);
        character.SetMaxHealth(keystoneHp);
        zdo.Set(ZDOVars.s_maxHealth, keystoneHp);
        zdo.Set(ZDOVars.s_health, keystoneHp);
        zdo.Set(ZDOVars.s_huntPlayer, true);
        zdo.Set(ZDOVars.s_alert, true);

        if (spec.IsBoss)
        {
            Vector3 bossScale = new(1.25f, 1.25f, 1.25f);
            zdo.Set(ZDOVars.s_scaleHash, bossScale);
            zdo.Set(ZDOVars.s_scaleScalarHash, 1.25f);
            go.transform.localScale = bossScale;
        }

        var monsterAi = go.GetComponent<MonsterAI>();
        if (monsterAi != null)
        {
            monsterAi.SetHuntPlayer(true);
        }

        _cleaner.Track(zdo.m_uid, indestructibleStructure: false);

        return new ActiveRiftMobState
        {
            ZdoId = zdo.m_uid,
            Prefab = spec.Prefab,
            Stars = spec.Stars,
            IsBoss = spec.IsBoss,
            BaseMaxHealth = keystoneHp,
            LastPosition = spawnPos,
        };
    }

    private void DropRoomExitGate(int roomIndex)
    {
        if (_builtRift == null || roomIndex < 0 || roomIndex >= _builtRift.Rooms.Count)
        {
            return;
        }

        var room = _builtRift.Rooms[roomIndex];
        foreach (var gateZdo in room.ExitGateZdos)
        {
            _cleaner.DestroyZdo(gateZdo);
        }
        room.ExitGateZdos.Clear();

        if (room.ExitGateCenter != Vector3.zero)
        {
            Affixes.SpawnOneShotVfx("fx_Lightning", room.ExitGateCenter);
        }
    }

    private void CompleteRiftVictory()
    {
        if (_activeManifest == null || _builtRift == null)
        {
            return;
        }

        _finalElapsedSeconds = Math.Max(1.0, (DateTime.UtcNow - _keystoneStartUtc).TotalSeconds);
        int par = _activeManifest.ParTimeSeconds;
        int keystoneUpgrade = ComputeKeystoneUpgrade(_finalElapsedSeconds, par);

        State = RiftRunState.VictoryStaging;

        SpawnRewardChest(_builtRift.RewardChestPoint, _activeManifest, keystoneUpgrade);
        SpawnLinkedReturnPortals(_builtRift.ReturnPortalPoint);

        string upgradeBanner = keystoneUpgrade switch
        {
            3 => "+++3 CHEST GOLD UPGRADE!",
            2 => "++2 CHEST SILVER UPGRADE!",
            1 => "+1 KEYSTONE TIMED!",
            _ => "KEYSTONE DEPLETED (Completed Over Par)",
        };

        BroadcastCenterToParticipants($"VICTORY! {upgradeBanner}\n{FormatClock(_finalElapsedSeconds)} / {FormatClock(par)}");
        BroadcastChatToParticipants(
            $"<color=#00FFAA>=== RIFT SCORECARD: {_activeManifest.Title} (+{_activeManifest.KeystoneLevel}) ===</color>\n" +
            $"Time: {FormatClock(_finalElapsedSeconds)} / Par {FormatClock(par)} | Result: {upgradeBanner} | Kills: {_totalKills}\n" +
            $"Claim your chest on the north dais and step through the return portal or type '!rift leave'.");

        Plugin.Log.LogInfo(
            $"[RiftDirector] Victory in '{_activeManifest.RiftId}': {_finalElapsedSeconds:F1}s / {par}s -> +{keystoneUpgrade} Keystone upgrade.");
    }

    public static int ComputeKeystoneUpgrade(double elapsedSeconds, int parTimeSeconds)
    {
        if (elapsedSeconds <= parTimeSeconds * 0.60)
        {
            return 3;
        }
        if (elapsedSeconds <= parTimeSeconds * 0.80)
        {
            return 2;
        }
        if (elapsedSeconds <= parTimeSeconds)
        {
            return 1;
        }
        return 0;
    }

    private void SpawnRewardChest(Vector3 chestPos, RiftManifest manifest, int keystoneUpgrade)
    {
        if (ZNetScene.instance == null)
        {
            return;
        }

        Affixes.SpawnOneShotVfx("fx_Lightning", chestPos + Vector3.up * 0.5f);
        ZDOID chestZdoId = SkyboxArenaBuilder.SpawnIndestructible("piece_chest_wood", chestPos, Quaternion.Euler(0f, 180f, 0f), _cleaner);
        if (chestZdoId.IsNone() || ZDOMan.instance == null)
        {
            return;
        }

        var chestZdo = ZDOMan.instance.GetZDO(chestZdoId);
        if (chestZdo == null || !ZNetScene.instance.m_instances.TryGetValue(chestZdo, out var chestView) || chestView == null)
        {
            return;
        }

        var container = chestView.GetComponent<Container>();
        if (container == null)
        {
            return;
        }

        Inventory inv = container.GetInventory();
        foreach (var drop in manifest.Rewards.BaseDrops)
        {
            AddLootToInventory(inv, drop, bonusMultiplier: 1);
        }

        if (keystoneUpgrade >= 1)
        {
            foreach (var bonus in manifest.Rewards.TimedBonusDrops)
            {
                AddLootToInventory(inv, bonus, bonusMultiplier: keystoneUpgrade);
            }
        }

        container.Save();
        ZDOMan.instance.ForceSendZDO(chestZdoId);
    }

    private static void AddLootToInventory(Inventory inv, LootDropSpec spec, int bonusMultiplier)
    {
        if (ZNetScene.instance == null)
        {
            return;
        }

        GameObject? itemPrefab = ObjectDB.instance != null
            ? ObjectDB.instance.GetItemPrefab(spec.Prefab)
            : ZNetScene.instance.GetPrefab(spec.Prefab);
        if (itemPrefab == null)
        {
            itemPrefab = ZNetScene.instance.GetPrefab(spec.Prefab);
        }
        if (itemPrefab == null)
        {
            Plugin.Log.LogWarning($"[RiftDirector] Reward item prefab '{spec.Prefab}' not found.");
            return;
        }

        var itemDrop = itemPrefab.GetComponent<ItemDrop>();
        if (itemDrop == null)
        {
            return;
        }

        int maxQuality = Mathf.Max(1, itemDrop.m_itemData.m_shared.m_maxQuality);
        int quality = Mathf.Clamp(spec.Quality, 1, maxQuality);
        int maxStack = Mathf.Max(1, itemDrop.m_itemData.m_shared.m_maxStackSize);
        int remaining = maxStack == 1
            ? Mathf.Min(spec.Amount, 2)
            : spec.Amount * Mathf.Max(1, bonusMultiplier);

        while (remaining > 0)
        {
            int stackSize = Mathf.Min(remaining, maxStack);
            var clone = itemDrop.m_itemData.Clone();
            clone.m_dropPrefab = itemPrefab;
            clone.m_quality = quality;
            clone.m_stack = stackSize;
            clone.m_durability = clone.GetMaxDurability(quality);
            if (!inv.AddItem(clone))
            {
                break;
            }
            remaining -= stackSize;
        }
    }

    private void SpawnLinkedReturnPortals(Vector3 skyboxPortalPos)
    {
        if (_participants.Count == 0 || ZDOMan.instance == null || ZNetScene.instance == null)
        {
            return;
        }

        SavedPlayerOrigin? anchorOrigin = null;
        foreach (var p in _participants.Values)
        {
            anchorOrigin = p;
            break;
        }
        if (anchorOrigin == null)
        {
            return;
        }

        string tag = $"rift-exit-{UnityEngine.Random.Range(100, 999)}";
        Vector3 groundPortalPos = anchorOrigin.ReturnPosition + new Vector3(2.5f, 0.25f, 2.5f);

        ZDOID skyPortalId = SkyboxArenaBuilder.SpawnIndestructible("portal_wood", skyboxPortalPos, Quaternion.Euler(0f, 180f, 0f), _cleaner);
        ZDOID groundPortalId = SkyboxArenaBuilder.SpawnIndestructible("portal_wood", groundPortalPos, Quaternion.identity, _cleaner);

        ConfigurePortalPair(skyPortalId, groundPortalId, tag);
        ConfigurePortalPair(groundPortalId, skyPortalId, tag);
    }

    private static void ConfigurePortalPair(ZDOID portalId, ZDOID targetId, string tag)
    {
        if (portalId.IsNone() || ZDOMan.instance == null)
        {
            return;
        }

        var zdo = ZDOMan.instance.GetZDO(portalId);
        if (zdo == null)
        {
            return;
        }

        zdo.Set(ZDOVars.s_tag, tag);
        if (ZNetScene.instance != null &&
            ZNetScene.instance.m_instances.TryGetValue(zdo, out var view) &&
            view != null)
        {
            var portal = view.GetComponent<TeleportWorld>();
            if (portal != null)
            {
                portal.SetText(tag);
                portal.SetConnectedPortal(targetId);
            }
        }
        ZDOMan.instance.ForceSendZDO(portalId);
    }

    private int CleanupInstance(bool teleportRemainingParticipantsBack)
    {
        if (_runCoroutine != null)
        {
            _coroutineHost.StopCoroutine(_runCoroutine);
            _runCoroutine = null;
        }

        if (teleportRemainingParticipantsBack)
        {
            foreach (var participant in _participants.Values)
            {
                TeleportPeer(participant.PeerUid, participant.ReturnPosition + Vector3.up * 0.5f, participant.ReturnRotation);
            }
        }

        _participants.Clear();
        _participantsByName.Clear();
        Affixes.Reset();

        int destroyed = _cleaner.TeardownAll();

        if (ZNet.instance != null)
        {
            ZNet.instance.SetReferencePosition(_previousServerRefPos);
            ZNet.instance.SetPublicReferencePosition(_previousServerPublicRefPos);
        }

        _builtRift = null;
        _activeManifest = null;
        State = RiftRunState.Idle;
        return destroyed;
    }

    private void GatherNearbyParticipants(long callerPeerUid, string callerName, Vector3 callerPos, Quaternion callerRot)
    {
        _participants.Clear();
        _participantsByName.Clear();

        var callerEntry = new SavedPlayerOrigin
        {
            PeerUid = callerPeerUid,
            PlayerName = callerName,
            ReturnPosition = callerPos,
            ReturnRotation = callerRot,
        };
        _participants[callerPeerUid] = callerEntry;
        if (!string.IsNullOrWhiteSpace(callerName))
        {
            _participantsByName[callerName] = callerEntry;
        }

        if (ZNet.instance == null || ZDOMan.instance == null)
        {
            return;
        }

        foreach (var peer in ZNet.instance.m_peers)
        {
            if (peer == null || peer.m_uid == callerPeerUid || peer.m_characterID.IsNone())
            {
                continue;
            }

            var peerZdo = ZDOMan.instance.GetZDO(peer.m_characterID);
            if (peerZdo == null)
            {
                continue;
            }

            Vector3 pos = peerZdo.GetPosition();
            if (Vector3.Distance(pos, callerPos) <= PartyGatherRadiusMeters)
            {
                var entry = new SavedPlayerOrigin
                {
                    PeerUid = peer.m_uid,
                    PlayerName = peer.m_playerName ?? string.Empty,
                    ReturnPosition = pos,
                    ReturnRotation = peerZdo.GetRotation(),
                };
                _participants[peer.m_uid] = entry;
                if (!string.IsNullOrWhiteSpace(entry.PlayerName))
                {
                    _participantsByName[entry.PlayerName] = entry;
                }
            }
        }
    }

    private static bool TryResolvePeerTransform(
        long peerUid,
        out string playerName,
        out Vector3 pos,
        out Quaternion rot)
    {
        playerName = string.Empty;
        pos = Vector3.zero;
        rot = Quaternion.identity;

        if (ZNet.instance == null || ZDOMan.instance == null)
        {
            return false;
        }

        if (peerUid == ZDOMan.GetSessionID() && Player.m_localPlayer != null)
        {
            playerName = Player.m_localPlayer.GetPlayerName();
            pos = Player.m_localPlayer.transform.position;
            rot = Player.m_localPlayer.transform.rotation;
            return true;
        }

        var peer = ZNet.instance.GetPeer(peerUid);
        if (peer == null || peer.m_characterID.IsNone())
        {
            return false;
        }

        var zdo = ZDOMan.instance.GetZDO(peer.m_characterID);
        if (zdo == null)
        {
            return false;
        }

        playerName = peer.m_playerName ?? string.Empty;
        pos = zdo.GetPosition();
        rot = zdo.GetRotation();
        return true;
    }

    private static void TeleportPeer(long peerUid, Vector3 targetPos, Quaternion targetRot)
    {
        if (ZDOMan.instance != null && peerUid == ZDOMan.GetSessionID() && Player.m_localPlayer != null)
        {
            Player.m_localPlayer.TeleportTo(targetPos, targetRot, distantTeleport: true);
            return;
        }

        if (Chat.instance != null)
        {
            Chat.instance.TeleportPlayer(peerUid, targetPos, targetRot, distantTeleport: true);
        }
        else if (ZRoutedRpc.instance != null)
        {
            ZRoutedRpc.instance.InvokeRoutedRPC(peerUid, "RPC_TeleportPlayer", targetPos, targetRot, true);
        }
    }

    public void SendCenterMessageToPeer(long peerUid, string text)
    {
        if (ZRoutedRpc.instance == null)
        {
            return;
        }
        ZRoutedRpc.instance.InvokeRoutedRPC(peerUid, "ShowMessage", (int)MessageHud.MessageType.Center, text);
    }

    public void SendChatReplyToPeer(long peerUid, string text)
    {
        if (ZRoutedRpc.instance == null)
        {
            return;
        }

        var serverUser = new UserInfo
        {
            Name = "RiftEngine",
            UserId = PlatformUserID.None,
        };
        Vector3 refPos = _builtRift?.Origin ?? Vector3.zero;
        ZRoutedRpc.instance.InvokeRoutedRPC(peerUid, "ChatMessage", refPos, (int)Talker.Type.Normal, serverUser, text);
    }

    private void BroadcastCenterToParticipants(string text)
    {
        foreach (long peerUid in _participants.Keys)
        {
            SendCenterMessageToPeer(peerUid, text);
        }
    }

    private void BroadcastChatToParticipants(string text)
    {
        foreach (long peerUid in _participants.Keys)
        {
            SendChatReplyToPeer(peerUid, text);
        }
    }

    private static string FormatClock(double totalSeconds)
    {
        int sec = Mathf.Max(0, Mathf.RoundToInt((float)totalSeconds));
        return $"{sec / 60:D2}:{sec % 60:D2}";
    }
}
