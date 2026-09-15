using System;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Collections.Generic;
namespace LinguaSelect {
public class CardTheme {
 public string Id,Name,Subtitle,Top,Bottom,Ink,Muted,Accent,Soft,Panel,Line,Font;
 public CardTheme(string id,string name,string subtitle,string top,string bottom,string ink,string muted,string accent,string soft,string panel,string line,string font="Segoe UI, Microsoft YaHei UI") {Id=id;Name=name;Subtitle=subtitle;Top=top;Bottom=bottom;Ink=ink;Muted=muted;Accent=accent;Soft=soft;Panel=panel;Line=line;Font=font;}
}
static class Themes {
 public static readonly CardTheme[] All={
  new CardTheme("ios","iOS 玻璃","清透、轻盈、专注","#FFFFFF","#EEF3FD","#242832","#737D8E","#3478F6","#DEE9FF","#B8FFFFFF","#23748195"),
  new CardTheme("bears","咱们裸熊","和三只熊一起慢慢学","#FFF9EF","#EFE6D6","#493A30","#88735E","#A76F42","#ECD8BD","#D9FFFDF6","#38A68565"),
  new CardTheme("kitty","Hello Kitty 和伙伴","把新单词装进小礼物","#FFF8FC","#FFE4ED","#633E52","#9A7086","#E9679A","#FFD1E2","#DAFFFFFF","#44E8A4BF"),
  new CardTheme("google","Google · Material","一点好奇，发现更多","#FCFDFF","#EAF1FA","#253549","#64768C","#4285F4","#D8E7FF","#E8FFFFFF","#32A0B7D5"),
  new CardTheme("doodle","卡通涂鸦","把灵感画在单词旁边","#FFFDF2","#FFF1B8","#35312C","#766951","#7255D8","#E4DBFF","#F8FFFFFF","#C535312C","Comic Sans MS, Microsoft YaHei UI"),
  new CardTheme("chiikawa","Chiikawa","今天也有小小的进步","#FFFBF7","#FAEDE8","#675C58","#97827B","#C9879E","#F2D8E2","#DFFFFFFF","#4AC7A59F")
 };
 static readonly XDocument art=Load();
 static XDocument Load(){using(var s=Assembly.GetExecutingAssembly().GetManifestResourceStream("LinguaSelect.themes.svg"))return XDocument.Load(s);}
 public static CardTheme Get(string id){return All.FirstOrDefault(t=>t.Id==id)??All[0];}
 public static SolidColorBrush Brush(string color){return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));}
 static readonly Dictionary<string,ImageSource> images=new Dictionary<string,ImageSource>();
 public static ImageSource Illustration(string id){
  ImageSource cached;if(images.TryGetValue(id,out cached))return cached;
  if(id=="bears"||id=="kitty"||id=="chiikawa"){
   using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("LinguaSelect.themes."+id+".png")){
    var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.DecodePixelWidth=600;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();images[id]=bitmap;return bitmap;
   }
  }
  var symbol=art.Descendants().First(x=>x.Name.LocalName=="symbol"&&(string)x.Attribute("id")==id);var drawing=new DrawingGroup();
  drawing.Children.Add(new GeometryDrawing(Brushes.Transparent,null,new RectangleGeometry(new Rect(0,0,180,80))));
  foreach(var p in symbol.Elements()){
   Geometry geo;string name=p.Name.LocalName;
   if(name=="path")geo=Geometry.Parse((string)p.Attribute("d"));
   else if(name=="ellipse")geo=new EllipseGeometry(new Point(N(p,"cx"),N(p,"cy")),N(p,"rx"),N(p,"ry"));
   else if(name=="circle")geo=new EllipseGeometry(new Point(N(p,"cx"),N(p,"cy")),N(p,"r"),N(p,"r"));
   else if(name=="rect")geo=new RectangleGeometry(new Rect(N(p,"x"),N(p,"y"),N(p,"width"),N(p,"height")),N(p,"rx"),N(p,"rx"));
   else continue;
   string fill=(string)p.Attribute("fill"),stroke=(string)p.Attribute("stroke");Pen pen=stroke==null||stroke=="none"?null:new Pen(Brush(stroke),p.Attribute("stroke-width")==null?1.8:N(p,"stroke-width")){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round,LineJoin=PenLineJoin.Round};
   drawing.Children.Add(new GeometryDrawing(fill==null||fill=="none"?null:Brush(fill),pen,geo));
  }
  drawing.Freeze();var vector=new DrawingImage(drawing);vector.Freeze();images[id]=vector;return vector;
 }
 static double N(XElement e,string n){return e.Attribute(n)==null?0:double.Parse((string)e.Attribute(n),System.Globalization.CultureInfo.InvariantCulture);}
}
}
