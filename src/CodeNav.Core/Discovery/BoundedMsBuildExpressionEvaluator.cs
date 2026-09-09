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
        // Retain whole-condition admission, completeness and expansion-size checks, including
        // skipped operands. The expanded text is never parsed: values cannot supply operators.
        if (!TryExpandProperties(condition, documentPath, unsetSelfProperty,
                out _, out bool complete, out error))
            return false;
        if (!complete)
        {
            error = "condition_property_unresolved";
            return false;
        }
        condition = condition.Trim();
        if (condition.Length == 0) return false;
        if (!HasBalancedConditionDelimiters(condition)) return false;
        if (depth == 0 && !ValidateConditionSyntax(condition, depth, out error)) return false;

        if (TrySplitLogical(condition, "Or", out string left, out string right))
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
        if (TrySplitLogical(condition, "And", out left, out right))
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

        if (HasWrappingParentheses(condition))
            return TryEvaluateCondition(condition[1..^1], documentPath, out result, out error,
                unsetSelfProperty, depth + 1);
        if (condition[0] == '!')
        {
            if (!TryEvaluateCondition(condition[1..], documentPath, out bool inner, out error,
                    unsetSelfProperty, depth + 1))
                return false;
            result = !inner;
            return true;
        }

        if (TryParseExists(condition, out string rawPath))
        {
            if (!TryExpandConditionScalar(rawPath, documentPath, unsetSelfProperty,
                    out string path, out error)) return false;
            BoundedMsBuildExistsResult exists = _exists(documentPath, path);
            if (!exists.Complete)
            {
                error = exists.Error;
                return false;
            }
            result = exists.Exists;
            return true;
        }

        if (TryParseConditionOperand(condition, out string scalar))
            return TryExpandConditionScalar(scalar, documentPath, unsetSelfProperty,
                out scalar, out error) && bool.TryParse(scalar, out result);
        if (!TryFindComparison(condition, out left, out string op, out right)) return false;
        if (!TryParseConditionOperand(left, out left) ||
            !TryParseConditionOperand(right, out right)) return false;
        if (!TryExpandConditionScalar(left, documentPath, unsetSelfProperty, out left, out error) ||
            !TryExpandConditionScalar(right, documentPath, unsetSelfProperty, out right, out error)) return false;
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

    private bool TryExpandConditionScalar(string input, string documentPath, string? unsetSelfProperty,
        out string value, out string? error)
    {
        if (!TryExpandProperties(input, documentPath, unsetSelfProperty,
                out value, out bool complete, out error)) return false;
        if (!complete) error = "condition_property_unresolved";
        return complete;
    }

    /// <summary>
    /// Proves a condition false independently of mutable properties. The caller must only admit
    /// properties whose assignments it prohibits. Unknown atoms retain the ordering guard.
    /// This reads raw syntax and compares scalar values, never reparsing a property's contents as
    /// condition syntax, and never invokes intrinsic expansion or Exists (including its capture).
    /// </summary>
    public bool IsConditionInvariantFalse(string condition, Func<string, bool> isInvariantProperty)
    {
        CheckCancellation();
        // The F# caller's ShouldProcess already enforces its stricter raw condition-character
        // bound. This shared guard retains the expansion bound; it does not replace that admission.
        if (condition.Length > _maxPropertyValueChars ||
            !ValidateConditionSyntax(condition, 0, out _)) return false;
        return Visit(condition, 0) == false;

        bool? Visit(string expression, int depth)
        {
            CheckCancellation();
            if (depth > _maxConditionDepth) return null;
            expression = expression.Trim();
            if (TrySplitLogical(expression, "Or", out string left, out string right))
            {
                bool? a = Visit(left, depth + 1);
                bool? b = Visit(right, depth + 1);
                return a == true || b == true ? true : a == false && b == false ? false : null;
            }
            if (TrySplitLogical(expression, "And", out left, out right))
            {
                bool? a = Visit(left, depth + 1);
                bool? b = Visit(right, depth + 1);
                return a == false || b == false ? false : a == true && b == true ? true : null;
            }
            if (HasWrappingParentheses(expression))
                return Visit(expression[1..^1], depth + 1);
            if (expression[0] == '!') return !Visit(expression[1..], depth + 1);
            if (TryReadOperand(expression, out string scalar) && bool.TryParse(scalar, out bool value))
                return value;
            if (!TryFindComparison(expression, out left, out string op, out right) ||
                op is not ("==" or "!=") ||
                !TryReadOperand(left, out left) || !TryReadOperand(right, out right)) return null;
            bool equal = left.Equals(right, StringComparison.OrdinalIgnoreCase);
            return op == "==" ? equal : !equal;
        }

        bool TryReadOperand(string operand, out string value)
        {
            value = "";
            if (!TryParseConditionOperand(operand, out string raw)) return false;
            Match match = PropertyReference.Match(raw);
            if (match.Success && match.Index == 0 && match.Length == raw.Length)
            {
                string name = match.Groups["name"].Value;
                if (!isInvariantProperty(name) || !_properties.TryGetValue(name, out var property) ||
                    !property.Complete) return false;
                value = property.Value;
                return true;
            }
            // Concatenation, functions, escaping and item/metadata references are not proof atoms.
            if (raw.IndexOfAny(['$', '@', '%']) >= 0) return false;
            value = raw;
            return true;
        }
    }

    public bool IsSelfDefaultCondition(string condition, string propertyName)
    {
        // The expansion exemption applies to the whole condition. Every occurrence of the
        // missing self property must therefore be an exact empty-equality operand, not merely
        // one occurrence somewhere in a larger expression. Other properties gain no exemption.
        return Visit(condition, 0, out bool hasSelf) && hasSelf;

        bool Visit(string expression, int depth, out bool hasSelf)
        {
            CheckCancellation();
            hasSelf = false;
            if (depth > _maxConditionDepth) return false;
            expression = expression.Trim();
            if (expression.Length == 0) return false;
            if (TrySplitLogical(expression, "Or", out string left, out string right) ||
                TrySplitLogical(expression, "And", out left, out right))
            {
                bool leftValid = Visit(left, depth + 1, out bool leftSelf);
                bool rightValid = Visit(right, depth + 1, out bool rightSelf);
                hasSelf = leftSelf || rightSelf;
                return leftValid && rightValid;
            }
            if (HasWrappingParentheses(expression))
                return Visit(expression[1..^1], depth + 1, out hasSelf);
            if (expression[0] == '!')
                return Visit(expression[1..], depth + 1, out hasSelf);

            hasSelf = ReferencedPropertyNames(expression).Any(name =>
                name.Equals(propertyName, StringComparison.OrdinalIgnoreCase));
            if (!hasSelf) return true; // Normal evaluation still validates syntax/completeness.
            if (!TryFindComparison(expression, out left, out string op, out right) ||
                op != "==" || !TryParseConditionOperand(left, out string leftValue) ||
                !TryParseConditionOperand(right, out string rightValue))
                return false;
            string self = "$(" + propertyName + ")";
            return leftValue.Equals(self, StringComparison.OrdinalIgnoreCase) && rightValue.Length == 0 ||
                   rightValue.Equals(self, StringComparison.OrdinalIgnoreCase) && leftValue.Length == 0;
        }
    }

    public bool TryExpandProperties(string input, string documentPath, string? selfProperty,
        out string output, out bool complete, out string? error,
        bool allowItemReferences = false,
        bool allowPropertyStringFunctions = true,
        bool preserveOpaqueItemAndMetadataReferences = false,
        bool stopOnUnresolvedProperty = false,
        Func<string, bool>? tryReservePropertyExpansion = null,
        Func<int, bool>? tryReserveExpandedValue = null)
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
        if (ContainsUnsupportedExpansion(input, allowItemReferences,
                allowPropertyStringFunctions, preserveOpaqueItemAndMetadataReferences))
        {
            error = "property_function_unsupported";
            return false;
        }

        string scalarInput = input;
        bool functionsComplete = true;
        if (allowPropertyStringFunctions &&
            !TryExpandPropertyStringFunctions(input, out scalarInput,
                out functionsComplete, out error, stopOnUnresolvedProperty,
                tryReservePropertyExpansion))
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
                if (stopOnUnresolvedProperty && !property.Complete) return false;
                if (tryReservePropertyExpansion is not null &&
                    !tryReservePropertyExpansion(name))
                {
                    error = "property_value_limit";
                    return false;
                }
                builder.Append(property.Value);
                allComplete &= property.Complete;
            }
            else if (name.Equals(selfProperty, StringComparison.OrdinalIgnoreCase))
            {
                // An unset property is empty only for its own value and canonical self-default.
            }
            else
            {
                if (stopOnUnresolvedProperty) return false;
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
                   (preserveOpaqueItemAndMetadataReferences ||
                    !output.Contains("%(", StringComparison.Ordinal)) &&
                   (preserveOpaqueItemAndMetadataReferences || allowItemReferences ||
                    !output.Contains("@(", StringComparison.Ordinal));
        if (complete && tryReserveExpandedValue is not null &&
            !tryReserveExpandedValue(output.Length))
        {
            error = "property_value_limit";
            return false;
        }
        return true;
    }

    internal static IEnumerable<string> ReferencedPropertyNames(string input)
    {
        foreach (Match match in SupportedPropertyMatches(input))
            yield return match.Groups["name"].Value;
    }

    private bool TryExpandPropertyStringFunctions(string input, out string output,
        out bool complete, out string? error, bool stopOnUnresolvedProperty,
        Func<string, bool>? tryReservePropertyExpansion)
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
                    if (tryReservePropertyExpansion is not null &&
                        !tryReservePropertyExpansion(name))
                    {
                        output = "";
                        complete = false;
                        error = "property_value_limit";
                        return false;
                    }
                    string receiver = UnescapeMsBuildScalar(property.Value);
                    string argument = UnescapeMsBuildScalar(match.Groups["argument"].Value);
                    bool startsWith = receiver.StartsWith(argument, StringComparison.Ordinal);
                    builder.Append(startsWith ? "True" : "False");
                }
                else
                {
                    if (stopOnUnresolvedProperty)
                    {
                        output = "";
                        complete = false;
                        return false;
                    }
                    builder.Append(match.Value);
                    allComplete = false;
                }
            }
            else
            {
                if (stopOnUnresolvedProperty)
                {
                    output = "";
                    complete = false;
                    return false;
                }
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
            (bool.TryParse(scalar, out _) || SupportedPropertyMatches(scalar).Length > 0)) return true;
        return TryFindComparison(expression, out left, out _, out right) &&
               TryParseConditionOperand(left, out _) && TryParseConditionOperand(right, out _);
    }

    private bool ContainsUnsupportedExpansion(string input, bool allowItemReferences,
        bool allowPropertyStringFunctions, bool preserveOpaqueItemAndMetadataReferences)
    {
        CheckCancellation();
        if (!preserveOpaqueItemAndMetadataReferences &&
            (!allowItemReferences && input.Contains("@(", StringComparison.Ordinal) ||
             input.Contains("%(", StringComparison.Ordinal))) return true;
        Match[] supported = SupportedPropertyMatches(input, allowPropertyStringFunctions);
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

    private static Match[] SupportedPropertyMatches(string input,
        bool includePropertyStringFunctions = true) =>
        PropertyReference.Matches(input).Cast<Match>()
            .Concat(includePropertyStringFunctions
                ? PropertyStartsWith.Matches(input).Cast<Match>()
                : [])
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

    private bool TryParseExists(string expression, out string path)
    {
        path = "";
        int open = expression.IndexOf('(');
        if (open < 0 || !expression[..open].TrimEnd().Equals("Exists", StringComparison.OrdinalIgnoreCase) ||
            expression[^1] != ')') return false;
        string operand = expression[(open + 1)..^1].Trim();
        return operand.Length > 0 && operand[0] is '\'' or '"' &&
            !operand.Contains('\r') && !operand.Contains('\n') && TryParseConditionOperand(operand, out path);
    }

    private static int PropertyExpansionLengthAt(string expression, int index)
    {
        if (expression[index] != '$' || index + 1 >= expression.Length || expression[index + 1] != '(')
            return 0;
        Match match = PropertyReference.Match(expression, index);
        if (match.Success && match.Index == index) return match.Length;
        match = PropertyStartsWith.Match(expression, index);
        return match.Success && match.Index == index ? match.Length : 0;
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
            int expansionLength = PropertyExpansionLengthAt(expression, i);
            if (expansionLength > 0) { i += expansionLength - 1; continue; }
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
        for (int i = 0; i < expression.Length; i++)
        {
            CheckCancellation();
            int expansionLength = PropertyExpansionLengthAt(expression, i);
            if (expansionLength > 0) { i += expansionLength - 1; continue; }
            char ch = expression[i];
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
            int expansionLength = PropertyExpansionLengthAt(expression, i);
            if (expansionLength > 0) { i += expansionLength - 1; continue; }
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
            int expansionLength = PropertyExpansionLengthAt(expression, i);
            if (expansionLength > 0) { i += expansionLength - 1; continue; }
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
            for (int i = 0; i < inner.Length; i++)
            {
                CheckCancellation();
                int expansionLength = PropertyExpansionLengthAt(inner, i);
                if (expansionLength > 0) { i += expansionLength - 1; continue; }
                if (inner[i] == quote) return false;
            }
            value = inner;
            return true;
        }
        for (int i = 0; i < operand.Length; i++)
        {
            CheckCancellation();
            int expansionLength = PropertyExpansionLengthAt(operand, i);
            if (expansionLength > 0) { i += expansionLength - 1; continue; }
            if (char.IsWhiteSpace(operand[i]) || operand[i] is '\'' or '"' or
                '(' or ')' or '=' or '<' or '>' or '!') return false;
        }
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
