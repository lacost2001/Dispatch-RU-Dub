using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace DispatchRu {
public class UpdateInfo {
    public int format {get;set;} public string version {get;set;} public string url {get;set;}
    public string sha256 {get;set;} public string exe_sha256 {get;set;} public string data_sha256 {get;set;}
    public long bytes {get;set;}
}
public static class Updates {
    const string Repository="https://github.com/lacost2001/Dispatch-RU-Dub/";
    public static string Settings=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DispatchRU");
    public static bool JustUpdated=false;
    public static string InitialFolder=null;
    static void Need(bool ok,string message){if(!ok)throw new InvalidDataException(message);}
    static void SafePath(string path) {
        var d=new DirectoryInfo(path);while(d!=null){if(d.Exists)Need((d.Attributes&FileAttributes.ReparsePoint)==0,"Папка обновления является ссылкой.");d=d.Parent;}
    }
    static HttpWebResponse Open(string url) {
        ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
        var request=(HttpWebRequest)WebRequest.Create(url);request.UserAgent="Dispatch-RU-Patcher";request.Timeout=20000;request.ReadWriteTimeout=30000;
        var response=(HttpWebResponse)request.GetResponse();Need(response.ResponseUri.Scheme=="https","Небезопасный адрес обновления.");return response;
    }
    public static bool CheckAndLaunch(string folder) {
        var current=Engine.ReadManifest();UpdateInfo update;
        using(var response=Open(Repository+"releases/latest/download/update.json"))using(var stream=response.GetResponseStream())using(var memory=new MemoryStream()) {
            byte[] buffer=new byte[4096];int n;while((n=stream.Read(buffer,0,buffer.Length))>0){Need(memory.Length+n<=65536,"Слишком большое описание обновления.");memory.Write(buffer,0,n);}
            update=new JavaScriptSerializer().Deserialize<UpdateInfo>(Encoding.UTF8.GetString(memory.ToArray()));
        }
        Need(update!=null && update.format==1 && Regex.IsMatch(update.version??"","^[0-9][a-zA-Z0-9.-]{0,49}$"),"Неверное описание обновления.");
        if(update.version==current.version)return false;
        Version oldVersion=null,newVersion=null;
        Need(Version.TryParse(current.version.Split('-')[0],out oldVersion)&&Version.TryParse(update.version.Split('-')[0],out newVersion),"Неверный номер версии.");
        if(newVersion<=oldVersion)return false;
        Need(update.url!=null && update.url.StartsWith(Repository+"releases/download/",StringComparison.Ordinal) && update.bytes>0 && update.bytes<1900000000,"Неверный адрес или размер обновления.");
        foreach(var hash in new[]{update.sha256,update.exe_sha256,update.data_sha256})Need(Regex.IsMatch(hash??"","^[a-f0-9]{64}$"),"Неверная контрольная сумма.");
        SafePath(Settings);Directory.CreateDirectory(Settings);
        string versions=Path.Combine(Settings,"versions");Directory.CreateDirectory(versions);
        string target=Path.Combine(versions,update.version);SafePath(target);
        string exe=Path.Combine(target,"Dispatch-RU-Patcher.exe"),data=Path.Combine(target,"Dispatch-RU.data");
        if(!(File.Exists(exe)&&File.Exists(data)&&Engine.Hash(exe)==update.exe_sha256&&Engine.Hash(data)==update.data_sha256)) {
            Need(!Directory.Exists(target),"Папка обновления повреждена. Удалите её и повторите запуск: "+target);
            string stage=Path.Combine(versions,"download-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
            string zip=Path.Combine(stage,"release.zip"),content=Path.Combine(stage,"content");Directory.CreateDirectory(content);
            try {
                Engine.Log("Загружается обновление "+update.version+"…");
                using(var response=Open(update.url))using(var input=response.GetResponseStream())using(var output=new FileStream(zip,FileMode.CreateNew,FileAccess.Write)) {
                    byte[] buffer=new byte[1024*1024];int n;while((n=input.Read(buffer,0,buffer.Length))>0){Need(output.Length+n<=update.bytes,"Размер обновления не совпадает.");output.Write(buffer,0,n);}Need(output.Length==update.bytes,"Обновление загружено не полностью.");
                }
                Need(Engine.Hash(zip)==update.sha256,"Проверка архива обновления не пройдена.");
                using(var archive=ZipFile.OpenRead(zip))foreach(var name in new[]{"Dispatch-RU-Patcher.exe","Dispatch-RU.data"}) {
                    var entry=archive.GetEntry("Dispatch-RU-Patcher/"+name);Need(entry!=null && entry.Length>0 && entry.Length<2000000000,"В обновлении отсутствует нужный файл.");
                    entry.ExtractToFile(Path.Combine(content,name));
                }
                Need(Engine.Hash(Path.Combine(content,"Dispatch-RU-Patcher.exe"))==update.exe_sha256&&Engine.Hash(Path.Combine(content,"Dispatch-RU.data"))==update.data_sha256,"Проверка файлов обновления не пройдена.");
                Directory.Move(content,target);
            } finally {
                if(File.Exists(zip))File.Delete(zip);
                foreach(var name in new[]{"Dispatch-RU-Patcher.exe","Dispatch-RU.data"}){string p=Path.Combine(content,name);if(File.Exists(p))File.Delete(p);}
                if(Directory.Exists(content))Directory.Delete(content);
                if(Directory.Exists(stage))Directory.Delete(stage);
            }
        }
        Process.Start(new ProcessStartInfo(exe,"--updated \""+folder.Replace("\"","")+"\""){UseShellExecute=true,WorkingDirectory=target});
        return true;
    }
}
}
