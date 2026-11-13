using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ValheimRiftEngine.Models;

public static class RiftConstants
{
    public const float SkyboxAltitudeY = 4500f;
    public const float MaxZoneSpanMeters = 58f;
    public const float CorridorLengthMeters = 4f;
    public const float CorridorWidthMeters = 4f;
    public const int MaxActiveAiCap = 12;
    public const int MaxMobsPerWave = 10;
    public const int MinRooms = 3;
    public const int MaxRooms = 4;

    public static readonly HashSet<string> ValidAffixes = new(StringComparer.Ordinal)
    {
        "molten_slag",
        "bolstering",
        "thunder_blessing",
        "shielded_pack",
    };

    public static readonly HashSet<string> PositiveBlessingAffixes = new(StringComparer.Ordinal)
    {
        "thunder_blessing",
    };

    public static readonly HashSet<string> ValidTemplates = new(StringComparer.Ordinal)
    {
        "entry_vestibule",
        "crucible_square",
        "gauntlet_bridge",
        "sanctum_oct",
    };

    public static readonly HashSet<string> ValidPillarLayouts = new(StringComparer.Ordinal)
    {
        "none",
        "four_corners",
        "center_los_blocker",
    };

    public static readonly HashSet<string> ValidAnchors = new(StringComparer.Ordinal)
    {
        "center",
        "north_left",
        "north_right",
        "south_left",
        "south_right",
        "sniper_perch_ne",
        "sniper_perch_nw",
    };

    public static (float WidthX, float LengthZ) GetTemplateDimensions(string template) => template switch
    {
        "entry_vestibule" => (8f, 8f),
        "crucible_square" => (16f, 16f),
        "gauntlet_bridge" => (8f, 16f),
        "sanctum_oct" => (20f, 20f),
        _ => (16f, 16f),
    };
}

public sealed class RiftManifest
{
    public string RiftId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int KeystoneLevel { get; set; } = 1;
    public int ParTimeSeconds { get; set; } = 300;
    public List<string> GlobalAffixes { get; set; } = new();
    public List<RoomSpec> Rooms { get; set; } = new();
    public RewardSpec Rewards { get; set; } = new();

    public float HealthMultiplier => 1.0f + (0.18f * KeystoneLevel);
    public float DamageMultiplier => 1.0f + (0.10f * KeystoneLevel);

    public float ComputeTotalZSpanMeters()
    {
        if (Rooms.Count == 0)
        {
            return 0f;
        }

        float sum = 0f;
        for (int i = 0; i < Rooms.Count; i++)
        {
            var (_, lengthZ) = RiftConstants.GetTemplateDimensions(Rooms[i].Template);
            sum += lengthZ;
            if (i < Rooms.Count - 1)
            {
                sum += RiftConstants.CorridorLengthMeters;
            }
        }
        return sum;
    }

