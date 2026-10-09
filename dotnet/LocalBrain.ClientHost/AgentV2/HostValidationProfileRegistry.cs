using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace LocalBrain.ClientHost.AgentV2;

[SupportedOSPlatform("windows")]
internal static class HostValidationProfileRegistry {
 internal sealed record KeySeal(string Relative,string Dacl);
 internal sealed record Seal(string Profile,string Sid,string Owner,KeySeal[] Keys);
 internal sealed class Lease:IDisposable {
  private IntPtr key;internal Seal Plan{get;}
  internal Lease(IntPtr value,Seal plan){key=value;Plan=plan;}
  internal void Verify(){if(key==IntPtr.Zero)throw new ObjectDisposedException(nameof(Lease));var tree=JsonSerializer.SerializeToElement(AuditTree(key,new SecurityIdentifier(Plan.Sid),new SecurityIdentifier(Plan.Owner)));RequireRows(tree,Plan);}
  public void Dispose(){var owned=key;key=IntPtr.Zero;if(owned!=IntPtr.Zero){var code=Api.RegCloseKey(owned);if(code!=0)throw new Win32Exception(code);}}
 }
 private static void RequireRows(JsonElement tree,Seal plan){
  var rows=tree.GetProperty("keys").EnumerateArray().ToArray();
  if(!rows.Select(r=>r.GetProperty("relative").GetString()).SequenceEqual(plan.Keys.Select(k=>k.Relative)))throw new IOException("Owned registry inventory changed");
  for(var index=0;index<rows.Length;index++)if(!rows[index].GetProperty("owner_matches_host").GetBoolean()||!rows[index].GetProperty("own_package_allowance").GetBoolean()||rows[index].GetProperty("registry_link").GetBoolean()||rows[index].GetProperty("sddl").GetString()!=plan.Keys[index].Dacl)throw new IOException("Owned registry identity or DACL changed");
 }
 internal static Lease Guard(HostTaskJournal journal,string cleanupHash,HostValidationProfileStorage.Seal files,IntPtr process,Lease? existing){
  HostValidationProfileStorage.Verify(files);
  var profile=new SecurityIdentifier(files.Sid);var user=WindowsIdentity.GetCurrent().User??throw new IOException("Host user missing");
  if(existing is not null&&(existing.Plan.Profile!=files.Profile||existing.Plan.Sid!=files.Sid||existing.Plan.Owner!=user.Value))throw new UnauthorizedAccessException("Registry seal identity differs from Host profile");
  if(!Api.OpenProcessToken(process,10,out var token))throw new Win32Exception();
  IntPtr duplicate=IntPtr.Zero;IntPtr root=IntPtr.Zero;IntPtr retained=IntPtr.Zero;Lease? candidate=null;var writeHandles=new List<IntPtr>();
  try{
   Api.GetTokenInformation(token,31,IntPtr.Zero,0,out var size);if(size is <8 or >4096)throw new IOException("Bounded child profile token required");var data=Marshal.AllocHGlobal((int)size);
   try{if(!Api.GetTokenInformation(token,31,data,size,out _))throw new Win32Exception();if(!new SecurityIdentifier(Marshal.ReadIntPtr(data)).Equals(profile))throw new UnauthorizedAccessException("Suspended child profile differs from Host seal");}finally{Marshal.FreeHGlobal(data);}
   if(!Api.DuplicateToken(token,2,out duplicate))throw new Win32Exception();
   using var safe=new SafeAccessTokenHandle(duplicate);duplicate=IntPtr.Zero;
   var hr=WindowsIdentity.RunImpersonated(safe,()=>Api.GetAppContainerRegistryLocation(0x20019,out root));if(hr<0)Marshal.ThrowExceptionForHR(hr);
   using var impersonated=WindowsIdentity.GetCurrent(true);if(impersonated is not null)throw new UnauthorizedAccessException("Registry guard did not revert to Host");
   var tree=JsonSerializer.SerializeToElement(AuditTree(root,profile,user));var rows=tree.GetProperty("keys").EnumerateArray().ToArray();
   if(rows.Any(r=>!r.GetProperty("owner_matches_host").GetBoolean()||!r.GetProperty("own_package_allowance").GetBoolean()||r.GetProperty("registry_link").GetBoolean()))throw new UnauthorizedAccessException("Fresh owned registry hierarchy or link differs");
   if(existing is not null){
    RequireRows(tree,existing.Plan);existing.Verify();
    return existing;
   }
   var targets=new List<(string path,IntPtr key)>();
   foreach(var row in rows){var relative=row.GetProperty("relative").GetString()!;var code=Api.RegOpenKeyEx(root,relative.Length==0?null:relative.Replace('/','\\'),8,0x6001F,out var handle);if(code!=0)throw new Win32Exception(code);writeHandles.Add(handle);targets.Add((relative,handle));}
   foreach(var target in targets){var descriptor=ReadRegistryDescriptor(target.key,user);if(descriptor.DiscretionaryAcl is null||descriptor.DiscretionaryAcl.Cast<GenericAce>().Any(a=>a is not CommonAce)||!descriptor.DiscretionaryAcl.Cast<CommonAce>().Any(a=>a.SecurityIdentifier.Equals(profile)&&a.AceQualifier==AceQualifier.AccessAllowed&&(a.AccessMask&0xF003F)==0xF003F))throw new UnauthorizedAccessException("Fresh registry key contract differs before first setter");}
   foreach(var target in targets)SealRegistry(target.key,profile,user,ReadRegistryDescriptor(target.key,user));
   var sealedTree=JsonSerializer.SerializeToElement(AuditTree(root,profile,user));var after=sealedTree.GetProperty("keys").EnumerateArray().ToArray();
   if(!after.Select(r=>r.GetProperty("relative").GetString()).SequenceEqual(rows.Select(r=>r.GetProperty("relative").GetString())))throw new IOException("Owned registry hierarchy changed during sealing");
   var result=new Seal(files.Profile,files.Sid,user.Value,after.Select(r=>new KeySeal(r.GetProperty("relative").GetString()!,r.GetProperty("sddl").GetString()!)).ToArray());
   RequireRows(sealedTree,result);var retainedCode=Api.RegOpenKeyEx(root,null,0,0x20019,out retained);if(retainedCode!=0)throw new Win32Exception(retainedCode);
   journal.Record("validation_isolation_profile_registry_sealed",new{cleanup_plan_hash=cleanupHash,seal=result,finite_scratch_qualified=false,general_registry_qualified=false},"host_verified");candidate=new Lease(retained,result);retained=IntPtr.Zero;return candidate;
  }finally{
   var errors=new List<Exception>();foreach(var handle in writeHandles)try{var code=Api.RegCloseKey(handle);if(code!=0)throw new Win32Exception(code);}catch(Exception e){errors.Add(e);}
   try{if(retained!=IntPtr.Zero){var code=Api.RegCloseKey(retained);if(code!=0)throw new Win32Exception(code);}}catch(Exception e){errors.Add(e);}
   try{if(root!=IntPtr.Zero){var code=Api.RegCloseKey(root);if(code!=0)throw new Win32Exception(code);}}catch(Exception e){errors.Add(e);}
   try{if(duplicate!=IntPtr.Zero&&!Api.CloseHandle(duplicate))throw new IOException("Owned duplicate token release failed");}catch(Exception e){errors.Add(e);}
   try{if(!Api.CloseHandle(token))throw new IOException("Owned child token release failed");}catch(Exception e){errors.Add(e);}
   if(errors.Count>0){try{candidate?.Dispose();}catch(Exception e){errors.Add(e);}throw new AggregateException("Owned registry guard handle release failed",errors);}
  }
 }
 private static object AuditTree(IntPtr root,SecurityIdentifier profile,SecurityIdentifier user){
  var handles=new List<IntPtr>();var rows=new List<object>();var pending=new Queue<(string path,IntPtr key)>();pending.Enqueue(("",root));
  try{
   while(pending.Count>0){var item=pending.Dequeue();if(rows.Count>=128)throw new IOException("Fresh owned registry hierarchy bound exceeded");
    var code=Api.GetSecurityInfo(item.key,4,5,out var owner,out _,out _,out _,out var descriptor);if(code!=0)throw new Win32Exception((int)code);string sddl;bool ownerMatch;bool packageAllowance;
    try{var length=Api.GetSecurityDescriptorLength(descriptor);if(length is <20 or >65536)throw new IOException("Bounded registry descriptor required");var data=new byte[length];Marshal.Copy(descriptor,data,0,data.Length);var raw=new RawSecurityDescriptor(data,0);sddl=raw.GetSddlForm(AccessControlSections.All);ownerMatch=new SecurityIdentifier(owner).Equals(user);packageAllowance=raw.DiscretionaryAcl?.Cast<GenericAce>().OfType<CommonAce>().Any(a=>a.SecurityIdentifier.Equals(profile)&&a.AceQualifier==AceQualifier.AccessAllowed)??false;}finally{Api.LocalFree(descriptor);}
    uint linkSize=0;var link=Api.RegQueryValueExInfo(item.key,"SymbolicLinkValue",IntPtr.Zero,out var linkType,IntPtr.Zero,ref linkSize);if(link!=0&&link!=2)throw new Win32Exception(link);var isLink=link==0&&linkType==6;
    rows.Add(new{relative=item.path,owner_matches_host=ownerMatch,own_package_allowance=packageAllowance,sddl,registry_link=isLink});if(isLink)continue;
    for(uint index=0;;index++){var name=new StringBuilder(256);uint length=256;code=(uint)Api.RegEnumKeyEx(item.key,index,name,ref length,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);if(code==259)break;if(code!=0)throw new Win32Exception((int)code);if(rows.Count+pending.Count+1>128)throw new IOException("Fresh registry key budget exceeded");
     var segment=name.ToString();if(segment.Contains('/')||segment.Contains('\\')||segment is "" or "." or "..")throw new IOException("Registry relative-name contract differs");
     var opened=Api.RegOpenKeyEx(item.key,segment,8,0x20019,out var child);if(opened!=0)throw new Win32Exception(opened);handles.Add(child);pending.Enqueue((item.path.Length==0?segment:item.path+"/"+segment,child));
    }
   }
   return new{status="READ_ONLY_FRESH_PROFILE_REGISTRY_HIERARCHY_AUDIT",profile_sid=profile.Value,keys=rows,general_storage_qualified=false};
  }finally{var errors=new List<Exception>();foreach(var handle in handles){try{var code=Api.RegCloseKey(handle);if(code!=0)throw new Win32Exception(code);}catch(Exception e){errors.Add(e);}}if(errors.Count>0)throw new AggregateException("Owned hierarchy handle release failed",errors);}
 }
 private static object SealRegistry(IntPtr key,SecurityIdentifier profile,SecurityIdentifier user,RawSecurityDescriptor raw){
  const int writes=0xD0026;var acl=raw.DiscretionaryAcl??throw new IOException("Owned registry DACL missing");
  var unrelated=acl.Cast<CommonAce>().Where(a=>!a.SecurityIdentifier.Equals(profile)&&a.SecurityIdentifier.Value!="S-1-3-4").Select(AceBytes).Order(StringComparer.Ordinal).ToArray();
  if(acl.Cast<CommonAce>().Any(a=>a.SecurityIdentifier.Value=="S-1-3-4"&&(a.AceQualifier!=AceQualifier.AccessAllowed||a.AccessMask!=0x20000)))throw new IOException("Unexpected registry OWNER RIGHTS allowance");
  foreach(var ace in acl.Cast<CommonAce>())if(ace.SecurityIdentifier.Equals(profile)&&ace.AceQualifier==AceQualifier.AccessAllowed)ace.AccessMask&=0x20019;
  var expected=acl.Cast<CommonAce>().Where(a=>a.SecurityIdentifier.Equals(profile)&&a.AceQualifier==AceQualifier.AccessAllowed).Select(a=>(a.AccessMask,a.AceFlags)).ToArray();
  acl.InsertAce(0,new CommonAce(AceFlags.ObjectInherit|AceFlags.ContainerInherit,AceQualifier.AccessDenied,writes,profile,false,null));
  var at=0;while(at<acl.Count&&(acl[at].AceFlags&AceFlags.Inherited)==0)at++;
  acl.InsertAce(at,new CommonAce(AceFlags.ObjectInherit|AceFlags.ContainerInherit,AceQualifier.AccessAllowed,0x20000,new SecurityIdentifier("S-1-3-4"),false,null));
  var bytes=new byte[acl.BinaryLength];acl.GetBinaryForm(bytes,0);var pointer=Marshal.AllocHGlobal(bytes.Length);
  try{Marshal.Copy(bytes,0,pointer,bytes.Length);var code=Api.SetSecurityInfo(key,4,4,IntPtr.Zero,IntPtr.Zero,pointer,IntPtr.Zero);if(code!=0)throw new Win32Exception((int)code);}finally{Marshal.FreeHGlobal(pointer);}
  var result=Api.GetSecurityInfo(key,4,5,out var owner,out _,out _,out _,out var descriptor);if(result!=0)throw new Win32Exception((int)result);
  try{
   var length=Api.GetSecurityDescriptorLength(descriptor);if(length is <20 or >65536)throw new IOException("Bounded owned registry descriptor required");var data=new byte[length];Marshal.Copy(descriptor,data,0,data.Length);var actual=new RawSecurityDescriptor(data,0);if(!new SecurityIdentifier(owner).Equals(user)||actual.DiscretionaryAcl is null||actual.DiscretionaryAcl.Cast<GenericAce>().Any(a=>a is not CommonAce))throw new IOException("Registry owner or DACL changed unexpectedly");
   var entries=actual.DiscretionaryAcl.Cast<CommonAce>().ToArray();if(!entries.Where(a=>a.SecurityIdentifier.Equals(profile)&&a.AceQualifier==AceQualifier.AccessAllowed).Select(a=>(a.AccessMask,a.AceFlags)).SequenceEqual(expected)||entries.Any(a=>a.SecurityIdentifier.Equals(profile)&&a.AceQualifier==AceQualifier.AccessAllowed&&(a.AccessMask&writes)!=0))throw new IOException("Owned registry readonly mask or flags differs");
   if(!entries.Where(a=>!a.SecurityIdentifier.Equals(profile)&&a.SecurityIdentifier.Value!="S-1-3-4").Select(AceBytes).Order(StringComparer.Ordinal).SequenceEqual(unrelated))throw new IOException("Unrelated registry ACE changed");
   if(!entries.Any(a=>a.SecurityIdentifier.Value=="S-1-3-4"&&a.AceQualifier==AceQualifier.AccessAllowed&&a.AccessMask==0x20000))throw new IOException("Registry OWNER RIGHTS guard absent");
   return new{after_sddl=actual.GetSddlForm(AccessControlSections.All),own_read_mask=0x20019,write_mask_removed=writes,other_aces_unchanged=true,own_allow_flags_preserved=true};
  }finally{Api.LocalFree(descriptor);}
 }
 static string AceBytes(GenericAce ace){var bytes=new byte[ace.BinaryLength];ace.GetBinaryForm(bytes,0);return Convert.ToHexString(bytes);}
 static RawSecurityDescriptor ReadRegistryDescriptor(IntPtr key,SecurityIdentifier owner){
  var code=Api.GetSecurityInfo(key,4,5,out var actualOwner,out _,out _,out _,out var pointer);if(code!=0)throw new Win32Exception((int)code);
  try{if(!new SecurityIdentifier(actualOwner).Equals(owner))throw new IOException("Fresh owned registry owner differs");var size=Api.GetSecurityDescriptorLength(pointer);if(size is <20 or >65536)throw new IOException("Bounded registry DACL required");var bytes=new byte[size];Marshal.Copy(pointer,bytes,0,bytes.Length);return new RawSecurityDescriptor(bytes,0);}finally{Api.LocalFree(pointer);}
 }
 internal static class Api {
  [DllImport("advapi32.dll",SetLastError=true)]internal static extern bool GetTokenInformation(IntPtr token,int kind,IntPtr buffer,uint size,out uint needed);
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode)]internal static extern int RegOpenKeyEx(IntPtr key,string? subkey,uint options,uint access,out IntPtr result);
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode)]internal static extern int RegEnumKeyEx(IntPtr key,uint index,StringBuilder name,ref uint length,IntPtr reserved,IntPtr keyClass,IntPtr classLength,IntPtr lastWrite);
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode)]internal static extern int RegCreateKeyEx(IntPtr key,string name,uint reserved,string? keyClass,uint options,uint access,IntPtr security,out IntPtr result,out uint disposition);
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode)]internal static extern int RegQueryValueEx(IntPtr key,string name,IntPtr reserved,out uint type,byte[] data,ref uint size);
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode,EntryPoint="RegQueryValueExW")]internal static extern int RegQueryValueExInfo(IntPtr key,string name,IntPtr reserved,out uint type,IntPtr data,ref uint size);
  [DllImport("advapi32.dll")]internal static extern uint SetSecurityInfo(IntPtr key,uint kind,uint info,IntPtr owner,IntPtr group,IntPtr dacl,IntPtr sacl);
  [DllImport("userenv.dll",CharSet=CharSet.Unicode)]internal static extern int GetAppContainerFolderPath(string sid,out IntPtr path);
  [DllImport("userenv.dll")]internal static extern int GetAppContainerRegistryLocation(uint access,out IntPtr key);
  [DllImport("advapi32.dll",CharSet=CharSet.Unicode)]internal static extern int RegSetValueEx(IntPtr key,string name,uint reserved,uint type,byte[] data,uint size);
  [DllImport("advapi32.dll")]internal static extern int RegCloseKey(IntPtr key);
  [DllImport("advapi32.dll",SetLastError=true)]internal static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
  [DllImport("advapi32.dll",SetLastError=true)]internal static extern bool DuplicateToken(IntPtr token,int level,out IntPtr duplicate);
  [DllImport("advapi32.dll")]internal static extern uint GetSecurityInfo(IntPtr handle,uint type,uint info,out IntPtr owner,out IntPtr group,out IntPtr dacl,out IntPtr sacl,out IntPtr descriptor);
  [DllImport("advapi32.dll")]internal static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
  [DllImport("kernel32.dll")]internal static extern IntPtr LocalFree(IntPtr memory);
  [DllImport("kernel32.dll")]internal static extern bool CloseHandle(IntPtr handle);
 }
}
