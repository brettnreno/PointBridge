using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

public record ExplorerBranch(string Kind, string Label);
public record ExplorerItem(string Id, string Name, string Reference, bool Selectable, ExplorerBranch[] Children);
public record ExplorerPage(List<ExplorerItem> Items, int? NextPage, bool Truncated);
public record BatchReadRequest(string[] ObjectIds);

public static class MetasysExplorer
{
    const int PageSize = 50;
    static string Identifier(string value) => Guid.TryParse(value, out var identifier) ? identifier.ToString()
        : throw new ArgumentException("Enter a Metasys object GUID.");
    static JsonArray Items(JsonNode? response) => response as JsonArray ?? (response as JsonObject)?["items"] as JsonArray
        ?? throw new IOException("Metasys returned an invalid collection response.");
    static string? Text(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text)
        ? text[..Math.Min(text.Length, 2048)] : null;
    static JsonNode? Scalar(JsonNode? value) => value is JsonValue ? value.DeepClone() : null;
    static bool HasNext(JsonNode? response) => response is JsonObject && !string.IsNullOrWhiteSpace(Text(response["next"]));

    public static async Task<ExplorerPage> Browse(Api api, string kind, string? parent, int page, CancellationToken ct)
    {
        if (page is < 1 or > 10000) throw new ArgumentException("Page must be between 1 and 10000.");
        var identifier = parent == null ? null : Identifier(parent);
        var route = kind switch
        {
            "objects" => identifier == null ? "objects" : $"objects/{identifier}/objects",
            "networkDevices" => identifier == null ? "networkDevices" : $"networkDevices/{identifier}/networkDevices",
            "equipment" when identifier == null => "equipment",
            "spaces" => identifier == null ? "spaces" : $"spaces/{identifier}/spaces",
            "spaceEquipment" when identifier != null => $"spaces/{identifier}/equipment",
            "equipmentPoints" when identifier != null => $"equipment/{identifier}/points",
            "deviceObjects" when identifier != null => $"objects/{identifier}/objects",
            _ => throw new ArgumentException("Choose a supported discovery branch.")
        };
        var hierarchy = kind is "objects" or "deviceObjects";
        if (hierarchy && page != 1) throw new ArgumentException("Object hierarchy does not support numbered pages.");
        route += hierarchy ? "?depth=1&flatten=true" : $"?page={page}&pageSize={PageSize}";
        var response = await api.Get(route, ct);
        var rawItems = Items(response);
        var result = new List<ExplorerItem>();
        foreach (var raw in rawItems.Take(hierarchy ? 500 : PageSize))
        {
            var item = raw is JsonObject && raw["item"] is JsonObject wrapped ? wrapped : raw;
            if (item is not JsonObject) continue;
            var objectId = kind == "equipmentPoints" ? PointIdentifier(api, Text(item["objectUrl"])) : Text(item["id"]);
            if (!Guid.TryParse(objectId, out var parsed)) continue;
            objectId = parsed.ToString();
            if (hierarchy && objectId == identifier) continue;
            var reference = Text(item["itemReference"]) ?? Text(item["reference"]) ?? objectId;
            var name = Text(item["name"]) ?? Text(item["label"]) ?? Text(item["shortName"]) ?? reference;
            var presentValue = Text(item["attribute"]) is null or "presentValue" or "attributeEnumSet.presentValue";
            ExplorerBranch[] children = kind switch
            {
                "networkDevices" => [new("networkDevices", "Child devices"), new("deviceObjects", "Objects")],
                "equipment" or "spaceEquipment" => [new("equipmentPoints", "Points")],
                "spaces" => [new("spaces", "Child spaces"), new("spaceEquipment", "Equipment")],
                "objects" or "deviceObjects" when item["hasChildrenMatchingQuery"]?.ToString() == "true"
                    || item["objectsUrl"] != null => [new("objects", "Child objects")],
                _ => []
            };
            result.Add(new(objectId, name, reference, kind is "objects" or "deviceObjects" || kind == "equipmentPoints" && presentValue, children));
        }
        return new(result, !hierarchy && HasNext(response) ? page + 1 : null, rawItems.Count > (hierarchy ? 500 : PageSize));
    }
    static string? PointIdentifier(Api api, string? objectUrl)
    {
        if (objectUrl == null || !Uri.TryCreate(new Uri(api.Origin + "/api/" + api.ApiVersion + "/"), objectUrl, out var uri)
            || !string.Equals(uri.GetLeftPart(UriPartial.Authority), new Uri(api.Origin).GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
            || uri.Query != "" || uri.Fragment != "") return null;
        var match = Regex.Match(uri.AbsolutePath, "^/api/" + api.ApiVersion + @"/objects/([a-fA-F0-9-]{36})$");
        return match.Success ? match.Groups[1].Value : null;
    }

    public static async Task<JsonObject> ReadValues(Api api, string[] identifiers, CancellationToken ct)
    {
        if (identifiers.Length is < 1 or > PageSize) throw new ArgumentException("Choose between 1 and 50 point GUIDs.");
        var expected = identifiers.Select(Identifier).Distinct().ToArray();
        var response = await api.ReadBatch(expected, ct);
        var responses = (response as JsonObject)?["responses"] as JsonArray ?? throw new IOException("Metasys returned an invalid batch response.");
        var rows = new JsonArray();
        foreach (var identifier in expected)
        {
            var matches = responses.Where(row => row is JsonObject && Text(row["id"]) == identifier).ToArray();
            var row = matches.Length == 1 ? matches[0] : null;
            var status = row?["status"] is JsonValue code && code.TryGetValue<int>(out var number) ? number : 502;
            var body = (row as JsonObject)?["body"] as JsonObject;
            var value = Scalar(body?["item"]?["presentValue"]);
            var success = status is >= 200 and < 300 && value != null;
            rows.Add(new JsonObject
            {
                ["id"] = identifier, ["status"] = status, ["success"] = success, ["value"] = success ? value : null,
                ["reliability"] = success ? Text(body?["condition"]?["presentValue"]?["reliability"]) : null,
                ["priority"] = success ? Text(body?["condition"]?["presentValue"]?["priority"]) : null,
                ["message"] = success ? null : "Value unavailable; verify the point, API licensing and read permissions."
            });
        }
        return new JsonObject { ["items"] = rows, ["checkedAt"] = DateTimeOffset.UtcNow, ["readOnly"] = true };
    }

    public static async Task<JsonObject> Inspect(Api api, string reference, string view, DateTimeOffset start,
        DateTimeOffset end, int page, string? continuation, CancellationToken ct)
    {
        if (reference.Length is < 1 or > 2048) throw new ArgumentException("Enter a point GUID or reference (maximum 2048 characters).");
        if (view is not ("summary" or "trends" or "alarms" or "audits")) throw new ArgumentException("Choose a supported inspection view.");
        if (end <= start || end - start > TimeSpan.FromDays(7) || end > DateTimeOffset.UtcNow.AddMinutes(5))
            throw new ArgumentException("Choose a past time range of up to seven days.");
        if (page is < 1 or > 10000 || continuation?.Length > 8192) throw new ArgumentException("Invalid inspection page.");
        var id = await api.Resolve(reference, ct);
        if (view == "summary")
        {
            var point = await Preflight.Describe(api, id, false, ct);
            var commands = new List<CommandInfo>();
            string? commandsWarning = null;
            try { commands = await Preflight.Commands(api, id, ct); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { commandsWarning = "Command discovery unavailable. Verify API licensing and permissions; no command was executed."; }
            return new JsonObject
            {
                ["id"] = point.Id, ["name"] = point.Name, ["value"] = Scalar(point.Value), ["type"] = point.Type,
                ["units"] = point.Units, ["reliability"] = point.Reliability, ["priority"] = point.Priority,
                ["commands"] = new JsonArray(commands.Select(command => (JsonNode)new JsonObject
                {
                    ["id"] = command.Id, ["title"] = command.Title, ["supported"] = command.Supported,
                    ["reason"] = command.Reason, ["priorities"] = new JsonArray(command.Priorities.Select(priority => (JsonNode)JsonValue.Create(priority)!).ToArray())
                }).ToArray()), ["commandsWarning"] = commandsWarning, ["readOnly"] = true
            };
        }
        var range = "startTime=" + Uri.EscapeDataString(start.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
            + "&endTime=" + Uri.EscapeDataString(end.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        if (view == "trends" && api.ApiVersion != "v4" && page != 1) throw new ArgumentException("This API version does not support numbered trend pages. Narrow the time range instead.");
        var trendRange = "startSampleTime=" + Uri.EscapeDataString(start.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
            + "&endSampleTime=" + Uri.EscapeDataString(end.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        var route = view == "trends" ? $"objects/{id}/trendedAttributes/presentValue/samples?"
                + (api.ApiVersion == "v4" ? $"{range}&page={page}&pageSize={PageSize}&sort=-timestamp" : trendRange + "&sort=-sampleTime")
            : $"activities?object={id}&activityType={(view == "alarms" ? "alarm" : "audit")}&{range}&sort=-creationTime"
                + (continuation == null ? "" : "&continuationToken=" + Uri.EscapeDataString(continuation));
        var response = await api.Get(route, ct);
        var items = Items(response);
        var result = new JsonArray();
        foreach (var item in items.Take(PageSize))
        {
            if (item is not JsonObject) continue;
            result.Add(view == "trends" ? new JsonObject
            {
                ["timestamp"] = Text(item[api.ApiVersion == "v4" ? "timestamp" : "sampleTime"]),
                ["value"] = Scalar(api.ApiVersion == "v4" ? (item["value"] as JsonObject)?["value"] : item["value"]),
                ["units"] = api.ApiVersion == "v4" ? Text((item["value"] as JsonObject)?["units"]) : null,
                ["reliability"] = api.ApiVersion == "v4" ? Text(item["isReliable"]) ?? item["isReliable"]?.ToString() : Text(item["reliability"])
            } : new JsonObject
            {
                ["id"] = Text(item["id"]), ["timestamp"] = Text(item["creationTime"]), ["objectName"] = Text(item["objectName"]),
                ["status"] = Text(item["activityManagementStatus"]),
                ["type"] = Text(item[view == "alarms" ? "alarm" : "audit"]?[view == "alarms" ? "type" : "actionType"]),
                ["priority"] = view == "alarms" ? Scalar(item["alarm"]?["priority"]) : null
            });
        }
        string? nextToken = null;
        if (view != "trends" && items.Count <= PageSize && HasNext(response) && Uri.TryCreate(new Uri(api.Origin + "/api/" + api.ApiVersion + "/"), Text(response?["next"]), out var next)
            && string.Equals(next.GetLeftPart(UriPartial.Authority), new Uri(api.Origin).GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
            && next.AbsolutePath == "/api/" + api.ApiVersion + "/activities" && next.Fragment == "" && next.UserInfo == "")
        {
            nextToken = QueryHelpers.ParseQuery(next.Query)["continuationToken"].FirstOrDefault();
            if (nextToken?.Length > 8192) nextToken = null;
        }
        return new JsonObject
        {
            ["items"] = result, ["nextPage"] = view == "trends" && api.ApiVersion == "v4" && HasNext(response) ? page + 1 : null,
            ["continuationToken"] = nextToken, ["truncated"] = items.Count > PageSize, ["readOnly"] = true
        };
    }
}
