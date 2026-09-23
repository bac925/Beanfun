using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Beanfun.GameMaintenance
{
    public sealed class TmsProductInfo
    {
        [JsonProperty("productName")] public string ProductName { get; set; } = "";
        [JsonProperty("productId")] public string ProductId { get; set; } = "";
        [JsonProperty("version")] public string Version { get; set; } = "";
        [JsonProperty("sizeInBytes")] public long SizeInBytes { get; set; }
        [JsonProperty("baseUrl")] public string BaseUrl { get; set; } = "";
        [JsonProperty("executionPath")] public string ExecutionPath { get; set; } = "";
        [JsonProperty("files")] public List<TmsFileItem> Files { get; set; } = new();
    }

    public sealed class TmsFileItem
    {
        [JsonProperty("path")] public string Path { get; set; } = "";
        [JsonProperty("sizeInBytes")] public long SizeInBytes { get; set; }
        [JsonProperty("sha256")] public string Sha256 { get; set; } = "";
    }

    public enum VerifyState { Ok, Missing, SizeMismatch, HashMismatch, Error, Skipped }

    public sealed class VerifyResult
    {
        public TmsFileItem File { get; init; } = new();
        public VerifyState State { get; init; }
        public string Detail { get; init; } = "";
        public bool NeedsRepair => State is VerifyState.Missing or VerifyState.SizeMismatch or VerifyState.HashMismatch;
    }

    public sealed class VerifyProgress
    {
        public int Current { get; init; }
        public int Total { get; init; }
        public string Path { get; init; } = "";
    }

    public sealed class TmsManifestService
    {
        public const string ProductInfoUrl = "https://maplestory-download.beanfun.com/maplestory/productInfo.json";
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

        public async Task<TmsProductInfo> GetProductInfoAsync(CancellationToken ct)
        {
            using var response = await Http.GetAsync(ProductInfoUrl, ct);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync(ct);
            return JsonConvert.DeserializeObject<TmsProductInfo>(json)
                ?? throw new InvalidDataException("官方 productInfo.json 格式無法解析。");
        }

        public static string GetGameRoot(string configuredPath)
        {
            if (string.IsNullOrWhiteSpace(configuredPath)) return "";
            string p = configuredPath.Trim().Trim('"');
            if (Directory.Exists(p)) return Path.GetFullPath(p);
            if (File.Exists(p) || Path.HasExtension(p))
                return Path.GetDirectoryName(Path.GetFullPath(p)) ?? "";
            return Path.GetFullPath(p);
        }

        public static string BuildDownloadUrl(TmsProductInfo info, TmsFileItem file)
        {
            string execution = info.ExecutionPath.Replace('\\', '/');
            int slash = execution.LastIndexOf('/');
            string basePath = slash >= 0 ? execution[..slash] : "";
            string rel = file.Path.Replace('\\', '/').TrimStart('/');
            string remote = string.IsNullOrEmpty(basePath) ? rel : $"{basePath}/{rel}";
            return $"{info.BaseUrl.TrimEnd('/')}/{remote}";
        }

        public async Task<List<VerifyResult>> VerifyAsync(
            string gameRoot,
            TmsProductInfo info,
            IProgress<VerifyProgress>? progress,
            CancellationToken ct)
        {
            var results = new List<VerifyResult>(info.Files.Count);
            int current = 0;
            foreach (var file in info.Files)
            {
                ct.ThrowIfCancellationRequested();
                current++;
                progress?.Report(new VerifyProgress { Current = current, Total = info.Files.Count, Path = file.Path });

                // GGM may apply ExePatch.dat to MapleStory.exe. The pristine manifest hash can then differ legitimately.
                if (file.Path.Equals("MapleStory.exe", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new VerifyResult { File = file, State = VerifyState.Skipped, Detail = "第一階段暫不驗證主程式（需搭配 ExePatch.dat 判定）" });
                    continue;
                }

                try
                {
                    string local = SafeLocalPath(gameRoot, file.Path);
                    if (!File.Exists(local))
                    {
                        results.Add(new VerifyResult { File = file, State = VerifyState.Missing, Detail = "檔案不存在" });
                        continue;
                    }
                    long len = new FileInfo(local).Length;
                    if (len != file.SizeInBytes)
                    {
                        results.Add(new VerifyResult { File = file, State = VerifyState.SizeMismatch, Detail = $"大小 {len:N0} / {file.SizeInBytes:N0}" });
                        continue;
                    }
                    string hash = await Sha256FileAsync(local, ct);
                    results.Add(new VerifyResult
                    {
                        File = file,
                        State = hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase) ? VerifyState.Ok : VerifyState.HashMismatch,
                        Detail = hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase) ? "正常" : "SHA-256 不符"
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    results.Add(new VerifyResult { File = file, State = VerifyState.Error, Detail = ex.Message });
                }
            }
            return results;
        }

        public async Task RepairAsync(
            string gameRoot,
            TmsProductInfo info,
            IEnumerable<VerifyResult> results,
            IProgress<VerifyProgress>? progress,
            CancellationToken ct)
        {
            var targets = results.Where(x => x.NeedsRepair).ToList();
            int current = 0;
            foreach (var result in targets)
            {
                ct.ThrowIfCancellationRequested();
                current++;
                progress?.Report(new VerifyProgress { Current = current, Total = targets.Count, Path = result.File.Path });
                await DownloadAndReplaceAsync(gameRoot, info, result.File, ct);
            }
        }

        private static async Task DownloadAndReplaceAsync(string gameRoot, TmsProductInfo info, TmsFileItem file, CancellationToken ct)
        {
            string dest = SafeLocalPath(gameRoot, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            string temp = dest + ".bacdownload";
            string backup = dest + ".bacbak";
            try
            {
                using (var response = await Http.GetAsync(BuildDownloadUrl(info, file), HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    response.EnsureSuccessStatusCode();
                    await using var input = await response.Content.ReadAsStreamAsync(ct);
                    await using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
                    await input.CopyToAsync(output, 1024 * 1024, ct);
                }

                var fi = new FileInfo(temp);
                if (fi.Length != file.SizeInBytes)
                    throw new InvalidDataException($"下載後大小不符：{fi.Length:N0} / {file.SizeInBytes:N0}");
                string hash = await Sha256FileAsync(temp, ct);
                if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("下載後 SHA-256 驗證失敗，未替換原檔。");

                if (File.Exists(backup)) File.Delete(backup);
                if (File.Exists(dest)) File.Move(dest, backup);
                try
                {
                    File.Move(temp, dest);
                    if (File.Exists(backup)) File.Delete(backup);
                }
                catch
                {
                    if (File.Exists(dest)) File.Delete(dest);
                    if (File.Exists(backup)) File.Move(backup, dest);
                    throw;
                }
            }
            finally
            {
                if (File.Exists(temp))
                {
                    try { File.Delete(temp); } catch { }
                }
            }
        }

        private static string SafeLocalPath(string root, string relative)
        {
            string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string rel = relative.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            string full = Path.GetFullPath(Path.Combine(rootFull, rel));
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Manifest 包含不安全的檔案路徑。");
            return full;
        }

        private static async Task<string> Sha256FileAsync(string path, CancellationToken ct)
        {
            using var sha = SHA256.Create();
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
            byte[] buffer = new byte[1024 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                sha.TransformBlock(buffer, 0, read, null, 0);
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }
    }
}
