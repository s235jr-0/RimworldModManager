using System.Text.Json;

namespace RimModManager.Core;

// Steam's public Workshop web API (no login needed).
public static class SteamApi
{
    public const string DetailsUrl =
        "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";
    public const string CollectionUrl =
        "https://api.steampowered.com/ISteamRemoteStorage/GetCollectionDetails/v1/";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static JsonElement Post(string url, IEnumerable<KeyValuePair<string, string>> form)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form)
        };

        using HttpResponseMessage response = Http.Send(request);
        response.EnsureSuccessStatusCode();

        using Stream stream = response.Content.ReadAsStream();
        using JsonDocument doc = JsonDocument.Parse(stream);
        return doc.RootElement.Clone();
    }

    // Steam sends some numbers as strings and some as numbers.
    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString())
            : "";

    private static long Long(JsonElement e, string name) =>
        long.TryParse(Str(e, name), out long result) ? result : 0;

    private static WorkshopDetails ParseDetails(JsonElement d)
    {
        WorkshopDetails x = new()
        {
            Id = Str(d, "publishedfileid"),
            AppId = Str(d, "consumer_app_id"),
            Title = Str(d, "title"),
            TimeUpdated = Long(d, "time_updated"),
            FileType = d.TryGetProperty("file_type", out _) ? (int)Long(d, "file_type") : -1,
            Result = (int)Long(d, "result"),
        };

        if (String.IsNullOrWhiteSpace(x.AppId) || x.AppId == "0")
            x.AppId = Str(d, "creator_app_id");

        return x;
    }

    public static Dictionary<string, WorkshopDetails> GetDetails(IEnumerable<string> ids)
    {
        List<string> source = ids.Where(s => !String.IsNullOrWhiteSpace(s)).Distinct().ToList();
        Dictionary<string, WorkshopDetails> result = new();
        const int batchSize = 100;

        for (int start = 0; start < source.Count; start += batchSize)
        {
            List<string> batch = source.Skip(start).Take(batchSize).ToList();
            List<KeyValuePair<string, string>> form = new() { new("itemcount", batch.Count.ToString()) };
            for (int i = 0; i < batch.Count; i++)
                form.Add(new("publishedfileids[" + i + "]", batch[i]));

            JsonElement root = Post(DetailsUrl, form);
            if (!root.TryGetProperty("response", out JsonElement response) ||
                !response.TryGetProperty("publishedfiledetails", out JsonElement list) ||
                list.ValueKind != JsonValueKind.Array)
                continue;

            foreach (JsonElement d in list.EnumerateArray())
            {
                WorkshopDetails details = ParseDetails(d);
                if (!String.IsNullOrWhiteSpace(details.Id))
                    result[details.Id] = details;
            }
        }

        return result;
    }

    public static Dictionary<string, WorkshopDetails> GetDetailsWithRetry(
        IEnumerable<string> ids, string operationLabel, int maxAttempts, Action<string>? status)
    {
        Exception? last = null;
        List<string> idList = ids.ToList();

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                status?.Invoke(operationLabel + " - attempt " + attempt + "/" + maxAttempts);
                return GetDetails(idList);
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt < maxAttempts)
                {
                    status?.Invoke(operationLabel + " - attempt " + attempt + "/" + maxAttempts + " failed; retrying...");
                    Thread.Sleep(1200);
                }
            }
        }

        throw new Exception(
            operationLabel + " failed after " + maxAttempts + " attempts." +
            Environment.NewLine + Environment.NewLine +
            (last == null ? "Unknown network error." : last.Message),
            last);
    }

    public static WorkshopDetails GetDetail(string id)
    {
        Dictionary<string, WorkshopDetails> d = GetDetails(new[] { id });
        if (!d.TryGetValue(id, out WorkshopDetails? details))
            throw new Exception("Steam returned no details for Workshop item " + id + ".");
        return details;
    }

    public static List<string> GetCollectionChildren(string id)
    {
        List<string> children = new();
        JsonElement root = Post(CollectionUrl, new KeyValuePair<string, string>[]
        {
            new("collectioncount", "1"),
            new("publishedfileids[0]", id),
        });

        if (!root.TryGetProperty("response", out JsonElement response) ||
            !response.TryGetProperty("collectiondetails", out JsonElement collections) ||
            collections.ValueKind != JsonValueKind.Array)
            return children;

        foreach (JsonElement collection in collections.EnumerateArray())
        {
            if (!collection.TryGetProperty("children", out JsonElement kids) ||
                kids.ValueKind != JsonValueKind.Array)
                continue;

            foreach (JsonElement child in kids.EnumerateArray())
            {
                string childId = Str(child, "publishedfileid");
                if (!String.IsNullOrWhiteSpace(childId))
                    children.Add(childId);
            }
        }

        return children;
    }

    // Expands collections recursively into their items.
    public static List<WorkshopDetails> ResolveItems(IEnumerable<string> rootIds, Action<string>? status)
    {
        HashSet<string> seen = new();
        List<WorkshopDetails> output = new();

        foreach (string id in rootIds)
            ResolveOne(id, seen, output, status);

        return output;
    }

    private static void ResolveOne(string id, HashSet<string> seen, List<WorkshopDetails> output, Action<string>? status)
    {
        if (!seen.Add(id)) return;

        status?.Invoke("Reading Workshop item " + id + "...");
        WorkshopDetails d = GetDetail(id);

        if (d.FileType == 2)
        {
            foreach (string child in GetCollectionChildren(id))
                ResolveOne(child, seen, output, status);
            return;
        }

        if (d.Result != 1)
            throw new Exception("Steam could not resolve Workshop item " + id + " (result " + d.Result + ").");

        if (String.IsNullOrWhiteSpace(d.AppId) || d.AppId == "0")
            throw new Exception("Steam did not provide an App ID for item " + id + ".");

        output.Add(d);
    }
}
