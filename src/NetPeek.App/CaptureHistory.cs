using System;
using System.Collections.Generic;
using System.Linq;
using NetPeek.Core;

namespace NetPeek
{
    public sealed class Observation
    {
        public EndpointRecord Record { get; set; }
        public DateTime LastSeenUtc { get; set; }
    }

    // Capture-thread history is independent of UI visibility and refresh/pause state.
    // No WPF references: snapshots can be tested without creating a window.
    public sealed class CaptureHistory
    {
        public const int HistorySeconds = 60;
        public const int HistoryLimit = 12000;
        private readonly object gate = new object();
        private readonly Dictionary<string, Observation> observations = new Dictionary<string, Observation>();
        private bool truncated;
        private DateTime truncatedUntilUtc = DateTime.MinValue;
        public bool Truncated { get { lock (gate) { return truncated; } } }

        public void Accept(NetworkSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.IsComplete) return;
            lock (gate)
            {
                HashSet<string> current = new HashSet<string>();
                foreach (EndpointRecord record in snapshot.Endpoints)
                {
                    current.Add(record.Key);
                    observations[record.Key] = new Observation { Record = CopyRecord(record), LastSeenUtc = snapshot.CapturedAtUtc };
                }
                // 不可核验身份仅显示当前快照，避免猜测历史归属。
                foreach (string key in observations.Where(p => !current.Contains(p.Key) &&
                    (p.Value.Record.Process.StartTimeUtcTicks <= 0 || (snapshot.CapturedAtUtc - p.Value.LastSeenUtc).TotalSeconds > HistorySeconds)).Select(p => p.Key).ToList()) observations.Remove(key);
                var history = observations.Where(p => !current.Contains(p.Key)).OrderByDescending(p => p.Value.LastSeenUtc).ToList();
                foreach (var entry in history.Skip(HistoryLimit)) {
                    DateTime expires = entry.Value.LastSeenUtc.AddSeconds(HistorySeconds);
                    if (expires > truncatedUntilUtc) truncatedUntilUtc = expires;
                    observations.Remove(entry.Key);
                }
                // A later small/empty sample cannot restore records already discarded.
                truncated = truncatedUntilUtc != DateTime.MinValue && snapshot.CapturedAtUtc <= truncatedUntilUtc;
            }
        }

        public List<Observation> Snapshot()
        {
            lock (gate)
            {
                return observations.Values.Select(o => new Observation { Record = CopyRecord(o.Record), LastSeenUtc = o.LastSeenUtc }).ToList();
            }
        }

        private static EndpointRecord CopyRecord(EndpointRecord record)
        {
            ProcessIdentity process = record.Process;
            return new EndpointRecord {
                Key = record.Key, Protocol = record.Protocol, AddressFamily = record.AddressFamily,
                LocalAddress = record.LocalAddress, LocalPort = record.LocalPort,
                RemoteAddress = record.RemoteAddress, RemotePort = record.RemotePort, State = record.State,
                Process = new ProcessIdentity { ProcessId = process.ProcessId, StartTimeUtcTicks = process.StartTimeUtcTicks,
                    InstanceId = process.InstanceId, AppKey = process.AppKey, Path = process.Path, Name = process.Name, AccessNote = process.AccessNote }
            };
        }
    }
}
