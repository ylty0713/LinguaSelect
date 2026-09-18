using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;
namespace LinguaSelect {
static class Startup {
 const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
 const string ValueName="LinguaSelect";
 static string Exe {get{return Assembly.GetExecutingAssembly().Location;}}
 internal static string Command(string exe){return "\""+Path.GetFullPath(exe)+"\" --startup";}
 internal static bool Matches(RegistryKey key,string exe){return key!=null&&String.Equals(key.GetValue(ValueName) as string,Command(exe),StringComparison.OrdinalIgnoreCase);}
 internal static void Write(RegistryKey key,string exe,bool enabled){if(enabled)key.SetValue(ValueName,Command(exe),RegistryValueKind.String);else key.DeleteValue(ValueName,false);}
 public static bool Enabled {get{using(var key=Registry.CurrentUser.OpenSubKey(RunKey))return Matches(key,Exe);}}
 public static void SetEnabled(bool enabled){using(var key=Registry.CurrentUser.CreateSubKey(RunKey)){Write(key,Exe,enabled);}if(Enabled!=enabled)throw new IOException("Windows 未能保存开机启动设置。");}
}
}
