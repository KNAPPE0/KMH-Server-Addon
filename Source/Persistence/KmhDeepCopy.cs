using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace KMHServerAddon.Persistence
{
    // Stores deep-copy their state under their own lock and serialize the copy outside it, so JSON never shows a
    // half-applied change. Every instance field is copied, private too, so a later DTO field is never missed;
    // immutable types are shared, and shared references and cycles are preserved.
    internal static class KmhDeepCopy
    {
        public static T Of<T>(T value) where T : class
            => value == null ? null : (T)Copy(value, new Dictionary<object, object>(ReferenceEqualityComparer.Instance));

        // A store's live values as a new list of copies, sharing one reference map so cross-links survive.
        public static List<T> OfList<T>(IEnumerable<T> items) where T : class
        {
            Dictionary<object, object> seen = new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
            List<T> copy = new List<T>();
            foreach (T item in items) copy.Add((T)Copy(item, seen));
            return copy;
        }

        internal static bool IsImmutable(Type t)
        {
            if (t.IsPrimitive || t.IsEnum) return true;
            if (t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(DateTimeOffset)
                || t == typeof(TimeSpan) || t == typeof(Guid) || t == typeof(Uri) || t == typeof(Version) || t == typeof(DBNull))
                return true;
            Type nullable = Nullable.GetUnderlyingType(t);
            return nullable != null && IsImmutable(nullable);
        }

        private static readonly ConcurrentDictionary<Type, FieldInfo[]> Fields = new ConcurrentDictionary<Type, FieldInfo[]>();

        private static FieldInfo[] FieldsOf(Type t) => Fields.GetOrAdd(t, type =>
        {
            List<FieldInfo> all = new List<FieldInfo>();
            for (Type c = type; c != null && c != typeof(object); c = c.BaseType)
                foreach (FieldInfo f in c.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    all.Add(f);
            return all.ToArray();
        });

        private static object Copy(object src, Dictionary<object, object> seen)
        {
            if (src == null) return null;
            Type t = src.GetType();
            if (IsImmutable(t)) return src;
            if (t.IsValueType) return CopyStruct(src, t, seen);
            if (seen.TryGetValue(src, out object done)) return done;
            if (typeof(Delegate).IsAssignableFrom(t))
                throw new NotSupportedException($"{t.Name}: a delegate in persisted state cannot be copied");
            if (src is Newtonsoft.Json.Linq.JToken token) { object j = token.DeepClone(); seen[src] = j; return j; }

            if (t.IsArray)
            {
                Array a = (Array)src;
                Array copy = Array.CreateInstance(t.GetElementType(), a.Length);
                seen[src] = copy;
                if (IsImmutable(t.GetElementType())) Array.Copy(a, copy, a.Length);
                else for (int i = 0; i < a.Length; i++) copy.SetValue(Copy(a.GetValue(i), seen), i);
                return copy;
            }

            if (t.IsGenericType)
            {
                Type def = t.GetGenericTypeDefinition();
                if (def == typeof(List<>))
                {
                    IList list = (IList)src;
                    if (IsImmutable(t.GetGenericArguments()[0])) { object flat = Activator.CreateInstance(t, src); seen[src] = flat; return flat; }
                    IList copy = (IList)Activator.CreateInstance(t, list.Count);
                    seen[src] = copy;
                    foreach (object item in list) copy.Add(Copy(item, seen));
                    return copy;
                }
                if (def == typeof(Dictionary<,>) || def == typeof(SortedDictionary<,>) || def == typeof(ConcurrentDictionary<,>))
                {
                    // The comparer is kept: a case-insensitive dictionary must stay case-insensitive.
                    object comparer = t.GetProperty("Comparer")?.GetValue(src);
                    IDictionary copy = (IDictionary)(comparer != null
                        ? Activator.CreateInstance(t, comparer)
                        : Activator.CreateInstance(t));
                    seen[src] = copy;
                    foreach (DictionaryEntry e in (IDictionary)src) copy[Copy(e.Key, seen)] = Copy(e.Value, seen);
                    return copy;
                }
                if (def == typeof(HashSet<>) || def == typeof(SortedSet<>))
                {
                    object comparer = t.GetProperty("Comparer").GetValue(src);
                    object copy = Activator.CreateInstance(t, comparer);
                    seen[src] = copy;
                    MethodInfo add = t.GetMethod("Add");
                    foreach (object item in (IEnumerable)src) add.Invoke(copy, new[] { Copy(item, seen) });
                    return copy;
                }
                if (def == typeof(Queue<>) || def == typeof(Stack<>) || def == typeof(LinkedList<>))
                {
                    List<object> items = new List<object>();
                    foreach (object item in (IEnumerable)src) items.Add(item);
                    // A stack enumerates top first, so it is rebuilt from the bottom up.
                    if (def == typeof(Stack<>)) items.Reverse();
                    object copy = Activator.CreateInstance(t);
                    seen[src] = copy;
                    MethodInfo add = t.GetMethod(def == typeof(Queue<>) ? "Enqueue" : def == typeof(Stack<>) ? "Push" : "AddLast",
                                                 new[] { t.GetGenericArguments()[0] });
                    foreach (object item in items) add.Invoke(copy, new[] { Copy(item, seen) });
                    return copy;
                }
            }
            if (src is ICollection && !(src is IDictionary) && t.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
                throw new NotSupportedException($"{t.FullName}: this collection type has no deep copy");

            object obj = RuntimeHelpers.GetUninitializedObject(t);
            seen[src] = obj;
            CopierOf(t)(src, obj, seen);
            return obj;
        }

        private static readonly MethodInfo CopyMethod = typeof(KmhDeepCopy).GetMethod(nameof(Copy), BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly ConcurrentDictionary<Type, Action<object, object, Dictionary<object, object>>> Copiers
            = new ConcurrentDictionary<Type, Action<object, object, Dictionary<object, object>>>();

        private static Action<object, object, Dictionary<object, object>> CopierOf(Type t) => Copiers.GetOrAdd(t, BuildCopier);

        // A compiled field-by-field copy; an expression cannot assign readonly fields, so those go through reflection.
        private static Action<object, object, Dictionary<object, object>> BuildCopier(Type t)
        {
            List<FieldInfo> reflected = new List<FieldInfo>();
            Action<object, object, Dictionary<object, object>> compiled = null;
            try
            {
                ParameterExpression src = Expression.Parameter(typeof(object), "src");
                ParameterExpression dst = Expression.Parameter(typeof(object), "dst");
                ParameterExpression seen = Expression.Parameter(typeof(Dictionary<object, object>), "seen");
                ParameterExpression s = Expression.Variable(t, "s"), d = Expression.Variable(t, "d");
                List<Expression> body = new List<Expression> { Expression.Assign(s, Expression.Convert(src, t)), Expression.Assign(d, Expression.Convert(dst, t)) };
                foreach (FieldInfo f in FieldsOf(t))
                {
                    if (f.IsInitOnly) { reflected.Add(f); continue; }
                    Expression read = Expression.Field(s, f);
                    Expression value = IsImmutable(f.FieldType) ? read
                        : Expression.Convert(Expression.Call(CopyMethod, Expression.Convert(read, typeof(object)), seen), f.FieldType);
                    body.Add(Expression.Assign(Expression.Field(d, f), value));
                }
                if (body.Count > 2)
                    compiled = Expression.Lambda<Action<object, object, Dictionary<object, object>>>(
                        Expression.Block(new[] { s, d }, body), src, dst, seen).Compile();
            }
            catch (Exception)
            {
                reflected = new List<FieldInfo>(FieldsOf(t));
                compiled = null;
            }
            FieldInfo[] slow = reflected.ToArray();
            return (from, to, seen) =>
            {
                compiled?.Invoke(from, to, seen);
                foreach (FieldInfo f in slow)
                {
                    object v = f.GetValue(from);
                    f.SetValue(to, v == null || IsImmutable(f.FieldType) ? v : Copy(v, seen));
                }
            };
        }

        private static object CopyStruct(object src, Type t, Dictionary<object, object> seen)
        {
            FieldInfo[] fields = FieldsOf(t);
            bool plain = true;
            foreach (FieldInfo f in fields) if (!IsImmutable(f.FieldType)) { plain = false; break; }
            if (plain) return src;
            object boxed = RuntimeHelpers.GetUninitializedObject(t);
            foreach (FieldInfo f in fields)
            {
                object v = f.GetValue(src);
                f.SetValue(boxed, v == null || IsImmutable(f.FieldType) ? v : Copy(v, seen));
            }
            return boxed;
        }
    }
}
