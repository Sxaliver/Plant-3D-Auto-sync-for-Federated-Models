using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace IWMF.PlantModelSync
{
    internal static class ReferenceNamePolicy
    {
        internal static string Digest(string source)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Paths.Canonical(source).ToUpperInvariant()))).Replace("-", "").Substring(0, 12);
        }
        internal static bool IsGenerated(string name) => Regex.IsMatch(name ?? "", @"^PMS_[0-9A-F]{12}(?:_[1-9][0-9]*)?$", RegexOptions.IgnoreCase);
        internal static bool IsOwned(string name, string source)
        {
            if (!IsGenerated(name)) return false;
            var prefix = "PMS_" + Digest(source);
            return string.Equals(name, prefix, StringComparison.OrdinalIgnoreCase) || name.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase);
        }
        internal static string Choose(string source, string safeStem, IEnumerable<string> occupied)
        {
            var names = new HashSet<string>(occupied, StringComparer.OrdinalIgnoreCase);
            var stem = string.IsNullOrWhiteSpace(safeStem) ? "Xref" : safeStem;
            if (stem.Length > 235) stem = stem.Substring(0, char.IsHighSurrogate(stem[234]) ? 234 : 235);
            if (!names.Contains(stem)) return stem;
            var disambiguated = stem + "__" + Digest(source);
            var result = disambiguated; var n = 1;
            while (names.Contains(result)) result = disambiguated + "_" + n++;
            return result;
        }
    }
}
