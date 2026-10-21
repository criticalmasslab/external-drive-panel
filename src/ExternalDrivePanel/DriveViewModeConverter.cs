using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExternalDrivePanel;

// GlobalView keeps its existing "icon" spelling; the new drive override uses "icons".
public sealed class DriveViewModeConverter : JsonConverter<GlobalViewMode?>
{
    public override GlobalViewMode? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("드라이브 viewMode는 문자열이어야 합니다.");
        return reader.GetString() switch
        {
            "icons" => GlobalViewMode.Icon,
            "details" => GlobalViewMode.Details,
            _ => throw new JsonException("드라이브 viewMode는 icons 또는 details여야 합니다.")
        };
    }

    public override void Write(Utf8JsonWriter writer, GlobalViewMode? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value == GlobalViewMode.Icon ? "icons" : "details");
    }
}
