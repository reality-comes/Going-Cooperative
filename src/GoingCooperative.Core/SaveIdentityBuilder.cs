using System;
using System.IO;
using System.Linq;

namespace GoingCooperative.Core
{
    public static class SaveIdentityBuilder
    {
        public static SaveIdentity FromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Path is required.", nameof(path));
            }

            if (File.Exists(path))
            {
                return FromFile(path);
            }

            if (Directory.Exists(path))
            {
                return FromDirectory(path);
            }

            throw new FileNotFoundException("Save path not found.", path);
        }

        public static SaveIdentity FromFile(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var info = new FileInfo(fullPath);
            var hash = new DeterminismHash();
            hash.Add("file");
            hash.Add(info.Name);
            hash.Add(info.Length);
            AddFileBytes(fullPath, ref hash);
            return new SaveIdentity(info.Name, hash.Value, info.Length);
        }

        public static SaveIdentity FromDirectory(string path)
        {
            var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = new DirectoryInfo(fullPath);
            var files = Directory.GetFiles(fullPath, "*", SearchOption.AllDirectories)
                .OrderBy(file => RelativePath(fullPath, file), StringComparer.Ordinal)
                .ToArray();

            var hash = new DeterminismHash();
            long totalBytes = 0;
            hash.Add("directory");
            hash.Add(root.Name);
            hash.Add(files.Length);

            foreach (var file in files)
            {
                var relative = RelativePath(fullPath, file);
                var info = new FileInfo(file);
                totalBytes += info.Length;
                hash.Add(relative.Replace('\\', '/'));
                hash.Add(info.Length);
                AddFileBytes(file, ref hash);
            }

            return new SaveIdentity(root.Name, hash.Value, totalBytes);
        }

        private static void AddFileBytes(string path, ref DeterminismHash hash)
        {
            var buffer = new byte[64 * 1024];
            using (var stream = File.OpenRead(path))
            {
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    hash.AddBytes(buffer, read);
                }
            }
        }

        private static string RelativePath(string root, string path)
        {
            var rootUri = new Uri(AppendDirectorySeparator(Path.GetFullPath(root)));
            var pathUri = new Uri(Path.GetFullPath(path));
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString())
                .Replace('/', Path.DirectorySeparatorChar);
        }

        private static string AppendDirectorySeparator(string path)
        {
            if (path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                || path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                return path;
            }

            return path + Path.DirectorySeparatorChar;
        }
    }
}
