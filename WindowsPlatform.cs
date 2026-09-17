using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace LaTaleGarden
{
    public static class Native
    {
        [DllImport("kernel32.dll")] public static extern uint GetACP();
        [DllImport("kernel32.dll")] public static extern uint GetSystemDefaultLCID();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        private delegate bool EnumCallback(IntPtr window, IntPtr data);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr data);
        public static bool IsAdmin { get { using (var id = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); } }
        public static string ProcessPath(int pid)
        {
            IntPtr handle = OpenProcess(0x1000, false, pid);
            if (handle == IntPtr.Zero) return null;
            try { var value = new StringBuilder(32768); int length = value.Capacity; return QueryFullProcessImageName(handle, 0, value, ref length) ? value.ToString() : null; }
            finally { CloseHandle(handle); }
        }
        public static bool SameProcess(int pid, long ticks)
        {
            IntPtr handle = OpenProcess(0x1000, false, pid);
            if (handle == IntPtr.Zero) return false;
            try
            {
                long created, exited, kernel, user; uint code;
                return GetProcessTimes(handle, out created, out exited, out kernel, out user) && GetExitCodeProcess(handle, out code) && code == 259 && exited == 0 && DateTime.FromFileTimeUtc(created).Ticks == ticks;
            }
            finally { CloseHandle(handle); }
        }
        public static IntPtr VisibleWindow(int pid)
        {
            IntPtr result = IntPtr.Zero;
            EnumWindows(delegate(IntPtr window, IntPtr data) {
                uint owner; GetWindowThreadProcessId(window, out owner);
                if (owner == pid && IsWindowVisible(window)) { result = window; return false; }
                return true;
            }, IntPtr.Zero);
            return result;
        }
        public static void FocusProcess(int pid, long ticks)
        {
            if (!SameProcess(pid, ticks)) return;
            IntPtr window = VisibleWindow(pid);
            if (window == IntPtr.Zero) return;
            if (IsIconic(window)) ShowWindow(window, 9);
            SetForegroundWindow(window);
        }
        public static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
        public static string Quote(string value)
        {
            // CommandLineToArgvW-compatible quoting; never interpolate a path into a shell command.
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') result.Append('\\', slashes * 2 + 1).Append(c);
                else result.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            result.Append('\\', slashes * 2).Append('"');
            return result.ToString();
        }
        public static string OSLabel()
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                int build; int.TryParse(Convert.ToString(key.GetValue("CurrentBuildNumber", "0")), out build);
                return (build >= 22000 ? "Windows 11" : "Windows 10 / 更早版本") + " · " + Convert.ToString(key.GetValue("DisplayVersion", "")) + " · Build " + build;
            }
        }
    }
    internal sealed class ProcessJob : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimit { public long PerProcessTime, PerJobTime; public uint LimitFlags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit { public BasicLimit Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int info, ref ExtendedLimit data, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        private IntPtr handle;
        public ProcessJob()
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) throw new Win32Exception();
            var limits = new ExtendedLimit(); limits.Basic.LimitFlags = 0x2000;
            if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf(limits))) { Dispose(); throw new Win32Exception(); }
        }
        public void Add(Process process)
        {
            if (!AssignProcessToJobObject(handle, process.Handle)) { try { process.Kill(); process.WaitForExit(); } catch { } throw new Win32Exception(); }
        }
        public void Dispose() { if (handle != IntPtr.Zero) { Native.CloseHandle(handle); handle = IntPtr.Zero; } }
    }
    public sealed class WindowsPlatform : ILaunchPlatform
    {
        private const string LanguageKey = @"SYSTEM\CurrentControlSet\Control\Nls\Language";
        private const string CodePageKey = @"SYSTEM\CurrentControlSet\Control\Nls\CodePage";
        private readonly SessionFiles files;
        private readonly LaunchRequest request;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private long launchTicks;
        public WindowsPlatform(SessionFiles files, LaunchRequest request) { this.files = files; this.request = request; }
        public double ElapsedSeconds { get { return clock.Elapsed.TotalSeconds; } }
        public void Sleep(int milliseconds) { Thread.Sleep(milliseconds); }
        public bool Cancelled { get { return File.Exists(files.FilePath("cancel.request")); } }
        public bool OwnerAlive { get { return request == null || Native.SameProcess(request.OwnerPid, request.OwnerStartTicks); } }
        public static void ValidateDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory)) throw new IOException("请先选择完整的游戏目录。");
            if (!File.Exists(Path.Combine(directory, "LaTaleLauncher.exe"))) throw new FileNotFoundException("这个目录中没有 LaTaleLauncher.exe，请选择彩虹岛台服的安装文件夹。");
            if (!File.Exists(Path.Combine(directory, "LaTaleClient.exe"))) throw new FileNotFoundException("这个目录中没有 LaTaleClient.exe，请先完成官方游戏安装。");
            if (!Environment.Is64BitOperatingSystem) throw new PlatformNotSupportedException("当前游戏客户端需要 64 位 Windows。");
        }
        public void ValidateGame(string directory) { ValidateDirectory(directory); }
        public bool ExistingGame(string directory)
        {
            foreach (string name in new[] { "LaTaleClient", "LaTaleLauncher" })
                foreach (var process in Process.GetProcessesByName(name))
                    using (process)
                    {
                        string image = Native.ProcessPath(process.Id);
                        if (image == null || Native.SamePath(image, Path.Combine(directory, name + ".exe"))) return true;
                    }
            return false;
        }
        private static string ReadString(RegistryKey key, string name)
        {
            object value = key.GetValue(name, null);
            if (!(value is string)) throw new InvalidDataException("系统区域值缺失或类型不支持：" + name);
            return (string)value;
        }
        public LocaleSnapshot CaptureLocale()
        {
            using (var language = Registry.LocalMachine.OpenSubKey(LanguageKey))
            using (var cp = Registry.LocalMachine.OpenSubKey(CodePageKey))
            {
                string defaultLanguage = ReadString(language, "Default");
                var snapshot = new LocaleSnapshot {
                    DefaultLanguage = defaultLanguage,
                    LocaleName = CultureInfo.GetCultureInfo(int.Parse(defaultLanguage, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).Name,
                    ACP = ReadString(cp, "ACP"), OEMCP = ReadString(cp, "OEMCP"), MACCP = ReadString(cp, "MACCP"), RuntimeACP = Native.GetACP()
                };
                ValidateSnapshot(snapshot);
                return snapshot;
            }
        }
        public static void ValidateSnapshot(LocaleSnapshot value)
        {
            if (value == null) throw new InvalidDataException("缺少原设置备份。");
            int id;
            if (!int.TryParse(value.DefaultLanguage, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id)) throw new InvalidDataException("原区域标识无效。");
            if (CultureInfo.GetCultureInfo(id).Name != value.LocaleName || string.IsNullOrEmpty(value.LocaleName)) throw new InvalidDataException("原区域备份不一致。");
            foreach (string cp in new[] { value.ACP, value.OEMCP, value.MACCP })
            {
                int number; if (!int.TryParse(cp, out number) || number < 1 || number > 65535) throw new InvalidDataException("原代码页备份无效。");
            }
        }
        private string PowerShell(string script)
        {
            string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            var info = new ProcessStartInfo(executable, "-NoLogo -NoProfile -NonInteractive -OutputFormat Text -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue'; " + script))) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            var output = new StringBuilder();
            using (var job = new ProcessJob())
            using (var process = new Process { StartInfo = info })
            {
                process.OutputDataReceived += (s, e) => { if (e.Data != null) lock (output) { if (output.Length < 16000) output.AppendLine(e.Data); } };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (output) { if (output.Length < 16000) output.AppendLine(e.Data); } };
                process.Start(); job.Add(process);
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
                if (!process.WaitForExit(45000)) { try { process.Kill(); process.WaitForExit(5000); } catch { } throw new TimeoutException("区域设置命令超时。"); }
                process.WaitForExit();
                string text; lock (output) text = output.ToString();
                if (process.ExitCode != 0) throw new InvalidOperationException("区域设置命令失败（" + process.ExitCode + "）：" + text.Trim());
                return text.Trim();
            }
        }
        private void SetLocale(string name)
        {
            string validated = CultureInfo.GetCultureInfo(name).Name;
            foreach (char c in validated) if (!char.IsLetterOrDigit(c) && c != '-') throw new InvalidDataException("区域名称无效。");
            PowerShell("$ErrorActionPreference='Stop'; try { Import-Module International -ErrorAction Stop; Set-WinSystemLocale -SystemLocale '" + validated + "' -ErrorAction Stop; Write-Output 'OK' } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }");
        }
        public string ReadOnlyCommandCheck()
        {
            return "ReadOnlyPowerShell=" + PowerShell("$ErrorActionPreference='Stop'; try { Import-Module International -ErrorAction Stop; Write-Output ((Get-WinSystemLocale).Name) } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }");
        }
        public void ApplyTraditional()
        {
            SetLocale("zh-TW");
            LocaleSnapshot current = CaptureLocale();
            if (current.LocaleName != "zh-TW" || current.ACP != "950") throw new IOException("繁体区域设置未写入预期值，已停止启动并尝试恢复。");
            files.Log("繁体系统区域已写入；ACP 配置=950。命令成功不代表游戏内部编码已生效。");
        }
        public void Restore(LocaleSnapshot original)
        {
            ValidateSnapshot(original);
            SetLocale(original.LocaleName);
            // Preserve exact pre-launch code-page values, including non-default OEM/Mac choices.
            using (var cp = Registry.LocalMachine.OpenSubKey(CodePageKey, true))
            using (var language = Registry.LocalMachine.OpenSubKey(LanguageKey, true))
            {
                cp.SetValue("ACP", original.ACP, RegistryValueKind.String);
                cp.SetValue("OEMCP", original.OEMCP, RegistryValueKind.String);
                cp.SetValue("MACCP", original.MACCP, RegistryValueKind.String);
                language.SetValue("Default", original.DefaultLanguage, RegistryValueKind.String);
                cp.Flush(); language.Flush();
            }
        }
        public bool Matches(LocaleSnapshot original)
        {
            var current = CaptureLocale();
            return current.DefaultLanguage == original.DefaultLanguage && current.ACP == original.ACP && current.OEMCP == original.OEMCP && current.MACCP == original.MACCP;
        }
        public void StartGuard()
        {
            File.Delete(files.FilePath("guard.ready"));
            var current = Process.GetCurrentProcess();
            var info = new ProcessStartInfo(AppPaths.Executable, "--guard " + Native.Quote(files.DirectoryPath) + " " + current.Id + " " + current.StartTime.ToUniversalTime().Ticks) {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(AppPaths.Executable)
            };
            using (var guard = Process.Start(info))
            {
                var timer = Stopwatch.StartNew();
                while (!File.Exists(files.FilePath("guard.ready")))
                {
                    if (guard.HasExited || timer.Elapsed.TotalSeconds > 12) throw new IOException("独立恢复进程未就绪，未修改系统区域。");
                    Thread.Sleep(100);
                }
            }
            files.Log("独立恢复进程已就绪。");
        }
        public void StartOfficialLauncher(string directory)
        {
            launchTicks = DateTime.UtcNow.Ticks;
            var info = new ProcessStartInfo(Path.Combine(directory, "LaTaleLauncher.exe")) { WorkingDirectory = directory, UseShellExecute = true };
            using (var process = Process.Start(info))
            {
                if (process == null) throw new IOException("官方启动器未返回进程信息。");
                File.WriteAllText(files.FilePath("launcher.pid"), process.Id.ToString(CultureInfo.InvariantCulture));
            }
        }
        public ClientInfo FindClient(string directory)
        {
            foreach (var process in Process.GetProcessesByName("LaTaleClient"))
                using (process)
                {
                    try
                    {
                        string image = Native.ProcessPath(process.Id);
                        if (!Native.SamePath(image, Path.Combine(directory, "LaTaleClient.exe"))) continue;
                        long ticks = process.StartTime.ToUniversalTime().Ticks;
                        if (ticks < launchTicks || process.HasExited) continue;
                        return new ClientInfo { Id = process.Id, StartTicks = ticks, HasWindow = Native.VisibleWindow(process.Id) != IntPtr.Zero };
                    }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception ex) { files.Log("暂时无法读取客户端状态：" + ex.Message); }
                }
            return null;
        }
        public void Probe()
        {
            try
            {
                var info = new ProcessStartInfo(AppPaths.Executable, "--probe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                using (var process = Process.Start(info))
                {
                    if (process.WaitForExit(5000)) files.Log("新建 x64 检查进程：" + process.StandardOutput.ReadToEnd().Trim());
                    else { process.Kill(); files.Log("新进程编码检查超时，未用该检查判断游戏文字。"); }
                }
            }
            catch (Exception ex) { files.Log("编码检查未完成：" + ex.Message); }
        }
    }
}
