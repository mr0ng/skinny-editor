using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace StereoKitEditor.ProjectSystem;

internal enum EditorLaunchHookPlanStatus
{
    AlreadyPresent,
    Ready,
    Ambiguous,
}

internal sealed record EditorLaunchHookPlan(
    EditorLaunchHookPlanStatus Status,
    string? SourcePath,
    string? OriginalText,
    string? ProposedText,
    string Message);

internal static class CSharpEntryPointHookPlanner
{
    private static readonly string[] ExcludedDirectories =
        [".git", ".skinny", "bin", "obj", "packages", "SKinnyEditor"];

    public static EditorLaunchHookPlan Analyze(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))
                               ?? throw new InvalidDataException("The project path has no parent directory.");
        var candidates = new List<EntryPointCandidate>();
        var existingHookPath = default(string);

        foreach (var sourcePath in EnumerateSourceFiles(projectDirectory))
        {
            string source;
            try
            {
                source = ReadUtf8Source(sourcePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or InvalidDataException)
            {
                return new(
                    EditorLaunchHookPlanStatus.Ambiguous,
                    null,
                    null,
                    null,
                    $"Could not safely inspect the possible entry point '{sourcePath}': {exception.Message}");
            }

            var root = CSharpSyntaxTree.ParseText(
                    source,
                    new CSharpParseOptions(LanguageVersion.Preview),
                    sourcePath)
                .GetCompilationUnitRoot();
            var globalStatements = root.Members.OfType<GlobalStatementSyntax>().ToArray();
            if (globalStatements.Length > 0)
            {
                if (ContainsEditorLaunchHook(globalStatements))
                {
                    existingHookPath = sourcePath;
                }
                else if (TryCreateTopLevelCandidate(sourcePath, source, globalStatements, out var topLevel))
                {
                    candidates.Add(topLevel);
                }
                else
                {
                    candidates.Add(new(sourcePath, source, null, "Top-level statements use a return shape that cannot be updated safely."));
                }
            }

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                         .Where(method => method.Identifier.ValueText == "Main"
                                          && method.Modifiers.Any(SyntaxKind.StaticKeyword)))
            {
                if (ContainsEditorLaunchHook(method))
                {
                    existingHookPath = sourcePath;
                    continue;
                }

                candidates.Add(TryCreateMethodCandidate(sourcePath, source, method, out var candidate)
                    ? candidate
                    : new(sourcePath, source, null, "A Main method was found, but its signature or body cannot be updated safely."));
            }
        }

        if (existingHookPath is not null)
        {
            return new(
                EditorLaunchHookPlanStatus.AlreadyPresent,
                existingHookPath,
                null,
                null,
                "The application entry point already routes explicit editor launches to SKinny.");
        }

        if (candidates.Count != 1 || candidates[0].ProposedText is null)
        {
            var reason = candidates.Count switch
            {
                0 => "No conventional C# entry point was found without evaluating the project.",
                1 => candidates[0].Failure,
                _ => $"Found {candidates.Count} possible C# entry points without evaluating project conditions.",
            };
            return new(EditorLaunchHookPlanStatus.Ambiguous, null, null, null, reason);
        }

        var selected = candidates[0];
        return new(
            EditorLaunchHookPlanStatus.Ready,
            selected.SourcePath,
            selected.OriginalText,
            selected.ProposedText,
            $"SKinny can safely add an editor-launch guard to {Path.GetFileName(selected.SourcePath)}.");
    }

    private static bool TryCreateMethodCandidate(
        string sourcePath,
        string source,
        MethodDeclarationSyntax method,
        out EntryPointCandidate candidate)
    {
        candidate = default!;
        if (method.Body is null || method.ExpressionBody is not null || method.TypeParameterList is not null)
        {
            return false;
        }

        string argumentsExpression;
        if (method.ParameterList.Parameters.Count == 0)
        {
            argumentsExpression = "System.Environment.GetCommandLineArgs()[1..]";
        }
        else if (method.ParameterList.Parameters.Count == 1)
        {
            var parameter = method.ParameterList.Parameters[0];
            var parameterType = NormalizeTypeName(parameter.Type?.ToString() ?? string.Empty);
            if (parameterType is not ("string[]" or "String[]" or "System.String[]"))
            {
                return false;
            }

            argumentsExpression = parameter.Identifier.Text;
        }
        else
        {
            return false;
        }

        var returnType = NormalizeTypeName(method.ReturnType.ToString());
        var isAsync = method.Modifiers.Any(SyntaxKind.AsyncKeyword);
        var exitCodeName = CreateUniqueIdentifier(method, "skinnyEditorExitCode");
        string[] completion;
        if (returnType is "void")
        {
            completion = [$"System.Environment.ExitCode = {exitCodeName};", "return;"];
        }
        else if (returnType is "int" or "System.Int32")
        {
            completion = [$"return {exitCodeName};"];
        }
        else if (returnType is "Task" or "System.Threading.Tasks.Task")
        {
            completion = isAsync
                ? [$"System.Environment.ExitCode = {exitCodeName};", "return;"]
                : [$"System.Environment.ExitCode = {exitCodeName};", "return System.Threading.Tasks.Task.CompletedTask;"];
        }
        else if (returnType is "Task<int>" or "Task<System.Int32>"
                 or "System.Threading.Tasks.Task<int>" or "System.Threading.Tasks.Task<System.Int32>")
        {
            completion = isAsync
                ? [$"return {exitCodeName};"]
                : [$"return System.Threading.Tasks.Task.FromResult({exitCodeName});"];
        }
        else
        {
            return false;
        }

        var hook = CreateHook(argumentsExpression, exitCodeName, completion);
        candidate = new(
            sourcePath,
            source,
            InsertAtStartOfBlock(source, method, method.Body, hook),
            string.Empty);
        return true;
    }

    private static bool TryCreateTopLevelCandidate(
        string sourcePath,
        string source,
        IReadOnlyList<GlobalStatementSyntax> statements,
        out EntryPointCandidate candidate)
    {
        candidate = default!;
        var returns = statements.SelectMany(statement => statement.DescendantNodesAndSelf())
            .OfType<ReturnStatementSyntax>()
            .Where(IsTopLevelReturn)
            .ToArray();
        if (returns.Any(statement => statement.Expression is not null)
            && returns.Any(statement => statement.Expression is null))
        {
            return false;
        }

        var exitCodeName = CreateUniqueIdentifier(statements, "skinnyEditorExitCode");
        var completion = returns.Any(statement => statement.Expression is not null)
            ? new[] { $"return {exitCodeName};" }
            : new[] { $"System.Environment.ExitCode = {exitCodeName};", "return;" };
        var hook = CreateHook(
            "System.Environment.GetCommandLineArgs()[1..]",
            exitCodeName,
            completion);
        var first = statements[0];
        var lineStart = source.LastIndexOf('\n', Math.Max(0, first.SpanStart - 1));
        var indentation = source[(lineStart < 0 ? 0 : lineStart + 1)..first.SpanStart];
        if (indentation.Any(character => !char.IsWhiteSpace(character)))
        {
            indentation = string.Empty;
        }

        var newLine = DetectNewLine(source);
        var formattedHook = Indent(hook, indentation, newLine);
        candidate = new(
            sourcePath,
            source,
            source.Insert(first.SpanStart, formattedHook + newLine + indentation),
            string.Empty);
        return true;
    }

    private static bool IsTopLevelReturn(ReturnStatementSyntax statement)
    {
        foreach (var ancestor in statement.Ancestors())
        {
            if (ancestor is GlobalStatementSyntax)
            {
                return true;
            }

            if (ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
            {
                return false;
            }
        }

        return false;
    }

    private static string InsertAtStartOfBlock(
        string source,
        MethodDeclarationSyntax method,
        BlockSyntax body,
        string hook)
    {
        var newLine = DetectNewLine(source);
        var methodIndentation = GetLineIndentation(source, method.SpanStart);
        var statementIndentation = body.Statements.Count > 0
            ? GetLineIndentation(source, body.Statements[0].SpanStart)
            : methodIndentation + DetectIndentUnit(source);
        if (statementIndentation.Length <= methodIndentation.Length)
        {
            statementIndentation = methodIndentation + DetectIndentUnit(source);
        }

        var insertionPosition = body.OpenBraceToken.Span.End;
        var lineEnd = source.IndexOfAny(['\r', '\n'], insertionPosition);
        if (lineEnd < 0)
        {
            lineEnd = source.Length;
        }

        var sameLineRemainder = source[insertionPosition..lineEnd];
        var hasSameLineContent = sameLineRemainder.Any(character => !char.IsWhiteSpace(character));
        var nextNonWhitespace = source.AsSpan(insertionPosition)
            .TrimStart();
        var closesImmediately = !nextNonWhitespace.IsEmpty && nextNonWhitespace[0] == '}';
        var suffix = hasSameLineContent
            ? newLine + (closesImmediately ? methodIndentation : statementIndentation)
            : string.Empty;
        return source.Insert(
            insertionPosition,
            newLine + Indent(hook, statementIndentation, newLine) + suffix);
    }

    private static string CreateHook(
        string argumentsExpression,
        string exitCodeName,
        IReadOnlyList<string> completion)
    {
        var lines = new List<string>
        {
            $"if (SKinnyOnboarding.EditorEntryPoint.TryRun({argumentsExpression}, out var {exitCodeName}))",
            "{",
        };
        lines.AddRange(completion.Select(line => "    " + line));
        lines.Add("}");
        return string.Join("\n", lines);
    }

    private static bool ContainsEditorLaunchHook(SyntaxNode scope) =>
        ContainsEditorLaunchHook([scope]);

    private static bool ContainsEditorLaunchHook(IEnumerable<SyntaxNode> scopes)
    {
        var invocations = scopes.SelectMany(scope => scope.DescendantNodesAndSelf())
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => !invocation.Ancestors().Any(ancestor =>
                ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            .Select(invocation => invocation.Expression.ToString()
                .Replace("global::", string.Empty, StringComparison.Ordinal)
                .Replace(" ", string.Empty, StringComparison.Ordinal))
            .ToArray();
        return invocations.Any(name => name.EndsWith(
                   "SKinnyOnboarding.EditorEntryPoint.TryRun",
                   StringComparison.Ordinal))
               || invocations.Any(name => name.EndsWith(
                   "EditorRuntimeHost.IsEditorLaunch",
                   StringComparison.Ordinal))
               && invocations.Any(name => name.EndsWith(
                   "EditorRuntimeHost.Run",
                   StringComparison.Ordinal));
    }

    private static string CreateUniqueIdentifier(SyntaxNode node, string baseName) =>
        CreateUniqueIdentifier(node.DescendantTokens(), baseName);

    private static string CreateUniqueIdentifier(IEnumerable<SyntaxNode> nodes, string baseName) =>
        CreateUniqueIdentifier(nodes.SelectMany(node => node.DescendantTokens()), baseName);

    private static string CreateUniqueIdentifier(IEnumerable<SyntaxToken> tokens, string baseName)
    {
        var names = tokens.Where(token => token.IsKind(SyntaxKind.IdentifierToken))
            .Select(token => token.ValueText)
            .ToHashSet(StringComparer.Ordinal);
        var candidate = baseName;
        for (var suffix = 2; names.Contains(candidate); suffix++)
        {
            candidate = baseName + suffix;
        }

        return candidate;
    }

    private static string NormalizeTypeName(string value) => value
        .Replace("global::", string.Empty, StringComparison.Ordinal)
        .Replace(" ", string.Empty, StringComparison.Ordinal)
        .Replace("?", string.Empty, StringComparison.Ordinal);

    private static string GetLineIndentation(string source, int position)
    {
        var lineStart = source.LastIndexOf('\n', Math.Max(0, position - 1));
        var prefix = source[(lineStart < 0 ? 0 : lineStart + 1)..position];
        return new(prefix.TakeWhile(char.IsWhiteSpace).ToArray());
    }

    private static string DetectIndentUnit(string source)
    {
        foreach (var line in source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var indentation = new string(line.TakeWhile(char.IsWhiteSpace).ToArray());
            if (indentation.Contains('\t'))
            {
                return "\t";
            }

            if (indentation.Length is > 0 and <= 8)
            {
                return indentation;
            }
        }

        return "    ";
    }

    private static string DetectNewLine(string source) =>
        source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static string ReadUtf8Source(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        try
        {
            return new UTF8Encoding(false, true).GetString(
                bytes.AsSpan(hasBom ? Encoding.UTF8.Preamble.Length : 0));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                $"'{path}' is not UTF-8 and will not be rewritten automatically.",
                exception);
        }
    }

    private static string Indent(string value, string indentation, string newLine) =>
        string.Join(newLine, value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => indentation + line));

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (!File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
                {
                    yield return file;
                }
            }

            foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (!ExcludedDirectories.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase)
                    && !File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
                {
                    pending.Push(child);
                }
            }
        }
    }

    private sealed record EntryPointCandidate(
        string SourcePath,
        string OriginalText,
        string? ProposedText,
        string Failure);
}
