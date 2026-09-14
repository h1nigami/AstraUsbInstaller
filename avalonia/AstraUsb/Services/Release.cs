using System.Text.Json;
using System.Text.RegularExpressions;

namespace AstraUsb.Services;

/// <summary>Архив под одну платформу и его контрольная сумма.</summary>
public sealed record ReleaseAsset(string Archive, string Checksum);

/// <summary>
/// Релиз на GitHub, каким его видит станция.
///
/// Репозиторий открытый, поэтому ответ приходит без ключей и на станции не
/// хранится никаких секретов.
/// </summary>
public sealed record Release(string Tag, DateTime Published,
    IReadOnlyDictionary<string, string> Assets)
{
    public static bool IsStationTag(string tag) =>
        Regex.IsMatch(tag, @"\Av2\.[0-9]+(?:\.[0-9]+)*(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?\z");

    public static Release? Select(string json, string platform)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var candidates = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray().Select(item => Parse(item.GetRawText()))
                : new[] { Parse(json) };
            return candidates.Where(release => release?.Pick(platform) is not null)
                .OrderByDescending(release => release!.Published).FirstOrDefault();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Разбирает ответ GitHub. Возвращает null, если это не он.</summary>
    public static Release? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("tag_name", out var tag))
                return null;

            var name = tag.GetString() ?? "";
            if (!IsStationTag(name)
                || root.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                return null;

            var published = root.TryGetProperty("published_at", out var stamp)
                            && DateTime.TryParse(stamp.GetString(), out var parsed)
                ? parsed
                : DateTime.MinValue;

            var assets = new Dictionary<string, string>(StringComparer.Ordinal);

            if (root.TryGetProperty("assets", out var list)
                && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in list.EnumerateArray())
                {
                    var file = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = asset.TryGetProperty("browser_download_url", out var u)
                        ? u.GetString()
                        : null;

                    if (file is { Length: > 0 } && url is { Length: > 0 })
                        assets[file] = url;
                }
            }

            return new Release(name, published, assets);
        }
        catch (Exception)
        {
            // Ответ не разобрался: станция остаётся на своей версии.
            return null;
        }
    }

    /// <summary>
    /// Архив для указанной платформы вместе с суммой. Архив без суммы не
    /// берётся: сумма это единственная защита от битой закачки, а половина
    /// архива хуже старой версии.
    /// </summary>
    public ReleaseAsset? Pick(string platform)
    {
        if (!IsStationTag(Tag) || platform is not ("linux-x64" or "linux-arm64"))
            return null;

        var archive = $"bestcam-station-{Tag}-{platform}.tar.gz";
        return Assets.TryGetValue(archive, out var url)
               && Assets.TryGetValue(archive + ".sha256", out var checksum)
            ? new ReleaseAsset(url, checksum)
            : null;
    }
}