    public static bool TryParseAndValidate(
        string json,
        ZNetScene? netScene,
        out RiftManifest? manifest,
        out List<string> errors)
    {
        manifest = null;
        errors = new List<string>();

        if (string.IsNullOrWhiteSpace(json))
        {
            errors.Add("Manifest JSON is empty.");
            return false;
        }

        Dictionary<string, object?> root;
        try
        {
            var parsed = MiniJsonParser.Parse(json);
            if (parsed is not Dictionary<string, object?> dict)
            {
                errors.Add("Root JSON value must be an object.");
                return false;
            }
            root = dict;
        }
        catch (Exception ex)
        {
            errors.Add($"JSON syntax error: {ex.Message}");
            return false;
        }

        try
        {
            var m = new RiftManifest
            {
                RiftId = ReadString(root, "rift_id", errors),
                Title = ReadString(root, "title", errors),
                KeystoneLevel = ReadInt(root, "keystone_level", errors),
                ParTimeSeconds = ReadInt(root, "par_time_seconds", errors),
            };

            if (TryGetList(root, "global_affixes", errors, out var affixList))
            {
                foreach (var item in affixList)
                {
                    if (item is string s)
                    {
                        m.GlobalAffixes.Add(s);
                    }
                    else
                    {
                        errors.Add("global_affixes entries must be strings.");
                    }
                }
            }

            if (TryGetList(root, "rooms", errors, out var roomList))
            {
                for (int i = 0; i < roomList.Count; i++)
                {
                    if (roomList[i] is not Dictionary<string, object?> roomObj)
                    {
                        errors.Add($"rooms[{i}] must be an object.");
                        continue;
                    }

                    var room = new RoomSpec
                    {
                        RoomIndex = ReadInt(roomObj, "room_index", errors),
                        Template = ReadString(roomObj, "template", errors),
                        PillarLayout = ReadString(roomObj, "pillar_layout", errors),
                    };

                    if (TryGetList(roomObj, "waves", errors, out var waveList))
                    {
                        for (int w = 0; w < waveList.Count; w++)
                        {
                            if (waveList[w] is not Dictionary<string, object?> waveObj)
                            {
                                errors.Add($"rooms[{i}].waves[{w}] must be an object.");
                                continue;
                            }

                            var wave = new WaveSpec
                            {
                                WaveIndex = ReadInt(waveObj, "wave_index", errors),
                            };

                            if (TryGetList(waveObj, "spawns", errors, out var spawnList))
                            {
                                for (int s = 0; s < spawnList.Count; s++)
                                {
                                    if (spawnList[s] is not Dictionary<string, object?> spawnObj)
                                    {
                                        errors.Add($"rooms[{i}].waves[{w}].spawns[{s}] must be an object.");
                                        continue;
                                    }

                                    wave.Spawns.Add(new SpawnSpec
                                    {
                                        Prefab = ReadString(spawnObj, "prefab", errors),
                                        Anchor = ReadString(spawnObj, "anchor", errors),
                                        Count = ReadInt(spawnObj, "count", errors),
                                        Stars = ReadInt(spawnObj, "stars", errors),
                                        IsBoss = ReadBool(spawnObj, "is_boss", errors),
                                    });
                                }
                            }

                            room.Waves.Add(wave);
                        }
                    }

                    m.Rooms.Add(room);
                }
            }

            if (root.TryGetValue("rewards", out var rewardsRaw) && rewardsRaw is Dictionary<string, object?> rewardsObj)
            {
                m.Rewards.BaseDrops = ReadDropList(rewardsObj, "base_drops", errors);
                m.Rewards.TimedBonusDrops = ReadDropList(rewardsObj, "timed_bonus_drops", errors);
            }
            else
            {
                errors.Add("Missing or invalid 'rewards' object.");
            }

            ValidateSemantics(m, netScene, errors);
            if (errors.Count > 0)
            {
                return false;
            }

            manifest = m;
            return true;
        }
        catch (Exception ex)
        {
            errors.Add($"Unexpected manifest parse error: {ex.Message}");
            return false;
        }
    }

    private static void ValidateSemantics(RiftManifest m, ZNetScene? netScene, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(m.RiftId) || !Regex.IsMatch(m.RiftId, "^[a-z0-9]+(?:-[a-z0-9]+)*$"))
        {
            errors.Add($"rift_id '{m.RiftId}' must be a non-empty kebab-case slug.");
        }

        if (string.IsNullOrWhiteSpace(m.Title) || m.Title.Length < 3 || m.Title.Length > 80)
        {
            errors.Add("title must be between 3 and 80 characters.");
        }

        if (m.KeystoneLevel < 1 || m.KeystoneLevel > 15)
        {
            errors.Add($"keystone_level ({m.KeystoneLevel}) must be in [1, 15].");
        }

        if (m.ParTimeSeconds < 180 || m.ParTimeSeconds > 900)
        {
            errors.Add($"par_time_seconds ({m.ParTimeSeconds}) must be in [180, 900].");
        }

        if (m.GlobalAffixes.Count < 1 || m.GlobalAffixes.Count > 3)
        {
            errors.Add($"global_affixes count ({m.GlobalAffixes.Count}) must be in [1, 3].");
        }

        var seenAffixes = new HashSet<string>(StringComparer.Ordinal);
        bool hasPositiveBlessing = false;
        foreach (var affix in m.GlobalAffixes)
        {
            if (!RiftConstants.ValidAffixes.Contains(affix))
            {
                errors.Add($"Unknown global_affix '{affix}'.");
            }
            if (!seenAffixes.Add(affix))
            {
                errors.Add($"Duplicate global_affix '{affix}'.");
            }
            if (RiftConstants.PositiveBlessingAffixes.Contains(affix))
            {
                hasPositiveBlessing = true;
            }
        }

        if (!hasPositiveBlessing)
        {
            errors.Add("global_affixes must include at least 1 positive blessing affix (e.g., 'thunder_blessing').");
        }

        if (m.Rooms.Count < RiftConstants.MinRooms || m.Rooms.Count > RiftConstants.MaxRooms)
        {
            errors.Add($"rooms count ({m.Rooms.Count}) must be between {RiftConstants.MinRooms} and {RiftConstants.MaxRooms}.");
            return;
        }

