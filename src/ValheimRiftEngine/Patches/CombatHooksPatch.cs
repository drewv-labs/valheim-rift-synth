using System;
using HarmonyLib;
using UnityEngine;
using ValheimRiftEngine.Runtime;

namespace ValheimRiftEngine.Patches;

[HarmonyPatch]
public static class CombatHooksPatch
{
    private const string DamageRpcName = "RPC_Damage";

    /// <summary>
    /// Enforces shielded_pack damage immunity on server-owned Rift mobs and scales incoming/local hits.
    /// </summary>
    [HarmonyPatch(typeof(Character), nameof(Character.RPC_Damage))]
    [HarmonyPrefix]
    public static bool CharacterRpcDamagePrefix(Character __instance, ref HitData hit)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return true;
        }

        var director = Plugin.Director;
        if (director == null || director.State != RiftRunState.Active || hit == null)
        {
            return true;
        }

        ZDOID targetId = __instance.GetZDOID();
        if (!targetId.IsNone() && director.Affixes.IsMobShielded(targetId))
        {
            Vector3 vfxPos = hit.m_point != Vector3.zero ? hit.m_point : __instance.GetCenterPoint();
            director.Affixes.SpawnOneShotVfx("vfx_blocked", vfxPos);
            return false;
        }

        if (!hit.m_attacker.IsNone() && director.Affixes.IsActiveRiftMob(hit.m_attacker))
        {
            float mult = director.Affixes.GetOutgoingMobDamageMultiplier(hit.m_attacker);
            if (Math.Abs(mult - 1.0f) > 0.001f)
            {
                hit.m_damage.Modify(mult);
            }
        }

        return true;
    }

    /// <summary>
    /// Scales outgoing HitData from server-owned Rift mobs BEFORE it is serialized and sent to remote client players.
    /// </summary>
    [HarmonyPatch(
        typeof(ZRoutedRpc),
        nameof(ZRoutedRpc.InvokeRoutedRPC),
        new Type[] { typeof(long), typeof(ZDOID), typeof(string), typeof(object[]) })]
    [HarmonyPrefix]
    public static bool InvokeRoutedRpcDamagePrefix(
        ZDOID targetZDO,
        string methodName,
        object[] parameters)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return true;
        }

        if (!string.Equals(methodName, DamageRpcName, StringComparison.Ordinal) ||
            parameters == null ||
            parameters.Length == 0 ||
            parameters[0] is not HitData hit)
        {
            return true;
        }

        var director = Plugin.Director;
        if (director == null || director.State != RiftRunState.Active)
        {
            return true;
        }

        if (!targetZDO.IsNone() && director.Affixes.IsMobShielded(targetZDO))
        {
            return false;
        }

        if (!hit.m_attacker.IsNone() &&
            director.Affixes.IsActiveRiftMob(hit.m_attacker) &&
            !director.Affixes.IsActiveRiftMob(targetZDO))
        {
            float mult = director.Affixes.GetOutgoingMobDamageMultiplier(hit.m_attacker);
            if (Math.Abs(mult - 1.0f) > 0.001f)
            {
                hit.m_damage.Modify(mult);
            }
        }

        return true;
    }

    /// <summary>
    /// Keeps active Rift creature ZDOs owned by the dedicated server so AI + affixes remain 100% server-authoritative
    /// and older "Potato-PC" clients experience zero AI simulation load.
    /// </summary>
    [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.ReleaseNearbyZDOS))]
    [HarmonyPostfix]
    public static void KeepRiftMobServerOwnershipPostfix(ZDOMan __instance)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return;
        }

        var director = Plugin.Director;
        if (director == null || director.State != RiftRunState.Active)
        {
            return;
        }

        long serverSessionId = ZDOMan.GetSessionID();
        foreach (var mobId in director.Affixes.ActiveMobs.Keys)
        {
            var zdo = __instance.GetZDO(mobId);
            if (zdo != null && zdo.GetOwner() != serverSessionId)
            {
                zdo.SetOwner(serverSessionId);
            }
        }
    }
}
