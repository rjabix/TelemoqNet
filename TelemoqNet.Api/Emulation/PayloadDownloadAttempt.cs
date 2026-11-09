using System.Text;

namespace TelemoqNet.Api.Emulation;

public sealed record PayloadDownloadAttempt(string Tool, string Url)
{
    public bool Executed => false;
    public bool Downloaded => false;
}

// Passive inspection of the bounded input line. This never evaluates shell syntax,
// expands variables/substitutions, resolves hosts, or retrieves a payload.
internal static class PayloadDownloadAttemptParser
{
    private static readonly HashSet<string> WgetValueOptions =
    [
        "-O", "-P", "-T", "-t", "-U", "-Y", "-e", "-o", "-a", "-i",
        "--output-document", "--directory-prefix", "--timeout", "--tries",
        "--user-agent", "--header", "--post-data", "--post-file", "--referer",
        "--user", "--password", "--http-user", "--http-password", "--proxy-user",
        "--proxy-password", "--execute", "--output-file", "--append-output", "--input-file"
    ];

    private static readonly HashSet<string> CurlValueOptions =
    [
        "-o", "-d", "-H", "-A", "-e", "-u", "-x", "-X", "-T", "-F", "-b", "-c", "-m",
        "--output", "--data", "--data-raw", "--data-binary", "--data-urlencode",
        "--header", "--user-agent", "--referer", "--user", "--proxy", "--request",
        "--upload-file", "--form", "--cookie", "--cookie-jar", "--max-time",
        "--connect-timeout", "--retry", "--config", "-K", "--resolve", "--connect-to"
    ];

    public static IEnumerable<PayloadDownloadAttempt> Find(string line)
    {
        var command = new List<Token>();
        foreach (var token in Tokenize(line))
        {
            if (token.Kind == TokenKind.Incomplete)
            {
                command.Clear();
            }
            else if (token.Kind == TokenKind.Operator && token.Value is ";" or "&" or "|" or "\n")
            {
                foreach (var attempt in FindInCommand(command))
                {
                    yield return attempt;
                }

                command.Clear();
            }
            else
            {
                command.Add(token);
            }
        }

        foreach (var attempt in FindInCommand(command))
        {
            yield return attempt;
        }
    }

    internal static (string? InformationOption, IReadOnlyList<string> Urls) ParseArguments(
        string tool, string[] arguments)
    {
        var tokens = Tokenize(string.Join(' ', arguments))
            .TakeWhile(token => token.Kind != TokenKind.Operator || token.Value is "<" or ">")
            .ToList();

        return tokens.Any(token => token.Kind == TokenKind.Incomplete)
            ? (null, [])
            : ParseArguments(tool, tokens, 0);
    }

    private static IEnumerable<PayloadDownloadAttempt> FindInCommand(List<Token> command)
    {
        if (command.Count == 0)
        {
            yield break;
        }

        var index = 0;
        var tool = Path.GetFileName(command[index++].Value);
        if (tool == "busybox" && index < command.Count)
        {
            tool = command[index++].Value;
        }

        if (tool is not ("wget" or "curl"))
        {
            yield break;
        }

        var parsed = ParseArguments(tool, command, index);
        if (parsed.InformationOption is not null)
        {
            yield break;
        }

        foreach (var value in parsed.Urls)
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                uri.Scheme is "http" or "https" or "ftp" or "tftp" &&
                !string.IsNullOrEmpty(uri.Host))
            {
                yield return new PayloadDownloadAttempt(tool, value);
            }
        }
    }

    private static (string? InformationOption, IReadOnlyList<string> Urls) ParseArguments(
        string tool, List<Token> command, int index)
    {
        var valueOptions = tool == "wget" ? WgetValueOptions : CurlValueOptions;
        var endOfOptions = false;
        string? informationOption = null;
        var urls = new List<string>();
        for (; index < command.Count; index++)
        {
            var token = command[index];
            if (token.Kind == TokenKind.Operator)
            {
                // A redirect destination is not a download URL.
                index++;
                continue;
            }

            var value = token.Value;
            if (!endOfOptions)
            {
                if (value == "--")
                {
                    endOfOptions = true;
                    continue;
                }

                if (tool == "curl" && value.StartsWith("--url=", StringComparison.Ordinal))
                {
                    value = value[6..];
                }
                else if (tool == "curl" && value == "--url" && index + 1 < command.Count)
                {
                    value = command[++index].Value;
                }
                else if (valueOptions.Contains(value))
                {
                    index++;
                    continue;
                }
                else if (value is "--help" or "-h")
                {
                    informationOption ??= "help";
                    continue;
                }
                else if (value is "--version" or "-V")
                {
                    informationOption ??= "version";
                    continue;
                }
                else if (value.StartsWith('-'))
                {
                    continue;
                }
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                urls.Add(value);
            }
        }

        return (informationOption, urls);
    }

    private static IEnumerable<Token> Tokenize(string line)
    {
        var word = new StringBuilder();
        var quote = '\0';
        var started = false;
        for (var index = 0; index < line.Length; index++)
        {
            var value = line[index];
            if (value == '\\' && quote != '\'' && index + 1 < line.Length)
            {
                word.Append(line[++index]);
                started = true;
            }
            else if (quote != '\0')
            {
                if (value == quote)
                {
                    quote = '\0';
                }
                else
                {
                    word.Append(value);
                }
            }
            else if (value is '\'' or '"')
            {
                quote = value;
                started = true;
            }
            else if (value == '#' && !started)
            {
                break;
            }
            else if (char.IsWhiteSpace(value) || value is ';' or '&' or '|' or '<' or '>')
            {
                if (started)
                {
                    yield return new Token(word.ToString());
                    word.Clear();
                    started = false;
                }

                if (!char.IsWhiteSpace(value) || value == '\n')
                {
                    // Keep a redirect operator together, so its operand is skipped once.
                    if (value is '<' or '>' && index + 1 < line.Length && line[index + 1] == value)
                    {
                        index++;
                    }

                    yield return new Token(value.ToString(), TokenKind.Operator);
                }
            }
            else
            {
                word.Append(value);
                started = true;
            }
        }

        if (quote != '\0')
        {
            yield return new Token(string.Empty, TokenKind.Incomplete);
        }
        else if (started)
        {
            yield return new Token(word.ToString());
        }
    }

    private enum TokenKind
    {
        Word, Operator, Incomplete
    }

    private sealed record Token(string Value, TokenKind Kind = TokenKind.Word);
}