        if (m.Rooms[0].Template != "entry_vestibule")
        {
            errors.Add($"rooms[0].template must be 'entry_vestibule' (got '{m.Rooms[0].Template}').");
        }

        float totalZ = m.ComputeTotalZSpanMeters();
        if (totalZ > RiftConstants.MaxZoneSpanMeters)
        {
            errors.Add($"Total Rift Z-span ({totalZ:F1}m) exceeds single-zone limit ({RiftConstants.MaxZoneSpanMeters:F1}m).");
        }

        for (int i = 0; i < m.Rooms.Count; i++)
        {
            var room = m.Rooms[i];
            if (room.RoomIndex != i)
            {
                errors.Add($"rooms[{i}].room_index must equal {i} (got {room.RoomIndex}).");
            }
            if (!RiftConstants.ValidTemplates.Contains(room.Template))
            {
                errors.Add($"rooms[{i}] has unknown template '{room.Template}'.");
            }
            if (!RiftConstants.ValidPillarLayouts.Contains(room.PillarLayout))
            {
                errors.Add($"rooms[{i}] has unknown pillar_layout '{room.PillarLayout}'.");
            }
            if (room.Waves.Count > 3)
            {
                errors.Add($"rooms[{i}] has {room.Waves.Count} waves (max 3).");
            }
            if (i == 0 && room.Waves.Count > 0)
            {
                errors.Add("rooms[0] (entry_vestibule) must have 0 waves (safe staging room).");
            }
            if (i > 0 && room.Waves.Count == 0)
            {
                errors.Add($"Combat room rooms[{i}] must contain at least 1 wave.");
            }

            for (int w = 0; w < room.Waves.Count; w++)
            {
                var wave = room.Waves[w];
                if (wave.WaveIndex != w)
                {
                    errors.Add($"rooms[{i}].waves[{w}].wave_index must equal {w} (got {wave.WaveIndex}).");
                }
                if (wave.Spawns.Count == 0)
                {
                    errors.Add($"rooms[{i}].waves[{w}] must have at least 1 spawn.");
                }

                int waveMobSum = 0;
                foreach (var sp in wave.Spawns)
                {
                    if (!RiftConstants.ValidAnchors.Contains(sp.Anchor))
                    {
                        errors.Add($"rooms[{i}].waves[{w}] has invalid anchor '{sp.Anchor}'.");
                    }
                    if (sp.Count < 1 || sp.Count > 4)
                    {
                        errors.Add($"rooms[{i}].waves[{w}] spawn '{sp.Prefab}' count ({sp.Count}) must be in [1, 4].");
                    }
                    if (sp.Stars < 0 || sp.Stars > 2)
                    {
                        errors.Add($"rooms[{i}].waves[{w}] spawn '{sp.Prefab}' stars ({sp.Stars}) must be in [0, 2].");
                    }
                    if (netScene != null && netScene.GetPrefab(sp.Prefab) == null)
                    {
                        errors.Add($"Creature prefab '{sp.Prefab}' does not exist in ZNetScene.");
                    }
                    waveMobSum += sp.Count;
                }

                if (waveMobSum > RiftConstants.MaxMobsPerWave)
                {
                    errors.Add($"rooms[{i}].waves[{w}] spawns {waveMobSum} mobs (hard lint cap is {RiftConstants.MaxMobsPerWave}).");
                }
            }
        }

        var finalRoom = m.Rooms[m.Rooms.Count - 1];
        bool hasFinalBoss = false;
        foreach (var wave in finalRoom.Waves)
        {
            foreach (var sp in wave.Spawns)
            {
                if (sp.IsBoss)
                {
                    hasFinalBoss = true;
                }
            }
        }

        if (!hasFinalBoss)
        {
            errors.Add("Final room must include at least 1 spawn with is_boss = true.");
        }

