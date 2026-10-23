using System.Text;

public interface ISafeError
{
    string SafeMessage { get; }
}

public sealed class UserInputException(string message) : ArgumentException(message), ISafeError
{
    public string SafeMessage => Message;
}

public sealed class UpstreamLimitException(string message) : IOException(message), ISafeError
{
    public string SafeMessage => Message;
}

public static class SafeErrors
{
    public static string Message(Exception error) => error is ISafeError safe
        ? safe.SafeMessage : "The operation could not be completed. Review the configuration and service availability.";
}

public sealed class BoundedLineReader(StreamReader reader)
{
    public const int MaximumLineCharacters = 65536;
    readonly char[] buffer = new char[4096];
    int position, length;
    bool skipLineFeed;

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        while (true)
        {
            if (position == length)
            {
                length = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                position = 0;
                if (length == 0) return line.Length == 0 ? null : line.ToString();
            }
            var character = buffer[position++];
            if (skipLineFeed) { skipLineFeed = false; if (character == '\n') continue; }
            if (character == '\r') { skipLineFeed = true; return line.ToString(); }
            if (character == '\n') return line.ToString();
            if (line.Length >= MaximumLineCharacters) throw new UpstreamLimitException("COV stream line exceeded its safety limit.");
            line.Append(character);
        }
    }
}
