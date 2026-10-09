import { Type } from "typebox";
import { requestHost } from "./host-request.mjs";
export default function(pi) {
  pi.registerTool({
    name:"host_recall",label:"Host Recall",
    description:"Read immutable hash-verified evidence attached to the current Agent task across Pi sessions. Offset is a UTF-16 character index; pages contain at most 4096 characters.",
    parameters:Type.Object({evidenceHash:Type.String({pattern:"^[a-f0-9]{64}$"}),offset:Type.Optional(Type.Integer({minimum:0}))},{additionalProperties:false}),
    async execute(_id,params,signal) {
      const result=await requestHost("recall",params,signal);
      if(!result.ok)throw new Error("Host recall rejected: "+result.status);
      return {content:[{type:"text",text:result.text}],details:{hash:result.contentHash}};
    }
  });
  pi.registerTool({
    name:"host_write",label:"Host Write",
    description:"Request a Windows Host write to a task-authorized relative file. The Host validates and owns the mutation.",
    promptSnippet:"Write an allowed file through the Windows Host",
    parameters:Type.Object({path:Type.String({minLength:1,maxLength:260}),content:Type.String({maxLength:1048576})},{additionalProperties:false}),
    async execute(_id,params,signal) {
      const result=await requestHost("write",params,signal);
      if(!result.ok)throw new Error("Host write rejected: "+result.status);
      return {content:[{type:"text",text:result.status+"; content SHA-256="+result.contentHash}],details:result};
    }
  });
  pi.registerTool({
    name:"host_edit",label:"Host Edit",
    description:"Request a Windows Host exact edit. oldText must occur once in a task-authorized relative file.",
    promptSnippet:"Edit an allowed file through the Windows Host",
    parameters:Type.Object({path:Type.String({minLength:1,maxLength:260}),oldText:Type.String({minLength:1,maxLength:1048576}),newText:Type.String({maxLength:1048576})},{additionalProperties:false}),
    async execute(_id,params,signal) {
      const result=await requestHost("edit",params,signal);
      if(!result.ok)throw new Error("Host edit rejected: "+result.status);
      return {content:[{type:"text",text:result.status+"; content SHA-256="+result.contentHash}],details:result};
    }
  });
}
