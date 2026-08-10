using System.Globalization;
using System.Linq;
using ValveKeyValue;

namespace ValveResourceFormat.ResourceTypes.ParticleUpgrade;

/// <summary>
/// KV3 tree accessors and editors matching the engine's KeyValues3 member helpers:
/// reads coerce numeric and boolean values and fall back to the default for other types,
/// writes replace existing members in place and append new members at the object tail.
/// </summary>
internal static class UpgradeKV
{
    public static bool IsObject(KVObject? value) => value is { IsCollection: true };

    public static KVObject? Find(this KVObject obj, string name)
        => obj.TryGetValue(name, out var value) ? value : null;

    public static IReadOnlyList<KVObject> Elements(this KVObject? value)
        => value is { IsArray: true } ? [.. value.Values] : [];

    public static IReadOnlyList<KVObject> ElementsOf(this KVObject obj, string name)
        => obj.Find(name).Elements();

    public static string GetString(this KVObject obj, string name, string defaultValue)
    {
        var value = obj.Find(name);
        return value?.ValueType == KVValueType.String ? (string)value! : defaultValue;
    }

    public static long GetInt(this KVObject obj, string name, long defaultValue)
    {
        var value = obj.Find(name);

        if (value == null)
        {
            return defaultValue;
        }

        return value.ValueType switch
        {
            KVValueType.Boolean => value.ToBoolean(CultureInfo.InvariantCulture) ? 1 : 0,
            KVValueType.Int16 or KVValueType.Int32 or KVValueType.Int64 => value.ToInt64(CultureInfo.InvariantCulture),
            KVValueType.UInt16 or KVValueType.UInt32 or KVValueType.UInt64 => unchecked((long)value.ToUInt64(CultureInfo.InvariantCulture)),
            KVValueType.FloatingPoint => (long)value.ToSingle(CultureInfo.InvariantCulture),
            KVValueType.FloatingPoint64 => (long)value.ToDouble(CultureInfo.InvariantCulture),
            _ => defaultValue,
        };
    }

    public static double GetDouble(this KVObject obj, string name, double defaultValue)
    {
        var value = obj.Find(name);

        if (value == null)
        {
            return defaultValue;
        }

        return value.ValueType switch
        {
            KVValueType.Boolean => value.ToBoolean(CultureInfo.InvariantCulture) ? 1.0 : 0.0,
            KVValueType.Int16 or KVValueType.Int32 or KVValueType.Int64 => value.ToInt64(CultureInfo.InvariantCulture),
            KVValueType.UInt16 or KVValueType.UInt32 or KVValueType.UInt64 => value.ToUInt64(CultureInfo.InvariantCulture),
            KVValueType.FloatingPoint => value.ToSingle(CultureInfo.InvariantCulture),
            KVValueType.FloatingPoint64 => value.ToDouble(CultureInfo.InvariantCulture),
            _ => defaultValue,
        };
    }

    public static float GetFloat(this KVObject obj, string name, float defaultValue)
        => (float)obj.GetDouble(name, defaultValue);

    public static bool GetBool(this KVObject obj, string name, bool defaultValue)
    {
        var value = obj.Find(name);

        if (value == null)
        {
            return defaultValue;
        }

        return value.ValueType switch
        {
            KVValueType.Boolean => value.ToBoolean(CultureInfo.InvariantCulture),
            KVValueType.Int16 or KVValueType.Int32 or KVValueType.Int64 => value.ToInt64(CultureInfo.InvariantCulture) != 0,
            KVValueType.UInt16 or KVValueType.UInt32 or KVValueType.UInt64 => value.ToUInt64(CultureInfo.InvariantCulture) != 0,
            KVValueType.FloatingPoint or KVValueType.FloatingPoint64 => value.ToDouble(CultureInfo.InvariantCulture) != 0.0,
            _ => defaultValue,
        };
    }

    public static bool IsFloatingPoint(this KVObject value)
        => value.ValueType is KVValueType.FloatingPoint or KVValueType.FloatingPoint64;

    public static bool TryGetNumber(this KVObject value, out double number)
    {
        switch (value.ValueType)
        {
            case KVValueType.Int16 or KVValueType.Int32 or KVValueType.Int64:
                number = value.ToInt64(CultureInfo.InvariantCulture);
                return true;
            case KVValueType.UInt16 or KVValueType.UInt32 or KVValueType.UInt64:
                number = value.ToUInt64(CultureInfo.InvariantCulture);
                return true;
            case KVValueType.FloatingPoint:
                number = value.ToSingle(CultureInfo.InvariantCulture);
                return true;
            case KVValueType.FloatingPoint64:
                number = value.ToDouble(CultureInfo.InvariantCulture);
                return true;
            default:
                number = 0.0;
                return false;
        }
    }

