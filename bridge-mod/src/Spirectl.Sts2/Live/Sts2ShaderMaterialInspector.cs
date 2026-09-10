using Godot;

namespace Spirectl.Sts2.Live;

internal sealed record Sts2ShaderParameterInspectionResult(
    IReadOnlyList<Sts2ShaderParameterValue> Parameters,
    IReadOnlyList<Sts2ShaderParameterNotice> Notices);

internal sealed record Sts2ShaderParameterValue(
    string Name,
    string ValueKind,
    string? StringValue = null,
    double? NumberValue = null,
    bool? BoolValue = null,
    Color? ColorValue = null,
    Vector2? Vector2Value = null,
    Resource? ResourceValue = null,
    // Godot-native-first extended uniform kinds (emitted on the mirror scene-delta only — the structured/proto
    // scene path keeps the original six kinds). The matching value is set for the corresponding ValueKind:
    // vector3 → Vector3Value, vector4/quaternion → Vector4Value, rect2 → Rect2Value, transform2d →
    // Transform2DValue, and the array kinds (vector3Array/vector4Array/vector2Array/floatArray/intArray) →
    // NumberArrayValue (flattened row-major; the element stride is implied by the kind).
    Vector3? Vector3Value = null,
    Vector4? Vector4Value = null,
    Rect2? Rect2Value = null,
    Transform2D? Transform2DValue = null,
    IReadOnlyList<double>? NumberArrayValue = null);

internal sealed record Sts2ShaderParameterNotice(
    string Code,
    string Field,
    string Message);

