using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using ValheimRiftEngine.Generation;
using ValheimRiftEngine.Models;

namespace ValheimRiftEngine.Runtime;

public sealed class ActiveRiftMobState
{
    public ZDOID ZdoId { get; init; }
    public string Prefab { get; init; } = string.Empty;
    public int Stars { get; init; }
    public bool IsBoss { get; init; }
    public float BaseMaxHealth { get; set; }
    public Vector3 LastPosition { get; set; }
    public int BolsterStacks { get; set; }
    public bool IsShielded { get; set; }
}

/// <summary>
/// Enforces server-authoritative Mythic+ global affixes and Keystone multipliers.
/// </summary>
public sealed class AffixController
{
    private const float MoltenSlagIntervalSec = 6.0f;
    private const float MoltenSlagDetonationDelaySec = 2.0f;
    private const float MoltenSlagStationaryThresholdM = 2.2f;
    private const float MoltenSlagRadiusM = 3.2f;

    private const float BolsterRadiusM = 8.0f;
    private const float BolsterBonusPerStack = 0.15f;
    private const int MaxBolsterStacks = 5;

    private const float ThunderBlessingIntervalSec = 10.0f;
    private const float ThunderMeleeRangeM = 6.5f;
    private const float ThunderChainRangeM = 7.5f;
    private const int ThunderMaxTargets = 3;

    private readonly MonoBehaviour _coroutineHost;
    private readonly HashSet<string> _activeAffixes = new(StringComparer.Ordinal);
    private readonly Dictionary<ZDOID, ActiveRiftMobState> _activeMobs = new();
    private readonly Dictionary<long, Vector3> _lastPlayerPositions = new();

    private RiftManifest? _manifest;
    private InstanceCleaner? _cleaner;
    private ZDOID _shieldedEliteZdo = ZDOID.None;
    private float _slagTimer;
    private float _thunderTimer;

    public AffixController(MonoBehaviour coroutineHost)
    {
        _coroutineHost = coroutineHost;
    }

    public IReadOnlyDictionary<ZDOID, ActiveRiftMobState> ActiveMobs => _activeMobs;

    public bool HasAffix(string affix) => _activeAffixes.Contains(affix);

    public void InitializeRun(RiftManifest manifest, InstanceCleaner cleaner)
    {
        _manifest = manifest;
        _cleaner = cleaner;
        _activeAffixes.Clear();
        foreach (var affix in manifest.GlobalAffixes)
        {
            _activeAffixes.Add(affix);
        }
        _activeMobs.Clear();
        _lastPlayerPositions.Clear();
        _shieldedEliteZdo = ZDOID.None;
        _slagTimer = 0f;
        _thunderTimer = 4.0f;
    }

    public void Reset()
    {
        _manifest = null;
        _cleaner = null;
        _activeAffixes.Clear();
        _activeMobs.Clear();
        _lastPlayerPositions.Clear();
        _shieldedEliteZdo = ZDOID.None;
    }

    public void RegisterWaveMobs(List<ActiveRiftMobState> waveMobs)
    {
        _shieldedEliteZdo = ZDOID.None;
        foreach (var mob in waveMobs)
        {
            _activeMobs[mob.ZdoId] = mob;
        }

        if (HasAffix("shielded_pack") && waveMobs.Count >= 2)
        {
            ActiveRiftMobState? candidate = null;
            foreach (var mob in waveMobs)
            {
                if (candidate == null ||
                    (!candidate.IsBoss && mob.IsBoss) ||
                    (mob.Stars > candidate.Stars))
                {
                    candidate = mob;
                }
            }

            if (candidate != null)
            {
                candidate.IsShielded = true;
                _shieldedEliteZdo = candidate.ZdoId;
                SpawnOneShotVfx("vfx_offering", candidate.LastPosition + Vector3.up * 1.0f);
                Plugin.Log.LogInfo($"[AffixController] shielded_pack active on '{candidate.Prefab}' ({candidate.ZdoId}).");
            }
        }
    }

    public bool IsMobShielded(ZDOID mobId)
    {
        return _activeMobs.TryGetValue(mobId, out var state) && state.IsShielded;
    }

