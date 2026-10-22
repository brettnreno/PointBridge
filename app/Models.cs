using System.Text.Json.Nodes;

public record DataLocation(string Path);
public record RuntimeRequest(bool Armed);
public record ExtensionRequest(bool Enabled);

public class Connection
{
    public string BaseUrl { get; set; } = "";
    public string ApiVersion { get; set; } = "v5";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public class Mapping
{
    public string SourceKind { get; set; } = "metasys";
    public string ExternalSourceId { get; set; } = "";
    public string DestinationConnectionId { get; set; } = "destination";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string SourceObject { get; set; } = "";
    public string DestinationObject { get; set; } = "";
    public string SourceAttribute { get; set; } = "presentValue";
    public string DestinationAttribute { get; set; } = "presentValue";
    public string WriteMode { get; set; } = "attribute";
    public string CommandId { get; set; } = "";
    public string? CommandPriority { get; set; }
    public bool Enabled { get; set; } = true;
    public JsonNode? SourceValue { get; set; }
    public JsonNode? DestinationValue { get; set; }
    public string Status { get; set; } = "Not synchronized since startup";
    public DateTimeOffset? LastSuccess { get; set; }
}

public class Settings
{
    public int SchemaVersion { get; set; } = 1;
    public bool ExtendedSourcesEnabled { get; set; }
    public Dictionary<string, Connection> Destinations { get; set; } = [];
    public Dictionary<string, ExternalJsonSource> ExternalSources { get; set; } = [];
    public bool TransfersEnabled { get; set; }
    public Connection Source { get; set; } = new();
    public Connection Destination { get; set; } = new();
    public List<Mapping> Mappings { get; set; } = [];
}

public class PointInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public JsonNode? Value { get; set; }
    public string Type { get; set; } = "";
    public string? Units { get; set; }
    public string? Reliability { get; set; }
    public string? Priority { get; set; }
    public JsonNode? Schema { get; set; }
    public bool Writable { get; set; }
}

public class CommandInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public JsonNode? CommandBodySchema { get; set; }
    public bool Supported { get; set; }
    public string Reason { get; set; } = "";
    public List<string> Priorities { get; set; } = [];
}

public class ValidationResult
{
    public bool Valid => Errors.Count == 0;
    public List<string> Errors { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public PointInfo? Source { get; set; }
    public PointInfo? Destination { get; set; }
    public List<CommandInfo> Commands { get; set; } = [];
    public CommandInfo? SelectedCommand { get; set; }
}

public record AuditEntry(DateTimeOffset Timestamp, string Level, string Message);
