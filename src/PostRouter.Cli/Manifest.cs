using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PostRouter.Domain;
using PostRouter.Infrastructure;

namespace PostRouter.Cli;

internal sealed record ManifestDto(int SchemaVersion, string? ClientRequestId, ContentDto? Content, ScheduleDto? Schedule, IReadOnlyList<TargetDto>? Targets);
internal sealed record ContentDto(string? Text, string? Title, string? Video, IReadOnlyList<string>? Images);
internal sealed record ScheduleDto(string At, string? TimeZone, int MaxLatenessSeconds = 900);
internal sealed record TargetDto(
    string Account,
    string Provider = "fake",
    string Visibility = "public",
    int OptionsVersion = 1,
    JsonElement? Options = null,
    string? ApprovalPolicy = null);

internal static class ManifestReader
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static async Task<CanonicalPostIntent> ReadAsync(string path, SpoolStore spool, IReadOnlyList<Account> accounts, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var fakeEnabled = string.Equals(Environment.GetEnvironmentVariable("POST_ROUTER_PROFILE"), "test", StringComparison.Ordinal);
        var fullPath = Path.GetFullPath(path);
        if (new FileInfo(fullPath).Length > 1024 * 1024) throw new InvalidDataException("Manifest exceeds the 1 MiB limit.");
        await using var stream = File.OpenRead(fullPath);
        var manifest = await JsonSerializer.DeserializeAsync<ManifestDto>(stream, Options, cancellationToken) ?? throw new InvalidDataException("Manifest is empty.");
        if (manifest.SchemaVersion != 1) throw new InvalidDataException("Unsupported manifest schemaVersion.");
        if (string.IsNullOrWhiteSpace(manifest.ClientRequestId)) throw new InvalidDataException("Manifest requires clientRequestId.");
        if (manifest.Content is null) throw new InvalidDataException("Manifest requires content.");
        if (manifest.Targets is not { Count: > 0 }) throw new InvalidDataException("Manifest requires targets.");
        foreach (var target in manifest.Targets)
        {
            if (string.IsNullOrWhiteSpace(target.Account)) throw new InvalidDataException("Each target requires an account alias.");
            var provider = target.Provider.ToLowerInvariant();
            if (provider is not "fake" and not "x" and not "youtube")
                throw new InvalidDataException("Supported manifest providers are x, youtube, and the test-only fake provider.");
            if (provider == "fake" && !fakeEnabled) throw new InvalidDataException("The fake provider requires POST_ROUTER_PROFILE=test.");
            if (target.Options is { ValueKind: not JsonValueKind.Object }) throw new InvalidDataException("Target options must be a JSON object.");
            if (provider == "youtube") ValidateYouTubeOptions(target.Options);
            _ = ParseApprovalPolicy(target.ApprovalPolicy);
        }
        if (manifest.Targets.Any(target => string.Equals(target.Provider, "youtube", StringComparison.OrdinalIgnoreCase)))
            WriteYouTubeUploadNotice();
        var requestedSchedule = manifest.Schedule is null ? null : ParseSchedule(manifest.Schedule);

        var media = new List<MediaAsset>();
        var baseDirectory = Path.GetDirectoryName(fullPath)!;
        ContentKind kind;
        if (manifest.Content.Video is not null)
        {
            if (manifest.Content.Images is { Count: > 0 }) throw new InvalidDataException("Video and images cannot be mixed in one post.");
            var asset = await spool.ImportAsync(Resolve(baseDirectory, manifest.Content.Video), cancellationToken);
            if (asset.DetectedMime != "video/mp4") throw new InvalidDataException("content.video must be an MP4 file.");
            media.Add(asset);
            kind = ContentKind.Video;
        }
        else if (manifest.Content.Images is { Count: > 0 })
        {
            foreach (var image in manifest.Content.Images)
            {
                var asset = await spool.ImportAsync(Resolve(baseDirectory, image), cancellationToken);
                if (asset.DetectedMime != "image/jpeg") throw new InvalidDataException("content.images must contain JPEG files.");
                media.Add(asset);
            }
            kind = ContentKind.ImageSet;
        }
        else kind = ContentKind.TextOnly;

        var schedule = requestedSchedule ?? new ScheduleIntent(ScheduleMode.Immediate, timeProvider.GetUtcNow(), TimeSpan.FromMinutes(15));
        var targets = manifest.Targets.Select(target =>
        {
            var provider = target.Provider.ToLowerInvariant();
            var registered = accounts.SingleOrDefault(account => string.Equals(account.ProviderKey, provider, StringComparison.OrdinalIgnoreCase) && string.Equals(account.Alias, target.Account, StringComparison.OrdinalIgnoreCase));
            if (registered is null && provider != "fake") throw new InvalidDataException($"Account alias '{target.Account}' is not registered for provider '{provider}'.");
            var accountId = registered?.Id ?? StableAccountId(provider, target.Account);
            return new TargetIntent(accountId, provider, target.Visibility, $"{provider}-options/v1", target.OptionsVersion,
                Canonicalize(target.Options), target.Account, ParseApprovalPolicy(target.ApprovalPolicy));
        }).ToArray();
        return new(manifest.ClientRequestId, new Content(Guid.NewGuid(), kind, manifest.Content.Text, manifest.Content.Title, media), targets, schedule);
    }

    private static void ValidateYouTubeOptions(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("YouTube targets require an options object.");
        bool? madeForKids = null;
        bool? uploadNoticeAcknowledged = null;
        foreach (var property in value.Value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "madeForKids":
                    if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new InvalidDataException("YouTube target.options.madeForKids must be boolean.");
                    madeForKids = property.Value.GetBoolean();
                    break;
                case "containsSyntheticMedia":
                    if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new InvalidDataException("YouTube target.options.containsSyntheticMedia must be boolean.");
                    break;
                case "uploadNoticeAcknowledged":
                    if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new InvalidDataException("YouTube target.options.uploadNoticeAcknowledged must be boolean.");
                    uploadNoticeAcknowledged = property.Value.GetBoolean();
                    break;
                default:
                    throw new InvalidDataException($"Unknown YouTube target option '{property.Name}'.");
            }
        }
        if (madeForKids is null)
            throw new InvalidDataException("YouTube target.options.madeForKids must be explicitly true or false.");
        if (uploadNoticeAcknowledged != true)
            throw new InvalidDataException("YouTube target.options.uploadNoticeAcknowledged must be true after reviewing the YouTube upload notice and Terms of Service.");
    }

    private static void WriteYouTubeUploadNotice()
    {
        Console.Error.WriteLine("YouTube upload notice: only upload content that complies with YouTube Terms and Community Guidelines and respects copyright and privacy rights.");
        Console.Error.WriteLine("Terms: https://www.youtube.com/t/terms");
        Console.Error.WriteLine("Made-for-kids content must set target.options.madeForKids=true before upload.");
    }

    private static ApprovalPolicy ParseApprovalPolicy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return ApprovalPolicy.Automatic;
        if (!Enum.TryParse<ApprovalPolicy>(value, ignoreCase: true, out var policy) || !Enum.IsDefined(policy))
            throw new InvalidDataException("target.approvalPolicy must be Automatic or RequireApproval.");
        return policy;
    }

    private static ScheduleIntent ParseSchedule(ScheduleDto schedule)
    {
        if (!DateTimeOffset.TryParse(schedule.At, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var due))
            throw new InvalidDataException("schedule.at must be an offset-bearing ISO 8601 value.");
        if (!schedule.At.EndsWith('Z') && schedule.At.LastIndexOf('+') < 10 && schedule.At.LastIndexOf('-') < 10)
            throw new InvalidDataException("schedule.at must include an offset.");
        if (schedule.MaxLatenessSeconds < 0) throw new InvalidDataException("maxLatenessSeconds must not be negative.");
        if (!string.IsNullOrWhiteSpace(schedule.TimeZone))
        {
            TimeZoneInfo zone;
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZone); }
            catch (TimeZoneNotFoundException) { throw new InvalidDataException("schedule.timeZone is unknown on this system."); }
            catch (InvalidTimeZoneException) { throw new InvalidDataException("schedule.timeZone is invalid on this system."); }
            if (zone.GetUtcOffset(due.UtcDateTime) != due.Offset) throw new InvalidDataException("schedule.at offset does not match schedule.timeZone at that instant.");
        }
        return new(ScheduleMode.AtTime, due.ToUniversalTime(), TimeSpan.FromSeconds(schedule.MaxLatenessSeconds), schedule.At, schedule.TimeZone, due.Offset);
    }

    private static string Resolve(string root, string path) => Path.IsPathRooted(path) ? path : Path.Combine(root, path);
    private static Guid StableAccountId(string provider, string alias)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"post-router/account/{provider.ToLowerInvariant()}/{alias}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string Canonicalize(JsonElement? value)
    {
        using var output = new MemoryStream();
        using var empty = value is null ? JsonDocument.Parse("{}") : null;
        using (var writer = new Utf8JsonWriter(output)) WriteElement(writer, value ?? empty!.RootElement);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal)) { writer.WritePropertyName(property.Name); WriteElement(writer, property.Value); }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteElement(writer, item); writer.WriteEndArray();
                break;
            default: value.WriteTo(writer); break;
        }
    }
}
