using System.Diagnostics.CodeAnalysis;

namespace TicketFlow;

/// <summary>
/// Internal C# 15 multi-key extension indexers and C# 14 extension members for tuple-keyed dictionaries,
/// including a zero-defaulting counter specialization for <see cref="long"/> accumulators.
/// </summary>
internal static class TupleDictionaryExtensions
{
    extension<T1, T2, TValue>(Dictionary<(T1, T2), TValue> dictionary)
    {
        public TValue this[T1 key1, T2 key2]
        {
            get => dictionary[(key1, key2)];
            set => dictionary[(key1, key2)] = value;
        }

        public bool TryGetValue(T1 key1, T2 key2, [MaybeNullWhen(false)] out TValue value) =>
            dictionary.TryGetValue((key1, key2), out value);

        public TValue? GetValueOrDefault(T1 key1, T2 key2) =>
            dictionary.GetValueOrDefault((key1, key2));

        public bool ContainsKey(T1 key1, T2 key2) =>
            dictionary.ContainsKey((key1, key2));

        public bool Remove(T1 key1, T2 key2) =>
            dictionary.Remove((key1, key2));

        public bool Remove(T1 key1, T2 key2, [MaybeNullWhen(false)] out TValue value) =>
            dictionary.Remove((key1, key2), out value);

        public void Add(T1 key1, T2 key2, TValue value) =>
            dictionary.Add((key1, key2), value);
    }

    extension<T1, T2, T3, TValue>(Dictionary<(T1, T2, T3), TValue> dictionary)
    {
        public TValue this[T1 key1, T2 key2, T3 key3]
        {
            get => dictionary[(key1, key2, key3)];
            set => dictionary[(key1, key2, key3)] = value;
        }

        public bool TryGetValue(T1 key1, T2 key2, T3 key3, [MaybeNullWhen(false)] out TValue value) =>
            dictionary.TryGetValue((key1, key2, key3), out value);

        public TValue? GetValueOrDefault(T1 key1, T2 key2, T3 key3) =>
            dictionary.GetValueOrDefault((key1, key2, key3));

        public bool ContainsKey(T1 key1, T2 key2, T3 key3) =>
            dictionary.ContainsKey((key1, key2, key3));

        public bool Remove(T1 key1, T2 key2, T3 key3) =>
            dictionary.Remove((key1, key2, key3));

        public bool Remove(T1 key1, T2 key2, T3 key3, [MaybeNullWhen(false)] out TValue value) =>
            dictionary.Remove((key1, key2, key3), out value);

        public void Add(T1 key1, T2 key2, T3 key3, TValue value) =>
            dictionary.Add((key1, key2, key3), value);
    }

    extension<T1, T2>(Dictionary<(T1, T2), long> counters)
    {
        public long this[T1 key1, T2 key2]
        {
            get => counters.GetValueOrDefault((key1, key2));
            set => counters[(key1, key2)] = value;
        }
    }
}
