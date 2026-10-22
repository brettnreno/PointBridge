using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public static class Preflight
{
    public static void CheckInput(Mapping mapping)
    {
        if(mapping.SourceKind is not("metasys" or "json"))throw new ArgumentException("Choose a supported source kind.");
        if ((mapping.SourceKind=="metasys" && string.IsNullOrWhiteSpace(mapping.SourceObject)) || (mapping.SourceKind=="json" && string.IsNullOrWhiteSpace(mapping.ExternalSourceId)) || string.IsNullOrWhiteSpace(mapping.DestinationObject))
            throw new ArgumentException("Both point IDs or references are required.");
        if (mapping.Name.Length > 128 || mapping.SourceObject.Length > 2048 || mapping.DestinationObject.Length > 2048)
            throw new ArgumentException("Point names are limited to 128 characters and references to 2048 characters.");
        if (mapping.SourceAttribute != "presentValue" || mapping.DestinationAttribute != "presentValue")
            throw new ArgumentException("Mappings transfer the presentValue attribute.");
        if (mapping.WriteMode is not ("attribute" or "command")) throw new ArgumentException("Choose attribute or command write mode.");
        if (mapping.WriteMode == "attribute" && mapping.CommandPriority != null)
            throw new ArgumentException("A command priority is available only for a command that declares it.");
    }

    public static async Task<ValidationResult> Validate(Mapping mapping, Api source, Api destination, CancellationToken ct)
    {
        try
        {
            CheckInput(mapping);
            if (string.Equals(source.Origin,destination.Origin,StringComparison.OrdinalIgnoreCase))
                return new ValidationResult{Errors=["Source and destination must be different server origins."]};
            return await ValidatePoint(mapping,await Describe(source,mapping.SourceObject,false,ct),destination,ct);
        }
        catch(Exception e)when(e is not OperationCanceledException){return new(){Errors=[e.Message]};}
    }
    public static async Task<ValidationResult> ValidatePoint(Mapping mapping,PointInfo source,Api destination,CancellationToken ct)
    {
        var result=new ValidationResult();
        try
        {
            CheckInput(mapping);
            result.Source=source;
            result.Destination = await Describe(destination, mapping.DestinationObject, true, ct);
            if (result.Source.Reliability != null && result.Source.Reliability != "reliabilityEnumSet.reliable")
                result.Warnings.Add("Source quality is currently unreliable. Transfers skip unreliable source updates until quality recovers.");
            if (result.Source.Type.Length == 0 || result.Destination.Type.Length == 0)
                result.Errors.Add("The servers did not provide complete scalar value schemas.");
            else if (result.Source.Type is not ("number" or "integer" or "string" or "boolean")
                || result.Destination.Type is not ("number" or "integer" or "string" or "boolean"))
                result.Errors.Add("Point mappings require scalar numeric, Boolean or enumeration values.");
            else if (!CompatibleTypes(result.Source.Type, result.Destination.Type))
                result.Errors.Add($"Source type {result.Source.Type} does not match destination type {result.Destination.Type}.");
            if (!string.Equals(result.Source.Units, result.Destination.Units, StringComparison.Ordinal))
                result.Errors.Add($"Engineering units differ ({result.Source.Units ?? "unknown"} → {result.Destination.Units ?? "unknown"}). Configure matching units in Metasys before mapping.");
            if (result.Source.Units == null && result.Destination.Units == null && result.Source.Type is "number" or "integer")
                result.Warnings.Add("Neither point declares engineering units. Confirm these values use the same physical scale.");
            if (result.Source.Value == null) result.Errors.Add("The source has no current value.");
            else
            {
                var error = SchemaRules.Validate(result.Source.Value, result.Destination.Schema);
                if (error != null) result.Errors.Add("Current source value is incompatible with destination: " + error);
                var sourceEnums = SchemaRules.Enums(result.Source.Schema).ToArray();
                var destinationEnums = SchemaRules.Enums(result.Destination.Schema).ToArray();
                if (sourceEnums.Length > 0 && destinationEnums.Length > 0
                    && sourceEnums.Any(value => !destinationEnums.Any(allowed => JsonNode.DeepEquals(value, allowed))))
                    result.Errors.Add("Source and destination enumeration domains differ. Select points with matching states.");
            }
            try { result.Commands = await Commands(destination, result.Destination.Id, ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (mapping.WriteMode == "command") throw;
                result.Warnings.Add("Command discovery unavailable: " + e.Message);
            }
            if (mapping.WriteMode == "attribute")
            {
                if (!result.Destination.Writable) result.Errors.Add("Destination presentValue is not listed as writable. Select a supported adjust command if available.");
            }
            else
            {
                result.SelectedCommand = result.Commands.FirstOrDefault(x => x.Id == mapping.CommandId);
                var command = result.SelectedCommand;
                if (command == null || !command.Supported) result.Errors.Add(command?.Reason ?? "Select a discovered, supported adjust command.");
                else
                {
                    if (mapping.CommandPriority != null && !command.Priorities.Contains(mapping.CommandPriority))
                        result.Errors.Add("The selected priority is not permitted by this command schema.");
                    if (Requires(command, "priority") && mapping.CommandPriority == null)
                        result.Errors.Add("This command requires an explicit priority. Select an advertised priority.");
                    if (result.Source.Value != null)
                    {
                        var error = SchemaRules.Validate(result.Source.Value, ParameterSchema(command));
                        if (error != null) result.Errors.Add("Current source value is incompatible with command: " + error);
                    }
                }
            }
            result.Warnings.Add("Validation reads metadata only. Actual writes are verified by destination readback after transfers start.");
        }
        catch (Exception e) when (e is not OperationCanceledException) { result.Errors.Add(e.Message); }
        return result;
    }

    public static async Task<PointInfo> Describe(Api api, string reference, bool writable, CancellationToken ct)
    {
        var id = await api.Resolve(reference, ct);
        var attribute = await api.Get($"objects/{id}/attributes/presentValue?includeSchema=true", ct);
        var metadata = await api.Get($"objects/{id}?includeSchema=true", ct);
        var schema = attribute?["schema"]?["properties"]?["presentValue"]
            ?? metadata?["schema"]?["properties"]?["presentValue"];
        var definitions = attribute?["schema"]?["definitions"] ?? metadata?["schema"]?["definitions"];
        var expanded = await Expand(api, schema, definitions, ct);
        var info = new PointInfo
        {
            Id = id,
            Name = metadata?["item"]?["name"]?.ToString() ?? reference,
            Value = attribute?["item"]?["presentValue"]?.DeepClone(),
            Type = SchemaRules.Type(expanded),
            Units = metadata?["item"]?["units"]?.ToString(),
            Reliability = attribute?["condition"]?["presentValue"]?["reliability"]?.ToString()
                ?? metadata?["item"]?["reliability"]?.ToString(),
            Schema = expanded
        };
        if (writable)
        {
            var attributes = await api.Get($"objects/{id}/attributes?attributeFilter=writableBasic", ct);
            info.Writable = attributes?["schema"]?["properties"]?["presentValue"] != null
                && schema?["readOnly"]?.ToString() != "true"
                && metadata?["effectivePermissions"]?["canModify"]?.ToString() != "false";
        }
        return info;
    }

    public static async Task<List<CommandInfo>> Commands(Api api, string reference, CancellationToken ct)
    {
        var id = await api.Resolve(reference, ct);
        var response = await api.Get($"objects/{id}/commands", ct);
        var items = response as JsonArray ?? response?["items"] as JsonArray ?? new JsonArray();
        var commands = new List<CommandInfo>();
        foreach (var raw in items)
        {
            var command = new CommandInfo
            {
                Id = raw?["id"]?.ToString() ?? "",
                Title = raw?["title"]?.ToString() ?? raw?["name"]?.ToString() ?? raw?["id"]?.ToString() ?? "",
                CommandBodySchema = await Expand(api, raw?["commandBodySchema"], raw?["commandBodySchema"]?["definitions"], ct)
            };
            var parameters = command.CommandBodySchema?["properties"]?["parameters"];
            var tuple = parameters?["items"] as JsonArray;
            var isAdjust = command.Id.Split('.').Last() == "adjustCommand";
            command.Supported = isAdjust && raw?["aggregateCommand"]?.ToString() != "true"
                && tuple?.Count == 1 && SchemaRules.Type(tuple[0]) is "number" or "integer" or "boolean" or "string"
                && (parameters?["maxItems"]?.GetValue<int>() ?? 1) == 1;
            command.Reason = command.Supported ? "" : "This command is not a direct scalar adjust command with exactly one value parameter.";
            if (command.Supported && command.CommandBodySchema?["required"] is JsonArray required)
            {
                var unknown = required.Select(x => x?.ToString()).Where(x => x is not ("parameters" or "annotation" or "priority")).ToArray();
                if (unknown.Length > 0) { command.Supported = false; command.Reason = "Command requires additional parameters: " + string.Join(", ", unknown); }
            }
            command.Priorities = SchemaRules.Enums(command.CommandBodySchema?["properties"]?["priority"])
                .Select(x => x?.ToString()).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).Distinct().ToList();
            commands.Add(command);
        }
        return commands;
    }

    public static JsonNode? ParameterSchema(CommandInfo command) => command.CommandBodySchema?["properties"]?["parameters"]?["items"] is JsonArray tuple && tuple.Count == 1 ? tuple[0] : null;

    public static JsonObject CommandBody(CommandInfo command, Mapping mapping, JsonNode value)
    {
        if (!command.Supported) throw new InvalidOperationException(command.Reason);
        if (Requires(command, "priority") && mapping.CommandPriority == null)
            throw new InvalidOperationException("This command requires an explicit priority.");
        var error = SchemaRules.Validate(value, ParameterSchema(command));
        if (error != null) throw new InvalidOperationException(error);
        var body = new JsonObject { ["parameters"] = new JsonArray(value.DeepClone()) };
        var properties = command.CommandBodySchema?["properties"];
        if (properties?["annotation"] != null) body["annotation"] = "Metasys Point Bridge";
        if (mapping.CommandPriority != null)
        {
            if (!command.Priorities.Contains(mapping.CommandPriority)) throw new InvalidOperationException("Command priority is not supported by the live command schema.");
            body["priority"] = mapping.CommandPriority;
        }
        return body;
    }

    static bool Requires(CommandInfo command, string property) => command.CommandBodySchema?["required"] is JsonArray required
        && required.Any(value => value?.ToString() == property);

    static bool CompatibleTypes(string source, string destination) => source == destination || source == "integer" && destination == "number";

    static async Task<JsonNode?> Expand(Api api, JsonNode? schema, JsonNode? definitions, CancellationToken ct, int depth = 0)
    {
        if (schema == null || depth > 12) return schema?.DeepClone();
        if (schema is JsonArray array)
        {
            var result = new JsonArray();
            foreach (var item in array) result.Add(await Expand(api, item, definitions, ct, depth + 1));
            return result;
        }
        if (schema is not JsonObject obj) return schema.DeepClone();
        if (obj["$ref"] is JsonNode reference)
        {
            var name = reference.ToString();
            JsonNode? target = null;
            if (name.StartsWith('#')) target = definitions?[name.TrimStart('#').Replace("/definitions/", "")];
            else
            {
                var match = Regex.Match(name, @"/schemas/enums/([A-Za-z0-9_]+)$");
                if (match.Success) target = await api.Get("schemas/enums/" + match.Groups[1].Value, ct);
            }
            if (target != null) return await Expand(api, target, definitions, ct, depth + 1);
        }
        var copy = new JsonObject();
        foreach (var pair in obj) copy[pair.Key] = await Expand(api, pair.Value, definitions, ct, depth + 1);
        return copy;
    }
}

