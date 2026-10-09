using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace LocalBrain.ClientHost.AgentV2;
internal sealed class HostValidationInputLease : IDisposable {
 [StructLayout(LayoutKind.Sequential)] struct Info { public uint attributes; public System.Runtime.InteropServices.ComTypes.FILETIME creation,access,write; public uint volume,sizeHigh,sizeLow,links,indexHigh,indexLow; }
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr sa,uint disposition,uint flags,IntPtr template);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetFileInformationByHandle(SafeFileHandle file,out Info info);
 readonly Dictionary<string,SafeFileHandle> directories=new(StringComparer.OrdinalIgnoreCase);
 readonly Dictionary<string,SafeFileHandle> files=new(StringComparer.OrdinalIgnoreCase);
 static string NativePath(string path){if(path.Length<248)return path;if(path.Length<3||path[1]!=(char)58)throw new IOException("Extended input requires local absolute drive");return new string((char)92,2)+"?"+(char)92+path;}
 static void Check(SafeFileHandle handle,bool directory){
  if(handle.IsInvalid)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
  if(!GetFileInformationByHandle(handle,out var info))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
  if((info.attributes&0x400)!=0 || ((info.attributes&0x10)!=0)!=directory)throw new IOException("Linked or unexpected input type");
 }
 public void LockDirectory(string path){
  path=Path.GetFullPath(path);
  if(directories.ContainsKey(path))return;
  var parent=Path.GetDirectoryName(path);
  if(parent!=null && parent!=path)LockDirectory(parent);
  // OPEN_REPARSE_POINT; no FILE_SHARE_DELETE, including all retained ancestors.
  var handle=CreateFile(NativePath(path),0,3,IntPtr.Zero,3,0x02200000,IntPtr.Zero);
  try{Check(handle,true);directories.Add(path,handle);}catch{handle.Dispose();throw;}
 }
 public byte[] Read(string path){
  path=Path.GetFullPath(path);LockDirectory(Path.GetDirectoryName(path)??throw new IOException("Input parent absent"));
  if(!files.TryGetValue(path,out var handle)){
   handle=CreateFile(NativePath(path),0x80000000,1,IntPtr.Zero,3,0x00200000,IntPtr.Zero);
   try{Check(handle,false);files.Add(path,handle);}catch{handle.Dispose();throw;}
  }
  // Retain source handles until Release: writes/deletes stay denied throughout admission.
  using var borrowed=new SafeFileHandle(handle.DangerousGetHandle(),false);
  using var stream=new FileStream(borrowed,FileAccess.Read);
  stream.Seek(0,SeekOrigin.Begin);
  using var memory=new MemoryStream();if(stream.Length>128*1024*1024)throw new IOException("Input exceeds fixed file cap");stream.CopyTo(memory);return memory.ToArray();
 }
 public void Dispose(){foreach(var handle in files.Values)handle.Dispose();files.Clear();foreach(var handle in directories.Values)handle.Dispose();directories.Clear();}
}