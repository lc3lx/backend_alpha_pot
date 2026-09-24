using System.Text.Json;
using ScarAlpha.Binolla.Models;

namespace ScarAlpha.Infrastructure.BrokerGateway;

/// <summary>
/// Gateway <c>/market/quotes</c> payload. Parsed by hand so a naming-policy mismatch on
/// the DTO cannot swallow the whole tick dump and freeze the live board.
/// </summary>
public static class BrokerQuoteJson
{
    public static List<QuoteData> Parse(string json, DateTimeOffset? receivedAt = null)
    {
        var list = new List<QuoteData>();
        if (string.IsNullOrWhiteSpace(json))
            return list;

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return list;

        var now = receivedAt ?? DateTimeOffset.UtcNow;
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
                continue;
            if (!TryString(row, "asset", out var asset) || string.IsNullOrWhiteSpace(asset))
                continue;
            if (!TryDecimal(row, "price", out var price) || price <= 0m)
                continue;

            TryDouble(row, "timestamp", out var timestamp);
            list.Add(new QuoteData
            {
                Pair = asset,
                Timestamp = timestamp,
                Price = (double)price,
                ReceivedAt = now
            });
        }

        return list;
    }

    private static bool TryString(JsonElement row, string name, out string value)
    {
        value = "";
        if (!row.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            return false;
        value = el.GetString() ?? "";
        return true;
    }

    private static bool TryDecimal(JsonElement row, string name, out decimal value)
    {
        value = 0m;
        if (!row.TryGetProperty(name, out var el))
            return false;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out value))
            return true;
        return el.ValueKind == JsonValueKind.String
            && decimal.TryParse(el.GetString(), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static bool TryDouble(JsonElement row, string name, out double value)
    {
        value = 0;
        if (!row.TryGetProperty(name, out var el))
            return false;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out value))
            return true;
        return el.ValueKind == JsonValueKind.String
            && double.TryParse(el.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}
