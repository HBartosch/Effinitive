using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EffinitiveFramework.Core.OpenApi;

/// <summary>
/// Turns CLR types into JSON Schema nodes, collecting complex types into a shared
/// <c>components/schemas</c> map and referencing them by <c>$ref</c>.
/// <para>
/// Schemas are derived through the same <see cref="JsonSerializerOptions"/> the server serializes
/// responses with, so the documented property names match the wire format — the framework camelCases by
/// default, and a schema built from raw CLR names would describe an API that doesn't exist.
/// </para>
/// <para>
/// Runs once at startup, so it favours clarity over speed.
/// </para>
/// </summary>
internal sealed class JsonSchemaGenerator
{
    private readonly JsonNamingPolicy? _namingPolicy;
    private readonly bool _stringEnums;

    private readonly Dictionary<string, OpenApiSchema> _schemas = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, string> _schemaNames = new();

    public JsonSchemaGenerator(JsonSerializerOptions? jsonOptions)
    {
        _namingPolicy = jsonOptions?.PropertyNamingPolicy;
        _stringEnums = jsonOptions?.Converters.Any(c => c is JsonStringEnumConverter) ?? false;
    }

    /// <summary>Complex types encountered so far, keyed by schema name.</summary>
    public IReadOnlyDictionary<string, OpenApiSchema> Schemas => _schemas;

    /// <summary>
    /// Produces a schema for <paramref name="type"/>. Complex types are registered in
    /// <see cref="Schemas"/> and returned as a <c>$ref</c>.
    /// </summary>
    public OpenApiSchema Generate(Type type)
    {
        // Unwrap awaitables defensively — response types arrive already unwrapped, but a caller
        // reflecting over a handler signature might not have done so.
        type = UnwrapTask(type);

        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null)
        {
            var inner = Generate(underlying);
            // A $ref cannot carry sibling keywords in 3.0, so wrap rather than mutate.
            if (inner.Ref != null)
                return inner;
            inner.Nullable = true;
            return inner;
        }

        if (type == typeof(object))
            return new OpenApiSchema();   // free-form

        if (TryGetPrimitiveSchema(type, out var primitive))
            return primitive;


        if (type.IsEnum)
            return GenerateEnum(type);

        if (TryGetDictionaryValueType(type, out var valueType))
        {
            return new OpenApiSchema
            {
                Type = "object",
                AdditionalProperties = Generate(valueType)
            };
        }

        if (TryGetElementType(type, out var elementType))
        {
            return new OpenApiSchema
            {
                Type = "array",
                Items = Generate(elementType)
            };
        }

