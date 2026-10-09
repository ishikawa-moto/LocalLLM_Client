using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
namespace LocalBrain.ClientHost.AgentV2;
internal static class WindowsIsolationNative
{
    internal const uint CompletionKey=0x4C425037;
    [StructLayout(LayoutKind.Sequential)] private struct CompletionAssociation {public IntPtr key,port;}
    [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr CreateIoCompletionPort(IntPtr file,IntPtr existing,UIntPtr key,uint threads);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetQueuedCompletionStatus(IntPtr port,out uint message,out UIntPtr key,out IntPtr pid,uint milliseconds);
    [DllImport("kernel32.dll",EntryPoint="SetInformationJobObject",SetLastError=true)] private static extern bool SetCompletionPort(IntPtr job,int kind,ref CompletionAssociation value,uint size);
    internal static bool IsJobMemoryPacket(UIntPtr key,uint message,IntPtr pid)=>key.ToUInt64()==CompletionKey&&message==10&&pid.ToInt64()>0&&pid.ToInt64()<=int.MaxValue;
    internal static (string reason,uint reportedExit) FinalizeMemoryOutcome(string reason,uint actualExit,bool memoryObserved)=>memoryObserved?("memory_limit",129u):(reason,actualExit);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr CreateFile(string path,uint access,uint share,IntPtr sa,uint creation,uint flags,IntPtr template);
    internal static int WriteDacCode(string path,bool directory=false){
        var handle=CreateFile(path,0x40000,7,IntPtr.Zero,3,directory?0x02200000u:0x00200000u,IntPtr.Zero);
        if(handle==new IntPtr(-1))return Marshal.GetLastWin32Error();
        CloseHandle(handle);return 0;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct Startup { public int cb; public string? reserved, desktop, title; public int x, y, width, height, xchars, ychars, fill, flags; public short show, reserved2Length; public IntPtr reserved2, input, output, error; }
    [StructLayout(LayoutKind.Sequential)] internal struct StartupEx { public Startup info; public IntPtr attributes; }
    [StructLayout(LayoutKind.Sequential)] internal struct ProcessInfo { public IntPtr process, thread; public uint pid, tid; }
    [StructLayout(LayoutKind.Sequential)] internal struct SecurityCaps { public IntPtr sid, capabilities; public uint count, reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct BasicLimits { public long processTime, jobTime; public uint flags; public UIntPtr minSet, maxSet; public uint active; public UIntPtr affinity; public uint priority, scheduling; }
    [StructLayout(LayoutKind.Sequential)] internal struct Io { public ulong readOps, writeOps, otherOps, readBytes, writeBytes, otherBytes; }
    [StructLayout(LayoutKind.Sequential)] internal struct ExtendedLimits { public BasicLimits basic; public Io io; public UIntPtr processMemory, jobMemory, peakProcess, peakJob; }
    [DllImport("userenv.dll", CharSet=CharSet.Unicode)] internal static extern int CreateAppContainerProfile(string name, string display, string description, IntPtr capabilities, uint count, out IntPtr sid);
    [DllImport("userenv.dll", CharSet=CharSet.Unicode)] internal static extern int DeleteAppContainerProfile(string name);
    [DllImport("userenv.dll", CharSet=CharSet.Unicode)] internal static extern int DeriveAppContainerSidFromAppContainerName(string name,out IntPtr sid);
    [DllImport("advapi32.dll")] internal static extern IntPtr FreeSid(IntPtr sid);
    [DllImport("kernel32.dll", SetLastError=true)] internal static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError=true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError=true)] private static extern bool GetTokenInformation(IntPtr token, int kind, IntPtr info, int length, out int required);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("advapi32.dll", SetLastError=true)] private static extern bool GetSecurityDescriptorSacl(IntPtr descriptor, out bool present, out IntPtr sacl, out bool defaulted);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode)] private static extern uint SetNamedSecurityInfo(string name, uint type, uint information, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref UIntPtr size);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, UIntPtr attribute, IntPtr value, UIntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttrs, IntPtr threadAttrs, bool inheritHandles, uint flags, IntPtr environment, string cwd, ref StartupEx start, out ProcessInfo process);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr OpenJobObject(uint access,bool inherit,string name);
    internal static bool JobAbsentOrEmpty(string name) {
        var job=OpenJobObject(4,false,name);
        if(job==IntPtr.Zero){var error=Marshal.GetLastWin32Error();if(error==2)return true;throw new Win32Exception(error);}
        try{if(!QueryInformationJobObject(job,1,out var state,(uint)Marshal.SizeOf<Accounting>(),IntPtr.Zero))throw new Win32Exception();return state.active==0;}
        finally{CloseHandle(job);}
    }
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool SetInformationJobObject(IntPtr job, int kind, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool TerminateProcess(IntPtr process, uint code);
    internal static int TokenInt(int kind) {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 8, out var token)) throw new Win32Exception();
        try { GetTokenInformation(token, kind, IntPtr.Zero, 0, out var length); var memory = Marshal.AllocHGlobal(length);
            try { if (!GetTokenInformation(token, kind, memory, length, out _)) throw new Win32Exception(); return Marshal.ReadInt32(memory); } finally { Marshal.FreeHGlobal(memory); }
        } finally { CloseHandle(token); }
    }
    internal static void LowIntegrity(string path) {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor("S:(ML;OICI;NW;;;LW)", 1, out var descriptor, out _)) throw new Win32Exception();
        try { if (!GetSecurityDescriptorSacl(descriptor, out _, out var sacl, out _)) throw new Win32Exception(); var code = SetNamedSecurityInfo(path, 1, 0x10, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, sacl); if (code != 0) throw new Win32Exception((int)code); }
        finally { LocalFree(descriptor); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SA { public int size;public IntPtr descriptor;public int inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct Accounting {public long userTime,kernelTime,thisUserTime,thisKernelTime;public uint faults,total,active,terminated;}
    [StructLayout(LayoutKind.Sequential)] private struct FileInfo {public uint attrs;public System.Runtime.InteropServices.ComTypes.FILETIME creation,access,write;public uint volume,sizeHigh,sizeLow,links,indexHigh,indexLow;}
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool CreatePipe(out IntPtr read,out IntPtr write,ref SA security,uint size);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool SetHandleInformation(IntPtr handle,uint mask,uint flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,EntryPoint="CreateFileW",SetLastError=true)] private static extern IntPtr InheritedFile(string path,uint access,uint share,ref SA security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool PeekNamedPipe(IntPtr pipe,IntPtr buffer,uint size,IntPtr read,out uint available,IntPtr left);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool ReadFile(IntPtr file,byte[] buffer,uint size,out uint read,IntPtr overlapped);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetFileInformationByHandle(IntPtr file,out FileInfo info);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool QueryInformationJobObject(IntPtr job,int kind,out Accounting info,uint size,IntPtr needed);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool TerminateJobObject(IntPtr job,uint code);
    [DllImport("kernel32.dll")] internal static extern IntPtr GetStdHandle(int kind);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetHandleInformation(IntPtr handle,out uint flags);
    private static int ProcessTokenInt(IntPtr process,int kind) {
        if(!OpenProcessToken(process,8,out var token))throw new Win32Exception();
        try{GetTokenInformation(token,kind,IntPtr.Zero,0,out var size);var memory=Marshal.AllocHGlobal(size);
            try{if(!GetTokenInformation(token,kind,memory,size,out _))throw new Win32Exception();return Marshal.ReadInt32(memory);
        }finally{Marshal.FreeHGlobal(memory);}
        }finally{CloseHandle(token);}
    }
    [StructLayout(LayoutKind.Sequential)] private struct CpuRate {public uint flags,rate;}
    [DllImport("kernel32.dll",EntryPoint="SetInformationJobObject",SetLastError=true)] private static extern bool SetCpuRate(IntPtr job,int kind,ref CpuRate value,uint size);
    [DllImport("kernel32.dll",EntryPoint="QueryInformationJobObject",SetLastError=true)] private static extern bool QueryCpuRate(IntPtr job,int kind,out CpuRate value,uint size,IntPtr needed);
    [DllImport("kernel32.dll",EntryPoint="QueryInformationJobObject",SetLastError=true)] private static extern bool QueryLimits(IntPtr job,int kind,out ExtendedLimits value,uint size,IntPtr needed);
    [DllImport("kernel32.dll",EntryPoint="QueryInformationJobObject",SetLastError=true)] private static extern bool QueryIds(IntPtr job,int kind,IntPtr memory,uint size,IntPtr needed);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder text,ref uint size);
    [StructLayout(LayoutKind.Sequential)] private struct MemoryInfo {public uint cb,faults;public UIntPtr peakWorking,working,peakPaged,paged,peakNonPaged,nonPaged,pagefile,peakPagefile,privateUsage;}
    [DllImport("kernel32.dll",EntryPoint="K32GetProcessMemoryInfo",SetLastError=true)] private static extern bool GetProcessMemoryInfo(IntPtr process,ref MemoryInfo value,uint size);
    private static HashSet<uint> MemberIds(IntPtr job){
        var ids=new HashSet<uint>();
        var memory=Marshal.AllocHGlobal(8+64*IntPtr.Size);
        try{
            if(!QueryIds(job,3,memory,(uint)(8+64*IntPtr.Size),IntPtr.Zero))throw new Win32Exception();
            var count=Marshal.ReadInt32(memory,4);if(count>64||count<0)throw new IOException("Fixed owned job list exceeded bound");
            for(var i=0;i<count;i++)ids.Add(unchecked((uint)Marshal.ReadIntPtr(memory,8+i*IntPtr.Size).ToInt64()));
            return ids;
        }finally{Marshal.FreeHGlobal(memory);}
    }
    internal static bool CanSkipExitedQuery(bool confirmedOwned,bool signaled,int error)=>confirmedOwned&&signaled&&error!=0;
    private static (ulong committed,int alive) ObserveMembers(IntPtr job,Dictionary<uint,string> members,List<object> skipped){
        ulong liveCommit=0;int alive=0;
        void Record(uint id,string stage,object value){if(skipped.Any(v=>JsonSerializer.SerializeToElement(v).GetProperty("pid").GetUInt32()==id&&JsonSerializer.SerializeToElement(v).GetProperty("stage").GetString()==stage))return;if(skipped.Count>=512)throw new IOException("Bounded owned-member retirement evidence exhausted");skipped.Add(value);}
        foreach(var id in MemberIds(job)){
            var process=OpenProcess(0x100410,false,(int)id);
            if(process==IntPtr.Zero){var error=Marshal.GetLastWin32Error();if(!MemberIds(job).Contains(id)){Record(id,"open",new{pid=id,stage="open",error,absence_confirmed=true});continue;}throw new Win32Exception(error,"Persisting Job member unavailable to Host");}
            try{
                if(!IsProcessInJob(process,job,out var inJob))throw new Win32Exception();if(!inJob)continue;
                var wait=WaitForSingleObject(process,0);if(wait==uint.MaxValue)throw new Win32Exception();if(wait==0){Record(id,"already_exited",new{pid=id,stage="already_exited",error=0,owned_handle_signaled=true});continue;}alive++;
                var text=new StringBuilder(32768);uint length=32768;
                if(!QueryFullProcessImageName(process,0,text,ref length)){var error=Marshal.GetLastWin32Error();if(!CanSkipExitedQuery(true,WaitForSingleObject(process,50)==0,error))throw new Win32Exception(error,"Live owned image query failed");Record(id,"image",new{pid=id,stage="image",error,owned_handle_signaled=true});continue;}
                members[id]=text.ToString();var memoryInfo=new MemoryInfo{cb=(uint)Marshal.SizeOf<MemoryInfo>()};
                if(!GetProcessMemoryInfo(process,ref memoryInfo,memoryInfo.cb)){var error=Marshal.GetLastWin32Error();if(!CanSkipExitedQuery(true,WaitForSingleObject(process,50)==0,error))throw new Win32Exception(error,"Live owned memory query failed");Record(id,"memory",new{pid=id,stage="memory",error,owned_handle_signaled=true});continue;}
                liveCommit+=memoryInfo.privateUsage.ToUInt64();
            }finally{CloseHandle(process);}
        }
        return(liveCommit,alive);
    }
    internal static object Capture(string exe,string[] args,string cwd,IntPtr sid,Dictionary<string,string> environment,int maxBytes,int deadlineMs,string evidenceRoot,CancellationToken cancellation,string jobName,Action<IntPtr>? suspendedHostGuard=null) {
        if(maxBytes is <1 or >65536||deadlineMs is <100 or >180000)throw new ArgumentException("Fixed capture limits required");
        string Quote(string value){if(value.Contains('"')||value.Contains('\0')||value.EndsWith('\\'))throw new ArgumentException("Unexpected fixed command argument");return "\""+value+"\"";}
        UIntPtr size=UIntPtr.Zero;InitializeProcThreadAttributeList(IntPtr.Zero,3,0,ref size);
        var attrs=Marshal.AllocHGlobal((int)size);var caps=Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCaps>());
        var jobList=Marshal.AllocHGlobal(IntPtr.Size);var handleList=Marshal.AllocHGlobal(IntPtr.Size*3);
        var env=Marshal.StringToHGlobalUni(string.Join('\0',environment.OrderBy(p=>p.Key,StringComparer.OrdinalIgnoreCase).Select(p=>p.Key+"="+p.Value))+"\0\0");
        IntPtr job=IntPtr.Zero,completionPort=IntPtr.Zero,outRead=IntPtr.Zero,outWrite=IntPtr.Zero,errRead=IntPtr.Zero,errWrite=IntPtr.Zero,stdin=IntPtr.Zero;
        ProcessInfo process=default;bool initialized=false,assigned=false,reaped=false,outEof=false,errEof=false,leaderExited=false;
        using var stdout=new MemoryStream();using var stderr=new MemoryStream();
        var watch=Stopwatch.StartNew();long observed=0;string reason="pending";uint lastActive=0,maxActive=0;ulong peakJobBytes=0,maxLiveCommit=0;int maxLive=0;var members=new Dictionary<uint,string>();var skipped=new List<object>();
        var notifications=new List<object>();bool jobMemoryPacketSeen=false;uint completionHandleFlags=uint.MaxValue;
        void DrainNotifications(){
            for(var n=0;n<128;n++){
                if(!GetQueuedCompletionStatus(completionPort,out var message,out var key,out var pid,0)){
                    var error=Marshal.GetLastWin32Error();if(error==258&&pid==IntPtr.Zero)return;throw new Win32Exception(error);
                }
                if(key.ToUInt64()!=CompletionKey||message is <1 or >13||notifications.Count>=512)throw new IOException("Unexpected or excessive owned Job completion packet");
                notifications.Add(new{message,key=key.ToUInt64(),pid=pid.ToInt64(),host_elapsed_ms=watch.ElapsedMilliseconds});
                if(IsJobMemoryPacket(key,message,pid)){jobMemoryPacketSeen=true;return;}
            }
            throw new IOException("Owned Job completion drain exceeded finite per-poll bound");
        }
        void Close(ref IntPtr handle){if(handle!=IntPtr.Zero&&handle!=new IntPtr(-1)){if(!CloseHandle(handle))throw new Win32Exception();handle=IntPtr.Zero;}}
        Accounting AccountingOnly(){if(!QueryInformationJobObject(job,1,out var state,(uint)Marshal.SizeOf<Accounting>(),IntPtr.Zero))throw new Win32Exception();maxActive=Math.Max(maxActive,state.active);return state;}
        Accounting State(){var sample=ObserveMembers(job,members,skipped);maxLiveCommit=Math.Max(maxLiveCommit,sample.committed);maxLive=Math.Max(maxLive,sample.alive);if(!QueryLimits(job,9,out var ext,(uint)Marshal.SizeOf<ExtendedLimits>(),IntPtr.Zero))throw new Win32Exception();peakJobBytes=Math.Max(peakJobBytes,ext.peakJob.ToUInt64());return AccountingOnly();}
        void Drain(IntPtr handle,MemoryStream capture,ref bool eof){
            if(eof)return;
            if(!PeekNamedPipe(handle,IntPtr.Zero,0,IntPtr.Zero,out var available,IntPtr.Zero)){
                if(Marshal.GetLastWin32Error()==109){eof=true;return;}throw new Win32Exception();
            }
            if(available==0)return;
            var buffer=new byte[Math.Min(4096,(int)available)];
            if(!ReadFile(handle,buffer,(uint)buffer.Length,out var n,IntPtr.Zero)){
                if(Marshal.GetLastWin32Error()==109){eof=true;return;}throw new Win32Exception();
            }
            observed+=n;
            var remaining=maxBytes-stdout.Length-stderr.Length;
            capture.Write(buffer,0,(int)Math.Min(n,Math.Max(remaining,0)));
        }
        try{
            if(!InitializeProcThreadAttributeList(attrs,3,0,ref size))throw new Win32Exception();initialized=true;
            Marshal.StructureToPtr(new SecurityCaps{sid=sid},caps,false);
            if(!UpdateProcThreadAttribute(attrs,0,(UIntPtr)0x20009,caps,(UIntPtr)Marshal.SizeOf<SecurityCaps>(),IntPtr.Zero,IntPtr.Zero))throw new Win32Exception();
            job=CreateJobObject(IntPtr.Zero,jobName);if(job==IntPtr.Zero)throw new Win32Exception();
            if(Marshal.GetLastWin32Error()==183)throw new IOException("Owned Job identity already exists");
            completionPort=CreateIoCompletionPort(new IntPtr(-1),IntPtr.Zero,UIntPtr.Zero,1);if(completionPort==IntPtr.Zero)throw new Win32Exception();
            if(!SetHandleInformation(completionPort,1,0)||!GetHandleInformation(completionPort,out completionHandleFlags)||completionHandleFlags!=0)throw new IOException("Private Job completion port inheritance not disabled");
            var association=new CompletionAssociation{key=new IntPtr(CompletionKey),port=completionPort};
            if(!SetCompletionPort(job,7,ref association,(uint)Marshal.SizeOf<CompletionAssociation>()))throw new Win32Exception();
            var limits=new ExtendedLimits{basic=new BasicLimits{flags=0x2208,active=16u},jobMemory=(UIntPtr)(1024UL*1024*1024)};
            if(!SetInformationJobObject(job,9,ref limits,(uint)Marshal.SizeOf<ExtendedLimits>()))throw new Win32Exception();

            Marshal.WriteIntPtr(jobList,job);
            if(!UpdateProcThreadAttribute(attrs,0,(UIntPtr)0x2000d,jobList,(UIntPtr)IntPtr.Size,IntPtr.Zero,IntPtr.Zero))throw new Win32Exception();
            var security=new SA{size=Marshal.SizeOf<SA>(),inherit=1};
            if(!CreatePipe(out outRead,out outWrite,ref security,4096)||!CreatePipe(out errRead,out errWrite,ref security,4096))throw new Win32Exception();
            if(!SetHandleInformation(outRead,1,0)||!SetHandleInformation(errRead,1,0))throw new Win32Exception();
            stdin=InheritedFile("NUL",0x80000000,3,ref security,3,0,IntPtr.Zero);if(stdin==new IntPtr(-1))throw new Win32Exception();
            var nullOutputCanary=false;
            if(nullOutputCanary){Close(ref outWrite);Close(ref errWrite);outWrite=InheritedFile("NUL",0x40000000,3,ref security,3,0,IntPtr.Zero);errWrite=InheritedFile("NUL",0x40000000,3,ref security,3,0,IntPtr.Zero);if(outWrite==new IntPtr(-1)||errWrite==new IntPtr(-1))throw new Win32Exception();}
            Marshal.WriteIntPtr(handleList,0,stdin);Marshal.WriteIntPtr(handleList,IntPtr.Size,outWrite);Marshal.WriteIntPtr(handleList,2*IntPtr.Size,errWrite);
            if(!UpdateProcThreadAttribute(attrs,0,(UIntPtr)0x20002,handleList,(UIntPtr)(IntPtr.Size*3),IntPtr.Zero,IntPtr.Zero))throw new Win32Exception();
            var start=new StartupEx{info=new Startup{cb=Marshal.SizeOf<StartupEx>(),flags=0x100,input=stdin,output=outWrite,error=errWrite},attributes=attrs};
            if(!CreateProcess(exe,new StringBuilder(Quote(exe)+" "+string.Join(' ',args.Select(Quote))),IntPtr.Zero,IntPtr.Zero,true,0x08080404,env,cwd,ref start,out process))throw new Win32Exception();
            assigned=true;
            if(ProcessTokenInt(process.process,29)!=1||ProcessTokenInt(process.process,30)!=0)throw new IOException("External suspended-child token was not capability0 AppContainer");
            if(!IsProcessInJob(process.process,job,out var membership)||!membership)throw new IOException("Atomic membership absent");
            suspendedHostGuard?.Invoke(process.process);
            Close(ref outWrite);Close(ref errWrite);Close(ref stdin);
            if(ResumeThread(process.thread)==uint.MaxValue)throw new Win32Exception();
            watch.Restart();
            while(true){
                Drain(outRead,stdout,ref outEof);Drain(errRead,stderr,ref errEof);
                var wait=WaitForSingleObject(process.process,0);if(wait==uint.MaxValue)throw new Win32Exception();leaderExited=wait==0;
                lastActive=State().active;
                DrainNotifications();
                if(jobMemoryPacketSeen){reason="memory_limit";break;}
                if(cancellation.IsCancellationRequested){reason="cancelled";break;}
                if(lastActive>limits.basic.active){reason="process_limit";break;}
                if(observed>maxBytes){reason="output_limit";break;}
                // Leader+both EOF alone does not prove the job has no detached descendants.
                if(leaderExited&&outEof&&errEof&&lastActive==0){reason="completed";break;}
                if(watch.ElapsedMilliseconds>=deadlineMs){reason="deadline";break;}
                Thread.Sleep(2);
            }
            var leaderAtStop=leaderExited;var outAtStop=outEof;var errAtStop=errEof;var activeAtStop=lastActive;var memoryPacketAtStop=jobMemoryPacketSeen;
            if(reason!="completed"&&!TerminateJobObject(job,129))throw new Win32Exception();
            if(WaitForSingleObject(process.process,5000)!=0)throw new IOException("Owned leader cleanup timeout");reaped=true;
            var cleanup=Stopwatch.StartNew();
            while(AccountingOnly().active!=0){if(cleanup.ElapsedMilliseconds>5000)throw new IOException("Owned job descendant cleanup timeout");Thread.Sleep(2);}
            // With active job count zero, only the private parent read ends remain.
            while(!outEof||!errEof){Drain(outRead,stdout,ref outEof);Drain(errRead,stderr,ref errEof);if(cleanup.ElapsedMilliseconds>5000)throw new IOException("Owned stdout/stderr EOF absent after job cleanup");}
            if(!GetExitCodeProcess(process.process,out var exit))throw new Win32Exception();
            DrainNotifications();
            var outcome=FinalizeMemoryOutcome(reason,exit,jobMemoryPacketSeen);reason=outcome.reason;var reportedExit=outcome.reportedExit;
            var output=stdout.ToArray();var error=stderr.ToArray();
            if(!QueryCpuRate(job,15,out var cpu,(uint)Marshal.SizeOf<CpuRate>(),IntPtr.Zero)||!QueryLimits(job,9,out var applied,(uint)Marshal.SizeOf<ExtendedLimits>(),IntPtr.Zero))throw new Win32Exception();var finalAccounting=AccountingOnly();
            var exitedText=new StringBuilder(32768);uint exitedLength=32768;var exitedImageOk=QueryFullProcessImageName(process.process,0,exitedText,ref exitedLength);var exitedImageError=exitedImageOk?0:Marshal.GetLastWin32Error();var exitedInfo=new MemoryInfo{cb=(uint)Marshal.SizeOf<MemoryInfo>()};var exitedMemoryOk=GetProcessMemoryInfo(process.process,ref exitedInfo,exitedInfo.cb);var exitedMemoryError=exitedMemoryOk?0:Marshal.GetLastWin32Error();var exitedSignaled=WaitForSingleObject(process.process,0)==0;
            var root=evidenceRoot;
            File.WriteAllBytes(Path.Combine(root,"captured.stdout.bin"),output);File.WriteAllBytes(Path.Combine(root,"captured.stderr.bin"),error);
            var result=new{reason,exit_code=reportedExit,actual_process_exit_code=exit,captured_bytes=output.Length+error.Length,observed_bytes=observed,limit_bytes=maxBytes,elapsed_ms=watch.ElapsedMilliseconds,deadline_ms=deadlineMs,hash_scope=reason=="completed"?"complete":"captured_prefix",stdout_sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(output)),stderr_sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(error)),leader_exited_at_stop=leaderAtStop,stdout_eof_at_stop=outAtStop,stderr_eof_at_stop=errAtStop,active_job_processes_at_stop=activeAtStop,max_active_job_processes=maxActive,active_job_processes_after_cleanup=State().active,own_leader_reaped=reaped,both_eof_after_cleanup=outEof&&errEof,atomic_job_assignment=true,job_membership_verified=true,externally_verified_app_container=true,externally_verified_capability_count=0,job_handle_inheritance_enabled=false,allowed_inherited_handles=3,stdin_is_null=true,stdio_null_eof_canary=nullOutputCanary,job_memory_limit_bytes=applied.jobMemory.ToUInt64(),job_active_process_limit=applied.basic.active,job_limit_flags=applied.basic.flags,kernel_peak_job_memory_bytes=peakJobBytes,kernel_max_live_private_commit_bytes=maxLiveCommit,kernel_max_sampled_live_job_processes=maxLive,exited_member_samples=skipped,exited_leader_probe=new{exitedImageOk,exitedImageError,exitedMemoryOk,exitedMemoryError,exitedSignaled,image_failure_skipped=CanSkipExitedQuery(true,exitedSignaled,exitedImageError),memory_failure_skipped=CanSkipExitedQuery(true,exitedSignaled,exitedMemoryError)},kernel_observed_member_images=members,kernel_user_cpu_100ns=finalAccounting.userTime,kernel_system_cpu_100ns=finalAccounting.kernelTime,kernel_cpu_rate_flags=cpu.flags,kernel_cpu_rate=cpu.rate,host_logical_processor_count=Environment.ProcessorCount,suspended_host_guard_applied=suspendedHostGuard is not null,capture_owner="windows_host"};
            File.WriteAllText(Path.Combine(root,"capture-result.json"),JsonSerializer.Serialize(result));
            var notified=new{capture=result,completion_port_associated_before_launch=true,completion_port_handle_flags=completionHandleFlags,completion_key=CompletionKey,job_memory_limit_message=10,job_memory_packet_observed=jobMemoryPacketSeen,late_memory_packet_observed_after_cleanup=!memoryPacketAtStop&&jobMemoryPacketSeen,notification_records=notifications,notification_record_limit=512,notification_per_poll_limit=128,notification_dequeue_wait_ms=0,notification_absence_proves_no_event=false,pid_is_diagnostic_only=true};
            File.WriteAllText(Path.Combine(root,"notification-result.json"),JsonSerializer.Serialize(notified));
            return result;
        }finally{
            try{
                if(process.process!=IntPtr.Zero){
                    if(assigned&&job!=IntPtr.Zero){if(!TerminateJobObject(job,129))throw new Win32Exception();}
                    else if(!TerminateProcess(process.process,129))throw new Win32Exception();
                    if(WaitForSingleObject(process.process,5000)!=0)throw new IOException("Owned process finally cleanup timeout");
                }
            }finally{
                foreach(var handle in new[]{outRead,outWrite,errRead,errWrite,stdin})if(handle!=IntPtr.Zero&&handle!=new IntPtr(-1))CloseHandle(handle);
                if(job!=IntPtr.Zero)CloseHandle(job);if(completionPort!=IntPtr.Zero)CloseHandle(completionPort);if(process.thread!=IntPtr.Zero)CloseHandle(process.thread);if(process.process!=IntPtr.Zero)CloseHandle(process.process);
                if(initialized)DeleteProcThreadAttributeList(attrs);
                Marshal.FreeHGlobal(attrs);Marshal.FreeHGlobal(caps);Marshal.FreeHGlobal(jobList);Marshal.FreeHGlobal(handleList);Marshal.FreeHGlobal(env);
            }
        }
    }
}
