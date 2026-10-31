using System.Globalization;
using System.Text.Json;

namespace Recommendarr.Infrastructure.Http;

/// <summary>
/// Lenient readers for loosely typed APIs (Tautulli, Plex) that send the same field as a
/// number in one response and a string in the next.
/// </summary>
internal static class JsonElementExtensions
{
    public static JsonElement? Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    public static string? GetStringOrNull(this JsonElement e, string name) => e.Prop(name) switch
    {
        { ValueKind: JsonValueKind.String } v => v.GetString(),
        { ValueKind: JsonValueKind.Number } v => v.GetRawText(),
        _ => null,
    };

    public static long? GetInt64OrNull(this JsonElement e, string name) => e.Prop(name) switch
    {
        { ValueKind: JsonValueKind.Number } v when v.TryGetInt64(out var n) => n,
        { ValueKind: JsonValueKind.Number } v => (long)v.GetDouble(),
        { ValueKind: JsonValueKind.String } v when long.TryParse(v.GetString(), CultureInfo.InvariantCulture, out var n) => n,
        _ => null,
    };

    public static double? GetDoubleOrNull(this JsonElement e, string name) => e.Prop(name) switch
    {
        { ValueKind: JsonValueKind.Number } v => v.GetDouble(),
        { ValueKind: JsonValueKind.String } v when double.TryParse(v.GetString(), CultureInfo.InvariantCulture, out var n) => n,
        _ => null,
    };

    public static IEnumerable<JsonElement> GetArrayOrEmpty(this JsonElement e, string name) =>
        e.Prop(name) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : [];
}
