using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DeadlockVmdlCompiler.Services;

/// <summary>Edits ModelDoc node arrays without relying on optional RootNode fields or line layout.</summary>
internal static class ModelDocAg2Editor
{
    private readonly record struct Node(int Start, int End, string ClassName);

    internal static string SetNodeDisabled(string content, string className, bool disabled)
    {
        var classPattern = new Regex(@"\b_class\s*=\s*""" + Regex.Escape(className) + @"""",
            RegexOptions.IgnoreCase);
        var disabledPattern = new Regex(@"\bdisabled\s*=\s*(true|false)\b", RegexOptions.IgnoreCase);
        var value = disabled ? "true" : "false";
        var searchStart = 0;

        while (searchStart < content.Length)
        {
            var classMatch = classPattern.Match(content, searchStart);
            if (!classMatch.Success) break;
            searchStart = classMatch.Index + classMatch.Length;
            if (IsIgnoredAt(content, classMatch.Index)) continue;

            var nodeStart = FindEnclosingOpenBrace(content, classMatch.Index);
            if (nodeStart < 0) continue;
            var nodeClose = FindMatching(content, nodeStart, '{', '}');
            if (nodeClose < 0) continue;
            var node = new Node(nodeStart, nodeClose + 1, className);
            if (!IsDirectField(content, node, classMatch.Index)) continue;

            var block = content.Substring(node.Start, node.End - node.Start);
            var fields = disabledPattern.Matches(block).Cast<Match>()
                .Where(field => !IsIgnoredAt(content, node.Start + field.Index) &&
                                IsDirectField(content, node, node.Start + field.Index))
                .ToList();
            if (fields.Count > 0)
            {
                foreach (var field in fields.AsEnumerable().Reverse())
                {
                    var oldValue = field.Groups[1];
                    block = block.Remove(oldValue.Index, oldValue.Length).Insert(oldValue.Index, value);
                }
            }
            else
            {
                var lineStart = content.LastIndexOf('\n', classMatch.Index) + 1;
                var indent = content.Substring(lineStart, classMatch.Index - lineStart);
                if (indent.Any(c => c is not (' ' or '\t'))) indent = "\t";
                var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                block = block.Insert(classMatch.Index + classMatch.Length - node.Start,
                    newline + indent + "disabled = " + value);
            }

            content = content.Remove(node.Start, node.End - node.Start).Insert(node.Start, block);
            searchStart = node.Start + block.Length;
        }

        return content;
    }

    private static int FindEnclosingOpenBrace(string content, int position)
    {
        var openBraces = new Stack<int>();
        for (var i = 0; i < position; i++)
        {
            var skipped = SkipIgnored(content, i);
            if (skipped != i) { i = skipped - 1; continue; }
            if (content[i] == '{') openBraces.Push(i);
            else if (content[i] == '}' && openBraces.Count > 0) openBraces.Pop();
        }
        return openBraces.Count > 0 ? openBraces.Peek() : -1;
    }

    public static (string Content, List<string> Changes) Upgrade(
        string content, string skelPath, string graphPath, string? uiGraphPath,
        bool addSkel, bool addGraph, bool addUiGraph, bool upgradeHeader, string header)
    {
        var changes = new List<string>();
        if (upgradeHeader)
        {
            var lineEnd = content.IndexOfAny(['\r', '\n']);
            var firstLine = lineEnd < 0 ? content : content[..lineEnd];
            if (firstLine.Contains("format:modeldoc", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(firstLine.Trim(), header, StringComparison.Ordinal))
            {
                content = header + (lineEnd < 0 ? string.Empty : content[lineEnd..]);
                changes.Add("Upgraded header format to modeldoc41");
            }
        }

        if ((addSkel && !ValidPath(skelPath)) ||
            (addGraph && !ValidPath(graphPath)) ||
            (addUiGraph && !ValidPath(uiGraphPath)))
        {
            changes.Add("Error: Selected AG2 reference path is empty or invalid; select a hero preset.");
            return (content, changes);
        }

        if (!addSkel && !addGraph && !addUiGraph)
            return (content, changes);

        if (!TryGetRootChildren(content, out var rootOpen, out var rootClose))
        {
            changes.Add("Error: Could not locate rootNode children array.");
            return (content, changes);
        }

        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        if (addSkel)
        {
            content = WrapStandaloneNodes(content, rootOpen, rootClose, "NmSkeletonList",
                ["NmSkeletonReference"], newline, out var wrappedSkeleton);
            if (wrappedSkeleton)
            {
                changes.Add("Moved standalone NmSkeletonReference into NmSkeletonList");
                TryGetRootChildren(content, out rootOpen, out rootClose);
            }
            var list = FindNode(content, rootOpen, rootClose, "NmSkeletonList");
            if (list is null)
            {
                content = InsertIntoArray(content, rootOpen, rootClose, ListNode("NmSkeletonList", [ReferenceNode("NmSkeletonReference", skelPath)], newline), newline);
                changes.Add("Injected NmSkeletonList node");
            }
            else if (!TryGetChildren(content, list.Value, out var childOpen, out var childClose))
            {
                changes.Add("Error: NmSkeletonList has no children array.");
                return (content, changes);
            }
            else
            {
                var reference = FindNode(content, childOpen, childClose, "NmSkeletonReference");
                if (reference is null)
                {
                    content = InsertIntoArray(content, childOpen, childClose, ReferenceNode("NmSkeletonReference", skelPath), newline);
                    changes.Add("Injected NmSkeletonReference node");
                }
                else
                {
                    content = SetFilename(content, reference.Value, skelPath, newline, out var changed);
                    if (changed) changes.Add("Updated NmSkeletonReference path");
                }
            }
        }

        if (addGraph || addUiGraph)
        {
            if (!TryGetRootChildren(content, out rootOpen, out rootClose))
            {
                changes.Add("Error: Could not locate rootNode children array after editing skeleton.");
                return (content, changes);
            }

            content = WrapStandaloneNodes(content, rootOpen, rootClose, "AnimGraph2List",
                ["DefaultAnimGraph2", "AnimGraph2"], newline, out var wrappedGraphs);
            if (wrappedGraphs)
            {
                changes.Add("Moved standalone graph nodes into AnimGraph2List");
                TryGetRootChildren(content, out rootOpen, out rootClose);
            }
            var list = FindNode(content, rootOpen, rootClose, "AnimGraph2List");
            if (list is null)
            {
                var children = new List<string>();
                if (addGraph) children.Add(ReferenceNode("DefaultAnimGraph2", graphPath));
                if (addUiGraph) children.Add(ReferenceNode("AnimGraph2", uiGraphPath!, "ui"));
                content = InsertIntoArray(content, rootOpen, rootClose, ListNode("AnimGraph2List", children, newline), newline);
                changes.Add("Injected AnimGraph2List node");
            }
            else
            {
                if (!TryGetChildren(content, list.Value, out var childOpen, out var childClose))
                {
                    changes.Add("Error: AnimGraph2List has no children array.");
                    return (content, changes);
                }

                if (addGraph)
                {
                    var reference = FindNode(content, childOpen, childClose, "DefaultAnimGraph2");
                    if (reference is null)
                    {
                        content = InsertIntoArray(content, childOpen, childClose, ReferenceNode("DefaultAnimGraph2", graphPath), newline);
                        changes.Add("Injected DefaultAnimGraph2 node");
                    }
                    else
                    {
                        content = SetFilename(content, reference.Value, graphPath, newline, out var changed);
                        if (changed) changes.Add("Updated DefaultAnimGraph2 path");
                    }
                }

                if (addUiGraph)
                {
                    if (!TryGetRootChildren(content, out rootOpen, out rootClose) ||
                        (list = FindNode(content, rootOpen, rootClose, "AnimGraph2List")) is null ||
                        !TryGetChildren(content, list.Value, out childOpen, out childClose))
                    {
                        changes.Add("Error: Could not reopen AnimGraph2List after editing default graph.");
                        return (content, changes);
                    }
                    var reference = EnumerateNodes(content, childOpen, childClose).FirstOrDefault(n =>
                        n.ClassName.Equals("AnimGraph2", StringComparison.OrdinalIgnoreCase) &&
                        FieldValue(content, n, "name").Equals("ui", StringComparison.OrdinalIgnoreCase));
                    if (reference == default)
                    {
                        content = InsertIntoArray(content, childOpen, childClose, ReferenceNode("AnimGraph2", uiGraphPath!, "ui"), newline);
                        changes.Add("Injected ui AnimGraph2 node");
                    }
                    else
                    {
                        content = SetFilename(content, reference, uiGraphPath!, newline, out var changed);
                        if (changed) changes.Add("Updated ui AnimGraph2 path");
                    }
                }
            }
        }

        return (content, changes);
    }

    private static bool ValidPath(string? path) => !string.IsNullOrWhiteSpace(path) &&
        !path.Any(c => c is '"' or '\r' or '\n' or '\0');

    private static string ReferenceNode(string className, string path, string? name = null) =>
        "{\n_class = \"" + className + "\"\n" +
        (name is null ? string.Empty : "name = \"" + name + "\"\n") +
        "filename = \"" + path.Replace('\\', '/') + "\"\n}";

    private static string ListNode(string className, IEnumerable<string> children, string newline) =>
        "{" + newline + "_class = \"" + className + "\"" + newline +
        "children =" + newline + "[" + newline +
        string.Join("," + newline, children) + newline + "]" + newline + "}";

    private static string WrapStandaloneNodes(string content, int open, int close, string listClass,
        string[] childClasses, string newline, out bool wrapped)
    {
        wrapped = false;
        if (FindNode(content, open, close, listClass) is not null) return content;
        var standalone = EnumerateNodes(content, open, close)
            .Where(node => childClasses.Any(name => name.Equals(node.ClassName, StringComparison.OrdinalIgnoreCase))).ToList();
        if (standalone.Count == 0) return content;

        var children = standalone.Select(node => content.Substring(node.Start, node.End - node.Start)).ToList();
        foreach (var node in standalone.AsEnumerable().Reverse())
            content = RemoveFromArray(content, open, node);

        if (!TryGetRootChildren(content, out open, out close)) return content;
        wrapped = true;
        return InsertIntoArray(content, open, close, ListNode(listClass, children, newline), newline);
    }

    private static string RemoveFromArray(string content, int open, Node node)
    {
        var start = node.Start;
        var end = node.End;
        while (end < content.Length && char.IsWhiteSpace(content[end])) end++;
        if (end < content.Length && content[end] == ',') end++;
        else
        {
            while (start > open + 1 && char.IsWhiteSpace(content[start - 1])) start--;
            if (start > open + 1 && content[start - 1] == ',') start--;
        }
        return content.Remove(start, end - start);
    }

    private static bool TryGetRootChildren(string content, out int open, out int close)
    {
        open = close = -1;
        var root = Regex.Matches(content, @"\brootNode\s*=\s*\{", RegexOptions.IgnoreCase)
            .Cast<Match>().FirstOrDefault(match => !IsIgnoredAt(content, match.Index));
        if (root is null) return false;
        var rootOpen = content.IndexOf('{', root.Index);
        var rootClose = FindMatching(content, rootOpen, '{', '}');
        if (rootClose < 0) return false;
        return TryGetChildren(content, new Node(rootOpen, rootClose + 1, "RootNode"), out open, out close);
    }

    private static bool TryGetChildren(string content, Node node, out int open, out int close)
    {
        open = close = -1;
        var block = content.Substring(node.Start, node.End - node.Start);
        foreach (Match match in Regex.Matches(block, @"\bchildren\s*=\s*\[", RegexOptions.IgnoreCase))
        {
            var matchIndex = node.Start + match.Index;
            if (IsIgnoredAt(content, matchIndex) || !IsDirectField(content, node, matchIndex)) continue;
            open = content.IndexOf('[', matchIndex);
            close = FindMatching(content, open, '[', ']');
            return close >= 0 && close < node.End;
        }
        return false;
    }

    private static Node? FindNode(string content, int open, int close, string className)
    {
        foreach (var node in EnumerateNodes(content, open, close))
            if (node.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase)) return node;
        return null;
    }

    private static IEnumerable<Node> EnumerateNodes(string content, int open, int close)
    {
        for (var i = open + 1; i < close; i++)
        {
            var skipped = SkipIgnored(content, i);
            if (skipped != i) { i = skipped - 1; continue; }
            if (content[i] != '{') continue;
            var end = FindMatching(content, i, '{', '}');
            if (end < 0 || end >= close) yield break;
            var node = new Node(i, end + 1, string.Empty);
            yield return node with { ClassName = FieldValue(content, node, "_class") };
            i = end;
        }
    }

    private static string FieldValue(string content, Node node, string field)
    {
        var pattern = @"\b" + Regex.Escape(field) + @"\s*=\s*""([^""]*)""";
        var block = content.Substring(node.Start, node.End - node.Start);
        foreach (Match match in Regex.Matches(block, pattern, RegexOptions.IgnoreCase))
        {
            if (!IsIgnoredAt(content, node.Start + match.Index) &&
                IsDirectField(content, node, node.Start + match.Index)) return match.Groups[1].Value;
        }
        return string.Empty;
    }

    private static bool IsDirectField(string content, Node node, int fieldIndex)
    {
        var braces = 0;
        var brackets = 0;
        for (var i = node.Start; i < fieldIndex; i++)
        {
            var skipped = SkipIgnored(content, i);
            if (skipped != i)
            {
                if (skipped > fieldIndex) return false;
                i = skipped - 1;
                continue;
            }
            if (content[i] == '{') braces++;
            else if (content[i] == '}') braces--;
            else if (content[i] == '[') brackets++;
            else if (content[i] == ']') brackets--;
        }
        return braces == 1 && brackets == 0;
    }

    private static string SetFilename(string content, Node node, string path, string newline, out bool changed)
    {
        var block = content.Substring(node.Start, node.End - node.Start);
        var match = Regex.Matches(block, @"\bfilename\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase)
            .Cast<Match>().FirstOrDefault(candidate => !IsIgnoredAt(block, candidate.Index));
        var normalized = path.Replace('\\', '/');
        if (match is not null && match.Groups[1].Value.Equals(normalized, StringComparison.OrdinalIgnoreCase))
        {
            changed = false;
            return content;
        }
        changed = true;
        if (match is not null)
            block = block.Remove(match.Groups[1].Index, match.Groups[1].Length).Insert(match.Groups[1].Index, normalized);
        else
            block = block.Insert(1, newline + "filename = \"" + normalized + "\"");
        return content.Remove(node.Start, node.End - node.Start).Insert(node.Start, block);
    }

    private static string InsertIntoArray(string content, int open, int close, string node, string newline)
    {
        var insertAt = close;
        while (insertAt > open + 1 && char.IsWhiteSpace(content[insertAt - 1])) insertAt--;
        var separator = insertAt > open + 1 && content[insertAt - 1] != ',' ? "," : string.Empty;
        return content.Insert(insertAt, separator + newline + node + ",");
    }

    private static int FindMatching(string content, int open, char openChar, char closeChar)
    {
        if (open < 0 || open >= content.Length || content[open] != openChar) return -1;
        var depth = 0;
        for (var i = open; i < content.Length; i++)
        {
            var skipped = SkipIgnored(content, i);
            if (skipped != i) { i = skipped - 1; continue; }
            if (content[i] == openChar) depth++;
            else if (content[i] == closeChar && --depth == 0) return i;
        }
        return -1;
    }

    private static int SkipIgnored(string content, int i)
    {
        if (content[i] == '"')
        {
            if (i + 2 < content.Length && content.AsSpan(i, 3).SequenceEqual("\"\"\""))
            {
                var end = content.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                return end < 0 ? content.Length : end + 3;
            }
            for (var j = i + 1; j < content.Length; j++)
            {
                if (content[j] == '\\') { j++; continue; }
                if (content[j] == '"') return j + 1;
            }
            return content.Length;
        }
        if (content[i] == '/' && i + 1 < content.Length && content[i + 1] == '/')
        {
            var end = content.IndexOf('\n', i + 2);
            return end < 0 ? content.Length : end + 1;
        }
        if (content[i] == '/' && i + 1 < content.Length && content[i + 1] == '*')
        {
            var end = content.IndexOf("*/", i + 2, StringComparison.Ordinal);
            return end < 0 ? content.Length : end + 2;
        }
        return i;
    }

    private static bool IsIgnoredAt(string content, int position)
    {
        for (var i = 0; i <= position && i < content.Length; i++)
        {
            var next = SkipIgnored(content, i);
            if (next <= i) continue;
            if (next > position) return true;
            i = next - 1;
        }
        return false;
    }
}
