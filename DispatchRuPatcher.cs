using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DispatchRu {
public class Bank {
    public string name {get;set;} public string source_sha256 {get;set;} public string target_sha256 {get;set;}
    public string patch_sha256 {get;set;} public long source_bytes {get;set;} public long target_bytes {get;set;}
    public int samples {get;set;}
}
public class Manifest {
    public int format {get;set;} public string version {get;set;} public string game_build {get;set;}
    public int generated_lines {get;set;} public int included_unique_lines {get;set;} public int remaining_unique_lines {get;set;}
    public int fully_included_unique_lines {get;set;} public int partial_unique_lines {get;set;} public int bound_unique_lines {get;set;}
    public Bank[] banks {get;set;}
}
public class Journal {
    public string status {get;set;} public Manifest manifest {get;set;}
    public Manifest previous_manifest {get;set;}
}
public static class Engine {
    public const string BackupName=".dispatch-ru-backup-v01";
    public static string Payload=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Dispatch-RU.data");
    public static Action<string> Log=delegate(string s){};
    public static string Hash(string path) {using(var h=SHA256.Create()) using(var f=File.OpenRead(path)) return Hex(h.ComputeHash(f));}
    static string Hex(byte[] data) {return BitConverter.ToString(data).Replace("-","").ToLowerInvariant();}
    static void Need(bool valid,string message) {if(!valid)throw new InvalidOperationException(message);}
    public static Manifest ReadManifest() {
        using(var z=ZipFile.OpenRead(Payload)) {
            var e=z.GetEntry("manifest.json");Need(e!=null && e.Length<2000000,"Повреждён пакет данных.");
            using(var r=new StreamReader(e.Open(),Encoding.UTF8)) {
                var m=new JavaScriptSerializer().Deserialize<Manifest>(r.ReadToEnd());
                Need(m.format==1 && m.banks!=null && m.banks.Length>0,"Неподдерживаемый пакет.");
                var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var b in m.banks) Need(b.name==Path.GetFileName(b.name) && b.name.EndsWith(".bank") && names.Add(b.name) && b.source_bytes>0 && b.target_bytes>0,"Неверное имя банка.");
                return m;
            }
        }
    }
    static void NoLink(string p) {if(File.Exists(p)||Directory.Exists(p)) Need((File.GetAttributes(p)&FileAttributes.ReparsePoint)==0,"Папка или файл является ссылкой: "+Path.GetFileName(p));}
    public static string Resolve(string root) {
        root=Path.GetFullPath(root.Trim().Trim('"'));
        foreach(var p in new[]{Path.Combine(root,"Dispatch","Content","FMOD","Desktop"),Path.Combine(root,"Content","FMOD","Desktop"),root}) {
            if(File.Exists(Path.Combine(p,"Master.bank"))) {
                var d=new DirectoryInfo(p); while(d!=null){NoLink(d.FullName);d=d.Parent;}
                return p;
            }
        }
        throw new InvalidOperationException("Не найдена папка звуков Dispatch. Выберите папку игры из Steam.");
    }
    static void GameClosed() {
        foreach(var p in Process.GetProcesses()) {try {if(p.ProcessName.Equals("Dispatch",StringComparison.OrdinalIgnoreCase)||p.ProcessName.StartsWith("Dispatch-Win64",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Закройте Dispatch перед изменением файлов.");} finally{p.Dispose();}}
    }
    static void CopyExactly(Stream input,Stream output,long length) {
        Need(length>=0,"Неверный размер блока.");byte[] buffer=new byte[1024*1024];
        while(length>0){int n=input.Read(buffer,0,(int)Math.Min(buffer.Length,length));if(n==0)throw new EndOfStreamException("Неполный пакет.");output.Write(buffer,0,n);length-=n;}
    }
    public static void ApplyDelta(string source,Stream delta,string dest,Bank b) {
        using(var r=new BinaryReader(delta,Encoding.UTF8,true)) using(var original=File.OpenRead(source)) using(var output=new FileStream(dest,FileMode.CreateNew,FileAccess.Write)) {
            Need(Encoding.ASCII.GetString(r.ReadBytes(8))=="DRUP0001","Неверный формат патча.");
            Need(Hex(r.ReadBytes(32))==b.source_sha256 && Hex(r.ReadBytes(32))==b.target_sha256,"Несовпадение описания патча.");
            ulong total=r.ReadUInt64();uint ops=r.ReadUInt32();Need(total==(ulong)b.target_bytes && ops<1000000,"Неверный размер патча.");
            for(uint i=0;i<ops;i++) {
                byte op=r.ReadByte();ulong size=r.ReadUInt64();Need(size<=total && (ulong)output.Position<=total-size,"Переполнение патча.");
                if(op==0){ulong pos=r.ReadUInt64();Need(pos<=(ulong)original.Length && size<=(ulong)original.Length-pos,"Неверный исходный блок.");original.Position=(long)pos;CopyExactly(original,output,(long)size);}
                else if(op==1)CopyExactly(delta,output,(long)size);
                else throw new InvalidDataException("Неизвестная операция патча.");
            }
            Need(output.Length==b.target_bytes && delta.ReadByte()==-1,"Неверная длина патча.");output.Flush(true);
        }
        Need(Hash(dest)==b.target_sha256,"Проверка собранного банка не пройдена: "+b.name);
    }
    static void ReplaceCopy(string from,string to) {
        string tmp=to+".ru-restore-"+Guid.NewGuid().ToString("N");
        try{File.Copy(from,tmp,false);File.Replace(tmp,to,null);}finally{if(File.Exists(tmp))File.Delete(tmp);}
    }
    static void State(string backup,Manifest m,string status,Manifest previous=null) {
        string path=Path.Combine(backup,"transaction.json"),tmp=path+".tmp";
        File.WriteAllText(tmp,new JavaScriptSerializer().Serialize(new Journal {status=status,manifest=m,previous_manifest=previous}),new UTF8Encoding(false));
        if(File.Exists(path))File.Replace(tmp,path,null);else File.Move(tmp,path);
    }
    static Journal ReadState(string backup) {
        NoLink(backup);string path=Path.Combine(backup,"transaction.json");NoLink(path);
        if(!File.Exists(path))return null;
        Need(new FileInfo(path).Length<4000000,"Повреждён журнал установки.");
        var j=new JavaScriptSerializer().Deserialize<Journal>(File.ReadAllText(path));
        Need(j!=null && j.manifest!=null && j.manifest.banks!=null,"Повреждён журнал установки.");
        foreach(var b in j.manifest.banks.Concat(j.previous_manifest==null?new Bank[0]:j.previous_manifest.banks))
            Need(b.name==Path.GetFileName(b.name) && b.name.EndsWith(".bank"),"Неверное имя в журнале.");
        return j;
    }
    static bool Compatible(string hash,Bank bank,Journal journal) {
        if(hash==bank.source_sha256 || hash==bank.target_sha256)return true;
        if(journal==null)return false;
        var all=journal.manifest.banks.Concat(journal.previous_manifest==null?new Bank[0]:journal.previous_manifest.banks);
        return all.Any(b=>b.name==bank.name && b.source_sha256==bank.source_sha256 && b.target_sha256==hash);
    }
    static Mutex Acquire(string root) {
        string key;using(var h=SHA256.Create())key=Hex(h.ComputeHash(Encoding.UTF8.GetBytes(root.ToLowerInvariant()))).Substring(0,24);
        var mutex=new Mutex(false,"Local\\DispatchRu_"+key);
        bool got=false;try{got=mutex.WaitOne(0);}catch(AbandonedMutexException){got=true;}
        if(!got){mutex.Dispose();throw new InvalidOperationException("Другой патчер уже работает с этой игрой.");}return mutex;
    }
    public static void Run(string folder,string operation) {
        string root=Resolve(folder);var m=ReadManifest();
        using(var mutex=Acquire(root)) {try {
            if(operation=="restore"){Restore(root,m);return;}
            string backup=Path.Combine(root,BackupName);var journal=ReadState(backup);
            if(journal!=null)Need(new[]{"installed","restored","restored_after_error"}.Contains(journal.status),"Незавершённая операция. Сначала нажмите «Восстановить».");
            if(operation=="check") {foreach(var b in m.banks){NoLink(Path.Combine(root,b.name));string h=Hash(Path.Combine(root,b.name));Need(Compatible(h,b,journal),"Несовместимый или изменённый файл: "+b.name);}Log("Проверка совместимости пройдена.");return;}
            Need(operation=="install","Неизвестная операция.");GameClosed();
            bool already=true;
            var initial=new Dictionary<string,string>();var sources=new Dictionary<string,string>();
            foreach(var b in m.banks){string p=Path.Combine(root,b.name);NoLink(p);string h=Hash(p);initial[b.name]=h;if(h!=b.target_sha256)already=false;Need(Compatible(h,b,journal),"Другая версия игры или сторонний мод: "+b.name+". Установка отменена.");
                string source=h==b.source_sha256?p:Path.Combine(backup,b.name);NoLink(source);Need(File.Exists(source)&&Hash(source)==b.source_sha256,"Нет исправного оригинала: "+b.name);sources[b.name]=source;}
            if(already){Log("Эта озвучка уже установлена.");return;}
            if(journal!=null)foreach(var old in journal.manifest.banks)Need(m.banks.Any(b=>b.name==old.name),"Новый пакет не содержит старый банк. Сначала восстановите оригиналы.");
            NoLink(backup);
            if(Directory.Exists(backup))Need(File.Exists(Path.Combine(backup,"transaction.json")),"Неизвестная папка резервных копий. Установка остановлена.");
            long needed=m.banks.Sum(b=>b.source_bytes+b.target_bytes)+256L*1024*1024;
            Need(new DriveInfo(Path.GetPathRoot(root)).AvailableFreeSpace>needed,"Недостаточно места: нужно примерно "+Math.Ceiling(needed/1073741824.0)+" ГБ.");
            string stage=Path.Combine(root,".dispatch-ru-stage-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
            bool commit=false;var replaced=new List<Bank>();
            try {
                using(var z=ZipFile.OpenRead(Payload)) {
                    int n=0;foreach(var b in m.banks) {
                        Log("Подготовка "+(++n)+"/"+m.banks.Length+": "+b.name);
                        var e=z.GetEntry(b.name+".drup");Need(e!=null,"В пакете отсутствует "+b.name);
                        using(var h=SHA256.Create())using(var s=e.Open())Need(Hex(h.ComputeHash(s))==b.patch_sha256,"Повреждён пакет: "+b.name);
                        using(var s=e.Open())ApplyDelta(sources[b.name],s,Path.Combine(stage,b.name),b);
                    }
                }
                GameClosed();Directory.CreateDirectory(backup);State(backup,m,"preparing",journal==null?null:journal.manifest);
                foreach(var b in m.banks){string from=Path.Combine(root,b.name),dest=Path.Combine(backup,b.name);NoLink(dest);Need(Hash(from)==initial[b.name],"Игра была изменена во время подготовки.");if(!File.Exists(dest))File.Copy(sources[b.name],dest,false);Need(Hash(dest)==b.source_sha256,"Повреждена резервная копия: "+b.name);}
                State(backup,m,"committing",journal==null?null:journal.manifest);commit=true;
                foreach(var b in m.banks){Log("Установка: "+b.name);File.Replace(Path.Combine(stage,b.name),Path.Combine(root,b.name),null);replaced.Add(b);}
                foreach(var b in m.banks)Need(Hash(Path.Combine(root,b.name))==b.target_sha256,"Проверка установки не пройдена.");
                State(backup,m,"installed");Log("Готово. Озвучка установлена. Можно запускать Dispatch.");
            } catch {
                if(commit){Log("Ошибка установки. Восстанавливаю оригиналы…");foreach(var b in m.banks)ReplaceCopy(Path.Combine(backup,b.name),Path.Combine(root,b.name));State(backup,m,"restored_after_error");}
                throw;
            } finally {foreach(var b in m.banks){string p=Path.Combine(stage,b.name);if(File.Exists(p))File.Delete(p);}if(Directory.Exists(stage)&&!Directory.EnumerateFileSystemEntries(stage).Any())Directory.Delete(stage);}
        } finally{mutex.ReleaseMutex();}}
    }
    static void Restore(string root,Manifest m) {
        GameClosed();string backup=Path.Combine(root,BackupName);NoLink(backup);
        Need(File.Exists(Path.Combine(backup,"transaction.json")),"Резервная копия этой озвучки не найдена.");
        var journal=ReadState(backup);m=journal.manifest;
        foreach(var b in m.banks){string p=Path.Combine(root,b.name),saved=Path.Combine(backup,b.name);NoLink(p);NoLink(saved);string h=Hash(p);Need(Compatible(h,b,journal),"Файл изменён после установки: "+b.name+". Автоматическое восстановление остановлено.");if(h!=b.source_sha256)Need(File.Exists(saved)&&Hash(saved)==b.source_sha256,"Нет исправной резервной копии: "+b.name);}
        State(backup,m,"restoring",journal.previous_manifest);
        foreach(var b in m.banks){string p=Path.Combine(root,b.name);if(Hash(p)!=b.source_sha256){Log("Восстановление: "+b.name);ReplaceCopy(Path.Combine(backup,b.name),p);}Need(Hash(p)==b.source_sha256,"Ошибка восстановления.");}
        State(backup,m,"restored");Log("Оригинальные звуки восстановлены. Резервная копия сохранена.");
    }
}
public class Window:Form {
    TextBox path=new TextBox(),log=new TextBox();Button install=new Button(),restore=new Button(),browse=new Button(),check=new Button();bool busy;
    public Window() {
        Text="Dispatch — русская озвучка";ClientSize=new Size(720,490);MinimumSize=new Size(736,529);MaximumSize=MinimumSize;StartPosition=FormStartPosition.CenterScreen;Font=new Font("Segoe UI",10);
        var title=new Label{Text="Русская озвучка Dispatch · пробная версия",Location=new Point(20,18),Size=new Size(670,30),Font=new Font("Segoe UI",15,FontStyle.Bold)};Controls.Add(title);
        var note=new Label{Location=new Point(20,57),Size=new Size(670,64)};
        try{var m=Engine.ReadManifest();note.Text=m.bound_unique_lines>0?String.Format("Русская речь: {0:N0} полных реплик; {1:N0} — частично. Обновления при запуске.\nSteam-сборка {2}. Перед установкой закройте игру.",m.fully_included_unique_lines,m.partial_unique_lines,m.game_build):String.Format("Включено {0:N0} из {1:N0} созданных реплик. Часть сцен пока звучит в оригинале.\nДля Steam-сборки {2}. Перед установкой закройте игру.",m.included_unique_lines,m.generated_lines,m.game_build);}catch(Exception ex){note.Text=ex.Message;}
        Controls.Add(note);path.SetBounds(20,133,550,29);path.Text=@"C:\Program Files (x86)\Steam\steamapps\common\Dispatch";
        try{string setting=Path.Combine(Updates.Settings,"game-folder.txt");if(File.Exists(setting))path.Text=File.ReadAllText(setting);}catch{}
        if(Updates.InitialFolder!=null)path.Text=Updates.InitialFolder;Controls.Add(path);
        browse.Text="Выбрать…";browse.SetBounds(580,131,120,32);browse.Click+=delegate{using(var d=new FolderBrowserDialog{Description="Выберите папку Dispatch"})if(d.ShowDialog()==DialogResult.OK)path.Text=d.SelectedPath;};Controls.Add(browse);
        install.Text="Установить";install.SetBounds(20,182,160,38);install.Click+=delegate{Start("install");};Controls.Add(install);
        restore.Text="Восстановить";restore.SetBounds(190,182,170,38);restore.Click+=delegate{Start("restore");};Controls.Add(restore);
        check.Text="Проверить игру";check.SetBounds(370,182,175,38);check.Click+=delegate{Start("check");};Controls.Add(check);
        log.Multiline=true;log.ReadOnly=true;log.ScrollBars=ScrollBars.Vertical;log.SetBounds(20,238,680,220);Controls.Add(log);
        Engine.Log=s=>{if(IsHandleCreated)BeginInvoke((Action)(()=>log.AppendText(s+Environment.NewLine)));};
        FormClosing+=delegate(object s,FormClosingEventArgs e){if(busy){e.Cancel=true;MessageBox.Show("Дождитесь завершения операции.");}};
        Shown+=async delegate {
            if(!Updates.JustUpdated) {
                busy=true;install.Enabled=restore.Enabled=check.Enabled=browse.Enabled=path.Enabled=false;
                try{string folder=path.Text;if(await Task.Run(()=>Updates.CheckAndLaunch(folder))){busy=false;Close();return;}}
                catch(Exception ex){Engine.Log("Обновление недоступно: "+ex.Message+" Можно использовать текущую версию.");}
                finally{busy=false;install.Enabled=restore.Enabled=check.Enabled=browse.Enabled=path.Enabled=true;}
            }
            try {
                string root=Engine.Resolve(path.Text),journalPath=Path.Combine(root,Engine.BackupName,"transaction.json");
                if(File.Exists(journalPath)) {
                    var journal=new JavaScriptSerializer().Deserialize<Journal>(File.ReadAllText(journalPath));
                    if(journal.status=="installed" && journal.manifest.version!=Engine.ReadManifest().version)Start("install");
                }
            }catch(Exception ex){Engine.Log("Автоустановка отложена: "+ex.Message);}
        };
    }
    async void Start(string op) {
        string folder=path.Text;busy=true;install.Enabled=restore.Enabled=check.Enabled=browse.Enabled=path.Enabled=false;
        try {Directory.CreateDirectory(Updates.Settings);File.WriteAllText(Path.Combine(Updates.Settings,"game-folder.txt"),folder);await Task.Run(()=>Engine.Run(folder,op));}
        catch(UnauthorizedAccessException){Engine.Log("Нет доступа к папке игры. Запустите патчер от имени администратора.");}
        catch(Exception ex){Engine.Log("Остановлено: "+ex.Message);}
        finally{busy=false;install.Enabled=restore.Enabled=check.Enabled=browse.Enabled=path.Enabled=true;}
    }
}
public static class Program {
    [STAThread] public static int Main(string[] args) {
        if(args.Length==2 && args[0]=="--updated"){Updates.JustUpdated=true;Updates.InitialFolder=args[1];args=new string[0];}
        if(args.Length>=2) {
            string logfile=args.Length>=3?args[2]:Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"patcher.log");
            Engine.Log=s=>File.AppendAllText(logfile,s+Environment.NewLine,Encoding.UTF8);
            try{Engine.Run(args[1],args[0].TrimStart('-'));return 0;}catch(Exception ex){Engine.Log("ERROR: "+ex.Message);return 1;}
        }
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new Window());return 0;
    }
}
}
