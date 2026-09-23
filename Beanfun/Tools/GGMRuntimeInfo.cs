using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Beanfun
{
    /// <summary>
    /// Gamania Games Manager runtime information used by get_webstart_otp_v2.
    ///
    /// Official GGMWebStart 1.5.x behavior:
    ///   CV   = GGMWebStart assembly version
    ///   Hash = SHA256(GGMWebStart.dll), lower-case hex
    ///   arch = x64 / x86
    ///
    /// This class initializes automatically when the application assembly is loaded.
    /// Failure to find GGM does NOT prevent the launcher from starting; GetOTP will
    /// show the actual error only when OTP is requested.
    /// </summary>
    internal static class GGMRuntimeInfo
    {
        private static readonly object SyncRoot = new object();

        public static string InstallDirectory { get; private set; } = "";
        public static string DllPath { get; private set; } = "";
        public static string Version { get; private set; } = "";
        public static string Hash { get; private set; } = "";
        public static string Arch { get; private set; } = "";
        public static string LastError { get; private set; } = "";

        public static bool IsAvailable =>
            !string.IsNullOrWhiteSpace(DllPath)
            && File.Exists(DllPath)
            && !string.IsNullOrWhiteSpace(Version)
            && !string.IsNullOrWhiteSpace(Hash)
            && !string.IsNullOrWhiteSpace(Arch);

        [ModuleInitializer]
        internal static void ModuleInitialize()
        {
            try
            {
                Initialize();
            }
            catch
            {
                // Never block application startup because GGM is missing/broken.
            }
        }

        public static bool Initialize(bool force = false)
        {
            lock (SyncRoot)
            {
                if (!force && IsAvailable)
                    return true;

                Clear();

                try
                {
                    string dll = FindGGMWebStartDll();
                    if (string.IsNullOrWhiteSpace(dll))
                    {
                        LastError =
                            "找不到 GGMWebStart.dll。預設應安裝於 " +
                            @"C:\Program Files\gamania Games\gamania Games Manager";
                        Debug.WriteLine("[GGM] " + LastError);
                        return false;
                    }

                    DllPath = dll;
                    InstallDirectory = Path.GetDirectoryName(dll) ?? "";

                    Version = ReadAssemblyVersion(dll);
                    Hash = ComputeSha256(dll);
                    Arch = DetectArchitecture(InstallDirectory);

                    if (string.IsNullOrWhiteSpace(Version))
                    {
                        LastError = "無法讀取 GGMWebStart.dll Assembly Version";
                        return false;
                    }

                    if (string.IsNullOrWhiteSpace(Hash))
                    {
                        LastError = "無法計算 GGMWebStart.dll SHA256";
                        return false;
                    }

                    if (string.IsNullOrWhiteSpace(Arch))
                        Arch = Environment.Is64BitOperatingSystem ? "x64" : "x86";

                    LastError = "";

                    Debug.WriteLine(
                        $"[GGM] Found={DllPath} CV={Version} arch={Arch} Hash={Hash}"
                    );

                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Debug.WriteLine("[GGM] Initialize failed: " + ex);
                    Clear(keepError: true);
                    return false;
                }
            }
        }

        public static bool EnsureInitialized()
        {
            return IsAvailable || Initialize();
        }

        public static bool Refresh()
        {
            return Initialize(force: true);
        }

        private static void Clear(bool keepError = false)
        {
            InstallDirectory = "";
            DllPath = "";
            Version = "";
            Hash = "";
            Arch = "";
            if (!keepError)
                LastError = "";
        }

        private static string FindGGMWebStartDll()
        {
            var candidateDirs = new List<string>();

            // Optional custom path for advanced users/development.
            AddDirectory(candidateDirs, Environment.GetEnvironmentVariable("GGM_HOME"));

            // 64-bit Program Files, even when this launcher itself is x86.
            AddDirectory(
                candidateDirs,
                CombineSafe(
                    Environment.GetEnvironmentVariable("ProgramW6432"),
                    "gamania Games",
                    "gamania Games Manager"
                )
            );

            AddDirectory(
                candidateDirs,
                CombineSafe(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "gamania Games",
                    "gamania Games Manager"
                )
            );

            AddDirectory(
                candidateDirs,
                CombineSafe(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "gamania Games",
                    "gamania Games Manager"
                )
            );

            // Explicit default path as a final normal candidate.
            AddDirectory(
                candidateDirs,
                @"C:\Program Files\gamania Games\gamania Games Manager"
            );

            AddDirectory(
                candidateDirs,
                @"C:\Program Files (x86)\gamania Games\gamania Games Manager"
            );

            foreach (string dir in candidateDirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;

                string dll = Path.Combine(dir, "GGMWebStart.dll");
                if (File.Exists(dll))
                    return Path.GetFullPath(dll);
            }

            return "";
        }

        private static void AddDirectory(List<string> dirs, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                dirs.Add(value.Trim().Trim('"'));
        }

        private static string CombineSafe(string root, params string[] parts)
        {
            if (string.IsNullOrWhiteSpace(root))
                return "";

            string result = root;
            foreach (string part in parts)
                result = Path.Combine(result, part);
            return result;
        }

        private static string ReadAssemblyVersion(string dll)
        {
            try
            {
                AssemblyName name = AssemblyName.GetAssemblyName(dll);
                if (name.Version != null)
                    return name.Version.ToString();
            }
            catch
            {
            }

            // Fallback only; normally AssemblyName above is the value official GGM uses.
            try
            {
                return FileVersionInfo.GetVersionInfo(dll).FileVersion ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static string ComputeSha256(string dll)
        {
            using (FileStream stream = File.OpenRead(dll))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(stream);
                return BitConverter
                    .ToString(digest)
                    .Replace("-", "")
                    .ToLowerInvariant();
            }
        }

        private static string DetectArchitecture(string installDir)
        {
            // The official value is the GGMWebStart process architecture.
            // Current GGM packages expose their RID in GGMWebStart.deps.json.
            try
            {
                string deps = Path.Combine(installDir, "GGMWebStart.deps.json");
                if (File.Exists(deps))
                {
                    string text = File.ReadAllText(deps);

                    Match rid = Regex.Match(
                        text,
                        @"""name""\s*:\s*""[^""]*/win-(x64|x86|arm64)""",
                        RegexOptions.IgnoreCase
                    );

                    if (rid.Success)
                    {
                        string value = rid.Groups[1].Value.ToLowerInvariant();
                        if (value == "x64" || value == "x86")
                            return value;
                    }

                    if (text.IndexOf("win-x64", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "x64";

                    if (text.IndexOf("win-x86", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "x86";
                }
            }
            catch
            {
            }

            // Current official package is normally x64 on a 64-bit Windows host.
            return Environment.Is64BitOperatingSystem ? "x64" : "x86";
        }
    }
}