internal static class Sts2ShaderMaterialInspector
{
    public static Sts2ShaderParameterInspectionResult Inspect(
        Resource? material,
        Resource? shader,
        int limit = 32,
        // Godot-native-first: the mirror scene-delta path opts in to the extended uniform kinds
        // (vector3/vector4/rect2/transform2d/arrays). The structured/proto scene path leaves this false so its
        // output stays byte-identical (those uniforms keep their prior `shader_parameter_unavailable` notice).
        bool includeExtendedKinds = false)
    {
        if (material is null || shader is null)
        {
            return new Sts2ShaderParameterInspectionResult([], []);
        }

        var uniforms = ShaderUniforms(shader)
            .DistinctBy(uniform => uniform.Name, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
        if (uniforms.Length == 0)
        {
            return new Sts2ShaderParameterInspectionResult([], []);
        }

        var parameters = new List<Sts2ShaderParameterValue>();
        var notices = new List<Sts2ShaderParameterNotice>();
        foreach (var uniform in uniforms)
        {
            try
            {
                var value = ReadShaderParameter(material, uniform.Name);
                if (TryDescribeShaderParameter(uniform.Name, value, includeExtendedKinds, out var parameter)
                    || (uniform.DefaultValue.HasValue
                        && TryDescribeShaderParameter(uniform.Name, uniform.DefaultValue.Value, includeExtendedKinds, out parameter)))
                {
                    parameters.Add(parameter);
                    continue;
                }

                notices.Add(new Sts2ShaderParameterNotice(
                    "shader_parameter_unavailable",
                    $"material.shaderParameters.{uniform.Name}",
                    $"Shader parameter '{uniform.Name}' returned no readable value and no default value was available."));
            }
            catch (Exception ex)
            {
                notices.Add(new Sts2ShaderParameterNotice(
                    "shader_parameter_read_failed",
                    $"material.shaderParameters.{uniform.Name}",
                    $"Failed to read shader parameter '{uniform.Name}': {ex.GetType().Name}: {ex.Message}"));
            }
        }

        return new Sts2ShaderParameterInspectionResult(parameters, notices);
    }

    private static object? ReadShaderParameter(Resource material, string name)
    {
        using var parameterName = new StringName(name);
        if (material is ShaderMaterial shaderMaterial)
        {
            return shaderMaterial.GetShaderParameter(parameterName);
        }

        return Sts2LiveIntrospection.InvokeMethod(material, "GetShaderParameter", parameterName);
    }

    private static IEnumerable<ShaderUniformInfo> ShaderUniforms(Resource shader)
    {
        var byName = new Dictionary<string, ShaderUniformInfo>(StringComparer.Ordinal);
        foreach (var uniform in ShaderUniformsFromNative(shader))
        {
            byName.TryAdd(uniform.Name, uniform);
        }

        foreach (var name in ShaderUniformNamesFromCode(shader))
        {
            byName.TryAdd(name, new ShaderUniformInfo(name, null));
        }

        foreach (var name in ShaderUniformFallbackNames(shader))
        {
            byName.TryAdd(name, new ShaderUniformInfo(name, null));
        }

        return byName.Values;
    }

    private static IEnumerable<ShaderUniformInfo> ShaderUniformsFromNative(Resource shader)
    {
        if (shader is not Shader typedShader)
        {
            yield break;
        }

        Godot.Collections.Array uniforms;
        try
        {
            uniforms = typedShader.GetShaderUniformList(false);
        }
        catch
        {
            yield break;
        }

        using (uniforms)
        {
            foreach (var entry in uniforms)
            {
                if (entry.VariantType != Variant.Type.Dictionary)
                {
                    continue;
                }

                using var dictionary = entry.AsGodotDictionary();
                if (!TryGetDictionaryValue(dictionary, "name", out var nameValue)
                    || VariantString(nameValue) is not { Length: > 0 } name)
                {
                    continue;
                }

                yield return new ShaderUniformInfo(
                    name,
                    TryGetDictionaryValue(dictionary, "default_value", out var defaultValue)
                        ? (Variant?)defaultValue
                        : null);
            }
        }
    }

    private static IEnumerable<string> ShaderUniformNamesFromCode(Resource shader)
    {
        var code = ReadProperty(shader, "Code")?.ToString();
        if (string.IsNullOrWhiteSpace(code))
        {
            yield break;
        }

        foreach (var line in code.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("uniform ", StringComparison.Ordinal))
            {
                continue;
            }

            var beforeSemicolon = trimmed.Split(';', 2)[0];
            var beforeHint = beforeSemicolon.Split(':', 2)[0];
            var beforeDefault = beforeHint.Split('=', 2)[0];
            var parts = beforeDefault.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                yield return parts[^1];
            }
        }
    }

    private static IEnumerable<string> ShaderUniformFallbackNames(Resource shader)
    {
        var path = shader.ResourcePath ?? string.Empty;
        if (path.EndsWith("/hsv.gdshader", StringComparison.OrdinalIgnoreCase))
        {
            yield return "h";
            yield return "s";
            yield return "v";
        }
    }

    private static bool TryGetDictionaryValue(
        Godot.Collections.Dictionary dictionary,
        string key,
        out Variant value)
    {
        foreach (var pair in dictionary)
        {
            if (string.Equals(VariantString(pair.Key), key, StringComparison.Ordinal))
            {
                value = pair.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryDescribeShaderParameter(
        string name,
        object? value,
        bool includeExtendedKinds,
        out Sts2ShaderParameterValue parameter)
    {
        parameter = default!;
        switch (value)
        {
            case null:
                return false;
            case bool typed:
                parameter = new(name, "bool", BoolValue: typed);
                return true;
            case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                parameter = new(
                    name,
                    "number",
                    NumberValue: Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
                return true;
            case string typed:
                parameter = new(name, "string", StringValue: typed);
                return true;
            case Color typed:
                parameter = new(name, "color", ColorValue: typed);
                return true;
            case Vector2 typed:
                parameter = new(name, "vector2", Vector2Value: typed);
                return true;
            case Vector3 typed when includeExtendedKinds:
                parameter = new(name, "vector3", Vector3Value: typed);
                return true;
            case Vector4 typed when includeExtendedKinds:
                parameter = new(name, "vector4", Vector4Value: typed);
                return true;
            case Quaternion typed when includeExtendedKinds:
                parameter = new(name, "vector4", Vector4Value: new Vector4(typed.X, typed.Y, typed.Z, typed.W));
                return true;
            case Rect2 typed when includeExtendedKinds:
                parameter = new(name, "rect2", Rect2Value: typed);
                return true;
            case Transform2D typed when includeExtendedKinds:
                parameter = new(name, "transform2d", Transform2DValue: typed);
                return true;
            case Resource resource:
                parameter = new(name, "resource", ResourceValue: resource);
                return true;
            case Variant variant:
                return TryDescribeVariantShaderParameter(name, variant, includeExtendedKinds, out parameter);
            default:
                return false;
        }
    }

    private static bool TryDescribeVariantShaderParameter(
        string name,
        Variant value,
        bool includeExtendedKinds,
        out Sts2ShaderParameterValue parameter)
    {
        parameter = default!;
        switch (value.VariantType)
        {
            case Variant.Type.Nil:
                return false;
            case Variant.Type.Bool:
                parameter = new(name, "bool", BoolValue: value.AsBool());
                return true;
            case Variant.Type.Int:
                parameter = new(name, "number", NumberValue: value.AsInt64());
                return true;
            case Variant.Type.Float:
                parameter = new(name, "number", NumberValue: value.AsDouble());
                return true;
            case Variant.Type.String:
                parameter = new(name, "string", StringValue: value.AsString());
                return true;
            case Variant.Type.StringName:
                parameter = new(name, "string", StringValue: value.AsStringName().ToString());
                return true;
            case Variant.Type.Color:
                parameter = new(name, "color", ColorValue: value.AsColor());
                return true;
            case Variant.Type.Vector2:
                parameter = new(name, "vector2", Vector2Value: value.AsVector2());
                return true;
            case Variant.Type.Object when value.AsGodotObject() is Resource resource:
                parameter = new(name, "resource", ResourceValue: resource);
                return true;
            case Variant.Type.Vector3 when includeExtendedKinds:
                parameter = new(name, "vector3", Vector3Value: value.AsVector3());
                return true;
            case Variant.Type.Vector4 when includeExtendedKinds:
                parameter = new(name, "vector4", Vector4Value: value.AsVector4());
                return true;
            case Variant.Type.Quaternion when includeExtendedKinds:
                var q = value.AsQuaternion();
                parameter = new(name, "vector4", Vector4Value: new Vector4(q.X, q.Y, q.Z, q.W));
                return true;
            case Variant.Type.Rect2 when includeExtendedKinds:
                parameter = new(name, "rect2", Rect2Value: value.AsRect2());
                return true;
            case Variant.Type.Rect2I when includeExtendedKinds:
                var ri = value.AsRect2I();
                parameter = new(name, "rect2", Rect2Value: new Rect2(ri.Position.X, ri.Position.Y, ri.Size.X, ri.Size.Y));
                return true;
            case Variant.Type.Transform2D when includeExtendedKinds:
                parameter = new(name, "transform2d", Transform2DValue: value.AsTransform2D());
                return true;
            default:
                return includeExtendedKinds && TryDescribeArrayShaderParameter(name, value, out parameter);
        }
    }

    // Flatten a numeric/vector packed (or generic) array uniform into a flat double list. The kind names the
    // element type so a native client recovers the stride (vector3Array→3, vector4Array→4, vector2Array→2,
    // float/intArray→1); the web ignores these kinds. Returns false for arrays whose element type is not numeric.
    private static bool TryDescribeArrayShaderParameter(
        string name,
        Variant value,
        out Sts2ShaderParameterValue parameter)
    {
        parameter = default!;
        switch (value.VariantType)
        {
            case Variant.Type.PackedVector3Array:
            {
                var items = value.AsVector3Array();
                var flat = new double[items.Length * 3];
                for (var i = 0; i < items.Length; i++)
                {
                    flat[i * 3] = items[i].X;
                    flat[i * 3 + 1] = items[i].Y;
                    flat[i * 3 + 2] = items[i].Z;
                }

                parameter = new(name, "vector3Array", NumberArrayValue: flat);
                return true;
            }
            case Variant.Type.PackedColorArray:
            {
                var items = value.AsColorArray();
                var flat = new double[items.Length * 4];
                for (var i = 0; i < items.Length; i++)
                {
                    flat[i * 4] = items[i].R;
                    flat[i * 4 + 1] = items[i].G;
                    flat[i * 4 + 2] = items[i].B;
                    flat[i * 4 + 3] = items[i].A;
                }

                parameter = new(name, "vector4Array", NumberArrayValue: flat);
                return true;
            }
            case Variant.Type.PackedVector2Array:
            {
                var items = value.AsVector2Array();
                var flat = new double[items.Length * 2];
                for (var i = 0; i < items.Length; i++)
                {
                    flat[i * 2] = items[i].X;
                    flat[i * 2 + 1] = items[i].Y;
                }

                parameter = new(name, "vector2Array", NumberArrayValue: flat);
                return true;
            }
            case Variant.Type.PackedFloat32Array:
            {
                var items = value.AsFloat32Array();
                var flat = new double[items.Length];
                for (var i = 0; i < items.Length; i++)
                {
                    flat[i] = items[i];
                }

                parameter = new(name, "floatArray", NumberArrayValue: flat);
                return true;
            }
            case Variant.Type.PackedFloat64Array:
            {
                var items = value.AsFloat64Array();
                parameter = new(name, "floatArray", NumberArrayValue: [.. items]);
                return true;
            }
            case Variant.Type.PackedInt32Array:
            {
                var items = value.AsInt32Array();
                var flat = new double[items.Length];
                for (var i = 0; i < items.Length; i++)
                {
                    flat[i] = items[i];
                }

                parameter = new(name, "intArray", NumberArrayValue: flat);
                return true;
            }
            case Variant.Type.PackedInt64Array:
            {
                var items = value.AsInt64Array();
                var flat = new double[items.Length];
                for (var i = 0; i < items.Length; i++)
                {
                    flat[i] = items[i];
                }

                parameter = new(name, "intArray", NumberArrayValue: flat);
                return true;
            }
            default:
                return false;
        }
    }

    private static string? VariantString(Variant value)
        => value.VariantType switch
        {
            Variant.Type.String => value.AsString(),
            Variant.Type.StringName => value.AsStringName().ToString(),
            _ => value.ToString(),
        };

    private static object? ReadProperty(object source, string name)
    {
        try
        {
            return source.GetType().GetProperty(name)?.GetValue(source);
        }
        catch
        {
            return null;
        }
    }

    private sealed record ShaderUniformInfo(string Name, Variant? DefaultValue);
}
