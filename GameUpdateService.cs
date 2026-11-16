using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LaTaleGarden
{
    public enum GameUpdateState { Unknown, NeedsUpdate, Current }
    [DataContract]
    public class GameFileStamp
    {
        [DataMember] public string Name;
        [DataMember] public long Length;
        [DataMember] public long WriteTicks;
        public bool Same(GameFileStamp other) { return other != null && Name == other.Name && Length == other.Length && WriteTicks == other.WriteTicks; }
    }
    [DataContract]
    public class GameFileCheck
    {
        [DataMember] public string Name;
        [DataMember] public string Local;
        [DataMember] public string Online;
        [DataMember] public bool Matches;
    }
    [DataContract]
    public class GameLaunchPlan
    {
        [DataMember] public GameUpdateState State;
        [DataMember] public string Message;
        [DataMember] public string GameDirectory;
        [DataMember] public string Arguments;
        [DataMember] public DateTime CheckedUtc;
        [DataMember] public GameFileCheck[] Checks = new GameFileCheck[0];
        [DataMember] public GameFileStamp[] Files = new GameFileStamp[0];
        public static GameLaunchPlan Official(string message) { return new GameLaunchPlan { State = GameUpdateState.Unknown, Message = message, CheckedUtc = DateTime.UtcNow }; }
    }
    [DataContract]
    public class GameUpdateCommand
    {
        [DataMember] public string Id;
        [DataMember] public string Action;
    }
    public interface IGameMetadataTransport { byte[] Download(CancellationToken cancel); }
    public interface IGameUpdateFiles
    {
        string DirectoryPath { get; }
        bool Exists(string name);
        GameFileStamp Stamp(string name);
        Version Version(string name);
        uint ResourceVersion(string name);
        string Md5(string name, CancellationToken cancel);
        byte[] ReadSmall(string name, int maximum);
    }
    public sealed class OfficialGameMetadataTransport : IGameMetadataTransport
    {
        public const string MetadataUrl = "https://patch-la.mangot5.com.tw/latale/live_server/Update.NFO";
        public byte[] Download(CancellationToken cancel)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(MetadataUrl);
            request.AllowAutoRedirect = false; request.Timeout = 5000; request.ReadWriteTimeout = 3000;
            request.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.UserAgent = "LaTaleGarden/" + Program.Version;
            using (cancel.Register(request.Abort))
            {
                Task<WebResponse> pending = request.GetResponseAsync();
                // Observe late faults and dispose a late response after cancellation (including DNS waits).
                pending.ContinueWith(t => { if (t.IsFaulted) { var ignored = t.Exception; } else if (cancel.IsCancellationRequested && t.Status == TaskStatus.RanToCompletion) t.Result.Dispose(); }, TaskScheduler.Default);
                try { pending.Wait(cancel); }
                catch (AggregateException ex) { throw ex.GetBaseException(); }
                using (var response = (HttpWebResponse)pending.Result)
                {
                    if (response.StatusCode != HttpStatusCode.OK || response.ContentLength > 65536) throw new InvalidDataException("官方更新清单响应不完整或超出大小限制。");
                    using (var input = response.GetResponseStream())
                    using (var output = new MemoryStream())
                    {
                        byte[] buffer = new byte[4096]; int length;
                        while ((length = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancel.ThrowIfCancellationRequested();
                            if (output.Length + length > 65536) throw new InvalidDataException("官方更新清单超出大小限制。");
                            output.Write(buffer, 0, length);
                        }
                        return output.ToArray();
                    }
                }
            }
        }
    }
    // Deliberately small INI reader. Unknown or duplicate instructions go to the official patcher.
    public sealed class GameIni
    {
        private readonly Dictionary<string, Dictionary<string, string>> sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        public IEnumerable<string> Sections { get { return sections.Keys; } }
        public IEnumerable<string> Keys(string section) { return sections.ContainsKey(section) ? sections[section].Keys : Enumerable.Empty<string>(); }
        public string Get(string section, string key, string fallback)
        {
            Dictionary<string, string> entries; string value;
            return sections.TryGetValue(section, out entries) && entries.TryGetValue(key, out value) ? value : fallback;
        }
        public int Number(string section, string key, int minimum, int maximum)
        {
            int value;
            if (!int.TryParse(Get(section, key, null), NumberStyles.None, CultureInfo.InvariantCulture, out value) || value < minimum || value > maximum)
                throw new InvalidDataException("官方清单字段无效：" + section + "/" + key);
            return value;
        }
        public static GameIni Read(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 65536 || bytes.Any(b => b > 127 || b == 0)) throw new InvalidDataException("无法识别官方清单或启动配置格式。");
            var result = new GameIni(); Dictionary<string, string> section = null; int fields = 0;
            foreach (string raw in Encoding.ASCII.GetString(bytes).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal) || line.StartsWith("#", StringComparison.Ordinal)) continue;
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    string name = line.Substring(1, line.Length - 2);
                    if (name.Length == 0 || name.Length > 128 || result.sections.ContainsKey(name) || result.sections.Count >= 8) throw new InvalidDataException("清单或配置中存在重复或无效的分组。");
                    section = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); result.sections.Add(name, section); continue;
                }
                int split = line.IndexOf('=');
                if (section == null || split <= 0 || ++fields > 2048) throw new InvalidDataException("清单或配置内容不完整。");
                string key = line.Substring(0, split).Trim(), value = line.Substring(split + 1).Trim();
                if (key.Length == 0 || key.Length > 128 || value.Length > 2048 || section.ContainsKey(key)) throw new InvalidDataException("清单或配置字段重复或无效。");
                section.Add(key, value);
            }
            return result;
        }
    }
    public sealed class WindowsGameUpdateFiles : IGameUpdateFiles
    {
        private readonly string root;
        public WindowsGameUpdateFiles(string directory) { root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar); }
        public string DirectoryPath { get { return root; } }
        private string PathFor(string name)
        {
            string path = Path.GetFullPath(Path.Combine(root, name));
            if (path.StartsWith(@"\\", StringComparison.Ordinal) || !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("快速启动需要本地游戏目录。");
            string current = path;
            while (current != null)
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("链接目录交由官方启动器处理。");
                if (Native.SamePath(current, root)) break;
                current = Path.GetDirectoryName(current);
            }
            return path;
        }
        public bool Exists(string name) { return File.Exists(PathFor(name)); }
        public GameFileStamp Stamp(string name)
        {
            var info = new FileInfo(PathFor(name));
            if (!info.Exists) throw new FileNotFoundException("游戏文件缺失：" + name);
            return new GameFileStamp { Name = name, Length = info.Length, WriteTicks = info.LastWriteTimeUtc.Ticks };
        }
        public Version Version(string name)
        {
            var value = FileVersionInfo.GetVersionInfo(PathFor(name));
            return new Version(value.FileMajorPart, value.FileMinorPart, value.FileBuildPart, value.FilePrivatePart);
        }
        public uint ResourceVersion(string name)
        {
            using (var source = new FileStream(PathFor(name), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (source.Length < 4) throw new InvalidDataException("资源文件尚未完整写入：" + name);
                source.Seek(-4, SeekOrigin.End);
                using (var reader = new BinaryReader(source)) return reader.ReadUInt32() & 0x7fffffff;
            }
        }
        public string Md5(string name, CancellationToken cancel)
        {
            using (var input = new FileStream(PathFor(name), FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var hash = MD5.Create())
            {
                if (input.Length > 64 * 1024 * 1024) throw new InvalidDataException("附加文件超出快速检查范围：" + name);
                byte[] buffer = new byte[65536]; int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0) { cancel.ThrowIfCancellationRequested(); hash.TransformBlock(buffer, 0, count, buffer, 0); }
                hash.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
            }
        }
        public byte[] ReadSmall(string name, int maximum)
        {
            using (var input = new FileStream(PathFor(name), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length > maximum) throw new InvalidDataException("启动配置超出预期大小。");
                using (var output = new MemoryStream()) { input.CopyTo(output); return output.ToArray(); }
            }
        }
    }
    public sealed class GameUpdateChecker
    {
        // A launcher protocol change requires review before taking the direct-client path.
        public static readonly Version SupportedLauncher = new Version(2025, 9, 23, 1);
        private readonly IGameMetadataTransport transport;
        private readonly IGameUpdateFiles files;
        public GameUpdateChecker(IGameMetadataTransport transport, IGameUpdateFiles files) { this.transport = transport; this.files = files; }
        public GameLaunchPlan Check(CancellationToken cancel, Action<string> log)
        {
            var clock = Stopwatch.StartNew();
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                deadline.CancelAfter(8000);
                try
                {
                    byte[] document = transport.Download(deadline.Token);
                    var result = Inspect(document, deadline.Token);
                    if (log != null)
                    {
                        log("官方游戏清单：" + document.Length + " 字节；检查耗时=" + clock.ElapsedMilliseconds + "ms；结果=" + result.State + "；" + result.Message);
                        foreach (var check in result.Checks) log("版本检查 " + check.Name + "；本地=" + check.Local + "；线上=" + check.Online + "；一致=" + check.Matches);
                    }
                    return result;
                }
                catch (Exception ex)
                {
                    cancel.ThrowIfCancellationRequested();
                    string message = deadline.IsCancellationRequested ? "检查官方游戏更新超时，将使用官方启动器。" : "游戏检查未完成：" + ex.Message + " 将使用官方启动器。";
                    if (log != null) log(message + " " + ex.GetType().Name + ": " + ex.Message);
                    return GameLaunchPlan.Official(message);
                }
            }
        }
        private static string FileName(string value)
        {
            if (value == null || !Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,99}\z") || value.Contains("..") || Regex.IsMatch(value, @"\A(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|\z)", RegexOptions.IgnoreCase))
                throw new InvalidDataException("清单中存在不支持的文件名。");
            return value;
        }
        private static void AllowKeys(GameIni ini, string section, IEnumerable<string> allowed)
        {
            if (ini.Keys(section).Except(allowed, StringComparer.OrdinalIgnoreCase).Any()) throw new InvalidDataException("官方更新规则已变化：" + section);
        }
        private static readonly string[] VersionKeys = { "VersionMajorHigh", "VersionMajorLow", "VersionMinorHigh", "VersionMinorLow" };
        private static Version OnlineVersion(GameIni ini, string section)
        {
            int[] parts = VersionKeys.Select(key => ini.Number(section, key, 0, 65535)).ToArray();
            return new Version(parts[0], parts[1], parts[2], parts[3]);
        }
        private GameLaunchPlan Inspect(byte[] document, CancellationToken cancel)
        {
            var ini = GameIni.Read(document);
            string[] known = { "LaTaleLauncher", "LaTaleClient", "LoginServer", "Resource", "AddFile" };
            if (ini.Sections.Except(known, StringComparer.OrdinalIgnoreCase).Any()) throw new InvalidDataException("官方清单出现新的更新步骤。");
            AllowKeys(ini, "LaTaleLauncher", VersionKeys.Concat(new[] { "ZIP", "FileName", "VersionDesc" }));
            AllowKeys(ini, "LaTaleClient", VersionKeys.Concat(new[] { "ZIP", "FileName" }));
            AllowKeys(ini, "LoginServer", new[] { "LoginServerCount", "1_IP" });
            var result = new GameLaunchPlan { GameDirectory = files.DirectoryPath, CheckedUtc = DateTime.UtcNow };
            var checks = new List<GameFileCheck>(); var stamps = new List<GameFileStamp>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string, Func<string>, string> checkFile = (name, read, expected) => {
                cancel.ThrowIfCancellationRequested();
                if (!names.Add(name)) throw new InvalidDataException("清单中存在重复文件。");
                try
                {
                    var before = files.Stamp(name); string value = read();
                    if (!before.Same(files.Stamp(name))) throw new IOException("检查过程中游戏文件发生变化。");
                    stamps.Add(before); checks.Add(new GameFileCheck { Name = name, Local = value, Online = expected, Matches = value == expected });
                }
                catch (FileNotFoundException) { checks.Add(new GameFileCheck { Name = name, Local = "缺失", Online = expected }); }
                catch (DirectoryNotFoundException) { checks.Add(new GameFileCheck { Name = name, Local = "缺失", Online = expected }); }
            };
            Version launcherVersion = OnlineVersion(ini, "LaTaleLauncher");
            foreach (string section in new[] { "LaTaleLauncher", "LaTaleClient" })
            {
                string name = FileName(ini.Get(section, "FileName", null));
                if (name != section + ".exe") throw new InvalidDataException("官方客户端名称已变化。");
                checkFile(name, () => files.Version(name).ToString(4), OnlineVersion(ini, section).ToString(4));
            }
            int count = ini.Number("Resource", "ResourceCount", 1, 128);
            var keys = new List<string> { "ResourceCount" };
            for (int index = 1; index <= count; index++)
            {
                string prefix = index.ToString(CultureInfo.InvariantCulture) + "_";
                keys.AddRange(new[] { prefix + "Version", prefix + "FileName", prefix + "ZIP" });
                string name = FileName(ini.Get("Resource", prefix + "FileName", null));
                if (!name.EndsWith(".SPF", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("官方资源类型已变化。");
                string expected = ini.Number("Resource", prefix + "Version", 1, int.MaxValue).ToString(CultureInfo.InvariantCulture);
                checkFile(name, () => files.ResourceVersion(name).ToString(CultureInfo.InvariantCulture), expected);
            }
            AllowKeys(ini, "Resource", keys);
            count = ini.Number("AddFile", "AddFileCount", 0, 64);
            keys = new List<string> { "AddFileCount" };
            for (int index = 1; index <= count; index++)
            {
                string prefix = index.ToString(CultureInfo.InvariantCulture) + "_";
                keys.AddRange(new[] { prefix + "MD5", prefix + "FileName", prefix + "SubFolder", prefix + "ZIP" });
                string name = FileName(ini.Get("AddFile", prefix + "FileName", null)), subfolder = ini.Get("AddFile", prefix + "SubFolder", "");
                if (subfolder != "" && subfolder != "XignCode") throw new InvalidDataException("附加文件目录规则已变化。");
                string expected = ini.Get("AddFile", prefix + "MD5", "").ToLowerInvariant();
                if (!Regex.IsMatch(expected, @"\A[0-9a-f]{32}\z")) throw new InvalidDataException("附加文件校验值缺失。");
                string relative = subfolder == "" ? name : subfolder + "\\" + name;
                checkFile(relative, () => files.Md5(relative, cancel), expected);
            }
            AllowKeys(ini, "AddFile", keys);
            result.Checks = checks.ToArray();
            if (checks.Any(check => !check.Matches))
            {
                result.State = GameUpdateState.NeedsUpdate; result.Message = "游戏文件需要更新：" + string.Join("、", checks.Where(check => !check.Matches).Select(check => check.Name)); return result;
            }
            if (launcherVersion != SupportedLauncher) throw new InvalidDataException("新版官方启动规则尚未验证，请使用官方启动。");
            if (ini.Number("LoginServer", "LoginServerCount", 1, 16) != 1) throw new InvalidDataException("官方登录服务器选择规则已变化。");
            string login = ini.Get("LoginServer", "1_IP", "");
            string[] parts = login.Split(':'); IPAddress address; int port;
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                address.ToString() != parts[0] || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
                throw new InvalidDataException("官方登录服务器信息无效。");
            foreach (string name in new[] { "LaTaleLauncher.CFG", "ClientResol.DAT" }) stamps.Add(files.Stamp(name));
            var config = GameIni.Read(files.ReadSmall("LaTaleLauncher.CFG", 65536));
            if (config.Get("LaTaleLauncher.CFG", "LOCALE", "") != "TAIWAN") throw new InvalidDataException("快速启动目前只支持台服配置。");
            string mode = config.Get("LaTaleLauncher.CFG", "ScreenMode", "WINDOW").ToUpperInvariant();
            string quality = config.Get("LaTaleLauncher.CFG", "Quality", "HIGH").ToUpperInvariant();
            string publisher = config.Get("LaTaleLauncher.CFG", "PUBLISHER", "NULL").ToUpperInvariant();
            if ((mode != "WINDOW" && mode != "FULL") || (quality != "HIGH" && quality != "LOW") || (publisher != "NULL" && publisher != "HAPPYTUK") ||
                config.Get("LaTaleLauncher.CFG", "LATALE_DEVELOPER_MODE", "0") != "0") throw new InvalidDataException("当前启动配置需由官方启动器处理。");
            byte[] resolution = files.ReadSmall("ClientResol.DAT", 8);
            // The official updater can save ScreenMode on exit without rewriting ClientResol.DAT.
            // Validate both stored settings, but use CFG for launch arguments without requiring agreement.
            if (resolution.Length != 8 || (BitConverter.ToInt32(resolution, 0) != 0 && BitConverter.ToInt32(resolution, 0) != 1) || BitConverter.ToInt32(resolution, 4) < 3 || BitConverter.ToInt32(resolution, 4) > 32)
                throw new InvalidDataException("请先在官方启动器保存窗口和分辨率设置。");
            result.Arguments = (mode == "WINDOW" ? "/Windowed" : "/Full") + " /LoginServer:" + login + " /Run:" + (quality == "HIGH" ? "VHIGH" : "VLOW") + " /LOCALE:TAIWAN /PUBLISHER:HAPPYTUK";
            result.Files = stamps.ToArray();
            result.State = GameUpdateState.Current; result.Message = "游戏已是最新版本，准备直接启动。";
            if (!StillCurrent(result, files)) throw new IOException("检查期间文件或配置发生变化，交由官方启动器确认。");
            return result;
        }
        public static bool StillCurrent(GameLaunchPlan plan, IGameUpdateFiles files)
        {
            try
            {
                return plan != null && plan.State == GameUpdateState.Current && Native.SamePath(plan.GameDirectory, files.DirectoryPath) &&
                    DateTime.UtcNow >= plan.CheckedUtc && DateTime.UtcNow - plan.CheckedUtc < TimeSpan.FromSeconds(30) && plan.Files.Length >= 4 &&
                    !files.Exists("LaTaleLauncher.CRC") && plan.Files.All(stamp => stamp.Same(files.Stamp(stamp.Name)));
            }
            catch { return false; }
        }
    }
}
