using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using System.Xml.Linq;
using Forms=System.Windows.Forms;

namespace LinguaSelect {
class GlassWindow : Window {
 readonly Settings cfg=Settings.Load(); readonly bool testing;
 Border shell; IntPtr handle,hook; HwndSource source; Native.HookProc callback;
 Forms.NotifyIcon tray; SpeechSynthesizer speech; CancellationTokenSource lookup;
 DispatcherTimer debounce; bool reading,quitting,pinned,preferences,initialized;
 int captureVersion,queryVersion,dwmResult=-1,borderResult=-1; System.Drawing.Point selectionPoint; IntPtr selectionWindow;
 string draftScope="";readonly Dictionary<string,string> draftKeys=new Dictionary<string,string>();
 string currentText=""; Answer answer; Button pinButton,settingsButton;
 readonly Dictionary<string,Answer> cache=new Dictionary<string,Answer>();
 readonly List<Pen> iconPens=new List<Pen>();readonly List<Button> themeButtons=new List<Button>();
 static readonly XDocument vectors=LoadVectors();
 [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd,int attr,ref int value,int size);
 [StructLayout(LayoutKind.Sequential)] struct Margins {public int Left,Right,Top,Bottom;}
 [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd,ref Margins margins);
 static System.Drawing.Icon LoadTrayIcon(){using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("LinguaSelect.app.ico"))using(var icon=new System.Drawing.Icon(stream)){return (System.Drawing.Icon)icon.Clone();}}
 static XDocument LoadVectors(){using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("LinguaSelect.icons.svg"))return XDocument.Load(stream);}
 T Find<T>(string name) where T:FrameworkElement { return (T)shell.FindName(name); }
 void Set(string name,string text){Find<TextBlock>(name).Text=text??"";}
 void Visible(string name,bool show){Find<FrameworkElement>(name).Visibility=show?Visibility.Visible:Visibility.Collapsed;}
 static Brush ColorBrush(string hex){return (Brush)new BrushConverter().ConvertFromString(hex);}
 public GlassWindow(bool test=false) {
  testing=test;ServicePresets.Migrate(cfg);cfg.ThemeId=Themes.Get(cfg.ThemeId).Id;cfg.Instant=true;cfg.Target=Math.Max(0,Math.Min(6,cfg.Target));
  Title="Lingua · 玻璃划词卡片";Width=400;SizeToContent=SizeToContent.Height;MaxHeight=SystemParameters.WorkArea.Height-30;
  WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;ShowActivated=false;Topmost=true;
  Background=Brushes.Transparent;UseLayoutRounding=true;SnapsToDevicePixels=true;FontFamily=new FontFamily("Segoe UI, Microsoft YaHei UI");FontSize=13;WindowStartupLocation=WindowStartupLocation.CenterScreen;
  // Windows 11 exposes Desktop Acrylic via DWM. Older Windows uses a translucent layered window.
  bool acrylic=Environment.OSVersion.Version.Build>=22621;
  if(!acrylic)AllowsTransparency=true;
  using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("LinguaSelect.Card.xaml"))shell=(Border)XamlReader.Load(stream);
  if(acrylic)shell.CornerRadius=new CornerRadius(8); // Match DWM's rounded corners; avoid exposing a second acrylic outline.
  Content=shell;TextOptions.SetTextFormattingMode(this,TextFormattingMode.Display);
  using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("LinguaSelect.logo.png")){var logo=new BitmapImage();logo.BeginInit();logo.CacheOption=BitmapCacheOption.OnLoad;logo.StreamSource=stream;logo.EndInit();logo.Freeze();Find<Image>("BrandLogo").Source=logo;Icon=logo;}
  pinButton=IconButton("pin","固定卡片",()=>{pinned=!pinned;pinButton.Background=pinned?(Brush)shell.Resources["AccentSoftBrush"]:Brushes.Transparent;pinButton.ToolTip=pinned?"取消固定":"固定卡片";});
  settingsButton=IconButton("sliders","自定义卡片",()=>TogglePreferences());
  var tools=Find<StackPanel>("Tools");tools.Children.Add(pinButton);tools.Children.Add(settingsButton);tools.Children.Add(IconButton("close","收起卡片",Dismiss));
  var wordTools=Find<StackPanel>("WordTools");wordTools.Children.Add(IconButton("sound","朗读原文 / 停止",Speak));wordTools.Children.Add(IconButton("copy","复制译文",()=>{try{if(answer!=null)Clipboard.SetText(answer.Translation);}catch{Set("Status","剪贴板正忙，请稍后重试");}}));
  Find<Grid>("Header").MouseLeftButtonDown+=(s,e)=>{if(e.OriginalSource is TextBlock || e.OriginalSource==s)try{DragMove();}catch{}};
  Find<TextBlock>("Original").MouseLeftButtonDown+=(s,e)=>{if(e.ClickCount==2)OpenEditor();};
  var menu=new ContextMenu();AddMenu(menu,"输入 / 粘贴文本",OpenEditor);AddMenu(menu,"重新查询",()=>StartLookup(currentText));AddMenu(menu,"复制原文",()=>{try{Clipboard.SetText(currentText);}catch{}});AddMenu(menu,"退出",Quit);ContextMenu=menu;
  Find<Button>("Submit").Click+=(s,e)=>{string text=Find<TextBox>("Input").Text.Trim();Visible("Editor",false);StartLookup(text);};
  Find<TextBox>("Input").PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Enter&&(Keyboard.Modifiers&ModifierKeys.Control)!=0){e.Handled=true;Visible("Editor",false);StartLookup(Find<TextBox>("Input").Text);}};
  SetupPreferences();
  Find<StackPanel>("Tools").Children.Insert(0,IconButton("edit","输入文本",OpenEditor));
  PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape){Dismiss();e.Handled=true;}};
  SourceInitialized+=(s,e)=>{
   handle=new WindowInteropHelper(this).Handle;source=HwndSource.FromHwnd(handle);source.CompositionTarget.BackgroundColor=Colors.Transparent;source.AddHook(WindowMessage);
   if(acrylic){var margins=new Margins {Left=-1,Right=-1,Top=-1,Bottom=-1};DwmExtendFrameIntoClientArea(handle,ref margins);int backdrop=3,round=2;dwmResult=DwmSetWindowAttribute(handle,38,ref backdrop,4);DwmSetWindowAttribute(handle,33,ref round,4);SuppressSystemBorder();if(dwmResult!=0){shell.Background=ColorBrush("#F0F3F8");Set("Status","系统玻璃效果不可用，已使用浅色卡片");}}
   if(testing)return;
   callback=MouseHook;hook=Native.SetWindowsHookEx(14,callback,Native.GetModuleHandle(null),0);
   if(!Native.RegisterHotKey(handle,1,0x4003,0x54)||hook==IntPtr.Zero)Set("Status","划词或快捷键注册失败，可右键手动输入");
  };
  debounce=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(180)};debounce.Tick+=async(s,e)=>{debounce.Stop();await Capture(false);};
  if(!testing){tray=new Forms.NotifyIcon {Icon=LoadTrayIcon(),Text="Lingua · 划词即译",Visible=true};var trayMenu=new Forms.ContextMenuStrip();trayMenu.Items.Add("打开卡片",null,(s,e)=>Dispatcher.Invoke((Action)(()=>{Show();Activate();})));trayMenu.Items.Add("自定义卡片",null,(s,e)=>Dispatcher.Invoke((Action)(()=>{Show();Activate();TogglePreferences(true);})));trayMenu.Items.Add("暂停 / 恢复划词",null,(s,e)=>Dispatcher.Invoke((Action)(()=>{cfg.Automatic=!cfg.Automatic;Find<CheckBox>("AutomaticOption").IsChecked=cfg.Automatic;})));trayMenu.Items.Add("退出",null,(s,e)=>Dispatcher.Invoke((Action)Quit));tray.ContextMenuStrip=trayMenu;tray.DoubleClick+=(s,e)=>Dispatcher.Invoke((Action)(()=>{Show();Activate();}));}
  Closing+=(s,e)=>{if(!quitting&&!testing){e.Cancel=true;Dismiss();}};
  Closed+=(s,e)=>{if(lookup!=null){lookup.Cancel();lookup.Dispose();}debounce.Stop();if(hook!=IntPtr.Zero)Native.UnhookWindowsHookEx(hook);Native.UnregisterHotKey(handle,1);if(tray!=null)tray.Dispose();if(speech!=null)speech.Dispose();};
  initialized=true;ApplyAppearance();Render();
 }
 void AddMenu(ContextMenu menu,string text,Action action){var item=new MenuItem {Header=text};item.Click+=(s,e)=>action();menu.Items.Add(item);}
 Button IconButton(string id,string tooltip,Action action){
  var symbol=vectors.Descendants().First(x=>x.Name.LocalName=="symbol"&&(string)x.Attribute("id")==id);
  var group=new DrawingGroup();foreach(var p in symbol.Elements()){var pen=new Pen(ColorBrush("#737D8E"),1.65){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round};iconPens.Add(pen);group.Children.Add(new GeometryDrawing(null,pen,Geometry.Parse((string)p.Attribute("d"))));}
  var image=new Image {Source=new DrawingImage(group),Width=15,Height=15,Stretch=Stretch.Uniform};
  var b=new Button {Content=image,Width=29,Height=29,Padding=new Thickness(7),ToolTip=tooltip,Margin=new Thickness(1,0,0,0)};
  System.Windows.Automation.AutomationProperties.SetName(b,tooltip);b.Click+=(s,e)=>action();return b;
 }
 void SetupPreferences(){
  SetupThemeGallery();
  string[] fields={"ShowOriginal","ShowPhonetic","ShowTranslation","ShowDetails","ShowExamples","ShowStructure"};
  string[] names={"原文与朗读","音标","译文","多种释义","双语例句","句式与搭配"};
  for(int i=0;i<fields.Length;i++){var field=typeof(Settings).GetField(fields[i]);var c=new CheckBox {Content=names[i],IsChecked=(bool)field.GetValue(cfg)};c.Click+=(s,e)=>{field.SetValue(cfg,c.IsChecked==true);Save();Render();};Find<StackPanel>("ModuleOptions").Children.Add(c);}
  var compact=Find<CheckBox>("CompactOption");compact.IsChecked=cfg.Compact;compact.Click+=(s,e)=>{cfg.Compact=compact.IsChecked==true;ApplyAppearance();Save();};
  var tint=Find<Slider>("Tint");tint.Value=Math.Max(70,Math.Min(240,cfg.GlassTint));tint.ValueChanged+=(s,e)=>{cfg.GlassTint=(int)tint.Value;ApplyAppearance();};tint.PreviewMouseLeftButtonUp+=(s,e)=>Save();tint.PreviewKeyUp+=(s,e)=>Save();
  var automatic=Find<CheckBox>("AutomaticOption");automatic.IsChecked=cfg.Automatic;automatic.Checked+=(s,e)=>{cfg.Automatic=true;Save();};automatic.Unchecked+=(s,e)=>{cfg.Automatic=false;captureVersion++;debounce.Stop();Save();};
  var languages=Find<ComboBox>("Language");languages.ItemsSource=Provider.Languages;languages.SelectedIndex=cfg.Target;languages.SelectionChanged+=(s,e)=>{cfg.Target=languages.SelectedIndex;Save();cache.Clear();if(currentText!="")StartLookup(currentText);else Render();};
  var voices=Find<ComboBox>("Voice");try{speech=new SpeechSynthesizer();var all=speech.GetInstalledVoices().Where(v=>v.Enabled).ToList();voices.ItemsSource=all.Select(v=>v.VoiceInfo.Name).ToList();int idx=all.FindIndex(v=>v.VoiceInfo.Culture.Name=="en-US");if(idx<0)idx=all.FindIndex(v=>v.VoiceInfo.Culture.TwoLetterISOLanguageName=="en");if(all.Count>0)voices.SelectedIndex=Math.Max(0,idx);}catch{}
  SetupServicePresets();
 }
 void SetupServicePresets(){
  Find<TextBox>("Endpoint").Text=cfg.ServiceId=="custom"?cfg.Endpoint:"";Find<TextBox>("Model").Text=cfg.ServiceId=="custom"?cfg.Model:"";Find<TextBox>("Excluded").Text=cfg.Excluded;
  var service=Find<ComboBox>("Service");service.ItemsSource=ServicePresets.All;service.SelectedItem=ServicePresets.Get(cfg.ServiceId);
  service.SelectionChanged+=(s,e)=>RefreshServiceEditor();RefreshServiceEditor();
  Find<TextBox>("Endpoint").TextChanged+=(s,e)=>{if(((ServicePreset)service.SelectedItem).Id=="custom")RefreshDraftKey();};
  Find<Button>("SaveExcluded").Click+=(s,e)=>{cfg.Excluded=Find<TextBox>("Excluded").Text;Save();Set("Status","排除应用已保存");};
  Find<Button>("SaveService").Click+=(s,e)=>{try{var next=PrepareService();if(!testing)next.Save();cfg.ServiceId=next.ServiceId;cfg.PresetId=next.PresetId;cfg.Endpoint=next.Endpoint;cfg.Model=next.Model;cfg.Secret=next.Secret;cfg.ServiceSecrets=next.ServiceSecrets;
    cache.Clear();if(lookup!=null)lookup.Cancel();queryVersion++;Set("ServiceStatus","已启用 "+ServicePresets.Get(cfg.ServiceId).Name+(cfg.Model==""?"":" · "+cfg.Model));Set("Status","设置已保存 · 下一次划词生效");
   }catch(Exception ex){Set("ServiceStatus",ex.Message);}};
  Find<Button>("TestService").Click+=async(s,e)=>{var button=Find<Button>("TestService");button.IsEnabled=false;Set("ServiceStatus","正在验证服务与模型…");try{var test=PrepareService();var result=await Provider.Lookup("hello",test,CancellationToken.None);Set("ServiceStatus","连接成功 · "+result.Translation.Replace("\n"," "));}catch(Exception ex){Set("ServiceStatus",ex is OperationCanceledException?"连接超时，请检查网络或更换模型分类。":ex.Message);}finally{button.IsEnabled=true;}};
 }
 void RefreshServiceEditor(){
  var service=Find<ComboBox>("Service").SelectedItem as ServicePreset;if(service==null)return;
  var models=Find<ComboBox>("ModelPreset");models.ItemsSource=service.Models;models.SelectedItem=service.Models.FirstOrDefault(m=>service.Id==cfg.ServiceId&&m.Id==cfg.PresetId)??service.Models.FirstOrDefault();
  Visible("PresetSection",service.Models.Length>0);Visible("CustomSection",service.Id=="custom");Visible("KeySection",service.Id!="free");
  Set("ServiceHint",service.Id=="free"?"无需 Key，提供基础译文与词典释义。":service.Id.StartsWith("qwen-")?"填入对应地域的百炼 API Key，地址与参数已预设。":service.Id=="custom"?"自定义 HTTPS 兼容接口，Key 单独保存。":"地址与参数已预设，只需填写该服务的 API Key。");
  Set("ServiceStatus","");RefreshDraftKey();Fit();
 }
 void RefreshDraftKey(){
  if(draftScope!="")draftKeys[draftScope]=Find<PasswordBox>("ApiKey").Password;
  var service=(ServicePreset)Find<ComboBox>("Service").SelectedItem;string endpoint=service.Id=="custom"?Find<TextBox>("Endpoint").Text:service.Endpoint;
  draftScope=ServicePresets.Scope(service.Id,endpoint);string saved;Find<PasswordBox>("ApiKey").Password=draftKeys.TryGetValue(draftScope,out saved)?saved:ServicePresets.SavedKey(cfg,service.Id,endpoint);
 }
 Settings PrepareService(){
  var serializer=new System.Web.Script.Serialization.JavaScriptSerializer();var next=serializer.Deserialize<Settings>(serializer.Serialize(cfg));
  var service=(ServicePreset)Find<ComboBox>("Service").SelectedItem;var model=Find<ComboBox>("ModelPreset").SelectedItem as ModelPreset;
  ServicePresets.Apply(next,service.Id,model==null?"":model.Id,Find<PasswordBox>("ApiKey").Password,Find<TextBox>("Endpoint").Text,Find<TextBox>("Model").Text);return next;
 }
 void SuppressSystemBorder(){if(handle!=IntPtr.Zero&&Environment.OSVersion.Version.Build>=22000){int none=unchecked((int)0xFFFFFFFE);borderResult=DwmSetWindowAttribute(handle,34,ref none,4);}}
 void Save(){if(testing)return;try{cfg.Save();}catch{Set("Status","无法保存设置，请检查本地目录权限");}}
 void ApplyAppearance(){
  var theme=Themes.Get(cfg.ThemeId);byte alpha=(byte)Math.Max(70,Math.Min(240,cfg.GlassTint));if(theme.Id!="ios")alpha=(byte)(215+(alpha-70)*0.2);
  var top=(Color)ColorConverter.ConvertFromString(theme.Top);var bottom=(Color)ColorConverter.ConvertFromString(theme.Bottom);top.A=(byte)Math.Min(250,alpha+30);bottom.A=alpha;shell.Background=new LinearGradientBrush(top,bottom,90);
  shell.Resources["Ink"]=Themes.Brush(theme.Ink);shell.Resources["Muted"]=Themes.Brush(theme.Muted);shell.Resources["AccentBrush"]=Themes.Brush(theme.Accent);shell.Resources["AccentSoftBrush"]=Themes.Brush(theme.Soft);shell.Resources["PanelBrush"]=Themes.Brush(theme.Panel);shell.Resources["LineBrush"]=Themes.Brush(theme.Line);
  Foreground=Themes.Brush(theme.Ink);FontFamily=new FontFamily(theme.Font);foreach(var pen in iconPens){pen.Brush=Themes.Brush(theme.Muted);pen.Thickness=theme.Id=="doodle"?2.1:1.65;}
  var example=Find<Border>("ExampleSurface");example.CornerRadius=new CornerRadius(theme.Id=="google"?18:theme.Id=="doodle"?5:12);example.BorderThickness=new Thickness(theme.Id=="doodle"?1.7:1);
  Find<Image>("ThemeArt").Source=Themes.Illustration(theme.Id);Set("ThemeName",theme.Name);Set("ThemeSubtitle",theme.Subtitle);Visible("ThemeBanner",theme.Id!="ios");
  Find<TextBlock>("Original").FontWeight=theme.Id=="google"?FontWeights.Medium:FontWeights.SemiBold;
  if(pinButton!=null)pinButton.Background=pinned?Themes.Brush(theme.Soft):Brushes.Transparent;if(settingsButton!=null)settingsButton.Background=preferences?Themes.Brush(theme.Soft):Brushes.Transparent;
  foreach(var b in themeButtons){var t=(CardTheme)b.Tag;b.Background=Themes.Brush(t.Id==theme.Id?t.Soft:t.Panel);var text=((StackPanel)((Grid)b.Content).Children[1]).Children[0] as TextBlock;text.Text=t.Name+(t.Id==theme.Id?" ✓":"");}
  shell.Padding=cfg.Compact?new Thickness(20,14,20,13):new Thickness(24,20,24,20);Width=cfg.Compact?400:440;
  Find<TextBlock>("Details").FontSize=cfg.Compact?12:14;Find<TextBlock>("Examples").FontSize=cfg.Compact?12:14;
  if(initialized)Fit();
 }
 void TogglePreferences(bool? value=null){preferences=value??!preferences;Visible("Preferences",preferences);Visible("Reading",!preferences);settingsButton.Background=preferences?(Brush)shell.Resources["AccentSoftBrush"]:Brushes.Transparent;Set("Mode",preferences?" /  个性化":" /  划词即译");Fit();}
 void SetupThemeGallery(){
  foreach(var theme in Themes.All){var t=theme;var grid=new Grid();grid.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(58)});grid.ColumnDefinitions.Add(new ColumnDefinition());grid.Children.Add(new Image {Source=Themes.Illustration(t.Id),Width=57,Height=42});
   var labels=new StackPanel {VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(5,0,0,0)};labels.Children.Add(new TextBlock {Text=t.Name,FontSize=10,FontWeight=FontWeights.SemiBold,Foreground=Themes.Brush(t.Ink),TextWrapping=TextWrapping.Wrap});labels.Children.Add(new TextBlock {Text=t.Id=="ios"?"默认主题":"整套配色与装饰",FontSize=9,Foreground=Themes.Brush(t.Muted),Margin=new Thickness(0,4,0,0)});Grid.SetColumn(labels,1);grid.Children.Add(labels);
   var button=new Button {Content=grid,Tag=t,Width=156,Height=64,Margin=new Thickness(0,0,8,8),Padding=new Thickness(6),ToolTip=t.Name+" · "+t.Subtitle};button.Click+=(s,e)=>{cfg.ThemeId=t.Id;ApplyAppearance();Save();};themeButtons.Add(button);Find<WrapPanel>("ThemeGallery").Children.Add(button);
  }
 }
 void OpenEditor(){TogglePreferences(false);Visible("Editor",true);Find<TextBox>("Input").Text=currentText;Show();Activate();Find<TextBox>("Input").Focus();Fit();}
 void Render(){
  Set("Original",currentText==""?"划词，即刻理解。":currentText);Set("LanguageLabel",(Provider.SourceLanguage(currentText)=="en"?"EN":"中文")+" → "+Provider.Languages[cfg.Target]);
  bool welcome=answer==null&&currentText=="";
  Set("Translation",welcome?"选中一段文字，翻译会自动浮现。":answer==null?"正在翻译…":answer.Translation);
  Set("Phonetic",answer==null?"":answer.Phonetic);
  string details=answer==null?"":answer.Details;
  details=System.Text.RegularExpressions.Regex.Replace(details,@"(?m)^音标[^\r\n]*\r?\n","").Trim();
  var learning=details.Split(new[]{"词条来源：","许可："},StringSplitOptions.None)[0].Trim();
  Set("Details",learning);Set("Examples",answer==null?"":answer.Examples);Set("Structure",answer==null?"":answer.Structure);
  // Full source and license attribution remains available without occupying the compact card.
  Find<TextBlock>("Status").ToolTip=answer==null?"":answer.Source+"\n"+details;
  Visible("OriginalSection",cfg.ShowOriginal||welcome);Visible("Phonetic",cfg.ShowPhonetic&&answer!=null&&answer.Phonetic!="");
  Visible("TranslationSection",cfg.ShowTranslation||welcome);Visible("DetailsSection",cfg.ShowDetails&&answer!=null&&learning!="");
  Visible("ExamplesSection",cfg.ShowExamples&&answer!=null&&answer.Examples!="");Visible("StructureSection",cfg.ShowStructure&&answer!=null&&answer.Structure!="");
  bool noModules=!cfg.ShowOriginal&&!cfg.ShowPhonetic&&!cfg.ShowTranslation&&!cfg.ShowDetails&&!cfg.ShowExamples&&!cfg.ShowStructure;
  Set("Notice",welcome?"右上角自定义内容 · 右键手动输入":noModules?"展示项已关闭 · 点击右上角自定义":"");Visible("Notice",welcome||noModules);
  Fit();
 }
 void Fit(){if(shell==null)return;Find<ScrollViewer>("Viewport").MaxHeight=Math.Max(180,Math.Min(preferences?600:540,MaxHeight-100));UpdateLayout();if(IsVisible){var work=Forms.Screen.FromHandle(handle).WorkingArea;var transform=source==null?Matrix.Identity:source.CompositionTarget.TransformFromDevice;var bottom=transform.Transform(new Point(work.Right,work.Bottom));Top=Math.Min(Top,Math.Max(transform.Transform(new Point(work.Left,work.Top)).Y,bottom.Y-ActualHeight-8));}}
 void ShowAt(System.Drawing.Point point){
  var work=Forms.Screen.FromPoint(point).WorkingArea;var matrix=source.CompositionTarget.TransformFromDevice;var p=matrix.Transform(new Point(point.X,point.Y));var tl=matrix.Transform(new Point(work.Left,work.Top));var br=matrix.Transform(new Point(work.Right,work.Bottom));MaxHeight=Math.Max(240,br.Y-tl.Y-24);
  Left=Math.Max(tl.X+8,Math.Min(p.X+16,br.X-Width-8));Top=Math.Max(tl.Y+8,Math.Min(p.Y+20,br.Y-Math.Max(ActualHeight,160)-8));
  if(!IsVisible){Show();shell.BeginAnimation(OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(140)));}Fit();
 }
 void Dismiss(){captureVersion++;queryVersion++;if(lookup!=null)lookup.Cancel();if(speech!=null)speech.SpeakAsyncCancelAll();Hide();}
 void Quit(){quitting=true;Close();}
 bool ContainsScreenPoint(System.Drawing.Point p){if(!IsVisible)return false;var tl=PointToScreen(new Point());var br=PointToScreen(new Point(ActualWidth,ActualHeight));return p.X>=tl.X&&p.X<=br.X&&p.Y>=tl.Y&&p.Y<=br.Y;}
 IntPtr MouseHook(int code,IntPtr w,IntPtr l){
  if(code>=0&&w.ToInt32()==0x202){var data=(Native.MouseData)Marshal.PtrToStructure(l,typeof(Native.MouseData));var window=Native.GetForegroundWindow();Dispatcher.BeginInvoke((Action)(()=>{
   if(ContainsScreenPoint(data.Point))return;
   if(!pinned&&!preferences&&IsVisible)Dismiss();
   if(!cfg.Automatic||preferences)return;selectionWindow=window;selectionPoint=data.Point;captureVersion++;debounce.Stop();debounce.Start();
  }),DispatcherPriority.Background);}
  return Native.CallNextHookEx(hook,code,w,l);
 }
 IntPtr WindowMessage(IntPtr hwnd,int msg,IntPtr w,IntPtr l,ref bool handled){if(msg==0x312&&w.ToInt32()==1){selectionWindow=Native.GetForegroundWindow();selectionPoint=Forms.Cursor.Position;captureVersion++;var task=Capture(true);handled=true;}if(msg==0x86||msg==0x31A||msg==0x31E)Dispatcher.BeginInvoke((Action)SuppressSystemBorder);return IntPtr.Zero;}
 async Task Capture(bool direct){
  var point=selectionPoint;var window=selectionWindow;int version=captureVersion;if(!Native.Allowed(window,cfg))return;
  if(reading)return;reading=true;var task=Task.Run(()=>Native.Selection(point,window));
  if(await Task.WhenAny(task,Task.Delay(1500))!=task){Set("Status","读取选区超时，可右键手动输入");var recovery=task.ContinueWith(t=>{if(!Dispatcher.HasShutdownStarted)Dispatcher.BeginInvoke((Action)(()=>reading=false));});return;}
  reading=false;string text=await task;if(version!=captureVersion||window!=Native.GetForegroundWindow()||(!direct&&!cfg.Automatic))return;
  if(text.Length==0||text.Length>2000){if(direct){OpenEditor();Set("Status",text.Length==0?"未读到选区，可粘贴查询":"请选取 2000 字符以内文本");}return;}
  TogglePreferences(false);Visible("Editor",false);ShowAt(point);StartLookup(text);
 }
 async void StartLookup(string text){
  text=(text??"").Trim();if(text==""||text.Length>2000){Set("Status","请输入 1–2000 字符");return;}
  if(lookup!=null){lookup.Cancel();lookup.Dispose();}lookup=new CancellationTokenSource();int version=++queryVersion;currentText=text;answer=null;Set("Status","正在翻译");Render();
  string key=cfg.Target+"|"+text;Answer saved;if(cache.TryGetValue(key,out saved)){answer=saved;Set("Status","已翻译 · 当前会话缓存");Render();return;}
  try{var result=await Provider.Lookup(text,cfg,lookup.Token,partial=>{if(version!=queryVersion)return;answer=partial;Set("Status","译文已就绪 · 正在补充释义");Render();});if(version!=queryVersion)return;
   answer=result;if(cache.Count>=20)cache.Clear();cache[key]=result;Set("Status",cfg.Endpoint==""?"基础翻译 · 词典":"AI 翻译 · 请结合语境核对");Render();
  }catch(OperationCanceledException){if(version==queryVersion){Set("Status","查询超时 · 右键重试");if(answer==null)Set("Translation","暂时没有收到翻译");}}
  catch(Exception ex){if(version==queryVersion){Set("Status","查询失败 · 右键重试");Set("Translation",ex.Message);Visible("TranslationSection",true);Fit();}}
 }
 void Speak(){try{if(speech==null||Find<ComboBox>("Voice").SelectedItem==null){Set("Status","请先安装 Windows 系统语音包");return;}if(speech.State==SynthesizerState.Speaking){speech.SpeakAsyncCancelAll();return;}speech.SelectVoice(Find<ComboBox>("Voice").SelectedItem.ToString());speech.SpeakAsync(currentText);}catch{Set("Status","当前声音不可用，请在设置中更换");}}
 public static void Preview(){
  var app=new Application();var card=new GlassWindow(true);card.currentText="serendipity";card.answer=new Answer {Phonetic="/ˌser.ənˈdɪp.ə.ti/",Translation="不期而遇的美好；意外的幸运",Details="n. 偶然发现美好事物的机缘\n常用于意外的相遇、发现或收获。",Examples="Finding this little café was pure serendipity.\n偶然发现这家小咖啡馆，真是意外之喜。",Structure="by serendipity  ·  机缘巧合之下",Source="界面示例 · 非实时查询"};
  card.Show();card.Render();card.Set("Status","界面示例 · 非实时查询");card.UpdateLayout();var first=Snapshot(card.shell);
  card.TogglePreferences(true);card.Find<ComboBox>("Service").SelectedItem=ServicePresets.Get("openai");card.Find<PasswordBox>("ApiKey").Password="";card.UpdateLayout();var second=Snapshot(card.shell);
  var visual=new DrawingVisual();using(var dc=visual.RenderOpen()){
   dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(211,222,231),Color.FromRgb(229,221,233),30),null,new Rect(0,0,1100,850));
   dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(110,193,211,217)),null,new Point(160,350),330,330);dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(95,245,239,217)),null,new Point(900,400),400,360);
   var title=new FormattedText("Lingua  /  划词即译",System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI, Microsoft YaHei UI"),24,ColorBrush("#47546A"),1);dc.DrawText(title,new Point(95,48));
   dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(20,35,51,76)),null,new Rect(96,124,first.Width,first.Height),23,23);dc.DrawImage(first,new Rect(90,115,first.Width,first.Height));
   dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(20,35,51,76)),null,new Rect(611,124,second.Width,second.Height),23,23);dc.DrawImage(second,new Rect(605,115,second.Width,second.Height));
  }
  var bitmap=new RenderTargetBitmap(1100,850,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"界面预览.png")))png.Save(file);
  card.Close();app.Shutdown();
 }
 static RenderTargetBitmap Snapshot(FrameworkElement element){var bitmap=new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),(int)Math.Ceiling(element.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(element);return bitmap;}
 public static void UiTests(){
  var results=new List<string>();int failures=0;Action<string,bool> check=(name,ok)=>{results.Add((ok?"PASS ":"FAIL ")+name);if(!ok)failures++;};
  var app=new Application();var card=new GlassWindow(true);card.cfg.ThemeId="ios";card.ApplyAppearance();IntPtr foreground=Native.GetForegroundWindow();card.Show();card.UpdateLayout();
  check("Card opens without activating",foreground==Native.GetForegroundWindow());check("Direct selection mode migrated",card.cfg.Instant);
  if(Environment.OSVersion.Version.Build>=22621)check("Desktop Acrylic accepted by DWM",card.dwmResult==0);
  if(Environment.OSVersion.Version.Build>=22000)check("System accepts border suppression",card.borderResult==0);
  if(!card.AllowsTransparency)check("Content corners match native rounding",card.shell.CornerRadius==new CornerRadius(8));
  check("No duplicate XAML edge stroke",card.shell.BorderThickness==new Thickness(0)&&card.UseLayoutRounding&&card.SnapsToDevicePixels);
  card.currentText="test";card.answer=new Answer {Translation="测试",Details="测试释义",Examples="This is a test.\n这是一个测试。",Phonetic="/test/",Structure="This is + noun"};card.Render();
  string[] fields={"ShowOriginal","ShowPhonetic","ShowTranslation","ShowDetails","ShowExamples","ShowStructure"};string[] sections={"OriginalSection","Phonetic","TranslationSection","DetailsSection","ExamplesSection","StructureSection"};
  for(int i=0;i<fields.Length;i++){var field=typeof(Settings).GetField(fields[i]);field.SetValue(card.cfg,false);card.Render();bool hidden=card.Find<FrameworkElement>(sections[i]).Visibility==Visibility.Collapsed;field.SetValue(card.cfg,true);card.Render();check(fields[i]+" hides and restores",hidden&&card.Find<FrameworkElement>(sections[i]).Visibility==Visibility.Visible);}
  double full=card.ActualHeight;card.cfg.ShowDetails=false;card.cfg.ShowExamples=false;card.cfg.ShowStructure=false;card.Render();check("Card shrinks with hidden modules",card.ActualHeight<full);
  card.TogglePreferences(true);check("Preferences replaces reading content",card.Find<StackPanel>("Reading").Visibility==Visibility.Collapsed&&card.Find<StackPanel>("Preferences").Visibility==Visibility.Visible);
  card.cfg.GlassTint=80;card.ApplyAppearance();check("Glass slider changes surface alpha",((LinearGradientBrush)card.shell.Background).GradientStops[1].Color.A==80);
  check("SVG assets loaded",vectors.Descendants().Count(x=>x.Name.LocalName=="symbol")==10);
  using(var icon=LoadTrayIcon())check("Application logo and tray ICO load",icon.Width>0&&card.Icon!=null&&card.Find<Image>("BrandLogo").Source!=null);
  card.Find<ComboBox>("Service").SelectedItem=ServicePresets.Get("openai");card.Find<PasswordBox>("ApiKey").Password="ui-fake-key";var prepared=card.PrepareService();check("Key-only setup fills endpoint and model",prepared.Endpoint=="https://api.openai.com/v1/chat/completions"&&prepared.Model=="gpt-4.1-mini"&&prepared.Key=="ui-fake-key");
  card.Find<ComboBox>("Service").SelectedItem=ServicePresets.Get("deepseek");check("Provider switch does not carry the other key",card.Find<PasswordBox>("ApiKey").Password!="ui-fake-key");
  card.Find<ComboBox>("Service").SelectedItem=ServicePresets.Get("openai");check("Switching back restores draft key",card.Find<PasswordBox>("ApiKey").Password=="ui-fake-key");
  check("Default theme is iOS",new Settings().ThemeId=="ios");
  foreach(var theme in Themes.All){card.cfg.ThemeId=theme.Id;card.ApplyAppearance();var json=new System.Web.Script.Serialization.JavaScriptSerializer();check("Theme "+theme.Id+" palette, art and persistence",((SolidColorBrush)card.shell.Resources["AccentBrush"]).Color==Themes.Brush(theme.Accent).Color&&card.Find<Image>("ThemeArt").Source!=null&&json.Deserialize<Settings>(json.Serialize(card.cfg)).ThemeId==theme.Id);}
  check("Unknown theme falls back to iOS",Themes.Get("unknown").Id=="ios");
  card.UpdateLayout();check("Theme chooser uses two columns",Math.Abs(card.themeButtons[0].TranslatePoint(new Point(),card.shell).Y-card.themeButtons[1].TranslatePoint(new Point(),card.shell).Y)<1);
  card.currentText=new string('W',120);card.answer.Translation=new string('字',240);card.Render();check("Long content remains inside card",card.ActualWidth==400&&card.ActualHeight<=card.MaxHeight&&card.Find<ScrollViewer>("Viewport").ActualHeight<=card.Find<ScrollViewer>("Viewport").MaxHeight);
  results.Add("OS build: "+Environment.OSVersion.Version.Build);results.Add("Failures: "+failures);File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ui-test-results.txt"),results);
  card.Close();app.Shutdown();Environment.ExitCode=failures==0?0:1;
 }
 public static void NativePreview(){
  var app=new Application();var backdrop=new Window {Width=580,Height=620,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,ShowActivated=false,Topmost=true,WindowStartupLocation=WindowStartupLocation.CenterScreen,Background=new LinearGradientBrush(Color.FromRgb(204,222,238),Color.FromRgb(242,217,228),40)};
  backdrop.Loaded+=async(s,e)=>{var card=new GlassWindow(true);card.Owner=backdrop;card.currentText="serendipity";card.answer=new Answer {Translation="不期而遇的美好；意外的幸运",Phonetic="/ˌser.ənˈdɪp.ə.ti/",Details="n. 偶然发现美好事物的机缘",Examples="Finding this café was pure serendipity.\n偶然发现这家咖啡馆，真是意外之喜。",Structure="by serendipity · 机缘巧合之下"};card.Show();card.Render();card.Set("Status","原生窗口测试 · 示例内容");card.Left=backdrop.Left+90;card.Top=backdrop.Top+65;await Task.Delay(450);
   var origin=backdrop.PointToScreen(new Point());var matrix=PresentationSource.FromVisual(backdrop).CompositionTarget.TransformToDevice;int width=(int)Math.Round(backdrop.ActualWidth*matrix.M11),height=(int)Math.Round(backdrop.ActualHeight*matrix.M22);
   using(var bitmap=new System.Drawing.Bitmap(width,height)){using(var graphics=System.Drawing.Graphics.FromImage(bitmap))graphics.CopyFromScreen((int)origin.X,(int)origin.Y,0,0,new System.Drawing.Size(width,height));bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"native-edge-preview.png"));}
   card.Close();backdrop.Close();};app.Run(backdrop);
 }
 public static void ThemePreview(){
  var app=new Application();var card=new GlassWindow(true);card.cfg.Compact=true;card.cfg.ShowOriginal=card.cfg.ShowPhonetic=card.cfg.ShowTranslation=card.cfg.ShowDetails=card.cfg.ShowExamples=card.cfg.ShowStructure=true;
  card.currentText="serendipity";card.answer=new Answer {Translation="不期而遇的美好；意外的幸运",Phonetic="/ˌser.ənˈdɪp.ə.ti/",Details="n. 偶然发现美好事物的机缘\n常用于意外的相遇、发现或收获。",Examples="Finding this café was pure serendipity.\n偶然发现这家咖啡馆，真是意外之喜。",Structure="by serendipity · 机缘巧合之下"};card.Show();card.Render();
  var visual=new DrawingVisual();using(var dc=visual.RenderOpen()){dc.DrawRectangle(Themes.Brush("#E8E9EE"),null,new Rect(0,0,1360,1300));dc.DrawText(new FormattedText("LINGUA / 六种心情，同样专注",System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI, Microsoft YaHei UI"),28,Themes.Brush("#3D4354"),1),new Point(45,26));
   for(int i=0;i<Themes.All.Length;i++){var theme=Themes.All[i];card.cfg.ThemeId=theme.Id;card.ApplyAppearance();card.Render();card.Set("Status","主题示例 · 非实时查询");card.UpdateLayout();var shot=Snapshot(card.shell);double x=45+(i%3)*445,y=100+(i/3)*590;
    dc.DrawRoundedRectangle(Themes.Brush("#15000000"),null,new Rect(x+3,y+7,shot.Width,shot.Height),8,8);dc.DrawImage(shot,new Rect(x,y,shot.Width,shot.Height));
    dc.DrawText(new FormattedText(theme.Name,System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI, Microsoft YaHei UI"),15,Themes.Brush(theme.Ink),1),new Point(x,y-27));
    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(shot));using(var f=File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"theme-"+theme.Id+".png")))encoder.Save(f);
   }
  }
  var bitmap=new RenderTargetBitmap(1360,1300,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var f=File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"主题总览.png")))png.Save(f);card.Close();app.Shutdown();
 }
}
}