    /// <summary>
    /// Reads a member holding an exactly-three-element numeric array; any other shape yields
    /// the default.
    /// </summary>
    public static Vector3 GetFloat3(this KVObject obj, string name, Vector3 defaultValue)
    {
        var value = obj.Find(name);

        if (value is not { IsArray: true } || value.Count != 3)
        {
            return defaultValue;
        }

        Span<float> components = stackalloc float[3];
        var index = 0;

        foreach (var component in value.Values)
        {
            if (!component.TryGetNumber(out var number))
            {
                return defaultValue;
            }

            components[index++] = (float)number;
        }

        return new Vector3(components[0], components[1], components[2]);
    }

    public static void SetMember(this KVObject obj, string name, KVObject value)
        => obj[name] = value;

    public static void SetInt(this KVObject obj, string name, int value)
        => obj.SetMember(name, new KVObject(value));

    public static void SetFloat(this KVObject obj, string name, float value)
        => obj.SetMember(name, new KVObject(value));

    public static void SetBool(this KVObject obj, string name, bool value)
        => obj.SetMember(name, new KVObject(value));

    public static void SetString(this KVObject obj, string name, string value)
        => obj.SetMember(name, new KVObject(value));

    public static void SetFloatArray(this KVObject obj, string name, params float[] values)
    {
        var array = KVObject.Array(values.Length);

        foreach (var value in values)
        {
            array.Add(new KVObject(value));
        }

        obj.SetMember(name, array);
    }

    /// <summary>
    /// Finds or creates the named member and resets its value to an empty object,
    /// keeping the member's position when it already exists.
    /// </summary>
    public static KVObject SetObject(this KVObject obj, string name)
    {
        var created = KVObject.ListCollection();
        obj.SetMember(name, created);
        return created;
    }

    /// <summary>
    /// Reads and removes the named member, coercing it like <see cref="GetInt"/> with a zero
    /// fallback; an absent member yields the default and leaves the object untouched.
    /// </summary>
    public static long TakeInt(this KVObject obj, string name, long defaultValue)
    {
        if (!obj.ContainsKey(name))
        {
            return defaultValue;
        }

        var value = obj.GetInt(name, 0);
        obj.Remove(name);
        return value;
    }

    /// <summary>
    /// Finds the named member's object value for in-place member merging, creating an empty
    /// object member at the tail when absent and resetting a non-object value in place.
    /// </summary>
    public static KVObject MergeObject(this KVObject obj, string name)
    {
        var value = obj.Find(name);
        return value is { IsCollection: true } ? value : obj.SetObject(name);
    }

    /// <summary>
    /// Finds the named member, creating it as a null member at the object tail when absent.
    /// </summary>
    public static KVObject EnsureMember(this KVObject obj, string name)
    {
        var value = obj.Find(name);

        if (value == null)
        {
            value = KVObject.Null();
            obj.Add(name, value);
        }

        return value;
    }

    /// <summary>
    /// Renames a member in place, keeping its position among its siblings. Returns the member
    /// value, or null when the old member is absent or the new name already exists, in which
    /// case the object is left untouched.
    /// </summary>
    public static KVObject? Rename(this KVObject obj, string oldName, string newName)
    {
        if (!obj.ContainsKey(oldName) || obj.ContainsKey(newName))
        {
            return null;
        }

        var children = obj.Children.ToList();
        obj.Clear();
        KVObject? renamed = null;

        foreach (var (name, value) in children)
        {
            if (renamed == null && name == oldName)
            {
                renamed = value;
                obj.Add(newName, value);
            }
            else
            {
                obj.Add(name, value);
            }
        }

        return renamed;
    }

    /// <summary>
    /// Visits every object in the document in preorder, arrays included in the traversal.
    /// Children are snapshotted after the visit so the callback may edit the visited object.
    /// </summary>
    public static void WalkObjects(KVObject node, Action<KVObject> visit)
    {
        if (node.IsCollection)
        {
            visit(node);

            foreach (var child in node.Values.ToList())
            {
                WalkObjects(child, visit);
            }
        }
        else if (node.IsArray)
        {
            foreach (var element in node.Values.ToList())
            {
                WalkObjects(element, visit);
            }
        }
    }
}
