using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace LocalBrain.ClientHost.AgentV2;

// Only a freshly created profile with a canonical Host creation receipt may be sealed.
// Registry and finite scratch qualification are separate gates.
[SupportedOSPlatform("windows")]
internal static class HostValidationProfileStorage
{
    internal sealed record ObjectAcl(string Path,bool Directory,string Dacl);
    internal sealed record Seal(string Profile,string Sid,string Root,ObjectAcl[] Objects);
    private const FileSystemRights ReadOnly=FileSystemRights.ReadAndExecute|FileSystemRights.Synchronize;
    private const FileSystemRights Writes=FileSystemRights.Write|FileSystemRights.Delete|
        FileSystemRights.DeleteSubdirectoriesAndFiles|FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership;
    private static readonly SecurityIdentifier OwnerRights=new("S-1-3-4");
    [DllImport("userenv.dll",CharSet=CharSet.Unicode)]
    private static extern int GetAppContainerFolderPath(string sid,out IntPtr path);

    internal static string OwnedRoot(string name,SecurityIdentifier sid){
        if(!System.Text.RegularExpressions.Regex.IsMatch(name,@"^LBV\.[a-f0-9]{12}$"))throw new UnauthorizedAccessException("Invalid owned profile name");
        var hr=GetAppContainerFolderPath(sid.Value,out var buffer);if(hr<0)Marshal.ThrowExceptionForHR(hr);
        string ac;try{ac=Marshal.PtrToStringUni(buffer)??throw new IOException("Profile folder missing");}finally{Marshal.FreeCoTaskMem(buffer);}
        var info=new DirectoryInfo(Path.GetFullPath(ac));var parent=info.Parent??throw new IOException("Profile root missing");
        var local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if(!info.Name.Equals("AC",StringComparison.OrdinalIgnoreCase)||!parent.Name.Equals(name,StringComparison.OrdinalIgnoreCase)||
            !parent.FullName.StartsWith(local+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Public profile folder does not identify the exact owned profile");
        CanonicalStore.GuardPath(parent.FullName);CanonicalStore.GuardPath(info.FullName);return parent.FullName;
    }
    private static string[] Inventory(string root){
        CanonicalStore.GuardPath(root);var paths=new List<string>{root};var pending=new Stack<string>();pending.Push(root);
        while(pending.Count>0)foreach(var path in Directory.EnumerateFileSystemEntries(pending.Pop())){
            var attrs=File.GetAttributes(path);if((attrs&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked owned profile object");
            paths.Add(path);if(paths.Count>128)throw new IOException("Fresh profile object budget exceeded");
            if((attrs&FileAttributes.Directory)!=0)pending.Push(path);
        }
        return paths.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static FileSystemSecurity Acl(string path,bool directory)=>directory?new DirectoryInfo(path).GetAccessControl():new FileInfo(path).GetAccessControl();
    private static CommonAce[] Common(RawSecurityDescriptor descriptor){
        if(descriptor.DiscretionaryAcl is null||descriptor.DiscretionaryAcl.Cast<GenericAce>().Any(a=>a is not CommonAce))throw new IOException("Unexpected fresh profile DACL");
        return descriptor.DiscretionaryAcl.Cast<CommonAce>().ToArray();
    }
    private static string AceBytes(GenericAce ace){var bytes=new byte[ace.BinaryLength];ace.GetBinaryForm(bytes,0);return Convert.ToHexString(bytes);}
    private static void RequireOwner(FileSystemSecurity acl,SecurityIdentifier owner){
        if(!(acl.GetOwner(typeof(SecurityIdentifier))??throw new IOException("Profile owner missing")).Equals(owner))throw new UnauthorizedAccessException("Fresh profile object is not owned by Host user");
    }
    private static ObjectAcl SealObject(string path,SecurityIdentifier sid,SecurityIdentifier owner){
        CanonicalStore.GuardPath(path);var directory=(File.GetAttributes(path)&FileAttributes.Directory)!=0;
        var acl=Acl(path,directory);RequireOwner(acl,owner);var raw=new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(),0);
        foreach(var ace in Common(raw))if(ace.SecurityIdentifier.Equals(sid)&&ace.AceQualifier==AceQualifier.AccessAllowed)ace.AccessMask&=(int)ReadOnly;
        var expected=Common(raw).Where(a=>a.SecurityIdentifier.Equals(sid)&&a.AceQualifier==AceQualifier.AccessAllowed)
            .Select(a=>(a.AccessMask,a.AceFlags)).OrderBy(a=>a.AccessMask).ThenBy(a=>a.AceFlags).ToArray();
        var unrelated=Common(raw).Where(a=>!a.SecurityIdentifier.Equals(sid)&&!a.SecurityIdentifier.Equals(OwnerRights)).Select(AceBytes).Order(StringComparer.Ordinal).ToArray();
        var bytes=new byte[raw.BinaryLength];raw.GetBinaryForm(bytes,0);acl.SetSecurityDescriptorBinaryForm(bytes,AccessControlSections.Access);
        var flags=directory?InheritanceFlags.ObjectInherit|InheritanceFlags.ContainerInherit:InheritanceFlags.None;
        acl.AddAccessRule(new FileSystemAccessRule(OwnerRights,FileSystemRights.ReadPermissions,flags,PropagationFlags.None,AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(sid,Writes,flags,PropagationFlags.None,AccessControlType.Deny));
        if(directory)new DirectoryInfo(path).SetAccessControl((DirectorySecurity)acl);else new FileInfo(path).SetAccessControl((FileSecurity)acl);
        var actual=Acl(path,directory);RequireOwner(actual,owner);var descriptor=new RawSecurityDescriptor(actual.GetSecurityDescriptorBinaryForm(),0);var entries=Common(descriptor);
        if(!entries.Where(a=>a.SecurityIdentifier.Equals(sid)&&a.AceQualifier==AceQualifier.AccessAllowed)
            .Select(a=>(a.AccessMask,a.AceFlags)).OrderBy(a=>a.AccessMask).ThenBy(a=>a.AceFlags).SequenceEqual(expected)||
            entries.Any(a=>a.SecurityIdentifier.Equals(sid)&&a.AceQualifier==AceQualifier.AccessAllowed&&(a.AccessMask&(int)Writes)!=0))
            throw new IOException("Profile read-only package ACE failed persistence check");
        if(!entries.Where(a=>!a.SecurityIdentifier.Equals(sid)&&!a.SecurityIdentifier.Equals(OwnerRights)).Select(AceBytes).Order(StringComparer.Ordinal).SequenceEqual(unrelated))
            throw new IOException("Unrelated profile ACE changed");
        if(!entries.Any(a=>a.SecurityIdentifier.Equals(OwnerRights)&&a.AceQualifier==AceQualifier.AccessAllowed&&(a.AccessMask&0x20000)!=0&&(a.AccessMask&0x40000)==0)||
            !entries.Any(a=>a.SecurityIdentifier.Equals(sid)&&a.AceQualifier==AceQualifier.AccessDenied&&(a.AccessMask&(int)Writes)==(int)Writes))
            throw new IOException("Owned profile write/owner guard missing");
        return new(path,directory,actual.GetSecurityDescriptorSddlForm(AccessControlSections.Access));
    }
    internal static Seal SealCreated(HostTaskJournal journal,string cleanupPlanHash,string name,IntPtr sid){
        var principal=new SecurityIdentifier(sid);var owned=false;
        foreach(var id in journal.Canonical.EventIds(journal.TaskId)){
            var e=journal.Canonical.ReadTaskEvent(journal.TaskId,id);
            if(e.Input.Producer!="windows_host"||e.Input.VerificationStatus!="host_verified"||e.Input.EventType!="validation_isolation_profile_created")continue;
            foreach(var hash in e.Input.EvidenceRefs){using var doc=JsonDocument.Parse(journal.Canonical.ReadEvidence(hash));var value=doc.RootElement;
                if(value.GetProperty("plan_hash").GetString()==cleanupPlanHash&&value.GetProperty("profile").GetString()==name&&value.GetProperty("sid").GetString()==principal.Value)owned=true;
            }
        }
        if(!owned)throw new UnauthorizedAccessException("Fresh profile needs canonical Host creation ownership");
        var root=OwnedRoot(name,principal);var owner=WindowsIdentity.GetCurrent().User??throw new IOException("Host user missing");var paths=Inventory(root);
        foreach(var path in paths){var acl=Acl(path,Directory.Exists(path));RequireOwner(acl,owner);Common(new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(),0));}
        var ac=Acl(Path.Combine(root,"AC"),true);
        if(!Common(new RawSecurityDescriptor(ac.GetSecurityDescriptorBinaryForm(),0)).Any(a=>a.SecurityIdentifier.Equals(principal)&&a.AceQualifier==AceQualifier.AccessAllowed&&(a.AccessMask&(int)FileSystemRights.Modify)==(int)FileSystemRights.Modify))
            throw new UnauthorizedAccessException("Fresh profile package SID access differs from the observed creation contract");
        var objects=paths.Select(path=>SealObject(path,principal,owner)).ToArray();var seal=new Seal(name,principal.Value,root,objects);Verify(seal);
        journal.Record("validation_isolation_profile_files_sealed",new{cleanup_plan_hash=cleanupPlanHash,seal,registry_write_denial_qualified=false,finite_scratch_qualified=false},"host_verified");return seal;
    }
    internal static void Verify(Seal seal){
        var sid=new SecurityIdentifier(seal.Sid);if(OwnedRoot(seal.Profile,sid)!=seal.Root)throw new IOException("Owned profile location changed");
        if(!Inventory(seal.Root).SequenceEqual(seal.Objects.Select(o=>o.Path),StringComparer.OrdinalIgnoreCase))throw new IOException("Sealed profile inventory changed");
        foreach(var item in seal.Objects){var attrs=File.GetAttributes(item.Path);if(((attrs&FileAttributes.Directory)!=0)!=item.Directory)throw new IOException("Profile object kind changed");
            var acl=Acl(item.Path,item.Directory);if(acl.GetSecurityDescriptorSddlForm(AccessControlSections.Access)!=item.Dacl)throw new IOException("Sealed profile ACL changed");
        }
    }
}
