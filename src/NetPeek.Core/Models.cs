using System;
using System.Collections.Generic;

namespace NetPeek.Core
{
    public class ProcessIdentity
    {
        public int ProcessId { get; set; }
        public long StartTimeUtcTicks { get; set; }
        public string InstanceId { get; set; }
        public string AppKey { get; set; }
        public string Path { get; set; }
        public string Name { get; set; }
        public string AccessNote { get; set; }
        public ProcessIdentity() { InstanceId = "unknown"; AppKey = "unknown"; Name = "未知进程"; }
    }

    public class EndpointRecord
    {
        public string Key { get; set; }
        public string Protocol { get; set; }
        public string AddressFamily { get; set; }
        public string LocalAddress { get; set; }
        public int LocalPort { get; set; }
        public string RemoteAddress { get; set; }
        public int RemotePort { get; set; }
        public string State { get; set; }
        public ProcessIdentity Process { get; set; }
        public EndpointRecord() { Key = ""; Protocol = ""; AddressFamily = ""; LocalAddress = ""; State = ""; Process = new ProcessIdentity(); }
    }

    public class NetworkSnapshot
    {
        public DateTime CapturedAtUtc { get; set; }
        public List<EndpointRecord> Endpoints { get; set; }
        public List<string> Warnings { get; set; }
        public bool IsComplete { get; set; }
        public NetworkSnapshot() { CapturedAtUtc = DateTime.UtcNow; Endpoints = new List<EndpointRecord>(); Warnings = new List<string>(); }
    }
}
