using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace LaTaleGarden
{
    [DataContract]
    public sealed class SignedDocument
    {
        [DataMember(Name = "payload")] public string Payload;
        [DataMember(Name = "signature")] public string Signature;
    }
    [DataContract]
    public sealed class UpdateFeed
    {
        [DataMember(Name = "schema")] public int Schema;
        [DataMember(Name = "releases")] public UpdateRelease[] Releases;
    }
    [DataContract]
    public sealed class UpdateRelease
    {
        [DataMember(Name = "version")] public string Version;
        [DataMember(Name = "channel")] public string Channel;
        [DataMember(Name = "notes")] public string Notes;
        [DataMember(Name = "url")] public string Url;
        [DataMember(Name = "sha256")] public string Sha256;
        [DataMember(Name = "size")] public long Size;
        [DataMember(Name = "minimumWindowsBuild")] public int MinimumWindowsBuild;
        [DataMember(Name = "minimumFrameworkRelease")] public int MinimumFrameworkRelease;
        public string ReleaseUrl { get { return Program.ProjectUrl + "/releases/tag/v" + Version; } }
    }
    [DataContract]
    public sealed class AnnouncementFeed
    {
        [DataMember(Name = "schema")] public int Schema;
        [DataMember(Name = "items")] public Announcement[] Items;
    }
    [DataContract]
    public sealed class Announcement
    {
        [DataMember(Name = "id")] public string Id;
        [DataMember(Name = "title")] public string Title;
        [DataMember(Name = "text")] public string Text;
        [DataMember(Name = "url")] public string Url;
        [DataMember(Name = "startsUtc")] public string StartsUtc;
        [DataMember(Name = "expiresUtc")] public string ExpiresUtc;
    }
    [DataContract]
    public sealed class UpdatePreferences
    {
        [DataMember] public bool AutoCheck = true;
        [DataMember] public bool AutoDownload;
        [DataMember] public bool ShowAnnouncements = true;
        [DataMember] public bool IncludePreview = Program.ReleaseChannel == "preview";
        [DataMember] public string IgnoredVersion = "";
        [DataMember] public string[] DismissedAnnouncements = new string[0];
        [DataMember(EmitDefaultValue = false)] public DateTime LastUpdateAttemptUtc;
        [DataMember(EmitDefaultValue = false)] public DateTime LastAnnouncementAttemptUtc;
        [DataMember(EmitDefaultValue = false)] public DateTime LastSuccessUtc;
        [OnDeserializing] private void Defaults(StreamingContext context)
        {
            AutoCheck = true; ShowAnnouncements = true; IncludePreview = Program.ReleaseChannel == "preview";
            IgnoredVersion = ""; DismissedAnnouncements = new string[0];
        }
        public void Normalize()
        {
            IgnoredVersion = IgnoredVersion ?? "";
            DismissedAnnouncements = (DismissedAnnouncements ?? new string[0]).Where(x => x != null).Distinct().Take(100).ToArray();
        }
        public static bool Due(DateTime previous, DateTime now)
        {
            return previous == DateTime.MinValue || previous > now.AddDays(1) || now - previous >= TimeSpan.FromHours(24);
        }
    }
    public static class UpdatePaths
    {
        public static string Root { get { return Path.Combine(AppPaths.DataRoot, "Updates"); } }
        public static string Settings { get { return Path.Combine(Root, "settings.json"); } }
        public static string CachedFeed { get { return Path.Combine(Root, "updates.json"); } }
        public static string CachedAnnouncements { get { return Path.Combine(Root, "announcements.json"); } }
        public static string Jobs { get { return Path.Combine(Root, "Jobs"); } }
        public static string Package(UpdateRelease release) { return Path.Combine(Root, "Downloads", release.Version + "-" + release.Sha256.Substring(0, 12), "LaTaleGarden.exe"); }
        public static void Log(string text)
        {
            try { Directory.CreateDirectory(Root); File.AppendAllText(Path.Combine(Root, "update.log"), DateTime.Now.ToString("s") + " " + text + Environment.NewLine, new UTF8Encoding(false)); } catch { }
        }
    }
    public static class UpdateProtocol
    {
        public const string FeedUrl = "https://raw.githubusercontent.com/visionki/latale-garden/main/updates/updates.json";
        public const string AnnouncementsUrl = "https://raw.githubusercontent.com/visionki/latale-garden/main/updates/announcements.json";
        public const int MaximumDocument = 256 * 1024;
        public static string PublicKey
        {
            get { using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("update-public-key.xml")) using (var reader = new StreamReader(input)) return reader.ReadToEnd(); }
        }
        public static T ReadJson<T>(byte[] data)
        {
            using (var input = new MemoryStream(data)) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(input);
        }
        public static byte[] WriteJson<T>(T data)
        {
            using (var output = new MemoryStream()) { new DataContractJsonSerializer(typeof(T)).WriteObject(output, data); return output.ToArray(); }
        }
        public static T Verify<T>(byte[] envelope, string publicKey)
        {
            if (envelope == null || envelope.Length > MaximumDocument) throw new InvalidDataException("更新信息过大，已停止处理。");
            var signed = ReadJson<SignedDocument>(envelope);
            if (signed == null || string.IsNullOrEmpty(signed.Payload) || string.IsNullOrEmpty(signed.Signature)) throw new InvalidDataException("缺少发布签名。");
            byte[] payload = Convert.FromBase64String(signed.Payload), signature = Convert.FromBase64String(signed.Signature);
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false; rsa.FromXmlString(publicKey);
                if (!rsa.VerifyData(payload, CryptoConfig.MapNameToOID("SHA256"), signature)) throw new InvalidDataException("发布签名不正确，已停止更新。");
            }
            return ReadJson<T>(payload);
        }
        public static Version ParseVersion(string value)
        {
            Version version;
            if (value == null || !Regex.IsMatch(value, @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$") || !Version.TryParse(value, out version) ||
                version.Major > 65535 || version.Minor > 65535 || version.Build > 65535) throw new InvalidDataException("版本号无效。");
            return version;
        }
        public static void Validate(UpdateRelease release)
        {
            if (release == null) throw new InvalidDataException("版本信息缺失。");
            ParseVersion(release.Version);
            if (release.Channel != "stable" && release.Channel != "preview") throw new InvalidDataException("更新通道无效。");
            if (release.Url != Program.ProjectUrl + "/releases/download/v" + release.Version + "/LaTaleGarden.exe") throw new InvalidDataException("下载地址不属于项目发布目录。");
            if (release.Sha256 == null || !Regex.IsMatch(release.Sha256, "^[a-f0-9]{64}$") || release.Size < 1024 || release.Size > 200L * 1024 * 1024) throw new InvalidDataException("安装包校验信息无效。");
            if (release.MinimumWindowsBuild < 10240 || release.MinimumFrameworkRelease < 528040 || release.Notes == null || release.Notes.Length > 8000) throw new InvalidDataException("版本要求或说明无效。");
        }
        public static UpdateFeed ReadFeed(byte[] data, string key)
        {
            var feed = Verify<UpdateFeed>(data, key);
            if (feed == null || feed.Schema != 1 || feed.Releases == null || feed.Releases.Length > 20) throw new InvalidDataException("暂不支持此更新信息格式，请查看项目页面。");
            foreach (var release in feed.Releases) Validate(release);
            if (feed.Releases.GroupBy(x => x.Version).Any(x => x.Count() > 1)) throw new InvalidDataException("更新清单包含重复版本。");
            return feed;
        }
        public static UpdateRelease Select(UpdateFeed feed, string current, bool previews, int build, int framework)
        {
            var version = ParseVersion(current);
            return feed.Releases.Where(x => ParseVersion(x.Version) > version && (previews || x.Channel == "stable") &&
                x.MinimumWindowsBuild <= build && x.MinimumFrameworkRelease <= framework).OrderByDescending(x => ParseVersion(x.Version)).FirstOrDefault();
        }
        public static int WindowsBuild()
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            { int build; return int.TryParse(Convert.ToString(key == null ? null : key.GetValue("CurrentBuildNumber")), out build) ? build : 0; }
        }
        public static int FrameworkRelease()
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                return key == null ? 0 : Convert.ToInt32(key.GetValue("Release", 0));
        }
        public static string Hash(string path)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
        }
        public static void VerifyPackage(string path, UpdateRelease release)
        {
            Validate(release);
            if (new FileInfo(path).Length != release.Size || Hash(path) != release.Sha256) throw new InvalidDataException("下载文件校验失败，请重新下载。");
            var actual = AssemblyName.GetAssemblyName(path);
            var expected = ParseVersion(release.Version);
            if (actual.Version.Major != expected.Major || actual.Version.Minor != expected.Minor || actual.Version.Build != expected.Build ||
                actual.ProcessorArchitecture != ProcessorArchitecture.Amd64) throw new InvalidDataException("安装包版本或架构不匹配。");
        }
        public static bool SafeLink(string value)
        {
            Uri uri; return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo);
        }
        public static bool DownloadHost(Uri uri)
        {
            return SafeLink(uri.AbsoluteUri) && new[] { "raw.githubusercontent.com", "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com", "github-releases.githubusercontent.com" }.Contains(uri.Host.ToLowerInvariant());
        }
        public static AnnouncementFeed ReadAnnouncements(byte[] data, string key)
        {
            var feed = Verify<AnnouncementFeed>(data, key);
            if (feed == null || feed.Schema != 1 || feed.Items == null || feed.Items.Length > 20) throw new InvalidDataException("项目动态格式无效。");
            foreach (var item in feed.Items)
            {
                if (item == null || item.Id == null || !Regex.IsMatch(item.Id, "^[a-zA-Z0-9-]{1,80}$") || string.IsNullOrEmpty(item.Title) || item.Title.Length > 100 ||
                    item.Text == null || item.Text.Length > 3000 || (!string.IsNullOrEmpty(item.Url) && !SafeLink(item.Url)) || Time(item.ExpiresUtc) <= Time(item.StartsUtc))
                    throw new InvalidDataException("项目动态内容无效。");
            }
            if (feed.Items.GroupBy(x => x.Id).Any(x => x.Count() > 1)) throw new InvalidDataException("项目动态标识重复。");
            return feed;
        }
        private static DateTimeOffset Time(string text)
        {
            DateTimeOffset result;
            if (text == null || !text.EndsWith("Z", StringComparison.Ordinal) || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out result))
                throw new InvalidDataException("项目动态时间无效。");
            return result;
        }
        public static Announcement VisibleAnnouncement(AnnouncementFeed feed, IEnumerable<string> dismissed, DateTime now)
        {
            return feed.Items.FirstOrDefault(x => Time(x.StartsUtc).UtcDateTime <= now && now < Time(x.ExpiresUtc).UtcDateTime && !dismissed.Contains(x.Id));
        }
    }
    public interface IUpdateTransport
    {
        void Copy(Uri uri, Stream output, long limit, Action<long> progress, CancellationToken cancel);
    }
    public sealed class GithubUpdateTransport : IUpdateTransport
    {
        public void Copy(Uri uri, Stream output, long limit, Action<long> progress, CancellationToken cancel)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            for (int redirect = 0; redirect < 6; redirect++)
            {
                cancel.ThrowIfCancellationRequested();
                if (!UpdateProtocol.DownloadHost(uri)) throw new InvalidDataException("下载跳转到了未认可的地址。");
                var request = (HttpWebRequest)WebRequest.Create(uri);
                request.UserAgent = "LaTaleGarden/" + Program.Version;
                request.AllowAutoRedirect = false; request.Timeout = 15000; request.ReadWriteTimeout = 20000;
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                using (cancel.Register(request.Abort))
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    int code = (int)response.StatusCode;
                    if (code == 301 || code == 302 || code == 303 || code == 307 || code == 308)
                    {
                        uri = new Uri(uri, response.Headers["Location"]); continue;
                    }
                    if (code != 200) throw new IOException("服务器未返回完整文件。");
                    if (response.ContentLength > limit) throw new InvalidDataException("下载文件超出预期大小。");
                    using (var input = response.GetResponseStream())
                    {
                        byte[] buffer = new byte[65536]; long total = 0; int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            cancel.ThrowIfCancellationRequested(); total += count;
                            if (total > limit) throw new InvalidDataException("下载文件超出预期大小。");
                            output.Write(buffer, 0, count); if (progress != null) progress(total);
                        }
                    }
                    return;
                }
            }
            throw new IOException("下载跳转次数过多。");
        }
    }
    public sealed class UpdateClient
    {
        private readonly IUpdateTransport transport;
        public UpdateClient() : this(new GithubUpdateTransport()) { }
        public UpdateClient(IUpdateTransport transport) { this.transport = transport; }
        public byte[] Document(string url, CancellationToken token)
        {
            using (var output = new MemoryStream()) { transport.Copy(new Uri(url), output, UpdateProtocol.MaximumDocument, null, token); return output.ToArray(); }
        }
        public void Download(UpdateRelease release, string path, Action<long> progress, CancellationToken token)
        {
            UpdateProtocol.Validate(release);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string partial = path + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { transport.Copy(new Uri(release.Url), file, release.Size, progress, token); file.Flush(true); }
                token.ThrowIfCancellationRequested();
                UpdateProtocol.VerifyPackage(partial, release);
                if (File.Exists(path)) File.Replace(partial, path, null); else File.Move(partial, path);
            }
            finally { try { File.Delete(partial); } catch { } }
        }
    }
}
