using System.Text;
using System.Text.Json;

internal static class HostValidationRegistryFixture
{
    internal static string Source => HostValidationProfileFixture.Source.Replace(
        "var pass=app==1", "var registryPass=RegistryFixture.Run();var pass=registryPass&&app==1",StringComparison.Ordinal)+"\n"+RegistrySource;
    private const string RegistrySource="""
        internal static class RegistryFixture {
            [DllImport("userenv.dll")]static extern int GetAppContainerRegistryLocation(uint access,out IntPtr key);
            [DllImport("advapi32.dll",CharSet=CharSet.Unicode)]static extern int RegOpenKeyEx(IntPtr key,string subkey,uint options,uint access,out IntPtr result);
            [DllImport("advapi32.dll",CharSet=CharSet.Unicode)]static extern int RegCreateKeyEx(IntPtr key,string name,uint reserved,string keyClass,uint options,uint access,IntPtr security,out IntPtr result,out uint disposition);
            [DllImport("advapi32.dll",CharSet=CharSet.Unicode)]static extern int RegSetValueEx(IntPtr key,string name,uint reserved,uint type,byte[] data,uint size);
            [DllImport("advapi32.dll")]static extern int RegCloseKey(IntPtr key);
            static void Close(IntPtr key){if(key!=IntPtr.Zero){var code=RegCloseKey(key);if(code!=0)throw new Win32Exception(code);}}
            static int Open(IntPtr key,uint access){var code=RegOpenKeyEx(key,null,0,access,out var opened);Close(opened);return code;}
            static int Create(IntPtr key){var code=RegCreateKeyEx(key,"LocalBrainFixedSyntheticAttempt",0,null,0,0x20019,IntPtr.Zero,out var created,out _);Close(created);return code;}
            static int Write(IntPtr key){var code=RegOpenKeyEx(key,null,0,2,out var opened);try{return code==0?RegSetValueEx(opened,"LocalBrainFixedSyntheticAttempt",0,3,new byte[]{1,2,3},3):code;}finally{Close(opened);}}
            internal static bool Run(){
                var codes=new Dictionary<string,int>();var hr=GetAppContainerRegistryLocation(0x20019,out var root);if(hr<0)Marshal.ThrowExceptionForHR(hr);
                try{
                    codes["root_read"]=Open(root,0x20019);
                    codes["root_create"]=Create(root);codes["root_set_value"]=Write(root);
                    foreach(var entry in new[]{("write_dac_access",0x40000u),("write_owner_access",0x80000u),("delete_access",0x10000u),("create_link_access",0x20u)})codes[entry.Item1]=Open(root,entry.Item2);
                    codes["children_read"]=RegOpenKeyEx(root,"Children",8,0x20019,out var child);
                    try{if(codes["children_read"]!=0)throw new Win32Exception(codes["children_read"]);codes["children_create"]=Create(child);codes["children_set_value"]=Write(child);}finally{Close(child);}
                }finally{Close(root);}
                var pass=codes.Where(p=>p.Key is "root_read" or "children_read").All(p=>p.Value==0)&&codes.Where(p=>p.Key is not "root_read" and not "children_read").All(p=>p.Value==5);
                Console.WriteLine("PROFILE_REGISTRY_PROOF:"+JsonSerializer.Serialize(new{pass,codes,private_fresh_hierarchy_only=true,general_registry_qualified=false,finite_scratch_qualified=false}));return pass;
            }
        }
        """;
    internal static void CheckRoutedBuild(string root,List<string> checks){
        var run=Directory.GetDirectories(Path.Combine(root,"host","validation-runs")).Single();
        var frames=File.ReadAllText(Path.Combine(run,"validate","captured.stdout.bin"),Encoding.UTF8).Split('\n').Select(l=>l.Trim()).Where(l=>l.StartsWith("PROFILE_REGISTRY_PROOF:",StringComparison.Ordinal)).ToArray();
        if(frames.Length!=1)throw new IOException("One actual registry probe frame required");
        using var document=JsonDocument.Parse(frames[0]["PROFILE_REGISTRY_PROOF:".Length..]);var frame=document.RootElement;
        var expected=new[]{"root_read","children_read","root_create","children_create","root_set_value","children_set_value","write_dac_access","write_owner_access","delete_access","create_link_access"};var codes=frame.GetProperty("codes");
        if(!frame.GetProperty("pass").GetBoolean()||!codes.EnumerateObject().Select(p=>p.Name).Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal))||expected.Any(k=>codes.GetProperty(k).GetInt32()!=(k is "root_read" or "children_read"?0:5)))throw new IOException("Actual registry readonly probe differed");
        File.WriteAllText(Path.Combine(root,"registry-probe-result.json"),frame.GetRawText());checks.Add("actual_router_profile_registry_readonly_probe");
    }
}
