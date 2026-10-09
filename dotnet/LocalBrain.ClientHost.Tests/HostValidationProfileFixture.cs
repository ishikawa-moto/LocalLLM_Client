using System.Text;
using System.Text.Json;

internal static class HostValidationProfileFixture
{
    internal const string Source=""""
        using System;
        using System.IO;
        using System.ComponentModel;
        using System.Collections.Generic;
        using System.Linq;
        using System.Runtime.InteropServices;
        using System.Security.Principal;
        using System.Text.Json;
        internal static class ProfileFixture {
            [DllImport("kernel32.dll")]static extern IntPtr GetCurrentProcess();
            [DllImport("advapi32.dll",SetLastError=true)]static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
            [DllImport("advapi32.dll",SetLastError=true)]static extern bool GetTokenInformation(IntPtr token,int kind,IntPtr data,uint size,out uint needed);
            [DllImport("userenv.dll",CharSet=CharSet.Unicode)]static extern int GetAppContainerFolderPath(string sid,out IntPtr path);
            [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)]static extern IntPtr CreateFile(string path,uint access,uint sharing,IntPtr attrs,uint creation,uint flags,IntPtr template);
            [DllImport("kernel32.dll",SetLastError=true)]static extern bool CloseHandle(IntPtr value);
            static int TokenInt(IntPtr token,int kind){GetTokenInformation(token,kind,IntPtr.Zero,0,out var needed);if(needed is <4 or >65536)throw new IOException("Bounded token metadata required");var data=Marshal.AllocHGlobal((int)needed);try{if(!GetTokenInformation(token,kind,data,needed,out _))throw new Win32Exception();return Marshal.ReadInt32(data);}finally{Marshal.FreeHGlobal(data);}}
            static int Code(Action action){try{action();return 0;}catch(Exception e)when(e is IOException or UnauthorizedAccessException){return e.HResult&65535;}}
            static int WriteDac(string path){var handle=CreateFile(path,0x40000,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero);if(handle==new IntPtr(-1))return Marshal.GetLastWin32Error();if(!CloseHandle(handle))throw new Win32Exception();return 0;}
            internal static int Run(){
                if(!OpenProcessToken(GetCurrentProcess(),8,out var token))throw new Win32Exception();
                try{
                    var app=TokenInt(token,29);var capabilities=TokenInt(token,30);
                    GetTokenInformation(token,31,IntPtr.Zero,0,out var size);if(size is <8 or >4096)throw new IOException("Bounded own SID required");var buffer=Marshal.AllocHGlobal((int)size);string sid;
                    try{if(!GetTokenInformation(token,31,buffer,size,out _))throw new Win32Exception();sid=new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;}finally{Marshal.FreeHGlobal(buffer);}
                    var hr=GetAppContainerFolderPath(sid,out var folder);if(hr<0)Marshal.ThrowExceptionForHR(hr);string ac;
                    try{ac=Marshal.PtrToStringUni(folder)??throw new IOException("Own profile absent");}finally{Marshal.FreeCoTaskMem(folder);}
                    var parent=Directory.GetParent(ac)??throw new IOException("Own parent absent");var scratch=Environment.GetEnvironmentVariable("TEMP")??throw new IOException("Scratch absent");
                    var codes=new Dictionary<string,int>{
                        ["ac_read"]=Code(()=>{if(Directory.GetFileSystemEntries(ac).Length==0)throw new IOException("Expected own profile entries");}),
                        ["package_create"]=Code(()=>File.WriteAllBytes(Path.Combine(parent.FullName,"synthetic-proof.bin"),new byte[]{1})),
                        ["ac_create"]=Code(()=>File.WriteAllBytes(Path.Combine(ac,"synthetic-proof.bin"),new byte[]{1})),
                        ["ac_temp_create"]=Code(()=>File.WriteAllBytes(Path.Combine(ac,"Temp","synthetic-proof.bin"),new byte[]{1})),
                        ["ac_write_dac"]=WriteDac(ac),
                        ["scratch_write"]=Code(()=>File.WriteAllBytes(Path.Combine(scratch,"synthetic-profile-control.bin"),new byte[]{7}))
                    };
                    var pass=app==1&&capabilities==0&&codes["ac_read"]==0&&codes["scratch_write"]==0&&codes.Where(p=>p.Key is not "ac_read" and not "scratch_write").All(p=>p.Value==5);
                    Console.WriteLine("PROFILE_STORAGE_PROOF:"+JsonSerializer.Serialize(new{pass,is_app_container=app==1,capability_count=capabilities,codes,registry_qualified=false,finite_scratch_qualified=false}));return pass?0:2;
                }finally{if(!CloseHandle(token))throw new Win32Exception();}
            }
        }
        public sealed class ProfileFixtureTask:Microsoft.Build.Utilities.Task {
            public override bool Execute(){return ProfileFixture.Run()==0;}
        }
        """";
    internal static void CheckRoutedBuild(string root,List<string> checks){
        var run=Directory.GetDirectories(Path.Combine(root,"host","validation-runs")).Single();
        var lines=File.ReadAllText(Path.Combine(run,"validate","captured.stdout.bin"),Encoding.UTF8).Split('\n');
        var frames=lines.Select(line=>line.Trim()).Where(line=>line.StartsWith("PROFILE_STORAGE_PROOF:",StringComparison.Ordinal)).ToArray();
        if(frames.Length!=1)throw new IOException("Exactly one actual routed profile probe frame required");
        using var document=JsonDocument.Parse(frames[0]["PROFILE_STORAGE_PROOF:".Length..]);var frame=document.RootElement;
        if(!frame.GetProperty("pass").GetBoolean()||!frame.GetProperty("is_app_container").GetBoolean()||frame.GetProperty("capability_count").GetInt32()!=0)throw new IOException("Routed profile probe failed");
        var codes=frame.GetProperty("codes");var expected=new[]{"ac_read","package_create","ac_create","ac_temp_create","ac_write_dac","scratch_write"};
        if(!codes.EnumerateObject().Select(p=>p.Name).Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)))throw new IOException("Routed probe operation set differs");
        if(codes.GetProperty("ac_read").GetInt32()!=0||codes.GetProperty("scratch_write").GetInt32()!=0||expected.Where(k=>k is not "ac_read" and not "scratch_write").Any(k=>codes.GetProperty(k).GetInt32()!=5))throw new IOException("Routed profile read-only checks failed");
        File.WriteAllText(Path.Combine(root,"profile-probe-result.json"),frame.GetRawText());checks.Add("actual_router_profile_readonly_probe");
    }
}
