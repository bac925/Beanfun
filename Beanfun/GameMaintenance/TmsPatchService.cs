using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Beanfun.GameMaintenance;

public sealed class PatchProgress
{
    public string Phase { get; init; } = "";
    public string Detail { get; init; } = "";
    public long Current { get; init; }
    public long Total { get; init; }
}

public sealed record PatchHop(int From, int To, string Url, long Size);

public sealed class TmsPatchService
{
    private const uint EndMarker = 0xF2F7FBF3;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("WzPatch\x1A");
    private static readonly HttpClient Http = CreateHttp();
    private const int BufSize = 1024 * 1024;

    private static HttpClient CreateHttp()
    {
        var h = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None, AllowAutoRedirect = true };
        return new HttpClient(h) { Timeout = TimeSpan.FromMinutes(30) };
    }

    public static string BuildPatchUrl(int oldVer, int newVer) =>
        $"http://tw.cdnpatch.maplestory.beanfun.com/maplestory/patch/patchdir/{newVer:00000}/{oldVer:00000}to{newVer:00000}.patch";
    public static string BuildExePatchUrl(int version) =>
        $"http://tw.cdnpatch.maplestory.beanfun.com/maplestory/patch/patchdir/{version:00000}/ExePatch.dat";

    public async Task<List<PatchHop>> PlanRouteAsync(int current, int target, IProgress<PatchProgress>? progress, CancellationToken ct)
    {
        var route = new List<PatchHop>();
        while (current < target)
        {
            PatchHop? found = null;
            for (int next = target; next > current; next--)
            {
                ct.ThrowIfCancellationRequested();
                string url = BuildPatchUrl(current, next);
                progress?.Report(new PatchProgress { Phase = "探測更新", Detail = $"V{current} → V{next}" });
                long size = await ProbeSizeAsync(url, ct);
                if (size > 0) { found = new PatchHop(current, next, url, size); break; }
            }
            if (found == null) throw new InvalidOperationException($"官方 CDN 找不到從 V{current} 往 V{target} 的可用更新檔。可改用完整檔案修復/重新下載。 ");
            route.Add(found);
            current = found.To;
        }
        return route;
    }

    public async Task UpdateAsync(string root, int current, int target, TmsProductInfo info, int? targetMinor,
        IProgress<PatchProgress>? progress, CancellationToken ct)
    {
        if (current > target) throw new InvalidOperationException("本機版本高於官方版本，拒絕降版。 ");
        Directory.CreateDirectory(Path.Combine(root, "patchdata"));
        if (current < target)
        {
            var route = await PlanRouteAsync(current, target, progress, ct);
            foreach (var hop in route)
            {
                ct.ThrowIfCancellationRequested();
                string patch = Path.Combine(root, "patchdata", $"{hop.From:00000}to{hop.To:00000}.patch");
                await DownloadWithResumeAsync(hop.Url, patch, hop.Size, progress, ct);
                var corrupted = await Task.Run(() => ApplyPatch(patch, root, progress, ct), ct);
                try { File.Delete(patch); } catch { }
                int actual = ReadLocalVersion(root);
                if (actual != hop.To) throw new InvalidDataException($"套用 Patch 後版本驗證失敗：預期 V{hop.To}，實際 V{actual}。 ");
                if (corrupted.Count > 0)
                {
                    progress?.Report(new PatchProgress { Phase = "修復 Patch 異常檔", Detail = $"{corrupted.Count} 個檔案" });
                    var repair = new TmsManifestService();
                    var map = info.Files.ToDictionary(x => Normalize(x.Path), StringComparer.OrdinalIgnoreCase);
                    var results = corrupted.Distinct(StringComparer.OrdinalIgnoreCase)
                        .Where(x => map.ContainsKey(Normalize(x)))
                        .Select(x => new VerifyResult { File = map[Normalize(x)], State = VerifyState.HashMismatch, Detail = "Patch 套用失敗，改抓官方完整檔" }).ToList();
                    await repair.RepairAsync(root, info, results, null, ct);
                }
            }
        }
        await ApplyExePatchIfAvailableAsync(root, target, targetMinor, progress, ct);
    }

    public async Task<bool> ApplyExePatchIfAvailableAsync(string root, int version, int? targetMinor, IProgress<PatchProgress>? progress, CancellationToken ct)
    {
        string existingExe = Path.Combine(root, "MapleStory.exe");
        if (File.Exists(existingExe) && targetMinor.HasValue)
        {
            var local = FileVersionInfo.GetVersionInfo(existingExe);
            if (local.ProductMinorPart == version && local.FileBuildPart >= targetMinor.Value)
                return false;
        }
        string url = BuildExePatchUrl(version);
        long size = await ProbeSizeAsync(url, ct);
        ct.ThrowIfCancellationRequested();
        if (size <= 0) return false;
        string tmp = Path.Combine(root, "ExePatch.dat");
        progress?.Report(new PatchProgress { Phase = "主程式 Hotfix", Detail = $"下載 ExePatch.dat ({FormatBytes(size)})" });
        await DownloadWithResumeAsync(url, tmp, size, progress, ct);
        if (new FileInfo(tmp).Length != size) throw new InvalidDataException("ExePatch.dat 大小驗證失敗。 ");
        var downloaded = FileVersionInfo.GetVersionInfo(tmp);
        if (downloaded.ProductMinorPart != version ||
            (targetMinor.HasValue && downloaded.FileBuildPart != targetMinor.Value))
            throw new InvalidDataException($"下載的 ExePatch.dat 版本不符：預期 V{version}.{targetMinor}，實際 {downloaded.ProductMinorPart}.{downloaded.FileBuildPart}；未替換遊戲主程式。");
        ct.ThrowIfCancellationRequested();
        string exe = existingExe;
        string bak = exe + ".bacbak";
        try
        {
            if (File.Exists(bak)) File.Delete(bak);
            if (File.Exists(exe)) File.Move(exe, bak);
            File.Move(tmp, exe, true);
            if (File.Exists(bak)) File.Delete(bak);
        }
        catch
        {
            if (!File.Exists(exe) && File.Exists(bak)) File.Move(bak, exe);
            throw;
        }
        return true;
    }

    public async Task<long> ProbeSizeAsync(string url, CancellationToken ct)
    {
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var r = await Http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct);
            if (r.StatusCode == HttpStatusCode.NotFound) return 0;
            if (r.IsSuccessStatusCode && r.Content.Headers.ContentLength is long n && n > 0) return n;
        }
        catch { }
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(0, 0);
            using var r = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (r.StatusCode == HttpStatusCode.NotFound) return 0;
            if (!r.IsSuccessStatusCode) return 0;
            return r.Content.Headers.ContentRange?.Length ?? r.Content.Headers.ContentLength ?? 0;
        }
        catch { return 0; }
    }

    private async Task DownloadWithResumeAsync(string url, string dest, long size, IProgress<PatchProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        long pos = File.Exists(dest) ? new FileInfo(dest).Length : 0;
        if (pos > size) { File.Delete(dest); pos = 0; }
        while (pos < size)
        {
            ct.ThrowIfCancellationRequested();
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (pos > 0) req.Headers.Range = new RangeHeaderValue(pos, null);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            if (pos > 0 && resp.StatusCode != HttpStatusCode.PartialContent) { File.Delete(dest); pos = 0; continue; }
            await using var input = await resp.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(dest, pos == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.Read, BufSize, true);
            byte[] buf = new byte[BufSize]; int n;
            while ((n = await input.ReadAsync(buf.AsMemory(), ct)) > 0)
            {
                await output.WriteAsync(buf.AsMemory(0, n), ct); pos += n;
                progress?.Report(new PatchProgress { Phase = "下載更新", Detail = Path.GetFileName(dest), Current = pos, Total = size });
            }
        }
        if (new FileInfo(dest).Length != size) throw new IOException("更新檔下載不完整。 ");
    }

    private sealed record Part(byte Type, string Name, uint A, uint B, uint Length, long Offset);

    private List<string> ApplyPatch(string patchPath, string root, IProgress<PatchProgress>? progress, CancellationToken ct)
    {
        string work = Path.Combine(root, "patchdata", ".bac_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        string streamPath = Path.Combine(work, "patch_stream.bin");
        try
        {
            DecompressPatch(patchPath, streamPath);
            using var stream = new FileStream(streamPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var (parts, kmst, oldHashes) = ParseParts(stream);
            var corrupted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // DeadPatch pre-validation. Failures are not fatal; failed parts fall back to full-file repair.
            var newHashes = parts.Where(p => p.Type == 1).ToDictionary(p => Normalize(p.Name), p => p.B, StringComparer.OrdinalIgnoreCase);
            IEnumerable<KeyValuePair<string,uint>> checks = kmst ? oldHashes : parts.Where(p => p.Type == 1).Select(p => new KeyValuePair<string,uint>(p.Name, p.A));
            foreach (var kv in checks)
            {
                ct.ThrowIfCancellationRequested();
                string local = SafePath(root, kv.Key);
                try
                {
                    if (!File.Exists(local)) { corrupted.Add(kv.Key); continue; }
                    uint crc = Crc32File(local);
                    if (newHashes.TryGetValue(Normalize(kv.Key), out uint newer) && crc == newer) continue;
                    if (crc != kv.Value) corrupted.Add(kv.Key);
                }
                catch { corrupted.Add(kv.Key); }
            }

            // Dependency ownership for KMST1125 DeadPatch.
            var deps = parts.Select(p => p.Type == 1 ? CollectDeps(stream, p.Offset, kmst) : new HashSet<string>(StringComparer.OrdinalIgnoreCase)).ToList();
            var nameToIndex = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
            var ownerName = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            for (int i=0;i<parts.Count;i++) if (parts[i].Type != 2)
            {
                string key=Normalize(parts[i].Name); nameToIndex[key]=i; ownerName[key]=key;
                foreach(var dep in deps[i]) ownerName[Normalize(dep)] = key;
            }
            int[] owner = new int[parts.Count];
            for(int i=0;i<parts.Count;i++)
            {
                string key=Normalize(parts[i].Name);
                owner[i] = ownerName.TryGetValue(key,out var o) && nameToIndex.TryGetValue(o,out var oi) ? oi : i;
            }

            var pending = new List<int>();
            long total = parts.Where(p=>p.Type!=2).Sum(p=>(long)p.Length); long done=0;
            for(int i=0;i<parts.Count;i++)
            {
                ct.ThrowIfCancellationRequested(); var p=parts[i]; if(p.Type==2) continue;
                bool ok;
                try { ok = p.Type==0 ? ApplyCreate(stream,p,work) : ApplyRebuild(stream,p,work,root,kmst,corrupted); }
                catch { ok=false; }
                if(!ok) corrupted.Add(p.Name); else pending.Add(i);
                done += p.Length; progress?.Report(new PatchProgress{Phase="套用更新",Detail=p.Name,Current=done,Total=total});
                foreach(var idx in pending.ToArray())
                {
                    if(owner[idx]==i && !IsBase(parts[idx].Name)) { Commit(parts[idx],work,root,corrupted); pending.Remove(idx); }
                }
            }
            foreach(var p in parts.Where(x=>x.Type==2)) { string path=SafePath(root,p.Name); try { if(Directory.Exists(path)) Directory.Delete(path,true); else if(File.Exists(path)) { File.SetAttributes(path,FileAttributes.Normal); File.Delete(path); } } catch { } }
            foreach(var idx in pending.OrderBy(i=>IsBase(parts[i].Name)?1:0)) Commit(parts[idx],work,root,corrupted);
            return corrupted.ToList();
        }
        finally { try { Directory.Delete(work,true); } catch { } }
    }

    private static void DecompressPatch(string patchPath,string outPath)
    {
        using var f=new FileStream(patchPath,FileMode.Open,FileAccess.Read,FileShare.Read);
        var (start,len)=LocateBlock(f); f.Position=start; byte[] h=new byte[16]; f.ReadExactly(h);
        if(!h.AsSpan(0,8).SequenceEqual(Magic)) throw new InvalidDataException("不是有效的 WzPatch。 ");
        long bodyLen=len-16; var limited=new LimitedStream(f,bodyLen); int a=limited.ReadByte(), b=limited.ReadByte(); limited.Rewind2((byte)a,(byte)b);
        bool z=a==0x78 && (((a<<8)+b)%31==0);
        using Stream dec=z?new ZLibStream(limited,CompressionMode.Decompress):new DeflateStream(limited,CompressionMode.Decompress);
        using var o=new FileStream(outPath,FileMode.Create,FileAccess.Write,FileShare.None); dec.CopyTo(o,BufSize);
    }

    private static (long start,long len) LocateBlock(FileStream f)
    {
        long n=f.Length; byte[] b4=new byte[4];
        if(n>=12){f.Position=n-4;f.ReadExactly(b4);if(BinaryPrimitives.ReadUInt32LittleEndian(b4)==EndMarker){byte[] x=new byte[8];f.Position=n-12;f.ReadExactly(x);long l=BinaryPrimitives.ReadUInt32LittleEndian(x.AsSpan(0,4));long e=n-12;if(l<=e)return(e-l,l);}}
        if(n>=24){byte[] x=new byte[8];f.Position=n-8;f.ReadExactly(x);if(BinaryPrimitives.ReadUInt32LittleEndian(x.AsSpan(0,4))==EndMarker&&BinaryPrimitives.ReadUInt32LittleEndian(x.AsSpan(4,4))==0){f.Position=n-24;f.ReadExactly(x);long l=(long)BinaryPrimitives.ReadUInt64LittleEndian(x);long e=n-24;if(l<=e)return(e-l,l);}}
        f.Position=0; byte[] buf=new byte[BufSize+7]; int carry=0; long pos=0;
        while(pos<n){int read=f.Read(buf,carry,Math.Min(BufSize,(int)Math.Min(int.MaxValue,n-pos)));int total=carry+read;for(int i=0;i<=total-8;i++)if(buf.AsSpan(i,8).SequenceEqual(Magic))return(pos-carry+i,n-(pos-carry+i));carry=Math.Min(7,total);Array.Copy(buf,total-carry,buf,0,carry);pos+=read;}
        return(0,n);
    }

    private static (List<Part>,bool,Dictionary<string,uint>) ParseParts(FileStream s)
    {
        bool kmst=false; var hashes=new Dictionary<string,uint>(StringComparer.OrdinalIgnoreCase); long start=s.Position;
        try { int count=ReadI32(s); if(count>0&&count<=500000){for(int i=0;i<count;i++){int l=ReadI32(s);if(l<=0||l>260)throw new Exception();byte[] n=new byte[l];s.ReadExactly(n);hashes[Encoding.UTF8.GetString(n)]=ReadU32(s);}kmst=true;} else throw new Exception(); } catch { kmst=false;hashes.Clear();s.Position=start; }
        var parts=new List<Part>();
        while(s.Position<s.Length){var (name,type)=ReadNameType(s);if(type<0||type>2)break;if(type==0){if(!Path.HasExtension(name))continue;uint len=(uint)ReadI32(s),crc=ReadU32(s);long off=s.Position;s.Position+=len;parts.Add(new Part(0,name,crc,0,len,off));}else if(type==1){uint old=kmst?(hashes.TryGetValue(name,out var c)?c:0):ReadU32(s);uint neu=ReadU32(s);long off=s.Position;uint len=SkipInstructions(s,kmst);parts.Add(new Part(1,name,old,neu,len,off));}else parts.Add(new Part(2,name,0,0,0,0));}
        return(parts,kmst,hashes);
    }

    private static HashSet<string> CollectDeps(FileStream s,long off,bool kmst)
    { var r=new HashSet<string>(StringComparer.OrdinalIgnoreCase);if(!kmst)return r;long save=s.Position;s.Position=off;try{while(true){uint cmd=ReadU32(s);if(cmd==0)break;uint kind=cmd>>28;if(kind==8)s.Position+=(cmd&0x0FFFFFFF);else if(kind==12){}else{ReadI32(s);int l=ReadI32(s);if(l>0&&l<=260){byte[] b=new byte[l];s.ReadExactly(b);r.Add(Encoding.UTF8.GetString(b));}}}}finally{s.Position=save;}return r; }

    private static bool ApplyCreate(FileStream s,Part p,string work)
    { string dest=SafePath(work,p.Name);Directory.CreateDirectory(Path.GetDirectoryName(dest)!);using var o=new FileStream(dest,FileMode.Create,FileAccess.Write);long save=s.Position;s.Position=p.Offset;uint crc=0;byte[] b=new byte[BufSize];long left=p.Length;while(left>0){int n=s.Read(b,0,(int)Math.Min(b.Length,left));if(n<=0)throw new EndOfStreamException();o.Write(b,0,n);crc=CrcUpdate(crc,b.AsSpan(0,n));left-=n;}s.Position=save;return crc==p.A; }

    private static bool ApplyRebuild(FileStream s,Part p,string work,string root,bool kmst,HashSet<string> corrupt)
    {
        string dest=SafePath(work,p.Name);Directory.CreateDirectory(Path.GetDirectoryName(dest)!);using var o=new FileStream(dest,FileMode.Create,FileAccess.Write);long save=s.Position;s.Position=p.Offset;uint crc=0;byte[] buf=new byte[BufSize];
        try{while(true){uint cmd=ReadU32(s);if(cmd==0)break;uint kind=cmd>>28;if(kind==8){long left=cmd&0x0FFFFFFF;while(left>0){int n=s.Read(buf,0,(int)Math.Min(buf.Length,left));if(n<=0)throw new EndOfStreamException();o.Write(buf,0,n);crc=CrcUpdate(crc,buf.AsSpan(0,n));left-=n;}}else if(kind==12){int len=(int)((cmd&0x0FFFFF00)>>8);byte val=(byte)(cmd&0xff);Array.Fill(buf,val);while(len>0){int n=Math.Min(len,buf.Length);o.Write(buf,0,n);crc=CrcUpdate(crc,buf.AsSpan(0,n));len-=n;}}else{long len=cmd;int oldOff=ReadI32(s);string src=p.Name;if(kmst){int l=ReadI32(s);byte[] nb=new byte[l];s.ReadExactly(nb);src=Encoding.UTF8.GetString(nb);}string sp=SafePath(root,src);if(!File.Exists(sp))return false;using var input=new FileStream(sp,FileMode.Open,FileAccess.Read,FileShare.Read);if(oldOff<0||oldOff+len>input.Length)return false;input.Position=oldOff;while(len>0){int n=input.Read(buf,0,(int)Math.Min(buf.Length,len));if(n<=0)return false;o.Write(buf,0,n);crc=CrcUpdate(crc,buf.AsSpan(0,n));len-=n;}}}return crc==p.B;}finally{s.Position=save;}
    }

    private static void Commit(Part p,string work,string root,HashSet<string> corrupt)
    { if(corrupt.Contains(p.Name))return;string src=SafePath(work,p.Name),dst=SafePath(root,p.Name);if(!File.Exists(src)){corrupt.Add(p.Name);return;}try{Directory.CreateDirectory(Path.GetDirectoryName(dst)!);if(File.Exists(dst)){File.SetAttributes(dst,FileAttributes.Normal);File.Delete(dst);}File.Move(src,dst,true);}catch{corrupt.Add(p.Name);} }

    private static uint SkipInstructions(FileStream s,bool kmst){uint total=0;while(true){uint cmd=ReadU32(s);if(cmd==0)return total;uint kind=cmd>>28;if(kind==8){uint l=cmd&0x0FFFFFFF;total+=l;s.Position+=l;}else if(kind==12){total+=(cmd&0x0FFFFF00)>>8;}else{total+=cmd;ReadI32(s);if(kmst){int l=ReadI32(s);if(l>0&&l<=260)s.Position+=l;}}}}
    private static (string,int) ReadNameType(FileStream s){var b=new List<byte>();while(s.Position<s.Length){int x=s.ReadByte();if(x<0)return(Encoding.UTF8.GetString(b.ToArray()),-1);if(x<=2)return(Encoding.UTF8.GetString(b.ToArray()),x);b.Add((byte)x);}return(Encoding.UTF8.GetString(b.ToArray()),-1);}
    private static int ReadI32(Stream s){Span<byte>b=stackalloc byte[4];s.ReadExactly(b);return BinaryPrimitives.ReadInt32LittleEndian(b);} private static uint ReadU32(Stream s){Span<byte>b=stackalloc byte[4];s.ReadExactly(b);return BinaryPrimitives.ReadUInt32LittleEndian(b);}

    private static readonly uint[] CrcTable=BuildCrcTable();
    private static uint[] BuildCrcTable(){var t=new uint[256];for(uint i=0;i<256;i++){uint r=i<<24;for(int j=0;j<8;j++)r=(r&0x80000000)!=0?(r<<1)^0x04C11DB7:r<<1;t[i]=r;}return t;}
    private static uint CrcUpdate(uint crc,ReadOnlySpan<byte> data){foreach(byte b in data)crc=(crc<<8)^CrcTable[(int)(((crc>>24)^b)&0xff)];return crc;}
    private static uint Crc32File(string p){uint c=0;byte[]b=new byte[BufSize];using var f=new FileStream(p,FileMode.Open,FileAccess.Read,FileShare.Read);int n;while((n=f.Read(b,0,b.Length))>0)c=CrcUpdate(c,b.AsSpan(0,n));return c;}
    private static int ReadLocalVersion(string root){string p=Path.Combine(root,"Data","Base","Base.wz");return WzVersionReader.ReadVersion(p);}
    private static bool IsBase(string p)=>Normalize(p).StartsWith("data/base/",StringComparison.OrdinalIgnoreCase);
    private static string Normalize(string p)=>p.Replace('\\','/').TrimStart('/');
    private static string SafePath(string root,string rel){string rf=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;string full=Path.GetFullPath(Path.Combine(rf,Normalize(rel).Replace('/',Path.DirectorySeparatorChar)));if(!full.StartsWith(rf,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Patch 含不安全路徑。 ");return full;}
    public static string FormatBytes(long n){string[]u={"B","KB","MB","GB","TB"};double v=n;int i=0;while(v>=1024&&i<u.Length-1){v/=1024;i++;}return $"{v:0.##} {u[i]}";}

    private sealed class LimitedStream : Stream
    { readonly Stream _s; long _left; readonly Queue<byte> _prefix=new(); public LimitedStream(Stream s,long len){_s=s;_left=len;} public void Rewind2(byte a,byte b){_prefix.Enqueue(a);_prefix.Enqueue(b);} public override int Read(byte[] buffer,int offset,int count){int w=0;while(_prefix.Count>0&&w<count)buffer[offset+w++]=_prefix.Dequeue();if(w==count)return w;int n=_s.Read(buffer,offset+w,(int)Math.Min(count-w,_left));_left-=n;return w+n;} public override int Read(Span<byte> buffer){int w=0;while(_prefix.Count>0&&w<buffer.Length)buffer[w++]=_prefix.Dequeue();if(w==buffer.Length)return w;int n=_s.Read(buffer[w..(w+(int)Math.Min(buffer.Length-w,_left))]);_left-=n;return w+n;} public override int ReadByte(){if(_prefix.Count>0)return _prefix.Dequeue();if(_left<=0)return -1;int x=_s.ReadByte();if(x>=0)_left--;return x;} public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}public override void Flush(){}public override long Seek(long o,SeekOrigin so)=>throw new NotSupportedException();public override void SetLength(long v)=>throw new NotSupportedException();public override void Write(byte[]b,int o,int c)=>throw new NotSupportedException(); }
}
