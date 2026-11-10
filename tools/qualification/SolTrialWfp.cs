using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

// Only the disposable network fixture uses this session. No production launch or real packet.
public sealed class SolTrialWfp : IDisposable
{
    private static readonly Guid V4 = new Guid("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    private static readonly Guid V6 = new Guid("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    private static readonly Guid Package = new Guid("71bc78fa-f17c-4997-a602-6abb261f351c");
    private static readonly Guid Application = new Guid("d78e1e87-8644-4ea5-9437-d809ecefc971");
    private static readonly Guid Address = new Guid("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
    private static readonly Guid Port = new Guid("c35a604d-d22b-4e1a-91b4-68f674ee674b");
    private static readonly Guid Protocol = new Guid("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7");
    private const uint Block = 0x1001, Permit = 0x1002, Equal = 0, NotEqual = 10, SidType = 13, BlobType = 12;
    private const uint FilterNotFound = 0x80320003, SublayerNotFound = 0x80320007;
    private const uint Persistent = 1, ClearActionRight = 8, Indexed = 0x40;
    private const string LayerName = "LocalBrain Sol fixture blocks";
    private IntPtr engine;
    private readonly Guid layerKey;
    private readonly bool persistent;
    private readonly List<ulong> filters = new List<ulong>();
    public ulong[] FilterIds => filters.ToArray();

    [StructLayout(LayoutKind.Sequential)] private struct Display {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Description;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Session {
        public Guid Key; public Display Display; public uint Flags, Timeout, Pid;
        public IntPtr Sid, Username; public int KernelMode;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public uint Size; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] private struct Sublayer {
        public Guid Key; public Display Display; public uint Flags; public IntPtr Provider;
        public Blob Data; public ushort Weight;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Value { public uint Type; public UIntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] private struct Condition {
        public Guid Field; public uint Match; public Value Value;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Action {
        public uint Type; public Guid Key;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Filter {
        public Guid Key; public Display Display; public uint Flags; public IntPtr Provider;
        public Blob Data; public Guid Layer, Sublayer; public Value Weight;
        public uint Count; public IntPtr Conditions; public Action Action;
        public ulong Context, ContextPadding; public IntPtr Reserved; public ulong Id; public Value EffectiveWeight;
    }
    [DllImport("fwpuclnt.dll", CharSet=CharSet.Unicode)] private static extern uint FwpmEngineOpen0(
        string server,uint authentication,IntPtr identity,ref Session session,out IntPtr engine);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmEngineClose0(IntPtr engine);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmSubLayerAdd0(IntPtr engine,ref Sublayer layer,IntPtr security);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterAdd0(IntPtr engine,ref Filter filter,IntPtr security,out ulong id);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterGetById0(IntPtr engine,ulong id,out IntPtr filter);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterGetByKey0(IntPtr engine,ref Guid key,out IntPtr filter);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmFilterDeleteByKey0(IntPtr engine,ref Guid key);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmSubLayerGetByKey0(IntPtr engine,ref Guid key,out IntPtr layer);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmSubLayerDeleteByKey0(IntPtr engine,ref Guid key);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmTransactionBegin0(IntPtr engine,uint flags);
    [DllImport("fwpuclnt.dll")] private static extern uint FwpmTransactionCommit0(IntPtr engine);
    [DllImport("fwpuclnt.dll")] private static extern void FwpmFreeMemory0(ref IntPtr memory);
    [DllImport("fwpuclnt.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]
    private static extern uint FwpmGetAppIdFromFileName0(string path,out IntPtr appId);

    private static IntPtr Open(uint flags)
    {
        var session = new Session { Flags=flags, Timeout=3000, Display=new Display { Name="LocalBrain Sol trial" } };
        Check(FwpmEngineOpen0(null,10,IntPtr.Zero,ref session,out var handle),"open engine");
        return handle;
    }

    public SolTrialWfp(string sid,int port):this(sid,port,Guid.NewGuid(),false) { }

    public SolTrialWfp(string sid,int port,Guid leaseKey):this(sid,port,leaseKey,true) { }

    public static SolTrialWfp PermitFixtureLoopback(string sid,int port,string path) => new SolTrialWfp(sid,port,path);

    private SolTrialWfp(string sid,int port,string path)
    {
        if(IntPtr.Size!=8 || Marshal.SizeOf<Filter>()!=200 || Marshal.SizeOf<Condition>()!=40)
            throw new PlatformNotSupportedException("Unexpected WFP fixture ABI");
        if(port<49152 || port>65535)throw new ArgumentOutOfRangeException(nameof(port));
        var identity=new SecurityIdentifier(sid);
        if(!identity.Value.StartsWith("S-1-15-2-",StringComparison.Ordinal) ||
            Path.GetFileName(path)!="LocalBrain.ClientHost.Tests.exe" || !Path.IsPathFullyQualified(path) ||
            !File.Exists(path))
            throw new ArgumentException("Expected a trial package SID and executable");
        layerKey=Guid.NewGuid();
        var bytes=new byte[identity.BinaryLength];
        identity.GetBinaryForm(bytes,0);
        var pointer=Marshal.AllocHGlobal(bytes.Length);
        var app=IntPtr.Zero;
        try {
            Marshal.Copy(bytes,0,pointer,bytes.Length);
            Check(FwpmGetAppIdFromFileName0(path,out app),"resolve trial application");
            engine=Open(1);
            Check(FwpmTransactionBegin0(engine,0),"begin trial permit install");
            var layer=new Sublayer {Key=layerKey,Display=new Display {Name="LocalBrain Sol fixture loopback permit"},Weight=65535};
            Check(FwpmSubLayerAdd0(engine,ref layer,IntPtr.Zero),"add trial permit sublayer");
            Add(Guid.NewGuid(),V4,"trial application loopback TCP",identity,new[]{
                Make(Package,Equal,SidType,unchecked((ulong)pointer.ToInt64())),
                Make(Application,Equal,BlobType,unchecked((ulong)app.ToInt64())),
                Make(Address,Equal,3,0x7f000001),Make(Port,Equal,2,(uint)port),Make(Protocol,Equal,1,6)
            },Permit);
            Check(FwpmTransactionCommit0(engine),"commit trial permit");
        } catch {
            if(engine!=IntPtr.Zero) {
                var previous=engine;engine=IntPtr.Zero;
                Check(FwpmEngineClose0(previous),"close failed permit");
            }
            throw;
        } finally {
            if(app!=IntPtr.Zero)FwpmFreeMemory0(ref app);
            Marshal.FreeHGlobal(pointer);
        }
    }

    private SolTrialWfp(string sid,int port,Guid leaseKey,bool durable)
    {
        if(IntPtr.Size!=8 || Marshal.SizeOf<Filter>()!=200 || Marshal.SizeOf<Condition>()!=40)
            throw new PlatformNotSupportedException("Unexpected WFP fixture ABI");
        if(port<49152 || port>65535)throw new ArgumentOutOfRangeException(nameof(port));
        if(leaseKey==Guid.Empty)throw new ArgumentException("A trial lease key is required",nameof(leaseKey));
        layerKey=leaseKey;
        persistent=durable;
        var identity=new SecurityIdentifier(sid);
        var bytes=new byte[identity.BinaryLength]; identity.GetBinaryForm(bytes,0);
        var pointer=Marshal.AllocHGlobal(bytes.Length);
        try {
            Marshal.Copy(bytes,0,pointer,bytes.Length);
            engine=Open(persistent?0u:1u);
            Check(FwpmTransactionBegin0(engine,0),"begin fixture install");
            var layer=new Sublayer { Key=layerKey, Display=new Display { Name=LayerName },
                Flags=persistent?Persistent:0, Weight=65535 };
            Check(FwpmSubLayerAdd0(engine,ref layer,IntPtr.Zero),"add fixture sublayer");
            foreach(var plan in Plans(layerKey,port,pointer))
                Add(plan.Key,plan.Layer,plan.Name,identity,plan.Conditions);
            Check(FwpmTransactionCommit0(engine),"commit fixture install");
        } catch {
            // Engine close aborts a pending install. A committed persistent lease survives owner exit.
            if(engine!=IntPtr.Zero) {
                var previous=engine;engine=IntPtr.Zero;
                Check(FwpmEngineClose0(previous),"close failed install");
            }
            throw;
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static Condition Make(Guid field,uint match,uint type,ulong data) =>
        new Condition { Field=field, Match=match, Value=new Value { Type=type,Data=new UIntPtr(data) } };

    private static (Guid Key,Guid Layer,string Name,Condition[] Conditions)[] Plans(Guid lease,int port,IntPtr sid)
    {
        var package=Make(Package,Equal,SidType,unchecked((ulong)sid.ToInt64()));
        return new[] {
            (Key(1),V4,"non-broker IPv4 address",new[]{package,Make(Address,NotEqual,3,0x7f000001)}),
            (Key(2),V4,"non-broker IPv4 port",new[]{package,Make(Port,NotEqual,2,(uint)port)}),
            (Key(3),V4,"IPv4 non-TCP",new[]{package,Make(Protocol,NotEqual,1,6)}),
            (Key(4),V6,"all IPv6",new[]{package})
        };
        Guid Key(byte offset) {var bytes=lease.ToByteArray();bytes[15]^=offset;return new Guid(bytes);}
    }

    private void Add(Guid key,Guid layer,string name,SecurityIdentifier identity,Condition[] planned,uint action=Block)
    {
        var size=Marshal.SizeOf<Condition>();
        var buffer=Marshal.AllocHGlobal(planned.Length*size);
        try {
            for(int i=0;i<planned.Length;i++)Marshal.StructureToPtr(planned[i],buffer+i*size,false);
            var filter=new Filter { Key=key, Display=new Display { Name="LocalBrain Sol trial: "+name },
                Flags=ClearActionRight|(persistent?Persistent:0),
                Layer=layer, Sublayer=layerKey, Count=(uint)planned.Length, Conditions=buffer,
                Action=new Action { Type=action } };
            Check(FwpmFilterAdd0(engine,ref filter,IntPtr.Zero,out var id),"add "+name);
            filters.Add(id);
            Check(FwpmFilterGetById0(engine,id,out var stored),"read back "+name);
            try {
                if(Verify(stored,key,layer,layerKey,name,identity,planned,persistent,action)!=id)
                    throw new InvalidOperationException("WFP filter ID mismatch: "+name);
            } finally { FwpmFreeMemory0(ref stored); }
        } finally { Marshal.FreeHGlobal(buffer); }
    }

    private static ulong Verify(IntPtr stored,Guid key,Guid layer,Guid sublayer,string name,
        SecurityIdentifier identity,Condition[] planned,bool durable,uint action=Block)
    {
        var actual=Marshal.PtrToStructure<Filter>(stored);
        var flags=ClearActionRight|(durable?Persistent:0);
        if(actual.Key!=key || actual.Layer!=layer || actual.Sublayer!=sublayer || actual.Action.Type!=action ||
            actual.Count!=planned.Length || (actual.Flags&~Indexed)!=flags || actual.Display.Name!="LocalBrain Sol trial: "+name)
            throw new InvalidOperationException("Unexpected WFP filter: "+name);
        var seen=new HashSet<Guid>();
        var size=Marshal.SizeOf<Condition>();
        for(int i=0;i<planned.Length;i++) {
            var item=Marshal.PtrToStructure<Condition>(actual.Conditions+i*size);
            var index=Array.FindIndex(planned,c=>c.Field==item.Field);
            if(index<0 || !seen.Add(item.Field) || item.Match!=planned[index].Match ||
                item.Value.Type!=planned[index].Value.Type ||
                (item.Field==Package ? !identity.Equals(new SecurityIdentifier(new IntPtr(unchecked((long)item.Value.Data.ToUInt64()))))
                    : item.Field==Application ? !SameBlob(item.Value.Data,planned[index].Value.Data)
                    : item.Value.Data.ToUInt64()!=planned[index].Value.Data.ToUInt64()))
                throw new InvalidOperationException("Unexpected WFP condition: "+name);
        }
        return actual.Id;
    }

    private static bool SameBlob(UIntPtr actual,UIntPtr planned)
    {
        var left=Marshal.PtrToStructure<Blob>(new IntPtr(unchecked((long)actual.ToUInt64())));
        var right=Marshal.PtrToStructure<Blob>(new IntPtr(unchecked((long)planned.ToUInt64())));
        if(left.Size!=right.Size || left.Size==0 || left.Size>32768)return false;
        for(int i=0;i<(int)left.Size;i++) {
            if(Marshal.ReadByte(left.Data,i)!=Marshal.ReadByte(right.Data,i))return false;
        }
        return true;
    }

    public static ulong[] VerifyPersistent(string sid,int port,Guid leaseKey) =>
        ReadPersistent(sid,port,leaseKey,false);

    public static void ReleasePersistent(string sid,int port,Guid leaseKey) =>
        ReadPersistent(sid,port,leaseKey,true);

    private static ulong[] ReadPersistent(string sid,int port,Guid leaseKey,bool remove)
    {
        var identity=new SecurityIdentifier(sid);
        var handle=Open(0);
        try {
            Check(FwpmTransactionBegin0(handle,remove?0u:1u),"begin fixture readback");
            var plans=Plans(leaseKey,port,IntPtr.Zero);
            var ids=new List<ulong>();
            var sublayerResult=FwpmSubLayerGetByKey0(handle,ref leaseKey,out var layer);
            if(remove && sublayerResult==SublayerNotFound) {
                foreach(var plan in plans)RequireMissingFilter(handle,plan.Key);
                Check(FwpmTransactionCommit0(handle),"confirm absent lease");
                return Array.Empty<ulong>();
            }
            Check(sublayerResult,"read back fixture sublayer");
            try {
                var actual=Marshal.PtrToStructure<Sublayer>(layer);
                if(actual.Key!=leaseKey || actual.Flags!=Persistent || actual.Display.Name!=LayerName)
                    throw new InvalidOperationException("Persistent fixture sublayer identity mismatch");
            } finally { FwpmFreeMemory0(ref layer); }
            foreach(var plan in plans) {
                var key=plan.Key;
                Check(FwpmFilterGetByKey0(handle,ref key,out var stored),"read back "+plan.Name);
                try { ids.Add(Verify(stored,key,plan.Layer,leaseKey,plan.Name,identity,plan.Conditions,true)); }
                finally { FwpmFreeMemory0(ref stored); }
            }
            if(remove) {
                foreach(var plan in plans) {
                    var key=plan.Key;
                    Check(FwpmFilterDeleteByKey0(handle,ref key),"remove "+plan.Name);
                    RequireMissingFilter(handle,key);
                }
                Check(FwpmSubLayerDeleteByKey0(handle,ref leaseKey),"remove fixture sublayer");
            }
            Check(FwpmTransactionCommit0(handle),"commit fixture readback or removal");
            return ids.ToArray();
        } finally { Check(FwpmEngineClose0(handle),"close fixture readback"); }
    }

    private static void RequireMissingFilter(IntPtr handle,Guid key)
    {
        var result=FwpmFilterGetByKey0(handle,ref key,out var stored);
        if(stored!=IntPtr.Zero)FwpmFreeMemory0(ref stored);
        if(result!=FilterNotFound)throw new InvalidOperationException($"Filter {key} was not verified absent: 0x{result:X8}");
    }

    public void Dispose()
    {
        if(engine==IntPtr.Zero)return;
        var previous=engine; engine=IntPtr.Zero;
        Check(FwpmEngineClose0(previous),"close dynamic session");
        if(persistent || filters.Count==0)return;
        var verify=Open(0);
        try {
            foreach(var id in filters) {
                var result=FwpmFilterGetById0(verify,id,out var found);
                if(found!=IntPtr.Zero)FwpmFreeMemory0(ref found);
                if(result!=FilterNotFound)throw new InvalidOperationException($"Filter {id} was not verified absent: 0x{result:X8}");
            }
        } finally { Check(FwpmEngineClose0(verify),"close verification session"); }
    }

    private static void Check(uint result,string operation)
    {
        if(result!=0)throw new InvalidOperationException($"WFP {operation}: 0x{result:X8}");
    }
}
