using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;

[assembly: AssemblyVersion("9.0.0.0")]
[assembly: AssemblyFileVersion("9.0.0.0")]
[assembly: AssemblyProduct("LaTale Garden updater test fixture")]
[DataContract] public class FixtureJob { [DataMember] public string Nonce; [DataMember] public string Version; }
[DataContract] public class FixtureReceipt
{
    [DataMember] public string Nonce; [DataMember] public string Version; [DataMember] public int Pid; [DataMember] public long Ticks;
}
public static class UpdateFixture
{
    public static int Main(string[] args)
    {
#if FAIL_STARTUP
        return 42;
#else
        if (args.Length != 2 || args[0] != "--updated") return 2;
        Guid id; if (!Guid.TryParseExact(args[1], "N", out id)) return 3;
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaTaleGarden", "Updates", "Jobs", args[1]);
        FixtureJob job;
        using (var input = File.OpenRead(Path.Combine(directory, "job.json"))) job = (FixtureJob)new DataContractJsonSerializer(typeof(FixtureJob)).ReadObject(input);
        using (var process = Process.GetCurrentProcess())
        using (var output = File.Create(Path.Combine(directory, "healthy.json")))
            new DataContractJsonSerializer(typeof(FixtureReceipt)).WriteObject(output, new FixtureReceipt { Nonce = job.Nonce, Version = job.Version, Pid = process.Id, Ticks = process.StartTime.ToUniversalTime().Ticks });
        Thread.Sleep(60000);
        return 0;
#endif
    }
}
