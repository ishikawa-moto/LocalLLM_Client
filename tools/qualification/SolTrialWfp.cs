using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Principal;

// Only the disposable network fixture uses this session. No production launch or real packet.
public sealed class SolTrialWfp : IDisposable
{
    private static readonly Guid V4 = new Guid("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    private static readonly Guid V6 = new Guid("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    private static readonly Guid Package = new Guid("71bc78fa-f17c-4997-a602-6abb261f351c");
    private static readonly Guid Address = new Guid("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
    private static readonly Guid Port = new Guid("c35a604d-d22b-4e1a-91b4-68f674ee674b");
    private static readonly Guid Protocol = new Guid("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7");
    private const uint Block = 0x1001, Equal = 0, NotEqual = 10, SidType = 13;
    private const uint FilterNotFound = 0x80320003;
    private IntPtr engine;
    private readonly Guid layerKey = Guid.NewGuid();
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
    [DllImport("fwpuclnt.dll")] private static extern void FwpmFreeMemory0(ref IntPtr memory);

    private static IntPtr Open(uint flags)
    {
        var session = new Session { Flags=flags, Timeout=3000, Display=new Display { Name="LocalBrain Sol trial" } };
        Check(FwpmEngineOpen0(null,10,IntPtr.Zero,ref session,out var handle),"open engine");
        return handle;
    }

    public SolTrialWfp(string sid, int port)
    {
        if(IntPtr.Size!=8 || Marshal.SizeOf<Filter>()!=200 || Marshal.SizeOf<Condition>()!=40)
            throw new PlatformNotSupportedException("Unexpected WFP fixture ABI");
        if(port<49152 || port>65535)throw new ArgumentOutOfRangeException(nameof(port));
        var identity=new SecurityIdentifier(sid);
        var bytes=new byte[identity.BinaryLength]; identity.GetBinaryForm(bytes,0);
        var pointer=Marshal.AllocHGlobal(bytes.Length);
        try {
            Marshal.Copy(bytes,0,pointer,bytes.Length);
            var package = Make(Package,Equal,SidType,unchecked((ulong)pointer.ToInt64()));
            engine=Open(1);
            var layer=new Sublayer { Key=layerKey, Display=new Display { Name="LocalBrain Sol fixture blocks" }, Weight=65535 };
            Check(FwpmSubLayerAdd0(engine,ref layer,IntPtr.Zero),"add fixture sublayer");
            Add(V4,"non-broker IPv4 address",identity,package,Make(Address,NotEqual,3,0x7f000001));
            Add(V4,"non-broker IPv4 port",identity,package,Make(Port,NotEqual,2,(uint)port));
            Add(V4,"IPv4 non-TCP",identity,package,Make(Protocol,NotEqual,1,6));
            Add(V6,"all IPv6",identity,package);
        } catch { Dispose(); throw; }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static Condition Make(Guid field,uint match,uint type,ulong data) =>
        new Condition { Field=field, Match=match, Value=new Value { Type=type,Data=new UIntPtr(data) } };

    private void Add(Guid layer,string name,SecurityIdentifier identity,params Condition[] planned)
    {
        var size=Marshal.SizeOf<Condition>();
        var buffer=Marshal.AllocHGlobal(planned.Length*size);
        try {
            for(int i=0;i<planned.Length;i++)Marshal.StructureToPtr(planned[i],buffer+i*size,false);
            var filter=new Filter { Key=Guid.NewGuid(), Display=new Display { Name="LocalBrain Sol trial: "+name }, Flags=8,
                Layer=layer, Sublayer=layerKey, Count=(uint)planned.Length, Conditions=buffer,
                Action=new Action { Type=Block } };
            Check(FwpmFilterAdd0(engine,ref filter,IntPtr.Zero,out var id),"add "+name);
            filters.Add(id);
            Check(FwpmFilterGetById0(engine,id,out var stored),"read back "+name);
            try {
                var actual=Marshal.PtrToStructure<Filter>(stored);
                if(actual.Id!=id || actual.Key!=filter.Key || actual.Layer!=layer || actual.Sublayer!=layerKey ||
                    actual.Action.Type!=Block || actual.Count!=planned.Length || (actual.Flags&8)==0 || (actual.Flags&~0x48u)!=0)
                    throw new InvalidOperationException($"Unexpected WFP filter {name}: ID={actual.Id}, key={actual.Key}, layer={actual.Layer}, sublayer={actual.Sublayer}, action=0x{actual.Action.Type:X}, count={actual.Count}, flags=0x{actual.Flags:X}");
                var seen=new HashSet<Guid>();
                for(int i=0;i<planned.Length;i++) {
                    var item=Marshal.PtrToStructure<Condition>(actual.Conditions+i*size);
                    var index=Array.FindIndex(planned,c=>c.Field==item.Field);
                    if(index<0 || !seen.Add(item.Field) || item.Match!=planned[index].Match ||
                        item.Value.Type!=planned[index].Value.Type ||
                        (item.Field==Package ? !identity.Equals(new SecurityIdentifier(new IntPtr(unchecked((long)item.Value.Data.ToUInt64()))))
                            : item.Value.Data.ToUInt64()!=planned[index].Value.Data.ToUInt64()))
                        throw new InvalidOperationException("Unexpected WFP condition: "+name);
                }
            } finally { FwpmFreeMemory0(ref stored); }
        } finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose()
    {
        if(engine==IntPtr.Zero)return;
        var previous=engine; engine=IntPtr.Zero;
        Check(FwpmEngineClose0(previous),"close dynamic session");
        if(filters.Count==0)return;
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
