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
    private long priorIdle,priorTotal;
    internal Sample Read(){
        var p=new PerformanceInfo{Size=(uint)Marshal.SizeOf<PerformanceInfo>()};
        if(!GetPerformanceInfo(ref p,p.Size)||!GetSystemTimes(out var idle,out var kernel,out var user))throw new OcrException("RESOURCE_PROBE_FAILED");
        var total=kernel+user;var delta=total-priorTotal;
        var cpu=priorTotal==0||delta<=0?100:100.0*(delta-(idle-priorIdle))/delta;priorIdle=idle;priorTotal=total;
        return new(checked((long)(p.PhysicalAvailable.ToUInt64()*p.PageSize.ToUInt64())),checked((long)((p.CommitLimit.ToUInt64()-p.CommitTotal.ToUInt64())*p.PageSize.ToUInt64())),Math.Clamp(cpu,0,100));
    }
    internal static string? Refusal(OcrConfig config,Sample sample,bool priority)=>priority?"PRIORITY_WORK_ACTIVE":
        sample.AvailableRam<config.RamAdmissionBytes?"RAM_ADMISSION":sample.CommitHeadroom<config.CommitAdmissionBytes?"COMMIT_ADMISSION":
        sample.MachineCpuPercent>config.MaxMachineCpuPercent?"CPU_ADMISSION":null;
}
