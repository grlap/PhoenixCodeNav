using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeNav.Core.Discovery;

internal readonly record struct BoundedMsBuildProperty(string Value, bool Complete);

internal readonly record struct BoundedMsBuildExpansion(bool Handled, string Value);

internal readonly record struct BoundedMsBuildExistsResult(
    bool Complete, bool Exists, string? Error = null);

/// <summary>
/// Language-neutral, deliberately bounded scalar-expression evaluator shared by semantic project
/// projections. It evaluates properties and conditions only; callers retain ownership of document
/// traversal and language-specific item projection. It never reads the filesystem or loads MSBuild.
/// </summary>
internal sealed class BoundedMsBuildExpressionEvaluator
{
    private static readonly Regex PropertyReference = new(
        @"\$\((?<name>[A-Za-z_][A-Za-z0-9_.-]*)\)",
        RegexOptions.CultureInvariant);

    private static readonly Regex PropertyStartsWith = new(
        @"\$\(\s*(?<name>[A-Za-z_][A-Za-z0-9_.-]*)\.StartsWith\(\s*" +
        @"(?:'(?<argument>[^'\r\n$]*)'|""(?<argument>[^""\r\n$]*)"")\s*\)\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ExistsCondition = new(
        @"^Exists\s*\(\s*(?:'(?<path>[^'\r\n]*)'|""(?<path>[^""\r\n]*)"")\s*\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly IReadOnlyDictionary<string, BoundedMsBuildProperty> _properties;
    private readonly Func<string, string, BoundedMsBuildExpansion> _expandIntrinsic;
    private readonly Func<string, string, BoundedMsBuildExistsResult> _exists;
    private readonly CancellationToken _cancellationToken;
    private readonly int _maxPropertyValueChars;
    private readonly int _maxConditionDepth;

    public BoundedMsBuildExpressionEvaluator(
        IReadOnlyDictionary<string, BoundedMsBuildProperty> properties,
        Func<string, string, BoundedMsBuildExpansion> expandIntrinsic,
        Func<string, string, BoundedMsBuildExistsResult> exists,
        CancellationToken cancellationToken,
        int maxPropertyValueChars,
        int maxConditionDepth)
    {
        _properties = properties;
        _expandIntrinsic = expandIntrinsic;
        _exists = exists;
        _cancellationToken = cancellationToken;
        _maxPropertyValueChars = maxPropertyValueChars;
        _maxConditionDepth = maxConditionDepth;
    }

    public bool TryEvaluateCondition(string condition, string documentPath, out bool result,
        out string? error, string? unsetSelfProperty = null, int depth = 0)
    {
        CheckCancellation();
        result = false;
        error = null;
        if (depth > _maxConditionDepth)
        {
            error = "condition_depth_limit";
            return false;
        }
        if (!TryExpandProperties(condition, documentPath, unsetSelfProperty,
                out string expanded, out bool complete, out error))
            return false;
        if (!complete)
        {
            error = "condition_property_unresolved";
            return false;
        }
        expanded = expanded.Trim();
        if (expanded.Length == 0) return false;
        if (!HasBalancedConditionDelimiters(expanded)) return false;
        if (depth == 0 && !ValidateConditionSyntax(expanded, depth, out error)) return false;

        if (TrySplitLogical(expanded, "Or", out string left, out string right))
        {
            if (!TryEvaluateCondition(left, documentPath, out bool leftResult, out error,
                    unsetSelfProperty, depth + 1))
                return false;
            if (leftResult)
            {
                result = true;
                return true;
            }
            return TryEvaluateCondition(right, documentPath, out result, out error,
                unsetSelfProperty, depth + 1);
        }
        if (TrySplitLogical(expanded, "And", out left, out right))
        {
            if (!TryEvaluateCondition(left, documentPath, out bool leftResult, out error,
                    unsetSelfProperty, depth + 1))
                return false;
            if (!leftResult)
            {
                result = false;
                return true;
            }
            return TryEvaluateCondition(right, documentPath, out result, out error,
                unsetSelfProperty, depth + 1);
        }

        if (HasWrappingParentheses(expanded))
            return TryEvaluateCondition(expanded[1..^1], documentPath, out result, out error,
                unsetSelfProperty, depth + 1);
        if (expanded[0] == '!')
        {
            if (!TryEvaluateCondition(expanded[1..], documentPath, out bool inner, out error,
                    unsetSelfProperty, depth + 1))
                return false;
            result = !inner;
            return true;
        }

        if (TryParseExists(expanded, out string rawPath))
        {
            BoundedMsBuildExistsResult exists = _exists(documentPath, rawPath);
            if (!exists.Complete)
            {
                error = exists.Error;
                return false;
            }
            result = exists.Exists;
            return true;
        }

        if (TryParseConditionOperand(expanded, out string scalar) &&
            bool.TryParse(scalar, out result)) return true;
        if (!TryFindComparison(expanded, out left, out string op, out right)) return false;
        if (!TryParseConditionOperand(left, out left) ||
            !TryParseConditionOperand(right, out right)) return false;
        switch (op)
        {
            case "==":
                result = left.Equals(right, StringComparison.OrdinalIgnoreCase);
                return true;
            case "!=":
                result = !left.Equals(right, StringComparison.OrdinalIgnoreCase);
                return true;
            default:
                if (!TryCompareOrdered(left, right, out int comparison)) return false;
                result = op switch
                {
                    ">" => comparison > 0,
                    ">=" => comparison >= 0,
                    "<" => comparison < 0,
                    "<=" => comparison <= 0,
                    _ => false,
                };
                return true;
        }
    }

    public bool TryExpandProperties(string input, string documentPath, string? selfProperty,
        out string output, out bool complete, out string? error,
        bool allowItemReferences = false)
    {
        CheckCancellation();
        output = "";
        complete = false;
        error = null;
        if (input.Length > _maxPropertyValueChars)
        {
            error = "property_function_unsupported";
            return false;
        }

        BoundedMsBuildExpansion intrinsic = _expandIntrinsic(input, documentPath);
        if (intrinsic.Handled)
        {
            output = intrinsic.Value;
            complete = true;
            return true;
        }
        if (ContainsUnsupportedExpansion(input, allowItemReferences))
        {
            error = "property_function_unsupported";
            return false;
        }

        if (!TryExpandPropertyStringFunctions(input, out string scalarInput,
                out bool functionsComplete, out error))
            return false;

        var builder = new StringBuilder(scalarInput.Length);
        int cursor = 0;
        bool allComplete = functionsComplete;
        foreach (Match match in PropertyReference.Matches(scalarInput))
        {
            CheckCancellation();
            builder.Append(scalarInput, cursor, match.Index - cursor);
            string name = match.Groups["name"].Value;
            if (_properties.TryGetValue(name, out BoundedMsBuildProperty property))
            {
                builder.Append(property.Value);
                allComplete &= property.Complete;
            }
            else if (name.Equals(selfProperty, StringComparison.OrdinalIgnoreCase))
            {
                // An unset property is empty only for its own value and canonical self-default.
            }
            else
            {
                builder.Append(match.Value);
                allComplete = false;
            }
            cursor = match.Index + match.Length;
            if (builder.Length > _maxPropertyValueChars)
            {
                error = "property_value_limit";
                return false;
            }
        }
        builder.Append(scalarInput, cursor, scalarInput.Length - cursor);
        if (builder.Length > _maxPropertyValueChars)
        {
            error = "property_value_limit";
            return false;
        }
        output = builder.ToString();
        complete = allComplete &&
                   !output.Contains("$(", StringComparison.Ordinal) &&
                   !output.Contains("%(", StringComparison.Ordinal) &&
                   (allowItemReferences || !output.Contains("@(", StringComparison.Ordinal));
        return true;
    }

    internal static IEnumerable<string> ReferencedPropertyNames(string input)
    {
        foreach (Match match in SupportedPropertyMatches(input))
            yield return match.Groups["name"].Value;
    }

    private bool TryExpandPropertyStringFunctions(string input, out string output,
        out bool complete, out string? error)
    {
        CheckCancellation();
        error = null;
        MatchCollection matches = PropertyStartsWith.Matches(input);
        if (matches.Count == 0)
        {
            output = input;
            complete = true;
            return true;
        }

        var builder = new StringBuilder(input.Length);
        int cursor = 0;
        bool allComplete = true;
        foreach (Match match in matches)
        {
            CheckCancellation();
            builder.Append(input, cursor, match.Index - cursor);
            string name = match.Groups["name"].Value;
            if (_properties.TryGetValue(name, out BoundedMsBuildProperty property))
            {
                if (property.Complete)
                {
                    string receiver = UnescapeMsBuildScalar(property.Value);
                    string argument = UnescapeMsBuildScalar(match.Groups["argument"].Value);
                    bool startsWith = receiver.StartsWith(argument, StringComparison.Ordinal);
                    builder.Append(startsWith ? "True" : "False");
                }
                else
                {
                    builder.Append(match.Value);
                    allComplete = false;
                }
            }
            else
            {
                builder.Append(match.Value);
                allComplete = false;
            }
            cursor = match.Index + match.Length;
            if (builder.Length > _maxPropertyValueChars)
            {
                output = "";
                complete = false;
                error = "property_value_limit";
                return false;
            }
        }
        builder.Append(input, cursor, input.Length - cursor);
        if (builder.Length > _maxPropertyValueChars)
        {
            output = "";
            complete = false;
            error = "property_value_limit";
            return false;
        }
        output = builder.ToString();
        complete = allComplete;
        return true;
    }

    private bool ValidateConditionSyntax(string expression, int depth, out string? error)
    {
        CheckCancellation();
        error = null;
        if (depth > _maxConditionDepth)
        {
            error = "condition_depth_limit";
            return false;
        }
        expression = expression.Trim();
        if (expression.Length == 0 || !HasBalancedConditionDelimiters(expression)) return false;
        if (TrySplitLogical(expression, "Or", out string left, out string right) ||
            TrySplitLogical(expression, "And", out left, out right))
        {
            if (!ValidateConditionSyntax(left, depth + 1, out error)) return false;
            return ValidateConditionSyntax(right, depth + 1, out error);
        }
        if (HasWrappingParentheses(expression))
            return ValidateConditionSyntax(expression[1..^1], depth + 1, out error);
        if (expression[0] == '!')
            return ValidateConditionSyntax(expression[1..], depth + 1, out error);
        if (TryParseExists(expression, out _)) return true;
        if (TryParseConditionOperand(expression, out string scalar) &&
            bool.TryParse(scalar, out _)) return true;
        return TryFindComparison(expression, out left, out _, out right) &&
               TryParseConditionOperand(left, out _) && TryParseConditionOperand(right, out _);
    }

    private bool ContainsUnsupportedExpansion(string input, bool allowItemReferences)
    {
        CheckCancellation();
        if (!allowItemReferences && input.Contains("@(", StringComparison.Ordinal) ||
            input.Contains("%(", StringComparison.Ordinal)) return true;
        Match[] supported = SupportedPropertyMatches(input);
        int supportedIndex = 0;
        int cursor = 0;
        while ((cursor = input.IndexOf("$(", cursor, StringComparison.Ordinal)) >= 0)
        {
            CheckCancellation();
            while (supportedIndex < supported.Length &&
                   supported[supportedIndex].Index < cursor) supportedIndex++;
            if (supportedIndex >= supported.Length ||
                supported[supportedIndex].Index != cursor) return true;
            cursor += supported[supportedIndex].Length;
            supportedIndex++;
        }
        return false;
    }

    private static Match[] SupportedPropertyMatches(string input) =>
        PropertyReference.Matches(input).Cast<Match>()
            .Concat(PropertyStartsWith.Matches(input).Cast<Match>())
            .OrderBy(match => match.Index)
            .ThenByDescending(match => match.Length)
            .ToArray();

    private static string UnescapeMsBuildScalar(string value)
    {
        int firstEscape = value.IndexOf('%');
        if (firstEscape < 0) return value;

        var builder = new StringBuilder(value.Length);
        builder.Append(value, 0, firstEscape);
        for (int index = firstEscape; index < value.Length; index++)
        {
            if (value[index] == '%' && index + 2 < value.Length &&
                char.IsAsciiHexDigit(value[index + 1]) &&
                char.IsAsciiHexDigit(value[index + 2]) &&
                byte.TryParse(value.AsSpan(index + 1, 2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out byte unescaped))
            {
                builder.Append((char)unescaped);
                index += 2;
            }
            else
            {
                builder.Append(value[index]);
            }
        }
        return builder.ToString();
    }

    private static bool TryParseExists(string expression, out string path)
    {
        Match match = ExistsCondition.Match(expression);
        path = match.Success ? match.Groups["path"].Value : "";
        return match.Success;
    }

    private bool TrySplitLogical(string expression, string word, out string left,
        out string right)
    {
        CheckCancellation();
        left = right = "";
        char quote = '\0';
        int depth = 0;
        for (int i = 0; i <= expression.Length - word.Length; i++)
        {
            CheckCancellation();
            char ch = expression[i];
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
                continue;
            }
            if (ch is '\'' or '"') { quote = ch; continue; }
            if (ch == '(') { depth++; continue; }
            if (ch == ')') { depth--; continue; }
            if (depth != 0 || !expression.AsSpan(i, word.Length).Equals(word,
                    StringComparison.OrdinalIgnoreCase)) continue;
            bool before = i == 0 || char.IsWhiteSpace(expression[i - 1]) ||
                          expression[i - 1] == ')';
            int afterIndex = i + word.Length;
            bool after = afterIndex == expression.Length ||
                         char.IsWhiteSpace(expression[afterIndex]) ||
                         expression[afterIndex] == '(';
            if (!before || !after) continue;
            left = expression[..i];
            right = expression[afterIndex..];
            return left.Trim().Length > 0 && right.Trim().Length > 0;
        }
        return false;
    }

    private bool HasBalancedConditionDelimiters(string expression)
    {
        CheckCancellation();
        char quote = '\0';
        int depth = 0;
        foreach (char ch in expression)
        {
            CheckCancellation();
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
                continue;
            }
            if (ch is '\'' or '"') { quote = ch; continue; }
            if (ch == '(') { depth++; continue; }
            if (ch == ')')
            {
                if (depth == 0) return false;
                depth--;
            }
        }
        return quote == '\0' && depth == 0;
    }

