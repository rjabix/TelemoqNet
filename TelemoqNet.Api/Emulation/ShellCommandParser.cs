using System.Text;

namespace TelemoqNet.Api.Emulation;

internal sealed record ShellRedirect(string Path, bool Append);

internal sealed record ShellCommand(string[] Arguments, ShellRedirect[] Redirects, bool RequiresSuccess);

// This grammar is deliberately finite: no expansion, pipelines, subprocesses, or scripts.
internal static class ShellCommandParser
{
    private const int MaxTokens = 4096;
    private const int MaxCommands = 128;

    public static bool TryParse(string line, out List<ShellCommand> commands)
    {
        commands = [];
        if (!TryTokenize(line, out var tokens))
        {
            return false;
        }

        var arguments = new List<string>();
        var redirects = new List<ShellRedirect>();
        var requiresSuccess = false;
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Kind == TokenKind.Word)
            {
                arguments.Add(token.Value);
                continue;
            }

            if (token.Kind is TokenKind.Overwrite or TokenKind.Append)
            {
                if (++index == tokens.Count || tokens[index].Kind != TokenKind.Word)
                {
                    return false;
                }

                redirects.Add(new ShellRedirect(tokens[index].Value, token.Kind == TokenKind.Append));
                continue;
            }

            if (arguments.Count == 0 && redirects.Count == 0)
            {
                return false;
            }

            commands.Add(new ShellCommand(arguments.ToArray(), redirects.ToArray(), requiresSuccess));
            arguments.Clear();
            redirects.Clear();
            requiresSuccess = token.Kind == TokenKind.And;
        }

        if (arguments.Count > 0 || redirects.Count > 0)
        {
            commands.Add(new ShellCommand(arguments.ToArray(), redirects.ToArray(), requiresSuccess));
        }
        else if (requiresSuccess)
        {
            return false;
        }

        return commands.Count <= MaxCommands;
    }

    private static bool TryTokenize(string line, out List<Token> tokens)
    {
        tokens = [];
        return new Tokenizer(line, tokens).TryRead();
    }

    private sealed class Tokenizer(string line, List<Token> tokens)
    {
        private readonly StringBuilder _word = new();
        private int _index;
        private char _quote;
        private bool _wordStarted;
        private bool _wordQuoted;
        private bool _inComment;

        public bool TryRead()
        {
            for (_index = 0; _index < line.Length && !_inComment; _index++)
            {
                if (!TryReadCharacter() || tokens.Count > MaxTokens)
                {
                    return false;
                }
            }

            if (_quote != '\0')
            {
                return false;
            }

            FlushWord();
            return tokens.Count <= MaxTokens;
        }

        private bool TryReadCharacter()
        {
            var value = line[_index];
            if (_quote != '\'' && IsCommandSubstitution(value))
            {
                return false;
            }

            if (value == '\\' && _quote != '\'')
            {
                return TryReadEscape();
            }

            if (_quote != '\0')
            {
                ReadQuotedCharacter(value);
                return true;
            }

            return TryReadUnquotedCharacter(value);
        }

        private bool IsCommandSubstitution(char value) =>
            value == '`' || value == '$' && _index + 1 < line.Length && line[_index + 1] == '(';

        private bool TryReadEscape()
        {
            if (++_index == line.Length)
            {
                return false;
            }

            var escaped = line[_index];
            if (_quote == '"' && escaped is not ('"' or '\\' or '$' or '`' or '\n'))
            {
                _word.Append('\\');
            }

            if (escaped != '\n')
            {
                _word.Append(escaped);
                _wordStarted = true;
                _wordQuoted = true;
            }

            return true;
        }

        private void ReadQuotedCharacter(char value)
        {
            if (value == _quote)
            {
                _quote = '\0';
            }
            else
            {
                _word.Append(value);
            }
        }

        private bool TryReadUnquotedCharacter(char value)
        {
            switch (value)
            {
                case '\'':
                case '"':
                    _quote = value;
                    _wordStarted = true;
                    _wordQuoted = true;
                    return true;
                case '#' when !_wordStarted:
                    _inComment = true;
                    return true;
                case '|' or '<' or '(' or ')':
                    return false;
            }

            if (char.IsWhiteSpace(value) || value is ';' or '&' or '>')
            {
                return TryReadSeparator(value);
            }

            _word.Append(value);
            _wordStarted = true;
            return true;
        }

        private bool TryReadSeparator(char value)
        {
            // Descriptor redirection is outside this grammar; never reinterpret it as stdout.
            if (value == '>' && _wordStarted && !_wordQuoted && _word.ToString().All(char.IsAsciiDigit))
            {
                return false;
            }

            FlushWord();
            switch (value)
            {
                case '&':
                    return TryReadAnd();
                case '>':
                    var append = TryConsumeNext('>');
                    tokens.Add(new Token(">", append ? TokenKind.Append : TokenKind.Overwrite));
                    break;
                case ';' or '\n':
                    tokens.Add(new Token(";", TokenKind.Sequence));
                    break;
            }

            return true;
        }

        private bool TryReadAnd()
        {
            if (!TryConsumeNext('&'))
            {
                return false;
            }

            tokens.Add(new Token("&&", TokenKind.And));
            return true;
        }

        private bool TryConsumeNext(char value)
        {
            if (_index + 1 == line.Length || line[_index + 1] != value)
            {
                return false;
            }

            _index++;
            return true;
        }

        private void FlushWord()
        {
            if (!_wordStarted)
            {
                return;
            }

            tokens.Add(new Token(_word.ToString(), TokenKind.Word));
            _word.Clear();
            _wordStarted = false;
            _wordQuoted = false;
        }
    }

    private enum TokenKind
    {
        Word, Sequence, And, Overwrite, Append
    }

    private sealed record Token(string Value, TokenKind Kind);
}