    public float GetOutgoingMobDamageMultiplier(ZDOID attackerId)
    {
        if (_manifest == null)
        {
            return 1.0f;
        }

        float keystoneMult = _manifest.DamageMultiplier;
        if (_activeMobs.TryGetValue(attackerId, out var state) && state.BolsterStacks > 0)
        {
            keystoneMult *= 1.0f + (BolsterBonusPerStack * state.BolsterStacks);
        }
        return keystoneMult;
    }

    public bool IsActiveRiftMob(ZDOID id) => _activeMobs.ContainsKey(id);

    public void TickCombat(
        float dt,
        IReadOnlyCollection<long> participantPeerIds,
        Action<string> broadcastCenterMessage)
    {
        if (_manifest == null || ZDOMan.instance == null || ZNet.instance == null)
        {
            return;
        }

        UpdateShieldedPackState(broadcastCenterMessage);

        if (_activeMobs.Count == 0)
        {
            return;
        }

        if (HasAffix("molten_slag"))
        {
            _slagTimer += dt;
            if (_slagTimer >= MoltenSlagIntervalSec)
            {
                _slagTimer = 0f;
                TriggerMoltenSlag(participantPeerIds);
            }
        }

        if (HasAffix("thunder_blessing"))
        {
            _thunderTimer += dt;
            if (_thunderTimer >= ThunderBlessingIntervalSec)
            {
                _thunderTimer = 0f;
                TriggerThunderBlessing(participantPeerIds);
            }
        }
    }

    public void OnRiftMobDied(ActiveRiftMobState deadMob, Action<string> broadcastCenterMessage)
    {
        _activeMobs.Remove(deadMob.ZdoId);

        if (HasAffix("bolstering") && !deadMob.IsBoss && _activeMobs.Count > 0)
        {
            int buffedCount = 0;
            foreach (var ally in _activeMobs.Values)
            {
                if (ally.BolsterStacks >= MaxBolsterStacks)
                {
                    continue;
                }

                float dist = Vector3.Distance(deadMob.LastPosition, ally.LastPosition);
                if (dist <= BolsterRadiusM)
                {
                    ally.BolsterStacks++;
                    ApplyBolsterVisualAndStats(ally);
                    buffedCount++;
                }
            }

            if (buffedCount > 0)
            {
                SpawnOneShotVfx("vfx_offering", deadMob.LastPosition + Vector3.up * 0.5f);
                Plugin.Log.LogInfo($"[AffixController] Bolstering from '{deadMob.Prefab}' buffed {buffedCount} nearby Rift mob(s).");
            }
        }

        UpdateShieldedPackState(broadcastCenterMessage);
    }

    private void UpdateShieldedPackState(Action<string> broadcastCenterMessage)
    {
        if (_shieldedEliteZdo.IsNone())
        {
            return;
        }

        if (!_activeMobs.TryGetValue(_shieldedEliteZdo, out var shieldedMob))
        {
            _shieldedEliteZdo = ZDOID.None;
            return;
        }

        int unshieldedAddsAlive = 0;
        foreach (var mob in _activeMobs.Values)
        {
            if (mob.ZdoId != _shieldedEliteZdo)
            {
                unshieldedAddsAlive++;
            }
        }

        if (unshieldedAddsAlive == 0)
        {
            shieldedMob.IsShielded = false;
            _shieldedEliteZdo = ZDOID.None;
            SpawnOneShotVfx("fx_Lightning", shieldedMob.LastPosition + Vector3.up * 1.0f);
            broadcastCenterMessage($"Shield shattered on {shieldedMob.Prefab}!");
            return;
        }

        var zdo = ZDOMan.instance?.GetZDO(shieldedMob.ZdoId);
        if (zdo != null)
        {
            float maxHp = zdo.GetFloat(ZDOVars.s_maxHealth, shieldedMob.BaseMaxHealth);
            float curHp = zdo.GetFloat(ZDOVars.s_health, maxHp);
            if (curHp < maxHp)
            {
                zdo.Set(ZDOVars.s_health, maxHp);
            }
        }
    }

