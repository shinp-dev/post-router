using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PostRouter.Domain;

public static class CanonicalIntent
{
    public const string Schema = "post-intent/v1";

    public static byte[] Serialize(CanonicalPostIntent intent)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", Schema);
            writer.WriteString("clientRequestId", intent.ClientRequestId);
            writer.WritePropertyName("content");
            writer.WriteStartObject();
            writer.WriteString("kind", intent.Content.Kind.ToString());
            WriteNullable(writer, "text", intent.Content.Text);
            WriteNullable(writer, "title", intent.Content.Title);
            writer.WritePropertyName("media");
            writer.WriteStartArray();
            foreach (var media in intent.Content.MediaAssets)
            {
                writer.WriteStartObject();
                writer.WriteString("sha256", media.Sha256);
                writer.WriteNumber("sizeBytes", media.SizeBytes);
                writer.WriteString("mime", media.DetectedMime);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WritePropertyName("targets");
            writer.WriteStartArray();
            foreach (var target in intent.Targets.OrderBy(x => x.AccountId))
            {
                writer.WriteStartObject();
                writer.WriteString("accountId", target.AccountId);
                writer.WriteString("visibility", target.Visibility);
                writer.WriteString("optionsSchema", target.OptionsSchema);
                writer.WriteNumber("optionsVersion", target.OptionsVersion);
                writer.WritePropertyName("options");
                using var options = JsonDocument.Parse(target.CanonicalOptionsJson);
                options.RootElement.WriteTo(writer);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WritePropertyName("schedule");
            writer.WriteStartObject();
            writer.WriteString("mode", intent.Schedule.Mode.ToString());
            if (intent.Schedule.Mode == ScheduleMode.AtTime)
                writer.WriteString("dueAtUtc", FormatUtc(intent.Schedule.DueAtUtc));
            writer.WriteNumber("maxLatenessSeconds", checked((long)intent.Schedule.MaxLateness.TotalSeconds));
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public static string Hash(ReadOnlySpan<byte> canonicalBytes) =>
        Convert.ToHexStringLower(SHA256.HashData(canonicalBytes));

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static void WriteNullable(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name); else writer.WriteString(name, value);
    }
}
