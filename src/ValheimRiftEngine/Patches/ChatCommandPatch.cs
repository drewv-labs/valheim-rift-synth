using System;
using HarmonyLib;

namespace ValheimRiftEngine.Patches;

[HarmonyPatch]
public static class ChatCommandPatch
{
    private static readonly int ChatMessageRpcHash = "ChatMessage".GetStableHashCode();
    private static readonly int SayRpcHash = "Say".GetStableHashCode();
    private static long _lastHandledRpcTickId = -1;
    private static string _lastHandledRpcSignature = string.Empty;

    [HarmonyPatch(typeof(ZRoutedRpc), nameof(ZRoutedRpc.RouteRPC))]
    [HarmonyPrefix]
    public static bool RouteRpcPrefix(ZRoutedRpc.RoutedRPCData rpcData)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer() || rpcData?.m_parameters == null)
        {
            return true;
        }

        if (TryInterceptCommand(rpcData))
        {
            // Swallow raw command packet so '/rift' or '!rift' isn't broadcast as public chat noise
            return false;
        }

        return true;
    }

    [HarmonyPatch(typeof(ZRoutedRpc), nameof(ZRoutedRpc.HandleRoutedRPC))]
    [HarmonyPrefix]
    public static bool HandleRoutedRpcPrefix(ZRoutedRpc.RoutedRPCData data)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer() || data?.m_parameters == null)
        {
            return true;
        }

        // Handles local/listen-server invocations that bypass RouteRPC
        if (TryInterceptCommand(data))
        {
            return false;
        }

        return true;
    }

    [HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
    [HarmonyPostfix]
    public static void InitTerminalPostfix()
    {
        if (Terminal.commands.ContainsKey("rift"))
        {
            return;
        }

        _ = new Terminal.ConsoleCommand(
            "rift",
            "[start|abort|status|leave|reload|kit dwarf] - Server-authoritative Mythic+ Skybox Rift controls",
            args =>
            {
                string subArgs = args.FullLine.Length > 4 ? args.FullLine.Substring(4).Trim() : "status";
                if (ZNet.instance != null && ZNet.instance.IsServer())
                {
                    long hostPeer = ZDOMan.GetSessionID();
                    string hostName = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "ServerAdmin";
                    string reply = ExecuteRiftCommand(hostPeer, hostName, subArgs);
                    args.Context?.AddString(reply);
                }
                else if (Chat.instance != null)
                {
                    Chat.instance.SendText(Talker.Type.Normal, $"/rift {subArgs}");
                }
            });
    }

    private static bool TryInterceptCommand(ZRoutedRpc.RoutedRPCData rpcData)
    {
        if (rpcData.m_methodHash != ChatMessageRpcHash && rpcData.m_methodHash != SayRpcHash)
        {
            return false;
        }

        var pkg = rpcData.m_parameters;
        int savedPos = pkg.GetPos();
        string rawText;
        string senderName = string.Empty;

        try
        {
            pkg.SetPos(0);
            if (rpcData.m_methodHash == ChatMessageRpcHash)
            {
                _ = pkg.ReadVector3();
                _ = pkg.ReadInt();
                var user = new UserInfo();
                user.Deserialize(ref pkg);
                senderName = user.Name ?? string.Empty;
                rawText = pkg.ReadString();
            }
            else
            {
                _ = pkg.ReadInt();
                var user = new UserInfo();
                user.Deserialize(ref pkg);
                senderName = user.Name ?? string.Empty;
                rawText = pkg.ReadString();
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            pkg.SetPos(savedPos);
        }

        if (!TryExtractRiftCommandArgs(rawText, out string commandArgs))
        {
            return false;
        }

        // Deduplicate if both RouteRPC and HandleRoutedRPC inspect the same frame packet
        long frame = UnityEngine.Time.frameCount;
        string sig = $"{rpcData.m_senderPeerID}:{commandArgs}";
        if (_lastHandledRpcTickId == frame && string.Equals(_lastHandledRpcSignature, sig, StringComparison.Ordinal))
        {
            return true;
        }
        _lastHandledRpcTickId = frame;
        _lastHandledRpcSignature = sig;

        string response = ExecuteRiftCommand(rpcData.m_senderPeerID, senderName, commandArgs);
        Plugin.Director?.SendChatReplyToPeer(rpcData.m_senderPeerID, response);
        return true;
    }

    private static bool TryExtractRiftCommandArgs(string? rawText, out string commandArgs)
    {
        commandArgs = string.Empty;
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return false;
        }

        string trimmed = rawText.Trim();
        if (trimmed.StartsWith("/rift", StringComparison.OrdinalIgnoreCase))
        {
            if (trimmed.Length == 5 || char.IsWhiteSpace(trimmed[5]))
            {
                commandArgs = trimmed.Length > 5 ? trimmed.Substring(5).Trim() : "status";
                return true;
            }
        }
        else if (trimmed.StartsWith("!rift", StringComparison.OrdinalIgnoreCase))
        {
            if (trimmed.Length == 5 || char.IsWhiteSpace(trimmed[5]))
            {
                commandArgs = trimmed.Length > 5 ? trimmed.Substring(5).Trim() : "status";
                return true;
            }
        }

        return false;
    }

    internal static string ExecuteRiftCommand(long senderPeerUid, string senderName, string commandArgs)
    {
        var director = Plugin.Director;
        var plugin = Plugin.Instance;
        if (director == null || plugin == null)
        {
            return "[Rift] Engine is not initialized yet.";
        }

        string[] parts = commandArgs.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        string sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "status";

        switch (sub)
        {
            case "status":
                return director.GetStatusSummary();

            case "start":
            {
                director.TryStartRift(senderPeerUid, out string msg);
                return $"[Rift] {msg}";
            }

            case "abort":
            {
                if (!IsPeerAdmin(senderPeerUid) && Plugin.RequireAdminForAbort)
                {
                    return "[Rift] Permission denied: '/rift abort' requires server admin.";
                }
                director.AbortRift(out string msg);
                return $"[Rift] {msg}";
            }

            case "leave":
            {
                director.TryLeaveRift(senderPeerUid, senderName, out string msg);
                return $"[Rift] {msg}";
            }

            case "reload":
            {
                bool ok = plugin.ReloadManifestImmediate(out string msg);
                return ok ? $"[Rift] {msg}" : $"[Rift] Reload failed: {msg}";
            }

            case "kit":
            {
                if (!IsPeerAdmin(senderPeerUid))
                {
                    return "[Rift] Permission denied: '/rift kit dwarf' requires adminlist.txt authorization.";
                }
                string kitName = parts.Length > 1 ? parts[1].ToLowerInvariant() : "dwarf";
                if (kitName != "dwarf")
                {
                    return $"[Rift] Unknown kit '{kitName}'. Supported kit: '/rift kit dwarf'.";
                }
                director.GrantDwarfKit(senderPeerUid, out string msg);
                return $"[Rift] {msg}";
            }

            default:
                return "[Rift] Usage: !rift [start | status | leave | abort | reload | kit dwarf] (or '/s /rift <cmd>' on vanilla clients)";
        }
    }

    private static bool IsPeerAdmin(long peerUid)
    {
        if (ZNet.instance == null)
        {
            return false;
        }
        if (ZDOMan.instance != null && peerUid == ZDOMan.GetSessionID())
        {
            return true;
        }

        var peer = ZNet.instance.GetPeer(peerUid);
        if (peer?.m_socket == null)
        {
            return false;
        }

        string hostName = peer.m_socket.GetHostName();
        return !string.IsNullOrWhiteSpace(hostName) && ZNet.instance.IsAdmin(hostName);
    }
}
