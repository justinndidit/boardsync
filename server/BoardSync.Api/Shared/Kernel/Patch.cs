using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoardSync.Api.Shared.Kernel;

/// <summary>
/// A field in a partial update: either supplied — possibly as null — or not mentioned at all.
/// </summary>
/// <remarks>
/// <para>
/// The distinction a plain nullable property cannot make. <c>{"assigneeId": null}</c> means
/// "unassign this", and omitting <c>assigneeId</c> means "leave it alone", but both arrive as
/// <c>null</c> on a <c>Guid?</c>. Without something like this a PATCH cannot express unassignment,
/// or it silently clears every field the caller did not mention — which is a full replace wearing a
/// PATCH's name.
/// </para>
/// <para>
/// <see cref="IsSet"/> is false by default and only a deserialized value makes it true, because
/// System.Text.Json invokes a converter's <c>Read</c> only for properties actually present in the
/// payload. Absence therefore needs no sentinel and cannot be forged by sending a particular value.
/// </para>
/// <para>
/// <b>Cost:</b> validation attributes do not see through this — <c>[MaxLength]</c> on a
/// <c>Patch&lt;string&gt;</c> inspects the struct, not the string. Fields carried this way are
/// validated explicitly by the service that applies them.
/// </para>
/// </remarks>
/// <typeparam name="T">The field's type. Use the nullable form when null is a meaningful value.</typeparam>
[JsonConverter(typeof(PatchConverterFactory))]
public readonly struct Patch<T>
{
    private Patch(T? value)
    {
        IsSet = true;
        Value = value;
    }

    /// <summary>Whether the caller mentioned this field at all.</summary>
    public bool IsSet { get; }

    /// <summary>What they set it to. Meaningless unless <see cref="IsSet"/>.</summary>
    public T? Value { get; }

    /// <summary>A field the caller supplied.</summary>
    public static Patch<T> Set(T? value) => new(value);

    /// <summary>A field the caller left alone. The default, stated for tests that build requests.</summary>
    public static Patch<T> Unset => default;

    /// <summary>
    /// The new value, or <paramref name="current"/> when the field was not mentioned.
    /// </summary>
    /// <remarks>
    /// The intended way to apply one: <c>item.Title = request.Title.Or(item.Title);</c> reads as
    /// what it does and cannot accidentally drop a value by forgetting to check
    /// <see cref="IsSet"/>.
    /// </remarks>
    public T? Or(T? current) => IsSet ? Value : current;
}

/// <summary>
/// Teaches System.Text.Json to read and write <see cref="Patch{T}"/> for any <c>T</c>.
/// </summary>
public sealed class PatchConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Patch<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(PatchConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
}

/// <inheritdoc cref="PatchConverterFactory"/>
internal sealed class PatchConverter<T> : JsonConverter<Patch<T>>
{
    /// <remarks>
    /// Reached only when the property is present, which is the whole mechanism: a missing property
    /// leaves the struct at its default and <see cref="Patch{T}.IsSet"/> false.
    /// </remarks>
    public override Patch<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Patch<T>.Set(JsonSerializer.Deserialize<T>(ref reader, options));

    public override void Write(Utf8JsonWriter writer, Patch<T> value, JsonSerializerOptions options)
    {
        // Only here so a request type round-trips in tests and in Swagger examples; nothing in the
        // API returns one.
        if (!value.IsSet)
        {
            writer.WriteNullValue();
            return;
        }

        JsonSerializer.Serialize(writer, value.Value, options);
    }
}
