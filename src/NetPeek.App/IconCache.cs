using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NetPeek
{
    // Stores values, not view objects. A recreated application row can always bind a cached icon.
    internal sealed class IconCache<T> : IDisposable where T : class
    {
        private sealed class Entry
        {
            internal T Value;
            internal TaskCompletionSource<T> Pending;
            internal DateTime RetryAfter;
            internal long Access;
        }
        private readonly object gate = new object();
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly int capacity;
        private readonly TimeSpan retryDelay;
        private readonly Func<DateTime> clock;
        private readonly Func<string, Task<T>> loader;
        private long access;
        private bool disposed;
        internal IconCache(int limit, TimeSpan retry, Func<DateTime> utcNow, Func<string, Task<T>> load)
        {
            if (limit < 1) throw new ArgumentOutOfRangeException("limit");
            capacity = limit; retryDelay = retry; clock = utcNow; loader = load;
        }
        internal int Count { get { lock (gate) { return entries.Count; } } }
        internal bool CanRequest(string path)
        {
            lock (gate) {
                if (disposed || String.IsNullOrEmpty(path)) return false;
                Entry entry;
                return !entries.TryGetValue(path, out entry) || (entry.Pending == null && (entry.Value != null || clock() >= entry.RetryAfter));
            }
        }
        internal Task<T> GetAsync(string path)
        {
            Entry entry;
            TaskCompletionSource<T> completion;
            lock (gate) {
                if (disposed || String.IsNullOrEmpty(path)) return Task.FromResult<T>(null);
                if (entries.TryGetValue(path, out entry)) {
                    entry.Access = ++access;
                    if (entry.Pending != null) return entry.Pending.Task;
                    if (entry.Value != null || clock() < entry.RetryAfter) return Task.FromResult(entry.Value);
                } else {
                    if (entries.Count >= capacity) {
                        var victim = entries.Where(p => p.Value.Pending == null).OrderBy(p => p.Value.Access).FirstOrDefault();
                        if (victim.Key == null) return Task.FromResult<T>(null); // Do not evict an in-flight load or exceed the bound.
                        entries.Remove(victim.Key);
                    }
                    entry = new Entry { Access = ++access }; entries.Add(path, entry);
                }
                completion = new TaskCompletionSource<T>(); entry.Pending = completion;
            }
            Load(path, entry, completion);
            return completion.Task;
        }
        private async void Load(string path, Entry entry, TaskCompletionSource<T> completion)
        {
            T value = null;
            try { value = await loader(path).ConfigureAwait(false); } catch (Exception) { /* Failed extraction is retryable, not a permanent attempted-path marker. */ }
            lock (gate) {
                if (disposed) value = null;
                else {
                    entry.Value = value; entry.Pending = null;
                    entry.RetryAfter = value == null ? clock().Add(retryDelay) : DateTime.MinValue;
                    entry.Access = ++access;
                }
            }
            completion.TrySetResult(value);
        }
        public void Dispose()
        {
            List<TaskCompletionSource<T>> pending;
            lock (gate) {
                if (disposed) return; disposed = true;
                pending = entries.Values.Where(e => e.Pending != null).Select(e => e.Pending).ToList(); entries.Clear();
            }
            foreach (var completion in pending) completion.TrySetResult(null);
        }
    }
}
