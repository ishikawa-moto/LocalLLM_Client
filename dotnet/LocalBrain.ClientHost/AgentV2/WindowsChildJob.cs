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
    public Stream? Input {get;private set;}
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits {
        public long ProcessTime,JobTime;public uint Flags;public UIntPtr MinWorkingSet,MaxWorkingSet;
        public uint ActiveProcesses;public UIntPtr Affinity;public uint Priority,Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters {public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits {
        public BasicLimits Basic;public IoCounters Io;public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Accounting {
        public long UserTime,KernelTime,PeriodUserTime,PeriodKernelTime;
        public uint PageFaults,TotalProcesses,ActiveProcesses,TerminatedProcesses;
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes {public int Length;public IntPtr Descriptor;public int Inherit;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct StartupInfo {
        public int Size;public IntPtr Reserved,Desktop,Title;public uint X,Y,Width,Height,CharsX,CharsY,Fill,Flags;
        public ushort Show,ReservedSize;public IntPtr ReservedBytes,Input,Output,Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx {public StartupInfo Info;public IntPtr Attributes;}
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation {public IntPtr Process,Thread;public uint ProcessId,ThreadId;}
    [StructLayout(LayoutKind.Sequential)] private struct SecurityCapabilities {public IntPtr Sid,Capabilities;public uint Count,Reserved;}
    [StructLayout(LayoutKind.Sequential)] private struct SidAndAttributes {public IntPtr Sid;public uint Attributes;}
    [DllImport("kernel32.dll",SetLastError=true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool SetInformationJobObject(SafeFileHandle job,int type,ref ExtendedLimits info,uint length);
    [DllImport("kernel32.dll",EntryPoint="QueryInformationJobObject",SetLastError=true)] private static extern bool QueryLimits(SafeFileHandle job,int type,out ExtendedLimits info,uint length,IntPtr returned);
    [DllImport("kernel32.dll",EntryPoint="QueryInformationJobObject",SetLastError=true)] private static extern bool QueryAccounting(SafeFileHandle job,int type,out Accounting info,uint length,IntPtr returned);
    [DllImport("kernel32.dll",EntryPoint="QueryInformationJobObject",SetLastError=true)] private static extern bool QueryProcessIds(SafeFileHandle job,int type,IntPtr info,uint length,IntPtr returned);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool IsProcessInJob(SafeProcessHandle process,SafeFileHandle job,out bool member);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,uint flags,ref UIntPtr bytes);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,UIntPtr attribute,IntPtr value,UIntPtr bytes,IntPtr previous,IntPtr returned);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetProcessMitigationPolicy(IntPtr process,int policy,out uint value,UIntPtr size);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool CreatePipe(out SafeFileHandle read,out SafeFileHandle write,ref SecurityAttributes attributes,uint size);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool SetHandleInformation(SafeFileHandle handle,uint mask,uint flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern SafeFileHandle CreateFileW(string name,uint access,uint share,ref SecurityAttributes attributes,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool CreateProcessW(string? application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,bool inherit,uint flags,IntPtr environment,string? directory,ref StartupInfoEx startup,out ProcessInformation process);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool ConvertStringSidToSid(string text,out IntPtr sid);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool OpenProcessToken(IntPtr process,uint access,out SafeFileHandle token);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool GetTokenInformation(SafeFileHandle token,int kind,IntPtr value,int size,out int required);
    [DllImport("kernelbase.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern bool DeriveCapabilitySidsFromName(string name,out IntPtr groups,out uint groupCount,
        out IntPtr capabilities,out uint capabilityCount);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr value);
    private static void Check(bool result) {if(!result)throw new Win32Exception(Marshal.GetLastWin32Error());}
    private WindowsChildJob(OcrLimits? limits=null,bool oneProcess=false) {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        if(limits is not null&&(limits.MemoryBytes<128L*1024*1024||limits.MemoryBytes>8L*1024*1024*1024||limits.Affinity==0||string.IsNullOrWhiteSpace(limits.AppContainerSid)))throw new ArgumentException("Invalid OCR job limits");
        handle=CreateJobObject(IntPtr.Zero,null);
        if(handle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
        var info=new ExtendedLimits {Basic=new BasicLimits {Flags=0x2000}};
        if(oneProcess){info.Basic.Flags|=8;info.Basic.ActiveProcesses=1;}
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
    internal static WindowsChildJob StartReviewer(ProcessStartInfo start) => Start(start,reviewer:true);
    internal static WindowsChildJob StartReviewerBootstrap(ProcessStartInfo start) => Start(start,reviewer:true,bootstrap:true);
    internal static WindowsChildJob StartOfflineIsolatedReviewer(ProcessStartInfo start,string packageSid,bool allowSystemRegistryRead=false) =>
        Start(start,reviewer:true,reviewerContainerSid:packageSid,allowSystemRegistryRead:allowSystemRegistryRead);
    internal static WindowsChildJob StartNetworkBoundaryFixture(ProcessStartInfo start,string packageSid)
    {
        if(start.RedirectStandardInput || start.ArgumentList.FirstOrDefault()!="--review-loopback-policy-child")
            throw new ArgumentException("Network boundary fixture cannot accept a packet");
        return Start(start,reviewer:true,reviewerContainerSid:packageSid,allowSystemRegistryRead:true,internetClientFixture:true);
    }

    internal static WindowsChildJob Start(ProcessStartInfo start,Action<int>? createdFixture=null,bool allowNoArguments=false,OcrLimits? ocrLimits=null) =>
        Start(start,createdFixture,allowNoArguments,ocrLimits,reviewer:false);

    private static WindowsChildJob Start(ProcessStartInfo start,Action<int>? createdFixture=null,bool allowNoArguments=false,OcrLimits? ocrLimits=null,bool reviewer=false,bool bootstrap=false,string? reviewerContainerSid=null,bool allowSystemRegistryRead=false,bool internetClientFixture=false) {
        if(start.UseShellExecute || start.ArgumentList.Count==0 && !allowNoArguments || !string.IsNullOrEmpty(start.Arguments))throw new ArgumentException("Explicit argument list required");
        var job=new WindowsChildJob(ocrLimits,oneProcess:reviewerContainerSid is not null);
        SafeFileHandle? readOut=null,writeOut=null,readError=null,writeError=null,input=null,writeInput=null;
        IntPtr attributes=IntPtr.Zero,jobValue=IntPtr.Zero,handlesValue=IntPtr.Zero,environment=IntPtr.Zero,sid=IntPtr.Zero,capabilities=IntPtr.Zero;
        IntPtr childPolicy=IntPtr.Zero,packagePolicy=IntPtr.Zero;
        var capabilitySids=new List<IntPtr>();
        IntPtr capabilityEntries=IntPtr.Zero;
        var initialized=false;ProcessInformation child=default;
        try {
            var security=new SecurityAttributes{Length=Marshal.SizeOf<SecurityAttributes>(),Inherit=1};
            Check(CreatePipe(out readOut,out writeOut,ref security,0));Check(CreatePipe(out readError,out writeError,ref security,0));
            Check(SetHandleInformation(readOut,1,0));Check(SetHandleInformation(readError,1,0));
            if(reviewer && start.RedirectStandardInput) {
                Check(CreatePipe(out input,out writeInput,ref security,0));
                Check(SetHandleInformation(writeInput,1,0));
            } else {
                input=CreateFileW("NUL",0x80000000,3,ref security,3,0,IntPtr.Zero);
                if(input.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            var denyChildCreation=reviewer && !bootstrap;
            var containerSid=reviewerContainerSid??ocrLimits?.AppContainerSid;
            var attributeCount=2+(denyChildCreation?1:0)+(containerSid is not null?1:0)+(reviewerContainerSid is not null?1:0);
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
            if(denyChildCreation) {
                // PROC_THREAD_ATTRIBUTE_CHILD_PROCESS_POLICY, PROCESS_CREATION_CHILD_PROCESS_RESTRICTED.
                childPolicy=Marshal.AllocHGlobal(sizeof(uint));
                Marshal.WriteInt32(childPolicy,1);
                Check(UpdateProcThreadAttribute(attributes,0,(UIntPtr)0x2000E,childPolicy,(UIntPtr)sizeof(uint),IntPtr.Zero,IntPtr.Zero));
            }
            if(containerSid is not null){
                Check(ConvertStringSidToSid(containerSid,out sid));
                if(allowSystemRegistryRead)capabilitySids.Add(ResolveCapabilitySid("registryRead"));
                if(internetClientFixture)capabilitySids.Add(ResolveCapabilitySid("internetClient"));
                if(capabilitySids.Count!=0) {
                    var entrySize=Marshal.SizeOf<SidAndAttributes>();
                    capabilityEntries=Marshal.AllocHGlobal(capabilitySids.Count*entrySize);
                    for(var i=0;i<capabilitySids.Count;i++)
                        Marshal.StructureToPtr(new SidAndAttributes{Sid=capabilitySids[i],Attributes=4},
                            capabilityEntries+i*entrySize,false);
                }
                capabilities=Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());
                Marshal.StructureToPtr(new SecurityCapabilities{Sid=sid,Capabilities=capabilityEntries,
                    Count=(uint)capabilitySids.Count},capabilities,false);
                Check(UpdateProcThreadAttribute(attributes,0,(UIntPtr)0x20009,capabilities,(UIntPtr)Marshal.SizeOf<SecurityCapabilities>(),IntPtr.Zero,IntPtr.Zero));
            }
            if(reviewerContainerSid is not null) {
                // PROC_THREAD_ATTRIBUTE_ALL_APPLICATION_PACKAGES_POLICY: opt out of the ambient package grants.
                packagePolicy=Marshal.AllocHGlobal(sizeof(uint));
                Marshal.WriteInt32(packagePolicy,1);
                Check(UpdateProcThreadAttribute(attributes,0,(UIntPtr)0x2000F,packagePolicy,(UIntPtr)sizeof(uint),IntPtr.Zero,IntPtr.Zero));
            }
            var block=string.Join('\0',start.Environment.OrderBy(p=>p.Key,StringComparer.OrdinalIgnoreCase).Select(p=>p.Key+"="+p.Value))+"\0\0";
            environment=Marshal.StringToHGlobalUni(block);
            var info=new StartupInfoEx {Attributes=attributes,Info=new StartupInfo {
                Size=Marshal.SizeOf<StartupInfoEx>(),Flags=0x100,Input=input.DangerousGetHandle(),Output=writeOut.DangerousGetHandle(),Error=writeError.DangerousGetHandle()}};
            // WSL uses the Windows executable-name convention (not CRT escaping) for argv[0].
            // Its path is quoted only when needed; arguments still use exact CRT escaping.
            var executable=start.FileName.Contains(' ')?"\""+start.FileName+"\"":start.FileName;
            var command=new StringBuilder(executable+string.Concat(start.ArgumentList.Select(a=>" "+Quote(a))));
            // Reviewers start suspended and detached from the console; stdin is a private pipe.
            var consoleFlags=reviewer?0x0000000Cu:0x08000000u;
            Check(CreateProcessW(Path.IsPathFullyQualified(start.FileName)?start.FileName:null,command,IntPtr.Zero,IntPtr.Zero,true,
                0x80000|consoleFlags|0x400|(ocrLimits is null?0u:0x4000u),environment,string.IsNullOrEmpty(start.WorkingDirectory)?null:start.WorkingDirectory,ref info,out child));
            if(denyChildCreation) {
                // ProcessChildProcessPolicy. A failed read or missing restriction leaves the Job unopened to input.
                Check(GetProcessMitigationPolicy(child.Process,13,out var policy,(UIntPtr)sizeof(uint)));
                if((policy&1)==0)throw new IOException("Reviewer child creation is not blocked");
            }
            createdFixture?.Invoke(checked((int)child.ProcessId));
            job.Process=Process.GetProcessById(checked((int)child.ProcessId));
            // Keep a managed process handle before closing CreateProcess's handle.
            // A fast OCR inspect process may exit before WaitForExitAsync opens one.
            _=job.Process.Handle;
            if(reviewerContainerSid is not null) {
                VerifyContainer(child.Process,sid,capabilitySids);
                job.RequireSealedReviewer();
            }
            GC.KeepAlive(input);GC.KeepAlive(writeOut);GC.KeepAlive(writeError);GC.KeepAlive(job.handle);
            writeOut.Dispose();writeError.Dispose();input.Dispose();
            job.Output=new StreamReader(new FileStream(readOut,FileAccess.Read),start.StandardOutputEncoding??Encoding.UTF8);readOut=null;
            job.Error=new StreamReader(new FileStream(readError,FileAccess.Read),start.StandardErrorEncoding??Encoding.UTF8);readError=null;
            if(writeInput is not null) {
                job.Input=new FileStream(writeInput,FileAccess.Write,bufferSize:1);
                writeInput=null;
            }
            if(reviewer)Check(ResumeThread(child.Thread)!=uint.MaxValue);
            return job;
        }catch{job.Dispose();throw;}
        finally {
            if(child.Thread!=IntPtr.Zero)CloseHandle(child.Thread);if(child.Process!=IntPtr.Zero)CloseHandle(child.Process);
            readOut?.Dispose();writeOut?.Dispose();readError?.Dispose();writeError?.Dispose();input?.Dispose();writeInput?.Dispose();
            if(initialized)DeleteProcThreadAttributeList(attributes);
            if(attributes!=IntPtr.Zero)Marshal.FreeHGlobal(attributes);if(jobValue!=IntPtr.Zero)Marshal.FreeHGlobal(jobValue);
            if(handlesValue!=IntPtr.Zero)Marshal.FreeHGlobal(handlesValue);if(environment!=IntPtr.Zero)Marshal.FreeHGlobal(environment);
            if(capabilities!=IntPtr.Zero)Marshal.FreeHGlobal(capabilities);if(sid!=IntPtr.Zero)LocalFree(sid);
            if(childPolicy!=IntPtr.Zero)Marshal.FreeHGlobal(childPolicy);if(packagePolicy!=IntPtr.Zero)Marshal.FreeHGlobal(packagePolicy);
            if(capabilityEntries!=IntPtr.Zero)Marshal.FreeHGlobal(capabilityEntries);
            foreach(var capabilitySid in capabilitySids)LocalFree(capabilitySid);
        }
    }
    [DllImport("advapi32.dll")] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool EqualSid(IntPtr left,IntPtr right);

    private static IntPtr ResolveCapabilitySid(string name) {
        Check(DeriveCapabilitySidsFromName(name,out var groups,out var groupCount,
            out var capabilities,out var capabilityCount));
        try {
            if(capabilityCount!=1)throw new IOException($"Expected one {name} capability");
            var sid=Marshal.ReadIntPtr(capabilities);
            Marshal.WriteIntPtr(capabilities,IntPtr.Zero);
            return sid;
        } finally {
            Free(groups,groupCount);
            Free(capabilities,capabilityCount);
        }
        static void Free(IntPtr list,uint count) {
            for(var i=0;i<count;i++) {
                var pointer=Marshal.ReadIntPtr(list,checked((int)i*IntPtr.Size));
                if(pointer!=IntPtr.Zero)LocalFree(pointer);
            }
            if(list!=IntPtr.Zero)LocalFree(list);
        }
    }

    private static void VerifyContainer(IntPtr process,IntPtr expectedSid,IReadOnlyList<IntPtr> capabilities) {
        Check(OpenProcessToken(process,8,out var token));
        using(token) {
            Read(29,(p,n)=>n==sizeof(uint) && Marshal.ReadInt32(p)==1);
            Read(30,(p,n)=>{
                if(n<sizeof(uint) || Marshal.ReadInt32(p)!=capabilities.Count)return false;
                if(capabilities.Count==0)return true;
                var entrySize=Marshal.SizeOf<SidAndAttributes>();
                if(n<IntPtr.Size+capabilities.Count*entrySize)return false;
                var expected=new HashSet<IntPtr>(capabilities);
                for(var i=0;i<capabilities.Count;i++) {
                    var entry=Marshal.PtrToStructure<SidAndAttributes>(p+IntPtr.Size+i*entrySize);
                    var match=expected.FirstOrDefault(candidate=>EqualSid(candidate,entry.Sid));
                    if(match==IntPtr.Zero || (entry.Attributes&4)==0 || !expected.Remove(match))return false;
                }
                return expected.Count==0;
            });
            Read(31,(p,n)=>n>=IntPtr.Size && Marshal.ReadIntPtr(p)!=IntPtr.Zero && EqualSid(Marshal.ReadIntPtr(p),expectedSid));
        }
        void Read(int kind,Func<IntPtr,int,bool> verify) {
            if(GetTokenInformation(token,kind,IntPtr.Zero,0,out var bytes) || Marshal.GetLastWin32Error()!=122 ||
                bytes<=0 || bytes>16384)throw new IOException("Reviewer token inspection failed");
            var buffer=Marshal.AllocHGlobal(bytes);
            try {
                Check(GetTokenInformation(token,kind,buffer,bytes,out var actual));
                if(!verify(buffer,actual))throw new IOException("Reviewer AppContainer token did not match expected restrictions");
            } finally { Marshal.FreeHGlobal(buffer); }
        }
    }
    internal async Task SealReviewerBootstrapAsync(CancellationToken token)
    {
        using var drain = CancellationTokenSource.CreateLinkedTokenSource(token);
        drain.CancelAfter(TimeSpan.FromSeconds(3));
        try {
            while (ActiveReviewerProcesses() != 1)
                await Task.Delay(50,drain.Token);
        } catch (OperationCanceledException) when (!token.IsCancellationRequested) {
            throw new IOException("Reviewer authentication helpers did not exit: " + DescribeJobMembers());
        }
        // Set the cap before the second membership check so a racing helper fails closed.
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2008, ActiveProcesses = 1 } };
        Check(SetInformationJobObject(handle,9,ref limits,(uint)Marshal.SizeOf<ExtendedLimits>()));
        RequireSealedReviewer();
    }

    internal void RequireSealedReviewer()
    {
        Check(QueryLimits(handle,9,out var limits,(uint)Marshal.SizeOf<ExtendedLimits>(),IntPtr.Zero));
        if (limits.Basic.Flags != 0x2008 || limits.Basic.ActiveProcesses != 1)
            throw new IOException("Reviewer Job restrictions are not effective");
        if (ActiveReviewerProcesses() != 1)
            throw new IOException("Reviewer Job does not contain exactly the live reviewer");
    }

    private uint ActiveReviewerProcesses()
    {
        Check(IsProcessInJob(Process.SafeHandle,handle,out var member));
        Check(QueryAccounting(handle,1,out var state,(uint)Marshal.SizeOf<Accounting>(),IntPtr.Zero));
        if (!member || Process.HasExited || state.ActiveProcesses == 0)
            throw new IOException("Reviewer is absent from its Job");
        return state.ActiveProcesses;
    }

    private string DescribeJobMembers()
    {
        const int capacity = 16;
        var size = 8 + capacity * IntPtr.Size;
        var buffer = Marshal.AllocHGlobal(size);
        try {
            if (!QueryProcessIds(handle,3,buffer,(uint)size,IntPtr.Zero)) return "process list unavailable";
            var count = Math.Clamp(Marshal.ReadInt32(buffer,4),0,capacity);
            var names = new List<string>();
            for (var index = 0; index < count; index++) {
                var id = Marshal.ReadIntPtr(buffer,8+index*IntPtr.Size).ToInt64();
                if (id is <= 0 or > int.MaxValue) continue;
                try {
                    using var process = Process.GetProcessById((int)id);
                    names.Add(process.ProcessName);
                } catch (ArgumentException) { }
                catch (Win32Exception) { }
                catch (InvalidOperationException) { }
            }
            return $"active={ActiveReviewerProcesses()} listed={count} images={string.Join(',',names)}";
        } finally { Marshal.FreeHGlobal(buffer); }
    }
    public void Terminate()=>handle.Dispose();
    public void Dispose(){Terminate();Input?.Dispose();Output?.Dispose();Error?.Dispose();Process?.Dispose();}
}
