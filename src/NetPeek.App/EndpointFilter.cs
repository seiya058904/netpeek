using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace NetPeek
{
    // Display-only projection. Never remove observations from the connection store.
    public sealed class EndpointFilter
    {
        private readonly string query;
        private readonly int state, time;
        private readonly int exactField, exactValue; // 0: free text, 1: PID, 2: port.
        public string Error { get; private set; }
        public bool IsActive { get { return query.Length > 0 || state != 0 || time != 0; } }
        public EndpointFilter(string search, int stateIndex, int timeIndex)
        {
            query = (search ?? "").Trim(); state = stateIndex; time = timeIndex;
            if (query.StartsWith("pid:", StringComparison.OrdinalIgnoreCase)) exactField = 1;
            else if (query.StartsWith("port:", StringComparison.OrdinalIgnoreCase)) exactField = 2;
            if (exactField != 0) {
                string value = query.Substring(query.IndexOf(':') + 1).Trim();
                if (!Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out exactValue) ||
                    exactValue < 0 || exactField == 2 && exactValue > 65535)
                    Error = exactField == 1 ? "PID 需为 0–2147483647 的整数，例如 pid:1234。" : "端口需为 0–65535 的整数，例如 port:443（匹配本地或远端端口）。";
            }
        }
        private bool Contains(string text) { return (text ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0; }
        public bool Matches(ApplicationView app, ProcessView process, EndpointView endpoint)
        {
            if (Error != null) return false;
            if (time == 1 && !endpoint.IsCurrent || time == 2 && endpoint.IsCurrent) return false;
            // Historical state is the last observed state, never a claim of a live connection.
            var record = endpoint.Record;
            if (state == 1 && (record.Protocol != "TCP" || record.State != "ESTABLISHED") ||
                state == 2 && (record.Protocol != "TCP" || record.State != "LISTEN") ||
                state == 3 && record.Protocol != "UDP" ||
                state == 4 && (record.Protocol != "TCP" || record.State == "ESTABLISHED" || record.State == "LISTEN")) return false;
            if (exactField == 1) return process.Identity.ProcessId == exactValue;
            if (exactField == 2) return record.LocalPort == exactValue || endpoint.CanCopyRemote && record.RemotePort == exactValue;
            return query.Length == 0 || Contains(app.Name) || Contains(app.Path) ||
                process.Identity.ProcessId.ToString() == query || Contains(endpoint.Local) || Contains(endpoint.Remote) || Contains(endpoint.Transport);
        }
        public int Apply(ApplicationView app)
        {
            var visible = new List<ProcessView>();
            int current = 0, history = 0;
            foreach (var process in app.Processes) {
                var matches = process.Endpoints.Where(e => Matches(app, process, e)).ToList();
                process.ShowEndpoints(matches, IsActive);
                if (matches.Count > 0) visible.Add(process);
                current += matches.Count(e => e.IsCurrent); history += matches.Count(e => !e.IsCurrent);
            }
            app.ShowProcesses(visible, IsActive, current, history);
            return current + history;
        }
    }

    internal static class DisplayCollection
    {
        // Retained objects and order stay intact; no Reset notification on sampling.
        internal static void Reconcile<T>(ObservableCollection<T> target, IList<T> desired) where T : class
        {
            var retained = new HashSet<T>(desired);
            for (int i = target.Count - 1; i >= 0; i--) if (!retained.Contains(target[i])) target.RemoveAt(i);
            for (int i = 0; i < desired.Count; i++) {
                if (i < target.Count && Object.ReferenceEquals(target[i], desired[i])) continue;
                if (i == target.Count) { target.Add(desired[i]); continue; }
                int previous = target.IndexOf(desired[i]);
                if (previous >= 0) target.Move(previous, i); else target.Insert(i, desired[i]);
            }
        }
    }
}