    private void ApplyBolsterVisualAndStats(ActiveRiftMobState ally)
    {
        var zdo = ZDOMan.instance?.GetZDO(ally.ZdoId);
        if (zdo == null)
        {
            return;
        }

        float scale = 1.0f + (BolsterBonusPerStack * ally.BolsterStacks);
        Vector3 scaleVec = new(scale, scale, scale);
        zdo.Set(ZDOVars.s_scaleHash, scaleVec);
        zdo.Set(ZDOVars.s_scaleScalarHash, scale);

        if (ZNetScene.instance != null &&
            ZNetScene.instance.m_instances.TryGetValue(zdo, out var netView) &&
            netView != null)
        {
            netView.transform.localScale = scaleVec;
        }
    }

    private void TriggerMoltenSlag(IReadOnlyCollection<long> participantPeerIds)
    {
        if (_manifest == null)
        {
            return;
        }

        foreach (long peerUid in participantPeerIds)
        {
            if (!TryGetPlayerPeerState(peerUid, out _, out Vector3 currentPos, out _))
            {
                continue;
            }

            if (currentPos.y < RiftConstants.SkyboxAltitudeY - 25f)
            {
                continue;
            }

            if (_lastPlayerPositions.TryGetValue(peerUid, out Vector3 prevPos))
            {
                Vector2 flatDelta = new(currentPos.x - prevPos.x, currentPos.z - prevPos.z);
                if (flatDelta.magnitude <= MoltenSlagStationaryThresholdM)
                {
                    Vector3 slagPoint = new(currentPos.x, currentPos.y, currentPos.z);
                    SpawnOneShotVfx("vfx_FireWork_SurtlingCore", slagPoint);
                    _coroutineHost.StartCoroutine(DetonateMoltenSlagAfterDelay(slagPoint, participantPeerIds));
                }
            }

            _lastPlayerPositions[peerUid] = currentPos;
        }
    }

    private IEnumerator DetonateMoltenSlagAfterDelay(
        Vector3 slagPoint,
        IReadOnlyCollection<long> participantPeerIds)
    {
        yield return new WaitForSeconds(MoltenSlagDetonationDelaySec);

        if (_manifest == null || ZNet.instance == null || ZRoutedRpc.instance == null)
        {
            yield break;
        }

        SpawnOneShotVfx("fx_blobLava_explosion", slagPoint + Vector3.up * 0.2f);
        SpawnOneShotVfx("vfx_FireballHit", slagPoint + Vector3.up * 0.35f);

        float fireDamage = 22f * _manifest.DamageMultiplier;
        foreach (long peerUid in participantPeerIds)
        {
            if (!TryGetPlayerPeerState(peerUid, out ZDOID playerZdoId, out Vector3 playerPos, out ZDO? playerZdo) ||
                playerZdo == null)
            {
                continue;
            }

            if (Vector3.Distance(playerPos, slagPoint) <= MoltenSlagRadiusM)
            {
                var hit = new HitData();
                hit.m_damage.m_fire = fireDamage;
                hit.m_point = playerPos + Vector3.up * 0.8f;
                hit.m_dir = Vector3.up;
                hit.m_hitType = HitData.HitType.EnemyHit;

                ZRoutedRpc.instance.InvokeRoutedRPC(playerZdo.GetOwner(), playerZdoId, "RPC_Damage", hit);
            }
        }
    }

