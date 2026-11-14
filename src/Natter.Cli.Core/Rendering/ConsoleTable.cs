namespace Natter.Cli.Core.Rendering;

/// <summary>Small ASCII table renderer used by diagnostic commands.</summary>
internal static class ConsoleTable
{
    public static void Write(
        TextWriter writer,
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);

        var widths = new int[headers.Count];
        for (var column = 0; column < headers.Count; column++)
            widths[column] = headers[column].Length;

        foreach (var row in rows)
        {
            for (var column = 0; column < headers.Count && column < row.Count; column++)
                widths[column] = Math.Max(widths[column], row[column]?.Length ?? 0);
        }

        writer.WriteLine(string.Join("  ", headers.Select((header, index) => header.PadRight(widths[index]))));
        writer.WriteLine(string.Join("  ", widths.Select(width => new string('-', Math.Min(width, 48)))));

        if (rows.Count == 0)
        {
            writer.WriteLine("(none)");
            return;
        }

        foreach (var row in rows)
        {
            var cells = new string[headers.Count];
            for (var column = 0; column < headers.Count; column++)
            {
                var value = column < row.Count ? row[column] ?? string.Empty : string.Empty;
                cells[column] = value.PadRight(widths[column]);
            }

            writer.WriteLine(string.Join("  ", cells).TrimEnd());
        }
    }
}
