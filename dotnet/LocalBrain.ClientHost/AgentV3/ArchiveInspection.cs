using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;

namespace LocalBrain.ClientHost.AgentV3;

// Inspection only. A trusted caller must own a stable private stream and
// compare these records against the Host's authorized workspace scope before
// any materialization or production update.
internal static class ArchiveInspection
{
    internal sealed record Entry(string Name, bool Directory, long Size, string? Sha256, bool Executable);
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) {
        "CON","CONIN$","CONOUT$","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
        "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9",
        "COM¹","COM²","COM³","LPT¹","LPT²","LPT³"
    };

    internal static string ValidateName(string input, bool directory)
    {
        if (string.IsNullOrEmpty(input) || input.StartsWith('/') || input.Contains('\\') ||
            Encoding.UTF8.GetByteCount(input)>4096 || (!directory && input.EndsWith('/')))
            throw new InvalidDataException("Unsafe archive path");
        var parts=input.TrimEnd('/').Split('/');
        if(parts.Length>32) throw new InvalidDataException("Archive path depth limit");
        foreach (var part in parts) {
            if (part.Length is 0 or >255 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                part.Any(c=>c<32 || "<>:\"|?*".Contains(c)) ||
                Reserved.Contains(part.Split('.')[0].TrimEnd(' ')) ||
                part.Equals(".git",StringComparison.OrdinalIgnoreCase) ||
                part.Equals(".localbrain",StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsafe archive path");
        }
        return string.Join('/',parts);
    }

    // TarReader may allocate extension metadata before yielding an entry.
    // Bound and validate raw headers first; only per-file PAX path metadata
    // emitted by our producer is supported. Reject size/sparse overrides.
    private static async Task PreflightAsync(Stream source, int maxEntries, CancellationToken token)
    {
        var header=new byte[512]; int count=0;
        while (source.Position<source.Length) {
            await source.ReadExactlyAsync(header,token);
            if (header.All(b=>b==0)) return;
            if (++count>(long)maxEntries*2+1) throw new InvalidDataException("Tar header limit");
            byte type=header[156];
            if (type is not (0 or (byte)'0' or (byte)'5' or (byte)'x'))
                throw new InvalidDataException("Unsupported tar header type");
            long size=0;
            string octal=Encoding.ASCII.GetString(header,124,12).Trim('\0',' ');
            foreach(char digit in octal) {
                if (digit<'0' || digit>'7') throw new InvalidDataException("Invalid tar size");
                if (size>(source.Length-(digit-'0'))/8) throw new InvalidDataException("Tar size exceeds archive");
                size=size*8+digit-'0';
            }
            if (size>source.Length-source.Position) throw new InvalidDataException("Truncated tar body");
            if (type==(byte)'x') {
                if(size>65536) throw new InvalidDataException("PAX metadata limit");
                var data=new byte[(int)size]; await source.ReadExactlyAsync(data,token);
                int offset=0;
                while(offset<data.Length) {
                    int space=Array.IndexOf(data,(byte)' ',offset);
                    if(space<=offset || space-offset>8 || data[offset]=='0' ||
                       data.AsSpan(offset,space-offset).ToArray().Any(b=>b<'0' || b>'9') ||
                       !int.TryParse(Encoding.ASCII.GetString(data,offset,space-offset),out int length) ||
                       length<=space-offset+1 || length>data.Length-offset || data[offset+length-1]!='\n')
                        throw new InvalidDataException("Invalid PAX record");
                    var record=data.AsSpan(space+1,offset+length-space-2);
                    if (!record.StartsWith("path="u8)) throw new InvalidDataException("Unsupported PAX field");
                    offset+=length;
                }
            } else source.Position+=size;
            long padding=(512-size%512)%512;
            if(padding>source.Length-source.Position) throw new InvalidDataException("Truncated tar padding");
            source.Position+=padding;
        }
        throw new InvalidDataException("Missing tar terminator");
    }

    internal static async Task<IReadOnlyList<Entry>> InspectAsync(Stream source, long maxArchiveBytes,
        int maxEntries, long maxFileBytes, CancellationToken token)
    {
        if (!source.CanRead || !source.CanSeek || maxArchiveBytes<0 || maxArchiveBytes>512L*1024*1024 ||
            maxEntries<0 || maxEntries>10000 || maxFileBytes<0 || maxFileBytes>512L*1024*1024 ||
            source.Length>maxArchiveBytes)
            throw new InvalidDataException("Invalid archive or limits");
        source.Position=0;
        await PreflightAsync(source,maxEntries,token);
        source.Position=0;
        using var reader=new TarReader(source,leaveOpen:true);
        var records=new List<Entry>();
        var paths=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
        var parents=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total=0, parentBytes=0;
        TarEntry? entry;
        while ((entry=await reader.GetNextEntryAsync(false,token)) is not null) {
            if (records.Count>=maxEntries) throw new InvalidDataException("Archive entry limit");
            bool directory=entry.EntryType==TarEntryType.Directory;
            bool regular=entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile;
            if (!directory && !regular) throw new InvalidDataException("Unsafe archive entry type");
            string name=ValidateName(entry.Name,directory);
            string key=name.Normalize(NormalizationForm.FormC);
            if (paths.ContainsKey(key)) throw new InvalidDataException("Duplicate archive path");
            var segments=key.Split('/');
            for (int i=1;i<segments.Length;i++) {
                string parent=string.Join('/',segments.Take(i));
                parentBytes+=Encoding.UTF8.GetByteCount(parent);
                if(parentBytes>8*1024*1024) throw new InvalidDataException("Archive parent path limit");
                if (paths.TryGetValue(parent,out var parentIsDirectory) && !parentIsDirectory)
                    throw new InvalidDataException("File used as parent");
                parents.Add(parent);
            }
            if (regular && parents.Contains(key)) throw new InvalidDataException("File replaces parent");
            if (entry.Length<0 || entry.Length>maxFileBytes-total || (directory && entry.Length!=0))
                throw new InvalidDataException("Archive file byte limit");
            string? digest=null;
            if (regular) {
                total+=entry.Length;
                using var sha=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var bytes=new byte[65536]; long count=0;
                if (entry.DataStream is { } data) {
                    int read;
                    while ((read=await data.ReadAsync(bytes,token))!=0) {
                        count+=read;
                        if (count>entry.Length) throw new InvalidDataException("Archive file length changed");
                        sha.AppendData(bytes,0,read);
                    }
                }
                if (count!=entry.Length) throw new InvalidDataException("Truncated archive file");
                digest=Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            }
            paths.Add(key,directory);
            records.Add(new Entry(name,directory,entry.Length,digest,((int)entry.Mode & 0x49)!=0));
        }
        return records;
    }
}
