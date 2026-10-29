using System;
using System.Reflection;
using System.Threading.Tasks;

// Reflection forwarding only: all cache state and behavior remain in the final EXE.
internal sealed class ArtifactIconCache<T> : IDisposable where T : class
{
    private readonly Type type;
    private readonly object target;
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    internal ArtifactIconCache(int limit, TimeSpan retry, Func<DateTime> now, Func<string, Task<T>> load)
    {
        type = typeof(NetPeek.ConnectionStore).Assembly.GetType("NetPeek.IconCache`1", true).MakeGenericType(typeof(T));
        target = Activator.CreateInstance(type, Members, null, new object[] { limit, retry, now, load }, null);
    }
    internal int Count { get { return (int)type.GetProperty("Count", Members).GetValue(target, null); } }
    internal bool CanRequest(string path) { return (bool)type.GetMethod("CanRequest", Members).Invoke(target, new object[] { path }); }
    internal Task<T> GetAsync(string path) { return (Task<T>)type.GetMethod("GetAsync", Members).Invoke(target, new object[] { path }); }
    public void Dispose() { ((IDisposable)target).Dispose(); }
}