        ValidateDrops(m.Rewards.BaseDrops, "rewards.base_drops", netScene, errors);
        ValidateDrops(m.Rewards.TimedBonusDrops, "rewards.timed_bonus_drops", netScene, errors);
    }

    private static void ValidateDrops(List<LootDropSpec> drops, string label, ZNetScene? netScene, List<string> errors)
    {
        if (drops.Count == 0)
        {
            errors.Add($"{label} must contain at least 1 loot entry.");
            return;
        }

        foreach (var drop in drops)
        {
            if (string.IsNullOrWhiteSpace(drop.Prefab))
            {
                errors.Add($"{label} contains an empty prefab name.");
                continue;
            }
            if (drop.Amount < 1 || drop.Amount > 999)
            {
                errors.Add($"{label} '{drop.Prefab}' amount ({drop.Amount}) must be in [1, 999].");
            }
            if (drop.Quality < 1 || drop.Quality > 4)
            {
                errors.Add($"{label} '{drop.Prefab}' quality ({drop.Quality}) must be in [1, 4].");
            }
            if (netScene != null && netScene.GetPrefab(drop.Prefab) == null)
            {
                errors.Add($"{label} item prefab '{drop.Prefab}' does not exist in ZNetScene.");
            }
        }
    }

    private static List<LootDropSpec> ReadDropList(Dictionary<string, object?> parent, string key, List<string> errors)
    {
        var result = new List<LootDropSpec>();
        if (!TryGetList(parent, key, errors, out var list))
        {
            return result;
        }

        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is not Dictionary<string, object?> obj)
            {
                errors.Add($"{key}[{i}] must be an object.");
                continue;
            }
            result.Add(new LootDropSpec
            {
                Prefab = ReadString(obj, "prefab", errors),
                Amount = ReadInt(obj, "amount", errors),
                Quality = ReadInt(obj, "quality", errors),
            });
        }
        return result;
    }

    private static string ReadString(Dictionary<string, object?> obj, string key, List<string> errors)
    {
        if (!obj.TryGetValue(key, out var val) || val is not string s)
        {
            errors.Add($"Missing or non-string property '{key}'.");
            return string.Empty;
        }
        return s;
    }

    private static int ReadInt(Dictionary<string, object?> obj, string key, List<string> errors)
    {
        if (!obj.TryGetValue(key, out var val) || val is null)
        {
            errors.Add($"Missing integer property '{key}'.");
            return 0;
        }
        if (val is long l)
        {
            return (int)l;
        }
        if (val is double d && Math.Abs(d - Math.Round(d)) < 1e-6)
        {
            return (int)Math.Round(d);
        }
        errors.Add($"Property '{key}' must be an integer.");
        return 0;
    }

    private static bool ReadBool(Dictionary<string, object?> obj, string key, List<string> errors)
    {
        if (!obj.TryGetValue(key, out var val) || val is not bool b)
        {
            errors.Add($"Missing or non-boolean property '{key}'.");
            return false;
        }
        return b;
    }

    private static bool TryGetList(Dictionary<string, object?> obj, string key, List<string> errors, out List<object?> list)
    {
        if (obj.TryGetValue(key, out var val) && val is List<object?> found)
        {
            list = found;
            return true;
        }
        errors.Add($"Missing or non-array property '{key}'.");
        list = new List<object?>();
        return false;
    }
}

public sealed class RoomSpec
{
    public int RoomIndex { get; set; }
    public string Template { get; set; } = string.Empty;
    public string PillarLayout { get; set; } = "none";
    public List<WaveSpec> Waves { get; set; } = new();
}

public sealed class WaveSpec
{
    public int WaveIndex { get; set; }
    public List<SpawnSpec> Spawns { get; set; } = new();
}

public sealed class SpawnSpec
{
    public string Prefab { get; set; } = string.Empty;
    public string Anchor { get; set; } = "center";
    public int Count { get; set; } = 1;
    public int Stars { get; set; }
    public bool IsBoss { get; set; }
}

public sealed class RewardSpec
{
    public List<LootDropSpec> BaseDrops { get; set; } = new();
    public List<LootDropSpec> TimedBonusDrops { get; set; } = new();
}

public sealed class LootDropSpec
{
    public string Prefab { get; set; } = string.Empty;
    public int Amount { get; set; } = 1;
    public int Quality { get; set; } = 1;
}

