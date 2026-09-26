using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DeadlockVmdlCompiler.Services;

public static class Ag2Sanitizer
{
    public const string ModelDoc41Header = "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc41:version{12fc9d44-453a-4ae4-b4d9-7e2ac0bbd4e0} -->";

    public static string RemoveModelDocNode(string content, string className)
    {
        while (true)
        {
            var match = Regex.Match(content, @"_class\s*=\s*""" + Regex.Escape(className) + @"""", RegexOptions.IgnoreCase);
            if (!match.Success) break;

            int classIdx = match.Index;

            int openBrace = -1;
            for (int i = classIdx - 1; i >= 0; i--)
            {
                if (content[i] == '{')
                {
                    openBrace = i;
                    break;
                }
                if (content[i] == '}')
                    break;
            }

            if (openBrace == -1) break;

            int depth = 0;
            int closeBrace = -1;
            for (int i = openBrace; i < content.Length; i++)
            {
                if (content[i] == '{') depth++;
                else if (content[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        closeBrace = i;
                        break;
                    }
                }
            }

            if (closeBrace == -1) break;

            int endIdx = closeBrace + 1;
            while (endIdx < content.Length && (content[endIdx] == ' ' || content[endIdx] == '\t'))
                endIdx++;
            if (endIdx < content.Length && content[endIdx] == ',')
                endIdx++;
            while (endIdx < content.Length && (content[endIdx] == '\r' || content[endIdx] == '\n'))
                endIdx++;

            int startIdx = openBrace;
            while (startIdx > 0 && (content[startIdx - 1] == ' ' || content[startIdx - 1] == '\t'))
                startIdx--;

            content = content.Remove(startIdx, endIdx - startIdx);
        }

        return content;
    }

    public static string DisableNodeByClass(string content, string className)
    {
        var pattern = @"_class\s*=\s*""" + Regex.Escape(className) + @"""";
        int searchStart = 0;

        while (true)
        {
            if (searchStart >= content.Length) break;
            var match = Regex.Match(content[searchStart..], pattern, RegexOptions.IgnoreCase);
            if (!match.Success) break;

            int classIdx = searchStart + match.Index;

            int openBrace = -1;
            for (int i = classIdx - 1; i >= 0; i--)
            {
                if (content[i] == '{')
                {
                    openBrace = i;
                    break;
                }
                if (content[i] == '}')
                    break;
            }

            if (openBrace == -1)
            {
                searchStart = classIdx + match.Length;
                continue;
            }

            int depth = 0;
            int closeBrace = -1;
            for (int i = openBrace; i < content.Length; i++)
            {
                if (content[i] == '{') depth++;
                else if (content[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        closeBrace = i;
                        break;
                    }
                }
            }

            if (closeBrace == -1)
            {
                searchStart = classIdx + match.Length;
                continue;
            }

            var block = content.Substring(openBrace, closeBrace - openBrace + 1);

            // Remove existing disabled = true/false
            block = Regex.Replace(block, @"^[ \t]*disabled\s*=\s*(true|false)[ \t]*[\r\n]*", "", RegexOptions.IgnoreCase | RegexOptions.Multiline);

            // Insert disabled = true right after _class line
            block = Regex.Replace(block,
                @"(_class\s*=\s*""" + Regex.Escape(className) + @""")",
                "$1\n\t\t\t\tdisabled = true",
                RegexOptions.IgnoreCase);

            content = content.Remove(openBrace, closeBrace - openBrace + 1).Insert(openBrace, block);
            searchStart = openBrace + block.Length;
        }

        return content;
    }

    public static (string CleanContent, List<string> Changes) SanitizeVmdlContent(string content, bool disableAnimationList = true)
    {
        var changes = new List<string>();

        // 1. Remove NmSkeletonList
        if (content.Contains("NmSkeletonList", StringComparison.OrdinalIgnoreCase))
        {
            content = RemoveModelDocNode(content, "NmSkeletonList");
            changes.Add("stripped NmSkeletonList");
        }

        // 2. Remove AnimGraph2List
        if (content.Contains("AnimGraph2List", StringComparison.OrdinalIgnoreCase))
        {
            content = RemoveModelDocNode(content, "AnimGraph2List");
            changes.Add("stripped AnimGraph2List");
        }

        // 3. Remove standalone DefaultAnimGraph2 or AnimGraph2
        if (content.Contains("DefaultAnimGraph2", StringComparison.OrdinalIgnoreCase))
        {
            content = RemoveModelDocNode(content, "DefaultAnimGraph2");
            changes.Add("stripped DefaultAnimGraph2");
        }
        if (content.Contains("AnimGraph2", StringComparison.OrdinalIgnoreCase))
        {
            content = RemoveModelDocNode(content, "AnimGraph2");
            changes.Add("stripped AnimGraph2");
        }

        // 4. Remove standalone NmSkeletonReference if any outside list
        if (content.Contains("NmSkeletonReference", StringComparison.OrdinalIgnoreCase))
        {
            content = RemoveModelDocNode(content, "NmSkeletonReference");
            changes.Add("stripped NmSkeletonReference");
        }

        // 5. Disable AnimationList to avoid missing anim clip warnings when opened in ModelDoc
        if (disableAnimationList && content.Contains("AnimationList", StringComparison.OrdinalIgnoreCase))
        {
            var disabled = DisableNodeByClass(content, "AnimationList");
            if (disabled != content)
            {
                content = disabled;
                changes.Add("set disabled = true on AnimationList");
            }
        }

        // 6. Disable EmptyAnimGraph / AnimGraph
        if (content.Contains("EmptyAnimGraph", StringComparison.OrdinalIgnoreCase) || content.Contains("AnimGraph", StringComparison.OrdinalIgnoreCase))
        {
            var disabled = DisableNodeByClass(DisableNodeByClass(content, "EmptyAnimGraph"), "AnimGraph");
            if (disabled != content)
            {
                content = disabled;
                changes.Add("disabled legacy anim graph nodes");
            }
        }

        return (content, changes);
    }

    public static async Task<(bool Success, string Message, List<string> Changes)> SanitizeVmdlFileAsync(string vmdlPath, bool createBackup = true)
    {
        var fullPath = Path.GetFullPath(vmdlPath);
        if (!File.Exists(fullPath))
        {
            return (false, $"File not found: {fullPath}", []);
        }

        if (createBackup)
        {
            try
            {
                var bak = fullPath + ".bak";
                File.Copy(fullPath, bak, overwrite: true);
            }
            catch { }
        }

        var content = await File.ReadAllTextAsync(fullPath);
        var (clean, changes) = SanitizeVmdlContent(content);

        await File.WriteAllTextAsync(fullPath, clean);

        var msg = changes.Count > 0
            ? $"CSDK12 ModelDoc compatibility applied: {string.Join(", ", changes)}"
            : "VMDL was already clean of AG2 nodes";

        return (true, msg, changes);
    }
}