public static class SchemaRules
{
    public static string Type(JsonNode? schema)
    {
        if (schema == null) return "";
        if (schema["type"] is JsonValue explicitType) return explicitType.ToString();
        if (schema["metasysType"]?.ToString() is string meta)
        {
            if (meta == "bool") return "boolean";
            if (meta == "enum" || meta == "string") return "string";
            if (meta is "float" or "double") return "number";
            if (meta is "byte" or "short" or "ushort" or "long" or "ulong") return "integer";
        }
        foreach (var key in new[] { "oneOf", "anyOf", "allOf" })
            if (schema[key] is JsonArray branches)
                foreach (var branch in branches) { var kind = Type(branch); if (kind.Length > 0 && kind != "null") return kind; }
        if (schema["enum"] is JsonArray values && values.Count > 0) return values[0]?.GetValueKind() switch { System.Text.Json.JsonValueKind.String => "string", System.Text.Json.JsonValueKind.Number => "number", _ => "" };
        return "";
    }

    public static IEnumerable<JsonNode?> Enums(JsonNode? schema)
    {
        if (schema == null) yield break;
        if (schema["enum"] is JsonArray values) foreach (var value in values) yield return value;
        foreach (var key in new[] { "oneOf", "anyOf", "allOf" })
            if (schema[key] is JsonArray branches) foreach (var branch in branches) foreach (var value in Enums(branch)) yield return value;
        if (schema["const"] != null) yield return schema["const"];
    }

