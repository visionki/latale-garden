using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using LaTaleGarden;

public sealed class FakeGameTransport : IGameMetadataTransport
{
    public byte[] Document;
    public bool Failure;
    public byte[] Download(CancellationToken cancel) { cancel.ThrowIfCancellationRequested(); if (Failure) throw new IOException("Network unavailable"); return Document; }
}
public sealed class FakeGameFiles : IGameUpdateFiles
{
    public readonly Dictionary<string, byte[]> Data = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, long> Ticks = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, Version> Versions = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
    public bool ChangeDuringRead;
    public string DirectoryPath { get { return @"D:\Fake LaTale"; } }
    public FakeGameFiles()
    {
        Data["LaTaleLauncher.exe"] = new byte[100]; Data["LaTaleClient.exe"] = new byte[100];
        Versions["LaTaleLauncher.exe"] = GameUpdateChecker.SupportedLauncher; Versions["LaTaleClient.exe"] = new Version(1, 355, 1309, 1);
        Data["AJJIYA.SPF"] = BitConverter.GetBytes(2026110201U | 0x80000000U); Data["ROWID.SPF"] = BitConverter.GetBytes(2026110901U | 0x80000000U);
        Data[@"XignCode\test.xem"] = new byte[] { 1, 2, 3 };
        Data["LaTaleLauncher.CFG"] = Encoding.ASCII.GetBytes("[LaTaleLauncher.CFG]\r\nLOCALE=TAIWAN\r\nScreenMode=WINDOW\r\nQuality=HIGH\r\n");
        Data["ClientResol.DAT"] = BitConverter.GetBytes(1).Concat(BitConverter.GetBytes(4)).ToArray();
        foreach (string name in Data.Keys) Ticks[name] = 1000;
    }
    public bool Exists(string name) { return Data.ContainsKey(name); }
    public GameFileStamp Stamp(string name) { if (!Exists(name)) throw new FileNotFoundException(name); return new GameFileStamp { Name = name, Length = Data[name].Length, WriteTicks = Ticks[name] }; }
    public Version Version(string name) { return Versions[name]; }
    public uint ResourceVersion(string name) { return BitConverter.ToUInt32(Data[name], 0) & 0x7fffffff; }
    public string Md5(string name, CancellationToken cancel) { cancel.ThrowIfCancellationRequested(); if (ChangeDuringRead) Ticks[name]++; return "5289df737df57326fcdd22597afb1fac"; }
    public byte[] ReadSmall(string name, int maximum) { return Data[name]; }
}
public static class GameUpdateTests
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    const string Manifest = @"[LaTaleLauncher]
VersionMajorHigh=2025
VersionMajorLow=9
VersionMinorHigh=23
VersionMinorLow=1
ZIP=1
FileName=LaTaleLauncher.exe
VersionDesc=[ LaTale LiveServer ]
[LaTaleClient]
VersionMajorHigh=1
VersionMajorLow=355
VersionMinorHigh=1309
VersionMinorLow=1
ZIP=1
FileName=LaTaleClient.exe
[LoginServer]
LoginServerCount=1
1_IP=113.196.245.152:20000
[Resource]
ResourceCount=2
1_Version=2026110201
1_FileName=AJJIYA.SPF
2_Version=2026110901
2_FileName=ROWID.SPF
[AddFile]
AddFileCount=1
1_MD5=5289df737df57326fcdd22597afb1fac
1_FileName=test.xem
1_SubFolder=XignCode
";
    static GameLaunchPlan Inspect(FakeGameFiles files, string document)
    {
        return new GameUpdateChecker(new FakeGameTransport { Document = Encoding.ASCII.GetBytes(document) }, files).Check(CancellationToken.None, null);
    }
    public static void Run(string root, Action<string, Action> test)
    {
        Directory.CreateDirectory(root);
        test("game metadata, signed resource footer and extra-file checks select direct launch", () => {
            var result = Inspect(new FakeGameFiles(), Manifest);
            Check(result.State == GameUpdateState.Current && result.Checks.Length == 5 && result.Checks.All(c => c.Matches), "Current install not recognized");
            Check(result.Arguments == "/Windowed /LoginServer:113.196.245.152:20000 /Run:VHIGH /LOCALE:TAIWAN /PUBLISHER:HAPPYTUK", "Incorrect official client arguments");
        });
        test("game client, launcher, resource and additional-file mismatches request official updates", () => {
            foreach (string changed in new[] { Manifest.Replace("VersionMinorHigh=1309", "VersionMinorHigh=1310"), Manifest.Replace("VersionMajorLow=9", "VersionMajorLow=10"),
                Manifest.Replace("2026110201", "2026110202"), Manifest.Replace("5289df737df57326fcdd22597afb1fac", "00000000000000000000000000000000") })
                Check(Inspect(new FakeGameFiles(), changed).State == GameUpdateState.NeedsUpdate, "Mismatch was permitted to launch directly");
        });
        test("missing client or game resource opens the official update path", () => {
            foreach (string missing in new[] { "LaTaleClient.exe", "AJJIYA.SPF", @"XignCode\test.xem" }) {
                var files = new FakeGameFiles(); files.Data.Remove(missing);
                Check(Inspect(files, Manifest).State == GameUpdateState.NeedsUpdate, "Missing game file was ignored");
            }
        });
        test("unknown directives, duplicate fields and malformed metadata use official launch", () => {
            foreach (string bad in new[] { Manifest + "[FutureFeature]\nNewRule=1", Manifest.Replace("FileName=LaTaleClient.exe", "FileName=LaTaleClient.exe\nStopUpdateMessage=Update required"),
                Manifest.Replace("1_Version=2026110201", "1_Version=2026110201\n1_Flag=1"), Manifest.Replace("ResourceCount=2", "ResourceCount=2\nResourceCount=2"), "<html>Service unavailable</html>", new string('x', 65537) })
                Check(Inspect(new FakeGameFiles(), bad).State == GameUpdateState.Unknown, "Unknown metadata accepted for direct launch");
        });
        test("metadata paths, digests and login command injection are rejected", () => {
            foreach (string bad in new[] { Manifest.Replace("AJJIYA.SPF", "../AJJIYA.SPF"), Manifest.Replace("AJJIYA.SPF", "C:\\AJJIYA.SPF"), Manifest.Replace("AJJIYA.SPF", "CON.SPF"),
                Manifest.Replace("XignCode", "..\\Windows"), Manifest.Replace("5289df737df57326fcdd22597afb1fac", "not-a-digest"), Manifest.Replace("113.196.245.152:20000", "113.196.245.152:20000 /WEBARG:injected"),
                Manifest.Replace("LoginServerCount=1", "LoginServerCount=2") })
                Check(Inspect(new FakeGameFiles(), bad).State == GameUpdateState.Unknown, "Untrusted metadata escaped validation");
        });
        test("fullscreen and quality settings use the existing official configuration", () => {
            var files = new FakeGameFiles(); files.Data["LaTaleLauncher.CFG"] = Encoding.ASCII.GetBytes("[LaTaleLauncher.CFG]\nLOCALE=TAIWAN\nScreenMode=FULL\nQuality=LOW\n");
            files.Data["ClientResol.DAT"] = BitConverter.GetBytes(0).Concat(BitConverter.GetBytes(4)).ToArray();
            var result = Inspect(files, Manifest); Check(result.State == GameUpdateState.Current && result.Arguments.StartsWith("/Full ") && result.Arguments.Contains("/Run:VLOW"), "Display settings were not preserved");
        });
        test("independently saved display modes do not invalidate current game files", () => {
            foreach (string mode in new[] { "WINDOW", "FULL" }) {
                var files = new FakeGameFiles();
                files.Data["LaTaleLauncher.CFG"] = Encoding.ASCII.GetBytes("[LaTaleLauncher.CFG]\nLOCALE=TAIWAN\nScreenMode=" + mode + "\nQuality=HIGH\n");
                files.Data["ClientResol.DAT"] = BitConverter.GetBytes(mode == "FULL" ? 1 : 0).Concat(BitConverter.GetBytes(4)).ToArray();
                var storedResolution = files.Data["ClientResol.DAT"].ToArray();
                var result = Inspect(files, Manifest);
                Check(result.State == GameUpdateState.Current && result.Checks.All(c => c.Matches) && result.Arguments.StartsWith(mode == "WINDOW" ? "/Windowed " : "/Full ") && GameUpdateChecker.StillCurrent(result, files), "Outdated display flag blocked current files or changed the launch mode");
                Check(storedResolution.SequenceEqual(files.Data["ClientResol.DAT"]), "Read-only version check rewrote display settings");
            }
        });
        test("resource repair survives the official updater saving a different display mode on exit", () => {
            var gameFiles = new FakeGameFiles(); var resource = gameFiles.Data["AJJIYA.SPF"]; gameFiles.Data.Remove("AJJIYA.SPF");
            var p = new FakePlatform { NeverWindow = true };
            p.GameCheckOverride = () => Inspect(gameFiles, Manifest);
            p.OnCheck = () => { if (p.GameCheckCount == 2) { gameFiles.Data["AJJIYA.SPF"] = resource; gameFiles.Ticks["AJJIYA.SPF"]++; } };
            p.OnCloseUpdater = () => { gameFiles.Data["LaTaleLauncher.CFG"] = Encoding.ASCII.GetBytes("[LaTaleLauncher.CFG]\nLOCALE=TAIWAN\nScreenMode=FULL\nQuality=HIGH\n"); gameFiles.Ticks["LaTaleLauncher.CFG"]++; };
            p.UpdateActions.Enqueue("complete");
            var session = new SessionFiles(Path.Combine(root, "repair-then-close"));
            new LaunchEngine(session, p).Run(new LaunchRequest { Compatibility = true, GameDirectory = gameFiles.DirectoryPath, WaitSeconds = 40 });
            var status = JsonFile.Read<SessionStatus>(session.FilePath("status.json"));
            Check(status.Stage == "client-started" && p.GameCheckCount == 3 && p.CloseUpdaterCount == 1 && p.DirectCount == 1 && p.LaunchCount == 1 && p.RestoreCount == 1 && !session.ReadJournal().Pending, "Repair was rejected after closing the updater");
            Check(p.LastDirectPlan.Arguments.StartsWith("/Full ") && BitConverter.ToInt32(gameFiles.Data["ClientResol.DAT"], 0) == 1, "Launch did not preserve independently saved display choices");
        });
        test("unsupported locale, launch configuration or incomplete display settings use official launch", () => {
            foreach (string config in new[] { "[LaTaleLauncher.CFG]\nLOCALE=JAPAN", "[LaTaleLauncher.CFG]\nLOCALE=TAIWAN\nQuality=unknown", "[LaTaleLauncher.CFG]\nLOCALE=TAIWAN\nLATALE_DEVELOPER_MODE=1" }) {
                var files = new FakeGameFiles(); files.Data["LaTaleLauncher.CFG"] = Encoding.ASCII.GetBytes(config);
                Check(Inspect(files, Manifest).State == GameUpdateState.Unknown, "Unsupported configuration launched directly");
            }
            foreach (var badResolution in new[] { new byte[3], BitConverter.GetBytes(2).Concat(BitConverter.GetBytes(4)).ToArray(), BitConverter.GetBytes(1).Concat(BitConverter.GetBytes(33)).ToArray() }) {
                var invalid = new FakeGameFiles(); invalid.Data["ClientResol.DAT"] = badResolution;
                var result = Inspect(invalid, Manifest);
                Check(result.State == GameUpdateState.Unknown && result.Message.Contains("分辨率"), "Invalid resolution was accepted or its reason was hidden");
            }
        });
        test("changed files, expired checks and local CRC markers prevent direct launch", () => {
            var files = new FakeGameFiles(); var plan = Inspect(files, Manifest);
            Check(GameUpdateChecker.StillCurrent(plan, files), "Fresh check did not validate");
            files.Ticks["LaTaleClient.exe"]++; Check(!GameUpdateChecker.StillCurrent(plan, files), "Changed executable not detected");
            files.Ticks["LaTaleClient.exe"]--; plan.CheckedUtc = DateTime.UtcNow.AddMinutes(-1); Check(!GameUpdateChecker.StillCurrent(plan, files), "Expired plan accepted");
            plan.CheckedUtc = DateTime.UtcNow; files.Data["LaTaleLauncher.CRC"] = new byte[0]; Check(!GameUpdateChecker.StillCurrent(plan, files), "CRC marker ignored");
            Check(Inspect(new FakeGameFiles { ChangeDuringRead = true }, Manifest).State == GameUpdateState.Unknown, "Mutation during checksum accepted");
        });
        test("a new official launcher protocol requires review before direct launch", () => {
            var files = new FakeGameFiles(); files.Versions["LaTaleLauncher.exe"] = new Version(2025, 10, 23, 1);
            Check(Inspect(files, Manifest.Replace("VersionMajorLow=9", "VersionMajorLow=10")).State == GameUpdateState.Unknown, "Unknown launcher protocol accepted");
        });
        test("network failure falls back while user cancellation stops checking", () => {
            var checker = new GameUpdateChecker(new FakeGameTransport { Failure = true }, new FakeGameFiles());
            Check(checker.Check(CancellationToken.None, null).State == GameUpdateState.Unknown, "Network error did not fall back");
            using (var cancelled = new CancellationTokenSource()) {
                cancelled.Cancel(); bool stopped = false;
                try { checker.Check(cancelled.Token, null); } catch (OperationCanceledException) { stopped = true; }
                Check(stopped, "Cancellation turned into official launch");
            }
        });
        test("real file adapter reads only the resource footer and blocks path traversal", () => {
            string folder = Path.Combine(root, "file-adapter"); Directory.CreateDirectory(folder);
            string resource = Path.Combine(folder, "Fixture.SPF"); File.WriteAllBytes(resource, new byte[1024].Concat(BitConverter.GetBytes(2026110901U | 0x80000000U)).ToArray());
            var files = new WindowsGameUpdateFiles(folder); Check(files.ResourceVersion("Fixture.SPF") == 2026110901U, "Wrong footer decoding");
            bool rejected = false; try { files.ReadSmall("..\\outside.txt", 32); } catch (InvalidDataException) { rejected = true; } Check(rejected, "Traversal escaped game directory");
        });
    }
}
