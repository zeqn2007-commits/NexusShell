using System.Text;

namespace Nexus.Core.Games;

/// <summary>
/// Valve's text KeyValues format, used by Steam for libraryfolders.vdf and appmanifest_*.acf:
/// <c>"key" "value"</c> pairs and <c>"key" { … }</c> blocks.
/// </summary>
public sealed class ValveKeyValues
{
    private ValveKeyValues()
    {
    }

    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, ValveKeyValues> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? this[string key] => Values.TryGetValue(key, out var value) ? value : null;

    public ValveKeyValues? Child(string key) => Children.TryGetValue(key, out var child) ? child : null;

    public static ValveKeyValues Parse(string text)
    {
        var tokens = Tokenize(text).GetEnumerator();
        var root = new ValveKeyValues();
        ParseBlock(tokens, root, isRoot: true);
        return root;
    }

    public static ValveKeyValues? TryLoad(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void ParseBlock(IEnumerator<Token> tokens, ValveKeyValues block, bool isRoot)
    {
        while (tokens.MoveNext())
        {
            var token = tokens.Current;
            if (token.Kind == TokenKind.Close)
            {
                if (isRoot)
                {
                    continue;
                }

                return;
            }

            if (token.Kind != TokenKind.Text || !tokens.MoveNext())
            {
                return;
            }

            var key = token.Value;
            var next = tokens.Current;
            if (next.Kind == TokenKind.Open)
            {
                var child = new ValveKeyValues();
                ParseBlock(tokens, child, isRoot: false);
                block.Children[key] = child;
            }
            else if (next.Kind == TokenKind.Text)
            {
                block.Values[key] = next.Value;
            }
        }
    }

    private static IEnumerable<Token> Tokenize(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            var ch = text[index];
            if (char.IsWhiteSpace(ch))
            {
                index++;
            }
            else if (ch == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                while (index < text.Length && text[index] != '\n')
                {
                    index++;
                }
            }
            else if (ch == '{')
            {
                index++;
                yield return new Token(TokenKind.Open, string.Empty);
            }
            else if (ch == '}')
            {
                index++;
                yield return new Token(TokenKind.Close, string.Empty);
            }
            else if (ch == '"')
            {
                var value = new StringBuilder();
                index++;
                while (index < text.Length && text[index] != '"')
                {
                    if (text[index] == '\\' && index + 1 < text.Length)
                    {
                        index++;
                        value.Append(text[index] switch
                        {
                            'n' => '\n',
                            't' => '\t',
                            _ => text[index]
                        });
                    }
                    else
                    {
                        value.Append(text[index]);
                    }

                    index++;
                }

                index++;
                yield return new Token(TokenKind.Text, value.ToString());
            }
            else
            {
                var start = index;
                while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] is not ('{' or '}' or '"'))
                {
                    index++;
                }

                yield return new Token(TokenKind.Text, text[start..index]);
            }
        }
    }

    private enum TokenKind
    {
        Text,
        Open,
        Close
    }

    private readonly record struct Token(TokenKind Kind, string Value);
}
