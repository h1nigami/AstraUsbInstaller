using System.Text.Json;

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
            if (name.Length == 0)
                return null;

            var published = root.TryGetProperty("published_at", out var stamp)
                            && DateTime.TryParse(stamp.GetString(), out var parsed)
                ? parsed
                : DateTime.Now;

            var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
        var archive = Assets.Keys.FirstOrDefault(
            name => name.EndsWith($"-{platform}.tar.gz", StringComparison.OrdinalIgnoreCase));

        if (archive is null)
            return null;

        return Assets.TryGetValue(archive + ".sha256", out var checksum)
            ? new ReleaseAsset(Assets[archive], checksum)
            : null;
    }
}
