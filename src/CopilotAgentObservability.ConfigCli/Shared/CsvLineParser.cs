namespace CopilotAgentObservability.ConfigCli;

internal static class CsvLineParser
{
    public static IReadOnlyList<string> ParseLine(string line)
    {
        var values = new List<string>();
        var builder = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                {
                    builder.Append('"');
                    index++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (character == ',' && !inQuotes)
            {
                values.Add(builder.ToString());
                builder.Clear();
            }
            else
            {
                builder.Append(character);
            }
        }

        if (inQuotes)
        {
            throw new InvalidDataException("CSV row contains an unterminated quoted value.");
        }

        values.Add(builder.ToString());
        return values;
    }
}
