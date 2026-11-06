using System.Text;

namespace allstarr.Core.Enrichment;

public enum PromotionNamingTokenKind
{
    Field,
    Literal
}

public sealed record PromotionNamingToken(PromotionNamingTokenKind Kind, string Value);

public static class PromotionNamingTokens
{
    public static string Compile(IEnumerable<PromotionNamingToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var script = new StringBuilder();
        foreach (var token in tokens)
        {
            ArgumentNullException.ThrowIfNull(token);
            ArgumentNullException.ThrowIfNull(token.Value);
            if (token.Kind == PromotionNamingTokenKind.Field)
            {
                if (!PromotionNaming.IsSupportedVariable(token.Value))
                    throw new ArgumentException("The token contains an unsupported field.", nameof(tokens));
                script.Append('%').Append(token.Value).Append('%');
            }
            else if (token.Kind == PromotionNamingTokenKind.Literal)
            {
                foreach (var character in token.Value)
                {
                    if (character is '$' or '%' or '(' or ')' or ',' or '\\') script.Append('\\');
                    script.Append(character);
                }
            }
            else throw new ArgumentException("The token kind is not supported.", nameof(tokens));
        }
        var result = script.ToString();
        if (PromotionNaming.Validate(result).Count > 0)
            throw new ArgumentException("The tokens do not produce a valid naming expression.", nameof(tokens));
        return result;
    }

    /// <summary>Returns false when a script needs functions or contains invalid syntax.</summary>
    public static bool TryParse(string script, out IReadOnlyList<PromotionNamingToken> tokens)
    {
        tokens = [];
        if (PromotionNaming.Validate(script).Count > 0) return false;
        var parsed = new List<PromotionNamingToken>();
        var literal = new StringBuilder();
        void FlushLiteral()
        {
            if (literal.Length == 0) return;
            parsed.Add(new(PromotionNamingTokenKind.Literal, literal.ToString()));
            literal.Clear();
        }
        for (var position = 0; position < script.Length; position++)
        {
            var character = script[position];
            if (character == '\\') literal.Append(script[++position]);
            else if (character == '$') return false;
            else if (character == '%')
            {
                FlushLiteral();
                var end = script.IndexOf('%', position + 1);
                parsed.Add(new(PromotionNamingTokenKind.Field, script[(position + 1)..end]));
                position = end;
            }
            else literal.Append(character);
        }
        FlushLiteral();
        tokens = parsed;
        return true;
    }
}
