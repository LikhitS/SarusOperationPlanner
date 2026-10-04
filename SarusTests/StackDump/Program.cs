// Usage: StackDump <pid> <outfile>
using System;
using System.IO;
using System.Linq;
using Microsoft.Diagnostics.Runtime;

static class StackDump
{
    static int Main(string[] args)
    {
        int pid = int.Parse(args[0]);
        using (var target = DataTarget.AttachToProcess(pid, suspend: true))
        using (var w = new StreamWriter(args[1], false))
        {
            var runtime = target.ClrVersions.Single().CreateRuntime();
            w.WriteLine($"Snapshot {DateTime.Now:HH:mm:ss.fff} pid {pid}");
            foreach (var t in runtime.Threads.Where(t => t.IsAlive))
            {
                var frames = t.EnumerateStackTrace().Take(60).ToList();
                if (frames.Count == 0) continue;
                w.WriteLine();
                w.WriteLine($"--- managed thread {t.ManagedThreadId} (os {t.OSThreadId:x}) {(t.IsFinalizer ? "finalizer" : "")}");
                foreach (var f in frames)
                    w.WriteLine("    " + (f.Method?.Signature ?? f.FrameName ?? f.Kind.ToString()));
            }
        }
        return 0;
    }
}
