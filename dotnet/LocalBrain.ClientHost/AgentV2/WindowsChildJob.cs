using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LocalBrain.ClientHost.AgentV2;

// JOB_LIST attaches the child at creation, even if the Host dies before CreateProcess returns.
internal sealed class WindowsChildJob : IDisposable
{
    internal sealed record OcrLimits(long MemoryBytes,ulong Affinity,string AppContainerSid);
    private readonly SafeFileHandle handle;
    public Process Process {get;private set;}=null!;
    public StreamReader Output {get;private set;}=null!;
    public StreamReader Error {get;private set;}=null!;
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits {
        public long ProcessTime,JobTime;public uint Flags;public UIntPtr MinWorkingSet,MaxWorkingSet;
        public uint ActiveProcesses;public UIntPtr Affinity;public uint Priority,Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters {public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits {
        public BasicLimits Basic;public IoCounters Io;public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes {public int Length;public IntPtr Descriptor;public int Inherit;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct StartupInfo {
        public int Size;public IntPtr Reserved,Desktop,Title;public uint X,Y,Width,Height,CharsX,CharsY,Fill,Flags;
        public ushort Show,ReservedSize;public IntPtr ReservedBytes,Input,Output,Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx {public StartupInfo Info;public IntPtr Attributes;}
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation {public IntPtr Process,Thread;public uint ProcessId,ThreadId;}
    [StructLayout(LayoutKind.Sequential)] private struct SecurityCapabilities {public IntPtr Sid,Capabilities;public uint Count,Reserved;}
    [DllImport("kernel32.dll",SetLastError=true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool SetInformationJobObject(SafeFileHandle job,int type,ref ExtendedLimits info,uint length);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,uint flags,ref UIntPtr bytes);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,UIntPtr attribute,IntPtr value,UIntPtr bytes,IntPtr previous,IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool CreatePipe(out SafeFileHandle read,out SafeFileHandle write,ref SecurityAttributes attributes,uint size);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool SetHandleInformation(SafeFileHandle handle,uint mask,uint flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern SafeFileHandle CreateFileW(string name,uint access,uint share,ref SecurityAttributes attributes,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool CreateProcessW(string? application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,bool inherit,uint flags,IntPtr environment,string? directory,ref StartupInfoEx startup,out ProcessInformation process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool ConvertStringSidToSid(string text,out IntPtr sid);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr value);
    private static void Check(bool result) {if(!result)throw new Win32Exception(Marshal.GetLastWin32Error());}
    private WindowsChildJob(OcrLimits? limits=null) {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        if(limits is not null&&(limits.MemoryBytes<128L*1024*1024||limits.MemoryBytes>8L*1024*1024*1024||limits.Affinity==0||string.IsNullOrWhiteSpace(limits.AppContainerSid)))throw new ArgumentException("Invalid OCR job limits");
        handle=CreateJobObject(IntPtr.Zero,null);
        if(handle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
        var info=new ExtendedLimits {Basic=new BasicLimits {Flags=0x2000}};
        if(limits is not null){
            info.Basic.Flags|=0x200|0x10|0x20|0x8;info.JobMemory=(UIntPtr)(ulong)limits.MemoryBytes;
            info.Basic.Affinity=(UIntPtr)limits.Affinity;info.Basic.Priority=0x4000;info.Basic.ActiveProcesses=1;
        }
        if(!SetInformationJobObject(handle,9,ref info,(uint)Marshal.SizeOf<ExtendedLimits>())) {
            var code=Marshal.GetLastWin32Error();handle.Dispose();throw new Win32Exception(code);
        }
    }
    private static string Quote(string value) {
        if(value.Length>0&&!value.Any(c=>char.IsWhiteSpace(c)||c=='"'))return value;
        var result=new StringBuilder("\"");var slashes=0;
        foreach(var c in value) {
            if(c=='\\'){slashes++;continue;}
            result.Append('\\',c=='"'?slashes*2+1:slashes);result.Append(c);slashes=0;
        }
        return result.Append('\\',slashes*2).Append('"').ToString();
    }
    internal static WindowsChildJob Start(ProcessStartInfo start,Action<int>? createdFixture=null,bool allowNoArguments=false,OcrLimits? ocrLimits=null) {
        if(start.UseShellExecute || start.ArgumentList.Count==0 && !allowNoArguments || !string.IsNullOrEmpty(start.Arguments))throw new ArgumentException("Explicit argument list required");
        var job=new WindowsChildJob(ocrLimits);SafeFileHandle? readOut=null,writeOut=null,readError=null,writeError=null,input=null;
        IntPtr attributes=IntPtr.Zero,jobValue=IntPtr.Zero,handlesValue=IntPtr.Zero,environment=IntPtr.Zero,sid=IntPtr.Zero,capabilities=IntPtr.Zero;
        var initialized=false;ProcessInformation child=default;
        try {
            var security=new SecurityAttributes{Length=Marshal.SizeOf<SecurityAttributes>(),Inherit=1};
            Check(CreatePipe(out readOut,out writeOut,ref security,0));Check(CreatePipe(out readError,out writeError,ref security,0));
            Check(SetHandleInformation(readOut,1,0));Check(SetHandleInformation(readError,1,0));
            input=CreateFileW("NUL",0x80000000,3,ref security,3,0,IntPtr.Zero);
            if(input.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
            var attributeCount=ocrLimits is null?2:3;
            UIntPtr size=UIntPtr.Zero;InitializeProcThreadAttributeList(IntPtr.Zero,attributeCount,0,ref size);
            attributes=Marshal.AllocHGlobal(checked((int)size.ToUInt64()));
            Check(InitializeProcThreadAttributeList(attributes,attributeCount,0,ref size));initialized=true;
            jobValue=Marshal.AllocHGlobal(IntPtr.Size);Marshal.WriteIntPtr(jobValue,job.handle.DangerousGetHandle());
            Check(UpdateProcThreadAttribute(attributes,0,(UIntPtr)0x2000D,jobValue,(UIntPtr)IntPtr.Size,IntPtr.Zero,IntPtr.Zero));
            handlesValue=Marshal.AllocHGlobal(IntPtr.Size*3);
            Marshal.WriteIntPtr(handlesValue,0,input.DangerousGetHandle());
            Marshal.WriteIntPtr(handlesValue,IntPtr.Size,writeOut.DangerousGetHandle());
            Marshal.WriteIntPtr(handlesValue,IntPtr.Size*2,writeError.DangerousGetHandle());
            Check(UpdateProcThreadAttribute(attributes,0,(UIntPtr)0x20002,handlesValue,(UIntPtr)(IntPtr.Size*3),IntPtr.Zero,IntPtr.Zero));
            if(ocrLimits is not null){
                Check(ConvertStringSidToSid(ocrLimits.AppContainerSid,out sid));
                capabilities=Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());
                Marshal.StructureToPtr(new SecurityCapabilities{Sid=sid},capabilities,false);
                Check(UpdateProcThreadAttribute(attributes,0,(UIntPtr)0x20009,capabilities,(UIntPtr)Marshal.SizeOf<SecurityCapabilities>(),IntPtr.Zero,IntPtr.Zero));
            }
            var block=string.Join('\0',start.Environment.OrderBy(p=>p.Key,StringComparer.OrdinalIgnoreCase).Select(p=>p.Key+"="+p.Value))+"\0\0";
            environment=Marshal.StringToHGlobalUni(block);
            var info=new StartupInfoEx {Attributes=attributes,Info=new StartupInfo {
                Size=Marshal.SizeOf<StartupInfoEx>(),Flags=0x100,Input=input.DangerousGetHandle(),Output=writeOut.DangerousGetHandle(),Error=writeError.DangerousGetHandle()}};
            // WSL uses the Windows executable-name convention (not CRT escaping) for argv[0].
            // Its path is quoted only when needed; arguments still use exact CRT escaping.
            var executable=start.FileName.Contains(' ')?"\""+start.FileName+"\"":start.FileName;
            var command=new StringBuilder(executable+string.Concat(start.ArgumentList.Select(a=>" "+Quote(a))));
            Check(CreateProcessW(Path.IsPathFullyQualified(start.FileName)?start.FileName:null,command,IntPtr.Zero,IntPtr.Zero,true,
                0x80000|0x08000000|0x400|(ocrLimits is null?0u:0x4000u),environment,string.IsNullOrEmpty(start.WorkingDirectory)?null:start.WorkingDirectory,ref info,out child));
            createdFixture?.Invoke(checked((int)child.ProcessId));
            job.Process=Process.GetProcessById(checked((int)child.ProcessId));
            // Keep a managed process handle before closing CreateProcess's handle.
            // A fast OCR inspect process may exit before WaitForExitAsync opens one.
            _=job.Process.Handle;
            GC.KeepAlive(input);GC.KeepAlive(writeOut);GC.KeepAlive(writeError);GC.KeepAlive(job.handle);
            writeOut.Dispose();writeError.Dispose();input.Dispose();
            job.Output=new StreamReader(new FileStream(readOut,FileAccess.Read),start.StandardOutputEncoding??Encoding.UTF8);readOut=null;
            job.Error=new StreamReader(new FileStream(readError,FileAccess.Read),start.StandardErrorEncoding??Encoding.UTF8);readError=null;
            return job;
        }catch{job.Dispose();throw;}
        finally {
            if(child.Thread!=IntPtr.Zero)CloseHandle(child.Thread);if(child.Process!=IntPtr.Zero)CloseHandle(child.Process);
            readOut?.Dispose();writeOut?.Dispose();readError?.Dispose();writeError?.Dispose();input?.Dispose();
            if(initialized)DeleteProcThreadAttributeList(attributes);
            if(attributes!=IntPtr.Zero)Marshal.FreeHGlobal(attributes);if(jobValue!=IntPtr.Zero)Marshal.FreeHGlobal(jobValue);
            if(handlesValue!=IntPtr.Zero)Marshal.FreeHGlobal(handlesValue);if(environment!=IntPtr.Zero)Marshal.FreeHGlobal(environment);
            if(capabilities!=IntPtr.Zero)Marshal.FreeHGlobal(capabilities);if(sid!=IntPtr.Zero)LocalFree(sid);
        }
    }
    public void Terminate()=>handle.Dispose();
    public void Dispose(){Terminate();Output?.Dispose();Error?.Dispose();Process?.Dispose();}
}