    private void TriggerThunderBlessing(IReadOnlyCollection<long> participantPeerIds)
    {
        if (_manifest == null || _activeMobs.Count == 0 || ZRoutedRpc.instance == null || ZDOMan.instance == null)
        {
            return;
        }

        float lightningDamage = 35f * (1.0f + 0.12f * _manifest.KeystoneLevel);

        foreach (long peerUid in participantPeerIds)
        {
            if (!TryGetPlayerPeerState(peerUid, out ZDOID playerZdoId, out Vector3 playerPos, out ZDO? playerZdo) ||
                playerZdo == null)
            {
                continue;
            }

            if (playerPos.y < RiftConstants.SkyboxAltitudeY - 25f)
            {
                continue;
            }

            if (!PlayerExceedsHalfStamina(playerZdo))
            {
                continue;
            }

            ActiveRiftMobState? primaryTarget = null;
            float bestDist = ThunderMeleeRangeM;
            foreach (var mob in _activeMobs.Values)
            {
                if (mob.IsShielded)
                {
                    continue;
                }
                float d = Vector3.Distance(playerPos, mob.LastPosition);
                if (d <= bestDist)
                {
                    bestDist = d;
                    primaryTarget = mob;
                }
            }

            if (primaryTarget == null)
            {
                continue;
            }

            var chained = new List<ActiveRiftMobState> { primaryTarget };
            foreach (var mob in _activeMobs.Values)
            {
                if (chained.Count >= ThunderMaxTargets)
                {
                    break;
                }
                if (mob.ZdoId == primaryTarget.ZdoId || mob.IsShielded)
                {
                    continue;
                }
                if (Vector3.Distance(primaryTarget.LastPosition, mob.LastPosition) <= ThunderChainRangeM)
                {
                    chained.Add(mob);
                }
            }

            foreach (var target in chained)
            {
                var mobZdo = ZDOMan.instance.GetZDO(target.ZdoId);
                if (mobZdo == null)
                {
                    continue;
                }

                SpawnOneShotVfx("fx_chainlightning_hit", target.LastPosition + Vector3.up * 0.9f);

                var hit = new HitData();
                hit.m_damage.m_lightning = lightningDamage;
                hit.m_point = target.LastPosition + Vector3.up * 0.9f;
                hit.m_dir = (target.LastPosition - playerPos).normalized;
                hit.m_attacker = playerZdoId;
                hit.m_hitType = HitData.HitType.PlayerHit;

                ZRoutedRpc.instance.InvokeRoutedRPC(mobZdo.GetOwner(), target.ZdoId, "RPC_Damage", hit);
            }
        }
    }

    private static bool PlayerExceedsHalfStamina(ZDO playerZdo)
    {
        if (ZNetScene.instance != null &&
            ZNetScene.instance.m_instances.TryGetValue(playerZdo, out var netView) &&
            netView != null)
        {
            var playerComp = netView.GetComponent<Player>();
            if (playerComp != null && playerComp.GetMaxStamina() > 0f)
            {
                return playerComp.GetStaminaPercentage() > 0.5f;
            }
        }

        float zdoStamina = playerZdo.GetFloat(ZDOVars.s_stamina, -1f);
        if (zdoStamina >= 0f)
        {
            float maxStamina = Mathf.Max(50f, playerZdo.GetFloat("max_stamina", 100f));
            return (zdoStamina / maxStamina) > 0.5f;
        }

        return true;
    }

    private static bool TryGetPlayerPeerState(
        long peerUid,
        out ZDOID characterZdoId,
        out Vector3 pos,
        out ZDO? playerZdo)
    {
        characterZdoId = ZDOID.None;
        pos = Vector3.zero;
        playerZdo = null;

        if (ZNet.instance == null || ZDOMan.instance == null)
        {
            return false;
        }

        if (peerUid == ZDOMan.GetSessionID() && Player.m_localPlayer != null)
        {
            characterZdoId = Player.m_localPlayer.GetZDOID();
            pos = Player.m_localPlayer.transform.position;
            playerZdo = ZDOMan.instance.GetZDO(characterZdoId);
            return playerZdo != null;
        }

        var peer = ZNet.instance.GetPeer(peerUid);
        if (peer == null || peer.m_characterID.IsNone())
        {
            return false;
        }

        characterZdoId = peer.m_characterID;
        playerZdo = ZDOMan.instance.GetZDO(characterZdoId);
        if (playerZdo == null)
        {
            return false;
        }

        pos = playerZdo.GetPosition();
        return true;
    }

    public void SpawnOneShotVfx(string prefabName, Vector3 pos)
    {
        if (ZNetScene.instance == null)
        {
            return;
        }

        GameObject? prefab = ZNetScene.instance.GetPrefab(prefabName);
        if (prefab == null)
        {
            return;
        }

        GameObject vfx = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.identity);
        var nv = vfx.GetComponent<ZNetView>();
        if (nv != null && nv.GetZDO() != null && _cleaner != null)
        {
            _cleaner.Track(nv.GetZDO().m_uid, indestructibleStructure: false);
        }
    }
}
