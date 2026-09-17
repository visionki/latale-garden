using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace LaTaleGarden
{
    [DataContract]
    public class Preferences
    {
        [DataMember] public string GameDirectory = "";
        [DataMember] public bool Compatibility = true;
        [DataMember] public bool MinimizeAfterStart = false;
        [DataMember] public int WaitMinutes = 30;
        [DataMember] public int SettleSeconds = 20;
        public void Normalize()
        {
            WaitMinutes = Math.Max(5, Math.Min(120, WaitMinutes));
            SettleSeconds = Math.Max(10, Math.Min(120, SettleSeconds));
            GameDirectory = GameDirectory ?? "";
        }
    }
    [DataContract]
    public class LaunchRequest
    {
        [DataMember] public string GameDirectory;
        [DataMember] public bool Compatibility;
        [DataMember] public int WaitSeconds;
        [DataMember] public int SettleSeconds;
        [DataMember] public int OwnerPid;
        [DataMember] public long OwnerStartTicks;
    }
    [DataContract]
    public class LocaleSnapshot
    {
        [DataMember] public string LocaleName;
        [DataMember] public string DefaultLanguage;
        [DataMember] public string ACP;
        [DataMember] public string OEMCP;
        [DataMember] public string MACCP;
        [DataMember] public uint RuntimeACP;
    }
    [DataContract]
    public class Journal
    {
        [DataMember] public string Id;
        [DataMember] public bool Pending;
        [DataMember] public LocaleSnapshot Original;
        [DataMember] public DateTime CreatedUtc;
        [DataMember] public int WorkerPid;
        [DataMember] public long WorkerStartTicks;
        [DataMember] public string GameDirectory;
        [DataMember(EmitDefaultValue = false)] public string Operation;
        [DataMember(EmitDefaultValue = false)] public LocaleSnapshot Target;
        [DataMember(EmitDefaultValue = false)] public bool Committed;
        [DataMember(EmitDefaultValue = false)] public JournalReference[] Resolves;
    }
    [DataContract]
    public class SessionStatus
    {
        [DataMember] public string Stage;
        [DataMember] public string Message;
        [DataMember] public bool Finished;
        [DataMember] public int ClientPid;
        [DataMember] public long ClientStartTicks;
        [DataMember] public int RemainingSeconds;
        [DataMember] public DateTime UpdatedUtc;
    }
    public class ClientInfo
    {
        public int Id;
        public long StartTicks;
        public bool HasWindow;
    }
    public static class JsonFile
    {
        public static T Read<T>(string path) where T : class
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(input);
        }
        public static T TryRead<T>(string path) where T : class
        {
            try { return Read<T>(path); } catch { return null; }
        }
        public static void Write<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            byte[] data;
            using (var buffer = new MemoryStream()) { new DataContractJsonSerializer(typeof(T)).WriteObject(buffer, value); data = buffer.ToArray(); }
            try
            {
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    try
                    {
                        if (!File.Exists(temp))
                            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(data, 0, data.Length); output.Flush(true); }
                        if (File.Exists(path)) File.Replace(temp, path, null, true);
                        else File.Move(temp, path);
                        return;
                    }
                    catch (IOException)
                    {
                        // Antivirus/indexers may briefly hold a destination without delete sharing.
                        // Recreate the same immutable temp data if a failed replacement consumed it.
                        if (attempt == 7) throw;
                        System.Threading.Thread.Sleep(40 * (attempt + 1));
                    }
                }
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }
    public class SessionFiles
    {
        public readonly string DirectoryPath;
        public SessionFiles(string path) { DirectoryPath = Path.GetFullPath(path); Directory.CreateDirectory(DirectoryPath); }
        public string FilePath(string name) { return Path.Combine(DirectoryPath, name); }
        public void Log(string message)
        {
            try { File.AppendAllText(FilePath("launch.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine, new UTF8Encoding(false)); }
            catch { /* A log write must never interrupt restoration. */ }
        }
        public void Status(string stage, string message, bool finished, ClientInfo client, int remaining)
        {
            JsonFile.Write(FilePath("status.json"), new SessionStatus {
                Stage = stage, Message = message, Finished = finished,
                ClientPid = client == null ? 0 : client.Id,
                ClientStartTicks = client == null ? 0 : client.StartTicks,
                RemainingSeconds = remaining, UpdatedUtc = DateTime.UtcNow
            });
        }
        public Journal ReadJournal() { return JsonFile.TryRead<Journal>(FilePath("recovery.json")); }
        public void SaveJournal(Journal journal) { JsonFile.Write(FilePath("recovery.json"), journal); }
        public static bool NeedsRecovery(string directory)
        {
            string path = Path.Combine(directory, "recovery.json");
            if (!File.Exists(path)) return false;
            var journal = JsonFile.TryRead<Journal>(path);
            bool invalid = !RegionCatalog.ValidJournal(journal, Path.GetFileName(directory));
            return (invalid || journal.Pending) && !RegionCatalog.HasResolution(directory);
        }
    }
    public static class AppPaths
    {
        public static string DataRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaTaleGarden"); } }
        public static string PreferencesPath { get { return Path.Combine(DataRoot, "settings.json"); } }
        public static string SessionsRoot { get { return Path.Combine(DataRoot, "Sessions"); } }
        public static string Executable { get { return System.Reflection.Assembly.GetExecutingAssembly().Location; } }
        public static IEnumerable<string> PendingSessions()
        {
            if (!Directory.Exists(SessionsRoot)) yield break;
            foreach (string directory in Directory.GetDirectories(SessionsRoot))
            {
                if (SessionFiles.NeedsRecovery(directory)) yield return directory;
            }
        }
        public static string LatestSession()
        {
            if (!Directory.Exists(SessionsRoot)) return null;
            var directories = new DirectoryInfo(SessionsRoot).GetDirectories();
            Array.Sort(directories, (a, b) => b.CreationTimeUtc.CompareTo(a.CreationTimeUtc));
            return directories.Length == 0 ? null : directories[0].FullName;
        }
    }
}
