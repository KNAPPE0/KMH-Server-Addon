using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace KMHServerAddon.Maintenance
{
    // Reflective and shared, because a per-store list of fields drifts the same way the copies it checks do.
    internal static class KmhCopyCoverage
    {
        // src must have every field set to something non-default, or a dropped field looks identical to a copied one.
        public static List<(string, bool, string)> Check(string label, object src, object copy,
                                                         params string[] derivedOrStripped)
        {
            var r = new List<(string, bool, string)>();
            var skip = new HashSet<string>(derivedOrStripped ?? Array.Empty<string>(), StringComparer.Ordinal);
            if (src == null || copy == null)
            {
                r.Add(($"{label}: copy coverage could run", false, "null fixture"));
                return r;
            }

            var lost = new List<string>();
            var shared = new List<string>();
            var emptied = new List<string>();
            int scalars = 0, collections = 0;

            foreach (PropertyInfo p in src.GetType().GetProperties())
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0 || skip.Contains(p.Name)) continue;
                object a = p.GetValue(src), b = p.GetValue(copy);
                bool isCollection = p.PropertyType != typeof(string)
                                    && typeof(IEnumerable).IsAssignableFrom(p.PropertyType);

                if (isCollection)
                {
                    if (a == null) continue;
                    collections++;
                    if (ReferenceEquals(a, b)) shared.Add(p.Name);
                    if (Count(a) > 0 && Count(b) == 0) emptied.Add(p.Name);
                }
                else if (p.PropertyType.IsClass && p.PropertyType != typeof(string))
                {
                    // For a nested DTO the instance must differ, or the copy shares state with its source.
                    if (a == null) continue;
                    collections++;
                    if (b == null) lost.Add(p.Name);
                    else if (ReferenceEquals(a, b)) shared.Add(p.Name);
                }
                else
                {
                    if (!p.CanWrite) continue;
                    scalars++;
                    if (!Equals(a, b)) lost.Add(p.Name);
                }
            }

            // Neither dictionary constructor inherits the source's comparer, so losing OrdinalIgnoreCase turns a lookup into a miss.
            var caseLost = new List<string>();
            foreach (PropertyInfo p in src.GetType().GetProperties())
            {
                if (!p.CanRead || skip.Contains(p.Name)) continue;
                if (!(p.GetValue(src) is IDictionary a) || !(p.GetValue(copy) is IDictionary b)) continue;
                foreach (object k in a.Keys)
                {
                    if (!(k is string key) || key.Length == 0) break;
                    string flipped = key.ToUpperInvariant() == key ? key.ToLowerInvariant() : key.ToUpperInvariant();
                    if (flipped == key) break;
                    if (a.Contains(flipped) && !b.Contains(flipped)) caseLost.Add(p.Name);
                    break;
                }
            }
            r.Add(($"{label}: the snapshot copy keeps its key comparers",
                   caseLost.Count == 0,
                   caseLost.Count == 0 ? "" : $"case-insensitive lookup lost: {string.Join(", ", caseLost)}"));

            // A fixture that set nothing would pass every check above without proving anything.
            r.Add(($"{label}: copy coverage has something to check",
                   scalars > 0, $"{scalars} scalar(s), {collections} nested/collection field(s)"));
            r.Add(($"{label}: the snapshot copy carries every field",
                   lost.Count == 0, lost.Count == 0 ? "" : $"dropped: {string.Join(", ", lost)}"));
            r.Add(($"{label}: the snapshot copy shares no reference with the store",
                   shared.Count == 0, shared.Count == 0 ? "" : $"shared: {string.Join(", ", shared)}"));
            r.Add(($"{label}: the snapshot copy carries every collection's contents",
                   emptied.Count == 0, emptied.Count == 0 ? "" : $"arrived empty: {string.Join(", ", emptied)}"));
            return r;
        }

        private static int Count(object o)
        {
            int n = 0;
            foreach (object _ in (IEnumerable)o) n++;
            return n;
        }
    }
}
