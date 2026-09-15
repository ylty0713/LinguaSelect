using System;
using System.Collections.Generic;
using System.Linq;
namespace LinguaSelect {
public class ModelPreset {
 public string Id,Label,Model; public bool Thinking;
 public ModelPreset(string id,string label,string model,bool thinking=false){Id=id;Label=label;Model=model;Thinking=thinking;}
 public override string ToString(){return Label+" · "+Model;}
}
public class ServicePreset {
 public string Id,Name,Endpoint;public ModelPreset[] Models;
 public ServicePreset(string id,string name,string endpoint,params ModelPreset[] models){Id=id;Name=name;Endpoint=endpoint;Models=models;}
 public override string ToString(){return Name;}
}
static class ServicePresets {
 public static readonly ServicePreset[] All={
  new ServicePreset("free","基础翻译 · 无需 Key",""),
  new ServicePreset("openai","OpenAI","https://api.openai.com/v1/chat/completions",
   new ModelPreset("fast","快速翻译","gpt-4.1-mini"),new ModelPreset("balanced","均衡学习","gpt-4.1"),new ModelPreset("deep","深入解析","gpt-5-mini",true)),
  new ServicePreset("deepseek","DeepSeek","https://api.deepseek.com/chat/completions",
   new ModelPreset("fast","快速翻译","deepseek-v4-flash"),new ModelPreset("balanced","均衡学习","deepseek-v4-pro"),new ModelPreset("deep","深入解析 · 思考模式","deepseek-v4-pro",true)),
  new ServicePreset("qwen-cn","Qwen · 北京","https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions",
   new ModelPreset("fast","快速翻译","qwen-flash"),new ModelPreset("balanced","均衡学习","qwen-plus"),new ModelPreset("deep","深入解析","qwen3-max")),
  new ServicePreset("qwen-intl","Qwen · 新加坡","https://dashscope-intl.aliyuncs.com/compatible-mode/v1/chat/completions",
   new ModelPreset("fast","快速翻译","qwen-flash"),new ModelPreset("balanced","均衡学习","qwen-plus"),new ModelPreset("deep","深入解析","qwen3-max")),
  new ServicePreset("custom","自定义兼容接口","")
 };
 public static ServicePreset Get(string id){return All.FirstOrDefault(p=>p.Id==id)??All[0];}
 public static string Infer(Settings cfg){
  if(String.IsNullOrWhiteSpace(cfg.Endpoint))return "free";
  var preset=All.FirstOrDefault(p=>p.Endpoint!=""&&String.Equals(p.Endpoint.TrimEnd('/'),cfg.Endpoint.TrimEnd('/'),StringComparison.OrdinalIgnoreCase)&&p.Models.Any(m=>m.Model==cfg.Model));
  return preset==null?"custom":preset.Id;
 }
 public static void Migrate(Settings cfg){
  if(String.IsNullOrEmpty(cfg.ServiceId)||!All.Any(p=>p.Id==cfg.ServiceId))cfg.ServiceId=Infer(cfg);
  if(cfg.ServiceSecrets==null)cfg.ServiceSecrets=new Dictionary<string,string>();
  string scope=Scope(cfg.ServiceId,cfg.Endpoint);if(!String.IsNullOrEmpty(cfg.Secret)&&!cfg.ServiceSecrets.ContainsKey(scope))cfg.ServiceSecrets[scope]=cfg.Secret;
  if(String.IsNullOrEmpty(cfg.PresetId)){var model=Get(cfg.ServiceId).Models.FirstOrDefault(m=>m.Model==cfg.Model);cfg.PresetId=model==null?"fast":model.Id;}
 }
 public static string Scope(string id,string endpoint){return id=="custom"?"custom:"+(endpoint??"").Trim().TrimEnd('/'):id;}
 public static string SavedKey(Settings cfg,string id,string endpoint){string encrypted;if(cfg.ServiceSecrets==null||!cfg.ServiceSecrets.TryGetValue(Scope(id,endpoint),out encrypted))return "";return new Settings {Secret=encrypted}.Key;}
 public static void Apply(Settings cfg,string serviceId,string modelId,string key,string customEndpoint,string customModel){
  var service=Get(serviceId);string endpoint=service.Endpoint,model="";
  if(service.Id=="custom"){Uri uri;endpoint=(customEndpoint??"").Trim();model=(customModel??"").Trim();if(!Uri.TryCreate(endpoint,UriKind.Absolute,out uri)||uri.Scheme!="https"||model=="")throw new Exception("请填写 HTTPS 完整接口地址和模型名称");}
  else if(service.Models.Length>0){var preset=service.Models.FirstOrDefault(m=>m.Id==modelId);if(preset==null)throw new Exception("请先选择模型分类");model=preset.Model;}
  if(service.Id!="free"&&String.IsNullOrWhiteSpace(key))throw new Exception("请输入该服务的 API Key");
  cfg.ServiceId=service.Id;cfg.PresetId=modelId;cfg.Endpoint=endpoint;cfg.Model=model;cfg.SetKey(service.Id=="free"?"":key.Trim());
  if(cfg.ServiceSecrets==null)cfg.ServiceSecrets=new Dictionary<string,string>();
  if(service.Id!="free")cfg.ServiceSecrets[Scope(service.Id,endpoint)]=cfg.Secret;
 }
 public static Dictionary<string,object> Payload(Settings cfg,string instruction,string text){
  var payload=new Dictionary<string,object>{{"model",cfg.Model},{"messages",new[]{new {role="system",content=instruction},new {role="user",content=text}}}};
  string id=String.IsNullOrEmpty(cfg.ServiceId)?Infer(cfg):cfg.ServiceId;
  if(id=="custom"||id=="free")return payload;
  payload["response_format"]=new {type="json_object"};
  var model=Get(id).Models.FirstOrDefault(m=>m.Id==cfg.PresetId);bool thinking=model!=null&&model.Thinking;
  if(id=="deepseek"){payload["thinking"]=new {type=thinking?"enabled":"disabled"};if(thinking)payload["reasoning_effort"]="high";}
  if(id.StartsWith("qwen-"))payload["enable_thinking"]=false;
  // GPT-5 Mini does not accept arbitrary temperature values. Let each model use its supported default.
  if(id=="openai"&&cfg.Model=="gpt-5-mini")payload["reasoning_effort"]="low";
  return payload;
 }
}
}
