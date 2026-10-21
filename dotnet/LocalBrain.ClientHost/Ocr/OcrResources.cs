using System.Runtime.InteropServices;

namespace LocalBrain.ClientHost.Ocr;

internal sealed class OcrResources
{
    [StructLayout(LayoutKind.Sequential)]private struct PerformanceInfo{
        public uint Size;public UIntPtr CommitTotal,CommitLimit,CommitPeak,PhysicalTotal,PhysicalAvailable,SystemCache,KernelTotal,KernelPaged,KernelNonpaged,PageSize;
        public uint HandleCount,ProcessCount,ThreadCount;
    }
    [DllImport("psapi.dll",SetLastError=true)]private static extern bool GetPerformanceInfo(ref PerformanceInfo value,uint size);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetSystemTimes(out long idle,out long kernel,out long user);
    internal sealed record Sample(long AvailableRam,long CommitHeadroom,double MachineCpuPercent);
    // Short-interval GetSystemTimes deltas can be zero or a single scheduler tick.
    // Keep a bounded CPU sampling window; RAM/commit are still measured on every call.
    internal sealed class CpuWindow
    {
        private bool initialized;
        private long priorIdle,priorTotal,priorAt;
        private double percent=100;
        internal double Observe(long idle,long total,long now)
        {
            if(!initialized||idle<priorIdle||total<priorTotal||now<priorAt){
                initialized=true;priorIdle=idle;priorTotal=total;priorAt=now;return percent=100;
            }
            if(now-priorAt<250||total==priorTotal)return percent;
            var delta=total-priorTotal;
            percent=Math.Clamp(100.0*(delta-(idle-priorIdle))/delta,0,100);
            priorIdle=idle;priorTotal=total;priorAt=now;return percent;
        }
    }
    private readonly object sampleGate=new();
    private readonly CpuWindow cpuWindow=new();
    internal Sample Read(){
        lock(sampleGate){
        var p=new PerformanceInfo{Size=(uint)Marshal.SizeOf<PerformanceInfo>()};
        if(!GetPerformanceInfo(ref p,p.Size)||!GetSystemTimes(out var idle,out var kernel,out var user))throw new OcrException("RESOURCE_PROBE_FAILED");
        var cpu=cpuWindow.Observe(idle,kernel+user,Environment.TickCount64);
        return new(checked((long)(p.PhysicalAvailable.ToUInt64()*p.PageSize.ToUInt64())),checked((long)((p.CommitLimit.ToUInt64()-p.CommitTotal.ToUInt64())*p.PageSize.ToUInt64())),Math.Clamp(cpu,0,100));
        }
    }
    internal static string? Refusal(OcrConfig config,Sample sample,bool priority)=>priority?"PRIORITY_WORK_ACTIVE":
        sample.AvailableRam<config.RamAdmissionBytes?"RAM_ADMISSION":sample.CommitHeadroom<config.CommitAdmissionBytes?"COMMIT_ADMISSION":
        sample.MachineCpuPercent>config.MaxMachineCpuPercent?"CPU_ADMISSION":null;
}