    private bool HasWrappingParentheses(string expression)
    {
        CheckCancellation();
        if (expression.Length < 2 || expression[0] != '(' || expression[^1] != ')')
            return false;
        char quote = '\0';
        int depth = 0;
        for (int i = 0; i < expression.Length; i++)
        {
            CheckCancellation();
            char ch = expression[i];
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
                continue;
            }
            if (ch is '\'' or '"') { quote = ch; continue; }
            if (ch == '(') depth++;
            else if (ch == ')' && --depth == 0 && i != expression.Length - 1) return false;
        }
        return depth == 0;
    }

    private bool TryFindComparison(string expression, out string left, out string op,
        out string right)
    {
        CheckCancellation();
        left = op = right = "";
        char quote = '\0';
        int depth = 0;
        for (int i = 0; i < expression.Length; i++)
        {
            CheckCancellation();
            char ch = expression[i];
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
                continue;
            }
            if (ch is '\'' or '"') { quote = ch; continue; }
            if (ch == '(') { depth++; continue; }
            if (ch == ')') { depth--; continue; }
            if (depth != 0) continue;
            foreach (string candidate in new[] { "==", "!=", ">=", "<=", ">", "<" })
            {
                if (!expression.AsSpan(i).StartsWith(candidate, StringComparison.Ordinal))
                    continue;
                left = expression[..i];
                op = candidate;
                right = expression[(i + candidate.Length)..];
                return left.Trim().Length > 0 && right.Trim().Length > 0;
            }
        }
        return false;
    }

    private bool TryParseConditionOperand(string operand, out string value)
    {
        CheckCancellation();
        value = "";
        operand = operand.Trim();
        if (operand.Length == 0) return false;
        if (operand[0] is '\'' or '"')
        {
            char quote = operand[0];
            if (operand.Length < 2 || operand[^1] != quote) return false;
            string inner = operand[1..^1];
            if (inner.Contains(quote)) return false;
            value = inner;
            return true;
        }
        if (operand.Any(ch => char.IsWhiteSpace(ch) || ch is '\'' or '"' or
                              '(' or ')' or '=' or '<' or '>' or '!')) return false;
        value = operand;
        return true;
    }

    private static bool TryCompareOrdered(string left, string right, out int comparison)
    {
        comparison = 0;
        if (Version.TryParse(left, out Version? leftVersion) &&
            Version.TryParse(right, out Version? rightVersion))
        {
            comparison = leftVersion.CompareTo(rightVersion);
            return true;
        }
        if (decimal.TryParse(left, NumberStyles.Number, CultureInfo.InvariantCulture,
                out decimal leftNumber) &&
            decimal.TryParse(right, NumberStyles.Number, CultureInfo.InvariantCulture,
                out decimal rightNumber))
        {
            comparison = leftNumber.CompareTo(rightNumber);
            return true;
        }
        return false;
    }

    private void CheckCancellation() => _cancellationToken.ThrowIfCancellationRequested();
}