/// <summary>
/// Zero-dependency RFC 8259 recursive-descent JSON parser.
/// </summary>
internal ref struct MiniJsonParser
{
    private readonly ReadOnlySpan<char> _src;
    private int _pos;

    private MiniJsonParser(ReadOnlySpan<char> src)
    {
        _src = src;
        _pos = 0;
    }

    public static object? Parse(string json)
    {
        var parser = new MiniJsonParser(json.AsSpan());
        parser.SkipWhitespace();
        var value = parser.ParseValue();
        parser.SkipWhitespace();
        if (parser._pos != parser._src.Length)
        {
            throw new FormatException($"Unexpected trailing character '{parser._src[parser._pos]}' at position {parser._pos}.");
        }
        return value;
    }

    private object? ParseValue()
    {
        SkipWhitespace();
        if (_pos >= _src.Length)
        {
            throw new FormatException("Unexpected end of JSON input.");
        }

        return _src[_pos] switch
        {
            '{' => ParseObject(),
            '[' => ParseArray(),
            '"' => ParseString(),
            't' => ParseLiteral("true", true),
            'f' => ParseLiteral("false", false),
            'n' => ParseLiteral("null", null),
            '-' or >= '0' and <= '9' => ParseNumber(),
            var c => throw new FormatException($"Unexpected token '{c}' at position {_pos}."),
        };
    }

    private Dictionary<string, object?> ParseObject()
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        _pos++; // '{'
        SkipWhitespace();
        if (_pos < _src.Length && _src[_pos] == '}')
        {
            _pos++;
            return dict;
        }

        while (true)
        {
            SkipWhitespace();
            if (_pos >= _src.Length || _src[_pos] != '"')
            {
                throw new FormatException($"Expected string property name at position {_pos}.");
            }
            string key = ParseString();
            SkipWhitespace();
            Expect(':');
            var val = ParseValue();
            dict[key] = val;
            SkipWhitespace();
            if (_pos < _src.Length && _src[_pos] == '}')
            {
                _pos++;
                return dict;
            }
            Expect(',');
        }
    }

    private List<object?> ParseArray()
    {
        var list = new List<object?>();
        _pos++; // '['
        SkipWhitespace();
        if (_pos < _src.Length && _src[_pos] == ']')
        {
            _pos++;
            return list;
        }

        while (true)
        {
            list.Add(ParseValue());
            SkipWhitespace();
            if (_pos < _src.Length && _src[_pos] == ']')
            {
                _pos++;
                return list;
            }
            Expect(',');
        }
    }

    private string ParseString()
    {
        Expect('"');
        var sb = new StringBuilder();
        while (_pos < _src.Length)
        {
            char c = _src[_pos++];
            if (c == '"')
            {
                return sb.ToString();
            }
            if (c == '\\')
            {
                if (_pos >= _src.Length)
                {
                    throw new FormatException("Unterminated escape sequence in string.");
                }
                char esc = _src[_pos++];
                switch (esc)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (_pos + 4 > _src.Length)
                        {
                            throw new FormatException("Invalid \\uXXXX escape sequence.");
                        }
                        var hex = _src.Slice(_pos, 4).ToString();
                        sb.Append((char)ushort.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        _pos += 4;
                        break;
                    default:
                        throw new FormatException($"Invalid escape character '\\{esc}' at position {_pos}.");
                }
            }
            else
            {
                sb.Append(c);
            }
        }
        throw new FormatException("Unterminated JSON string.");
    }

    private object ParseNumber()
    {
        int start = _pos;
        if (_src[_pos] == '-')
        {
            _pos++;
        }
        while (_pos < _src.Length && char.IsDigit(_src[_pos]))
        {
            _pos++;
        }
        bool isFloat = false;
        if (_pos < _src.Length && _src[_pos] == '.')
        {
            isFloat = true;
            _pos++;
            while (_pos < _src.Length && char.IsDigit(_src[_pos]))
            {
                _pos++;
            }
        }
        if (_pos < _src.Length && (_src[_pos] == 'e' || _src[_pos] == 'E'))
        {
            isFloat = true;
            _pos++;
            if (_pos < _src.Length && (_src[_pos] == '+' || _src[_pos] == '-'))
            {
                _pos++;
            }
            while (_pos < _src.Length && char.IsDigit(_src[_pos]))
            {
                _pos++;
            }
        }

        string token = _src.Slice(start, _pos - start).ToString();
        if (!isFloat && long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long intVal))
        {
            return intVal;
        }
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double dblVal))
        {
            return dblVal;
        }
        throw new FormatException($"Invalid numeric literal '{token}' at position {start}.");
    }

    private object? ParseLiteral(string literal, object? result)
    {
        if (_pos + literal.Length <= _src.Length &&
            _src.Slice(_pos, literal.Length).SequenceEqual(literal.AsSpan()))
        {
            _pos += literal.Length;
            return result;
        }
        throw new FormatException($"Invalid token at position {_pos}; expected '{literal}'.");
    }

    private void Expect(char expected)
    {
        if (_pos >= _src.Length || _src[_pos] != expected)
        {
            throw new FormatException($"Expected '{expected}' at position {_pos}.");
        }
        _pos++;
    }

    private void SkipWhitespace()
    {
        while (_pos < _src.Length && char.IsWhiteSpace(_src[_pos]))
        {
            _pos++;
        }
    }
}
