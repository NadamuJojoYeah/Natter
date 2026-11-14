using System.Text;

namespace Natter.Cli.Core;

/// <summary>
/// Splits a command line string into tokens, honouring single/double quotes.
/// Used by the `exec` command to run multiple commands inside one composed host.
/// </summary>
public static class CliCommandTokenizer
{
    public static IReadOnlyList<string> Split(string commandLine)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
            return tokens;

        var current = new StringBuilder();
        char quote = '\0';

        for (var index = 0; index < commandLine.Length; index++)
        {
            var character = commandLine[index];

            if (character == '\\'
                && index + 1 < commandLine.Length
                && commandLine[index + 1] is '"' or '\'' or '\\' or ' ')
            {
                current.Append(commandLine[++index]);
                continue;
            }

            if (quote != '\0')
            {
                if (character == quote)
                    quote = '\0';
                else
                    current.Append(character);
                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (quote != '\0')
            throw new FormatException("Unterminated quote in command line.");

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens;
    }
}