    public static string? Validate(JsonNode value, JsonNode? schema)
    {
        if (schema == null) return "No schema was supplied.";
        if (schema["$ref"] != null) return "The schema contains an unresolved reference.";
        var kind = value.GetValueKind();
        var type = schema["type"]?.ToString();
        if (type == "number" && kind != System.Text.Json.JsonValueKind.Number
            || type == "integer" && (kind != System.Text.Json.JsonValueKind.Number || !decimal.TryParse(value.ToJsonString(), out var integer) || decimal.Truncate(integer) != integer)
            || type == "string" && kind != System.Text.Json.JsonValueKind.String
            || type == "boolean" && kind is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)) return "Value does not match declared type " + type;
        if (schema["enum"] is JsonArray allowed && !allowed.Any(x => JsonNode.DeepEquals(x, value))) return "Value is not a permitted enumeration member.";
        if (schema["const"] != null && !JsonNode.DeepEquals(schema["const"], value)) return "Value does not match enumeration member.";
        if (kind == System.Text.Json.JsonValueKind.Number && double.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            if (schema["minimum"] is JsonNode minimum && number < minimum.GetValue<double>()) return "Value is below the minimum.";
            if (schema["maximum"] is JsonNode maximum && number > maximum.GetValue<double>()) return "Value exceeds the maximum.";
        }
        if (kind == System.Text.Json.JsonValueKind.String)
        {
            var length = value.GetValue<string>().Length;
            if (schema["maxLength"] is JsonNode max && length > max.GetValue<int>()) return "Value is too long.";
            if (schema["minLength"] is JsonNode min && length < min.GetValue<int>()) return "Value is too short.";
        }
        if (schema["allOf"] is JsonArray all) foreach (var branch in all) { var error = Validate(value, branch); if (error != null) return error; }
        foreach (var key in new[] { "oneOf", "anyOf" })
            if (schema[key] is JsonArray branches && !branches.Any(branch => branch?["type"]?.ToString() != "null" && Validate(value, branch) == null)) return "Value does not match an allowed schema alternative.";
        return null;
    }
}
