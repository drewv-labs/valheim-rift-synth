using System.Collections.Generic;

namespace ValheimRiftEngine.Generation;

/// <summary>
/// Tracks every ZDO spawned by a Rift run (structures, wave gates, torches, mobs, chests, portals)
/// and guarantees deterministic, 100% leak-free server-side ZDO teardown on completion or abort.
/// </summary>
public sealed class InstanceCleaner
{
    private readonly HashSet<ZDOID> _trackedZdos = new();
    private readonly HashSet<ZDOID> _indestructibleStructureZdos = new();

    public int TrackedCount => _trackedZdos.Count;

    public IReadOnlyCollection<ZDOID> TrackedZdos => _trackedZdos;

    public void Track(ZDOID id, bool indestructibleStructure = false)
    {
        if (id.IsNone())
        {
            return;
        }
        _trackedZdos.Add(id);
        if (indestructibleStructure)
        {
            _indestructibleStructureZdos.Add(id);
        }
    }

    public void Untrack(ZDOID id)
    {
        _trackedZdos.Remove(id);
        _indestructibleStructureZdos.Remove(id);
    }

    /// <summary>
    /// Refreshes indestructible health/support on structural ZDOs in case a client peer briefly owned the ZDO.
    /// </summary>
    public void RefreshIndestructibleStructures()
    {
        var zdoMan = ZDOMan.instance;
        if (zdoMan == null)
        {
            return;
        }

        foreach (var id in _indestructibleStructureZdos)
        {
            var zdo = zdoMan.GetZDO(id);
            if (zdo == null)
            {
                continue;
            }

            float hp = zdo.GetFloat(ZDOVars.s_health, 1e9f);
            if (hp < 5e8f)
            {
                zdo.SetOwner(ZDOMan.GetSessionID());
                zdo.Set(ZDOVars.s_health, 1e9f);
                zdo.Set(ZDOVars.s_support, 1e9f);
            }
        }
    }

    /// <summary>
    /// Reclaims ZDO ownership on the server and destroys a single tracked ZDO cleanly across all peers.
    /// </summary>
    public bool DestroyZdo(ZDOID id)
    {
        _trackedZdos.Remove(id);
        _indestructibleStructureZdos.Remove(id);

        if (id.IsNone() || ZDOMan.instance == null)
        {
            return false;
        }

        var zdo = ZDOMan.instance.GetZDO(id);
        if (zdo == null)
        {
            return false;
        }

        zdo.SetOwner(ZDOMan.GetSessionID());

        if (ZNetScene.instance != null &&
            ZNetScene.instance.m_instances.TryGetValue(zdo, out var netView) &&
            netView != null)
        {
            ZNetScene.instance.m_instances.Remove(zdo);
            UnityEngine.Object.Destroy(netView.gameObject);
        }

        ZDOMan.instance.DestroyZDO(zdo);
        return true;
    }

    /// <summary>
    /// Destroys every remaining ZDO belonging to the Rift instance.
    /// </summary>
    public int TeardownAll()
    {
        if (_trackedZdos.Count == 0)
        {
            _indestructibleStructureZdos.Clear();
            return 0;
        }

        var snapshot = new List<ZDOID>(_trackedZdos);
        int destroyed = 0;
        foreach (var id in snapshot)
        {
            if (DestroyZdo(id))
            {
                destroyed++;
            }
        }

        _trackedZdos.Clear();
        _indestructibleStructureZdos.Clear();
        Plugin.Log.LogInfo($"[InstanceCleaner] Teardown complete: destroyed {destroyed}/{snapshot.Count} ZDOs.");
        return destroyed;
    }
}
