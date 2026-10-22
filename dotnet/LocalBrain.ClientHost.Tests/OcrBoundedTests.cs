using System.Text.Json;
using LocalBrain.ClientHost.Ocr;
internal static class OcrBoundedTests {
 internal static async Task RunAsync(string assets,string fixtures,string output,string? selected=null){
  assets=Path.GetFullPath(assets);fixtures=Path.GetFullPath(fixtures);output=Path.GetFullPath(output);Directory.CreateDirectory(output);
  var hashes=OcrJobStore.Read<JsonElement>(Path.Combine(assets,"runtime-manifest.json")).GetProperty("files").EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.GetProperty("sha256").GetString()!);
  var config=new OcrConfig(true,Path.Combine(output,"jobs"),assets,Path.Combine(assets,"python","python.exe"),Path.Combine(assets,"local_ocr_worker.py"),Path.Combine(assets,"models"),[fixtures],hashes,["AUTO","TABLE_LAYOUT","PDF_DOCUMENT"],[],1024L<<20,1024L<<20,704L<<20,50,new string('a',64));
  if(selected is not null&&selected!="financial-large.jpg")throw new ArgumentException("PUBLIC_FIXTURE_ONLY");
  foreach(var name in selected is null?new[]{"financial.png","financial.webp","financial-small.jpg","financial-large.jpg","financial-exif.jpg","financial.pdf"}:new[]{selected}){
   var input=Path.Combine(fixtures,name);var id=Guid.NewGuid().ToString("N");var attempt=Path.Combine(output,"attempt-"+id);
   using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));
   var result=await OcrWorkerProcess.RunAsync(config,id,input,OcrJobStore.HashFile(input),attempt,"page",0,"TABLE_LAYOUT",()=>false,timeout.Token);
   File.WriteAllText(Path.Combine(output,name+".receipt.json"),JsonSerializer.Serialize(result with{Result=null}));
   if(result.Result is {} value)File.WriteAllText(Path.Combine(output,name+".json"),value.GetRawText());
   Console.WriteLine(JsonSerializer.Serialize(new{fixture=name,result.State,result.ErrorCode,result.ElapsedMs,result.PeakPrivateCommit,result.PeakWorkingSet,result.CpuSeconds,memory_limit=704L<<20,network_capabilities=0,real_ocr=true,private_data=false}));
   if(result.State!="complete")throw new Exception("PUBLIC_BOUNDED_OCR_FAILED:"+name+":"+result.ErrorCode);
  }
 }
}
