using System.Text;

namespace BaronDesk.Shared.Ipc;

/// <summary>
/// Reads newline-delimited messages with a length cap, so a misbehaving peer cannot make the
/// other side buffer an unbounded line (<see cref="StreamReader.ReadLineAsync()"/> has no limit).
/// </summary>
public static class PipeLineReader
{
    /// <returns>The next line without its terminator, or null at end of stream.</returns>
    /// <exception cref="InvalidDataException">The line exceeds <paramref name="maxChars"/>.</exception>
    public static async Task<string?> ReadLineAsync(StreamReader reader, int maxChars, CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        var buffer = new char[1];

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                return line.Length > 0 ? line.ToString() : null;
            }

            var c = buffer[0];
            if (c == '\n')
            {
                return line.ToString().TrimEnd('\r');
            }

            if (line.Length >= maxChars)
            {
                throw new InvalidDataException($"IPC message exceeds {maxChars} characters.");
            }

            line.Append(c);
        }
    }
}
