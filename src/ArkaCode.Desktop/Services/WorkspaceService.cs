using System.Text;
using System.IO;
using System.Linq;
using System.Collections.Generic;

namespace ArkaCode.Services;

public sealed class WorkspaceService
{
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase) { ".git", "bin", "obj", "node_modules", ".idea", ".vs", "dist", "build" };
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".cs", ".xaml", ".tsx", ".ts", ".js", ".jsx", ".json", ".kt", ".kts", ".java", ".py", ".go", ".rs", ".cpp", ".h", ".css", ".html", ".md", ".yml", ".yaml", ".xml", ".sql", ".sh", ".ps1" };

    public IReadOnlyList<string> ListFiles(string root) => Directory.Exists(root)
        ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(path => IsAllowed(root, path)).Select(path => Path.GetRelativePath(root, path)).OrderBy(path => path).Take(800).ToList()
        : [];

    public IReadOnlyList<WorkspaceFile> Snapshot(string root, int maxFiles = 70, int maxCharacters = 90000)
    {
        var result = new List<WorkspaceFile>();
        var remaining = maxCharacters;
        foreach (var relative in ListFiles(root).Take(maxFiles))
        {
            var path = Path.Combine(root, relative);
            var info = new FileInfo(path);
            if (info.Length > 150_000) continue;
            string content;
            try { content = File.ReadAllText(path); } catch { continue; }
            if (content.Length > remaining) content = content[..Math.Max(0, remaining)];
            result.Add(new WorkspaceFile(relative.Replace('\\', '/'), content));
            remaining -= content.Length;
            if (remaining <= 0) break;
        }
        return result;
    }

    public string Read(string root, string relative)
    {
        var path = ResolveSafe(root, relative);
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    public int Apply(string root, IEnumerable<FileOperation> operations)
    {
        var backupRoot = Path.Combine(root, ".arkacode", "backups", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var count = 0;
        foreach (var operation in operations.Where(op => op.Action.Equals("write", StringComparison.OrdinalIgnoreCase)))
        {
            var target = ResolveSafe(root, operation.Path);
            if (File.Exists(target))
            {
                var backup = Path.Combine(backupRoot, Path.GetRelativePath(root, target));
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(target, backup, false);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, operation.Content, new UTF8Encoding(false));
            count++;
        }
        return count;
    }

    private static bool IsAllowed(string root, string path)
    {
        var relativeParts = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !relativeParts.Any(Ignored.Contains) && Extensions.Contains(Path.GetExtension(path));
    }

    private static string ResolveSafe(string root, string relative)
    {
        var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(rootPath, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("مسیر پیشنهادی خارج از Workspace است.");
        return target;
    }
}