        return GenerateObjectRef(type);
    }

    // ── Primitives ──────────────────────────────────────────────────────────────────────────────

    private static bool TryGetPrimitiveSchema(Type type, [NotNullWhen(true)] out OpenApiSchema? schema)
    {
        schema = type switch
        {
            _ when type == typeof(string) || type == typeof(char) => new OpenApiSchema { Type = "string" },
            _ when type == typeof(bool) => new OpenApiSchema { Type = "boolean" },
            _ when type == typeof(byte) || type == typeof(sbyte)
                || type == typeof(short) || type == typeof(ushort)
                || type == typeof(int) || type == typeof(uint) => new OpenApiSchema { Type = "integer", Format = "int32" },
            _ when type == typeof(long) || type == typeof(ulong) => new OpenApiSchema { Type = "integer", Format = "int64" },
            _ when type == typeof(float) => new OpenApiSchema { Type = "number", Format = "float" },
            _ when type == typeof(double) || type == typeof(decimal) => new OpenApiSchema { Type = "number", Format = "double" },
            _ when type == typeof(DateTime) || type == typeof(DateTimeOffset) => new OpenApiSchema { Type = "string", Format = "date-time" },
            _ when type == typeof(DateOnly) => new OpenApiSchema { Type = "string", Format = "date" },
            _ when type == typeof(TimeOnly) => new OpenApiSchema { Type = "string", Format = "time" },
            _ when type == typeof(TimeSpan) => new OpenApiSchema { Type = "string" },
            _ when type == typeof(Guid) => new OpenApiSchema { Type = "string", Format = "uuid" },
            _ when type == typeof(Uri) => new OpenApiSchema { Type = "string", Format = "uri" },
            // Byte arrays are base64 on the wire, not an array of integers.
            _ when type == typeof(byte[]) => new OpenApiSchema { Type = "string", Format = "byte" },
            _ => null
        };

        return schema != null;
    }

    private OpenApiSchema GenerateEnum(Type type)
    {
        var schema = new OpenApiSchema { Enum = new List<object>() };

        // Honor a globally registered JsonStringEnumConverter, or one applied to the enum itself —
        // otherwise System.Text.Json writes the numeric value.
        var stringEnum = _stringEnums ||
                         type.GetCustomAttribute<JsonConverterAttribute>()?.ConverterType == typeof(JsonStringEnumConverter);

        if (stringEnum)
        {
            schema.Type = "string";
            foreach (var name in System.Enum.GetNames(type))
                schema.Enum.Add(name);
        }
        else
        {
            schema.Type = "integer";
            schema.Format = "int32";
            foreach (var value in System.Enum.GetValues(type))
                schema.Enum.Add(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
        }

        return schema;
    }

    // ── Objects ─────────────────────────────────────────────────────────────────────────────────

    private OpenApiSchema GenerateObjectRef(Type type)
    {
        if (_schemaNames.TryGetValue(type, out var existing))
            return new OpenApiSchema { Ref = "#/components/schemas/" + existing };

        var name = MakeUniqueSchemaName(type);

        // Register the name *before* walking properties so a type that references itself resolves to
        // the $ref already in flight instead of recursing forever.
        _schemaNames[type] = name;
        _schemas[name] = new OpenApiSchema { Type = "object" };

        var schema = BuildObjectSchema(type);
        _schemas[name] = schema;

        return new OpenApiSchema { Ref = "#/components/schemas/" + name };
    }

    private OpenApiSchema BuildObjectSchema(Type type)
    {
        var schema = new OpenApiSchema
        {
            Type = "object",
            Properties = new Dictionary<string, OpenApiSchema>(StringComparer.Ordinal)
        };

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead)
                continue;
            if (property.GetCustomAttribute<JsonIgnoreAttribute>() != null)
                continue;
            if (property.GetIndexParameters().Length > 0)
                continue;   // indexers have no wire representation

            var wireName = GetWireName(property);
            var propertySchema = Generate(property.PropertyType);

            ApplyValidationAttributes(property, propertySchema);

            schema.Properties[wireName] = propertySchema;

            if (property.GetCustomAttribute<RequiredAttribute>() != null)
            {
                schema.Required ??= new List<string>();
                schema.Required.Add(wireName);
            }
        }

        if (schema.Properties.Count == 0)
            schema.Properties = null;

        return schema;
    }

    /// <summary>
    /// The name the property serializes under: an explicit <c>[JsonPropertyName]</c> if present,
    /// otherwise the configured naming policy applied to the CLR name.
    /// </summary>
    private string GetWireName(PropertyInfo property)
    {
        var explicitName = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
        if (!string.IsNullOrEmpty(explicitName))
            return explicitName;

        return _namingPolicy?.ConvertName(property.Name) ?? property.Name;
    }

    /// <summary>
    /// Mirrors DataAnnotations constraints into the schema. These are the same attributes the
    /// validation middleware enforces, so the document describes the rules the server actually applies.
    /// </summary>
    private static void ApplyValidationAttributes(PropertyInfo property, OpenApiSchema schema)
    {
        // A $ref node cannot carry sibling keywords in OpenAPI 3.0 — constraints would be ignored, or
        // rejected by strict validators.
        if (schema.Ref != null)
            return;

        var isArray = schema.Type == "array";

        if (property.GetCustomAttribute<RangeAttribute>() is { } range)
        {
            if (TryToDouble(range.Minimum, out var min)) schema.Minimum = min;
            if (TryToDouble(range.Maximum, out var max)) schema.Maximum = max;
        }

        if (property.GetCustomAttribute<StringLengthAttribute>() is { } stringLength)
        {
            schema.MaxLength = stringLength.MaximumLength;
            if (stringLength.MinimumLength > 0)
                schema.MinLength = stringLength.MinimumLength;
        }

        if (property.GetCustomAttribute<MinLengthAttribute>() is { } minLength)
        {
            if (isArray) schema.MinItems = minLength.Length;
            else schema.MinLength = minLength.Length;
        }

        if (property.GetCustomAttribute<MaxLengthAttribute>() is { } maxLength)
        {
            if (isArray) schema.MaxItems = maxLength.Length;
            else schema.MaxLength = maxLength.Length;
        }

        if (property.GetCustomAttribute<RegularExpressionAttribute>() is { } regex)
            schema.Pattern = regex.Pattern;

        if (property.GetCustomAttribute<EmailAddressAttribute>() != null)
            schema.Format = "email";
    }

    private static bool TryToDouble(object? value, out double result)
    {
        if (value is IConvertible convertible)
        {
            try
            {
                result = convertible.ToDouble(System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                // Range can be constructed with a custom type whose bounds aren't numeric.
            }
        }

        result = 0;
        return false;
    }

    // ── Type inspection ─────────────────────────────────────────────────────────────────────────

    private static Type UnwrapTask(Type type)
    {
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(Task<>) || definition == typeof(ValueTask<>))
                return type.GetGenericArguments()[0];
        }
        return type;
    }

    private static bool TryGetDictionaryValueType(Type type, out Type valueType)
    {
        foreach (var candidate in Interfaces(type))
        {
            if (candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>))
            {
                // Only string-keyed dictionaries map onto a JSON object.
                var args = candidate.GetGenericArguments();
                if (args[0] == typeof(string))
                {
                    valueType = args[1];
                    return true;
                }
            }
        }

        valueType = null!;
        return false;
    }

    private static bool TryGetElementType(Type type, out Type elementType)
    {
        // string is IEnumerable<char> and byte[] is handled as base64 — both are resolved before here.
        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
            return true;
        }

        foreach (var candidate in Interfaces(type))
        {
            if (candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                elementType = candidate.GetGenericArguments()[0];
                return true;
            }
        }

        // Non-generic IEnumerable: describe as an array of anything rather than as an object.
        if (typeof(IEnumerable).IsAssignableFrom(type))
        {
            elementType = typeof(object);
            return true;
        }

        elementType = null!;
        return false;
    }

    private static IEnumerable<Type> Interfaces(Type type)
    {
        if (type.IsInterface)
            yield return type;
        foreach (var i in type.GetInterfaces())
            yield return i;
    }

    /// <summary>
    /// Readable schema name: <c>Page&lt;Product&gt;</c> becomes <c>PageOfProduct</c>. Distinct types
    /// sharing a simple name (same class name in different namespaces) get a numeric suffix so one
    /// never silently overwrites the other.
    /// </summary>
    private string MakeUniqueSchemaName(Type type)
    {
        var baseName = BuildTypeName(type);

        if (!_schemas.ContainsKey(baseName))
            return baseName;

        for (int i = 2; ; i++)
        {
            var candidate = baseName + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!_schemas.ContainsKey(candidate))
                return candidate;
        }
    }

    private static string BuildTypeName(Type type)
    {
        if (!type.IsGenericType)
            return type.Name;

        var name = type.Name;
        var tick = name.IndexOf('`');
        if (tick >= 0)
            name = name[..tick];

        var args = type.GetGenericArguments();
        var parts = new string[args.Length];
        for (int i = 0; i < args.Length; i++)
            parts[i] = BuildTypeName(args[i]);

        return name + "Of" + string.Join("And", parts);
    }
}
