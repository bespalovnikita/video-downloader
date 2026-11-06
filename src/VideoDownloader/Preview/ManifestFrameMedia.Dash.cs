using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace VideoDownloader.Native;

internal static partial class ManifestFrameMedia
{
    private static class DashPlan
    {
        private sealed record Representation(XElement Element,IReadOnlyList<XElement> Chain,Uri Address,string Id,long Bandwidth,
            int Width,int Height,string Mime);
        internal static Plan Parse(byte[] bytes,Uri address,MediaFrameSource source,double seconds,double? endSeconds)
        {
            XDocument document;
            try
            {
                using var input=new MemoryStream(bytes,false);
                using var reader=XmlReader.Create(input,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,
                    MaxCharactersInDocument=MaxManifestBytes,IgnoreComments=true,IgnoreProcessingInstructions=true});
                document=XDocument.Load(reader,LoadOptions.None);
            }
            catch(XmlException){throw Unsupported("Некорректный или небезопасный DASH-манифест.");}
            var mpd=document.Root;
            if(mpd is null || mpd.Name.LocalName!="MPD" || mpd.Attribute("type")?.Value=="dynamic")throw Unsupported("Кадры доступны только для статического DASH.");
            if(document.Descendants().Take(100001).Count()>100000)throw Unsupported("DASH-манифест слишком сложный.");
            double? total=Duration(mpd.Attribute("mediaPresentationDuration")?.Value)??source.Duration;
            var periods=Children(mpd,"Period").ToArray();
            if(periods.Length is 0 or >64)throw Unsupported("Некорректные периоды DASH.");
            double position=0;XElement? selected=null;double periodStart=0;double? periodDuration=null;
            for(int index=0;index<periods.Length;index++)
            {
                var period=periods[index];
                double start=Duration(period.Attribute("start")?.Value)??position;
                double? length=Duration(period.Attribute("duration")?.Value);
                if(length is null && index+1<periods.Length && Duration(periods[index+1].Attribute("start")?.Value) is {} next)length=next-start;
                length??=total is {} end?end-start:null;
                if(length is {} invalid && invalid<=0)throw Unsupported("Некорректная длительность периода DASH.");
                if(seconds>=start && (length is null || seconds<start+length.Value || index==periods.Length-1 && seconds<=start+length.Value+.001))
                {selected=period;periodStart=start;periodDuration=length;break;}
                position=length is {} known?CheckedTime(start+known):start;
            }
            if(selected is null)throw Unsupported("Время выходит за доступные периоды DASH.");
            Uri rootBase=Base(address,mpd),periodBase=Base(rootBase,selected);
            var representations=new List<Representation>();
            foreach(var adaptation in Children(selected,"AdaptationSet"))
            {
                string kind=adaptation.Attribute("contentType")?.Value??"";
                string mime=adaptation.Attribute("mimeType")?.Value??"";
                if(source.HasVideo && (kind=="audio" || mime.StartsWith("audio/",StringComparison.OrdinalIgnoreCase)))continue;
                Uri adaptationBase=Base(periodBase,adaptation);
                foreach(var representation in Children(adaptation,"Representation"))
                {
                    if(representations.Count>=1024)throw Unsupported("DASH содержит слишком много представлений.");
                    string type=representation.Attribute("mimeType")?.Value??mime;
                    string content=representation.Attribute("contentType")?.Value??kind;
                    string codecs=representation.Attribute("codecs")?.Value??adaptation.Attribute("codecs")?.Value??"";
                    int width=Int(representation,"width",Int(adaptation,"width",0)),height=Int(representation,"height",Int(adaptation,"height",0));
                    bool isAudio=content=="audio" || type.StartsWith("audio/",StringComparison.OrdinalIgnoreCase) ||
                        Regex.IsMatch(codecs,@"(?:mp4a|opus|vorbis)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant) && width==0 && height==0;
                    if(source.HasVideo?isAudio || content!="video" && !type.StartsWith("video/",StringComparison.OrdinalIgnoreCase) && width==0 && height==0 &&
                        !Regex.IsMatch(codecs,@"(?:avc|hev|hvc|av01|vp0|vp9)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant):!isAudio)continue;
                    var chain=new[]{mpd,selected,adaptation,representation};
                    if(chain.Any(element=>Children(element,"ContentProtection").Any()))continue;
                    string id=representation.Attribute("id")?.Value??"";
                    if(id.Length>256)throw Unsupported("Некорректный идентификатор DASH.");
                    long bandwidth=Long(representation.Attribute("bandwidth")?.Value,0);
                    representations.Add(new(representation,chain,Base(adaptationBase,representation),id,bandwidth,width,height,type));
                }
            }
            if(representations.Count==0)throw Unsupported("DASH не содержит пригодное незашифрованное видео.");
            var exact=representations.Where(value=>value.Id.Length>0 && (source.FormatId==value.Id || source.FormatId.EndsWith("-"+value.Id,StringComparison.Ordinal))).ToArray();
            Representation chosen;
            if(exact.Length==1)chosen=exact[0];
            else
            {
                var dimensions=representations.Where(value=>(source.Width==0 || value.Width==source.Width) && (source.Height==0 || value.Height==source.Height)).ToArray();
                if(dimensions.Length==1)chosen=dimensions[0];
                else if(representations.Count==1)chosen=representations[0];
                else throw Unsupported("Не удалось однозначно сопоставить выбранный видеоформат с DASH.");
            }
            string container=chosen.Mime.Contains("webm",StringComparison.OrdinalIgnoreCase) || chosen.Address.AbsolutePath.EndsWith(".webm",StringComparison.OrdinalIgnoreCase)?"matroska":"mov";
            var template=Inherited(chosen.Chain,"SegmentTemplate");
            if(template is not null)return FromTemplate(template,chosen,source,seconds,endSeconds,periodStart,periodDuration,container);
            var list=Inherited(chosen.Chain,"SegmentList");
            if(list is not null)return FromList(list,chosen,periodStart,periodDuration,container);
            // SegmentBase is a single seekable media resource; its index stays
            // inside the existing bounded progressive reader, never in FFmpeg XML.
            if(chosen.Address==address || chosen.Address.AbsolutePath.EndsWith(".mpd",StringComparison.OrdinalIgnoreCase))throw Unsupported("DASH не содержит поддерживаемую схему сегментов.");
            return new([],container,chosen.Address,periodStart);
        }
        private static IEnumerable<XElement> Children(XElement parent,string local)=>parent.Elements().Where(element=>element.Name.LocalName==local);
        private static int Int(XElement element,string name,int fallback)
        {
            string? text=element.Attribute(name)?.Value;if(text is null)return fallback;
            if(!int.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out int value) || value<0 || value>131072)throw Unsupported("Некорректный размер DASH.");return value;
        }
        private static long Long(string? text,long fallback)
        {if(text is null)return fallback;if(!long.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out long value) || value<0)throw Unsupported("Некорректное число DASH.");return value;}
        private static double? Duration(string? text)
        {
            if(text is null)return null;
            try{return CheckedTime(XmlConvert.ToTimeSpan(text).TotalSeconds);}
            catch(Exception error) when(error is FormatException or OverflowException){throw Unsupported("Некорректная длительность DASH.");}
        }
        private static Uri Base(Uri previous,XElement element)
        {
            string? value=Children(element,"BaseURL").FirstOrDefault()?.Value.Trim();
            return string.IsNullOrEmpty(value)?previous:Resolve(previous,value);
        }
        private static XElement? Inherited(IReadOnlyList<XElement> chain,string name)
        {
            XElement? result=null;
            foreach(var parent in chain)
            {
                var child=Children(parent,name).SingleOrDefault();if(child is null)continue;
                if(result is null)result=new XElement(child);
                else
                {
                    foreach(var attribute in child.Attributes())result.SetAttributeValue(attribute.Name,attribute.Value);
                    foreach(var group in child.Elements().GroupBy(item=>item.Name.LocalName))
                    {result.Elements().Where(value=>value.Name.LocalName==group.Key).Remove();result.Add(group.Select(item=>new XElement(item)));}
                }
            }
            return result;
        }
        private static Plan FromTemplate(XElement template,Representation representation,MediaFrameSource source,double seconds,double? endSeconds,
            double periodStart,double? periodDuration,string container)
        {
            long scale=Long(template.Attribute("timescale")?.Value,1),startNumber=Long(template.Attribute("startNumber")?.Value,1),offset=Long(template.Attribute("presentationTimeOffset")?.Value,0);
            if(scale is <=0 or >1000000000)throw Unsupported("Некорректный timescale DASH.");
            string? media=template.Attribute("media")?.Value,initial=template.Attribute("initialization")?.Value;
            if(string.IsNullOrEmpty(media))throw Unsupported("В DASH отсутствует шаблон сегмента.");
            Resource? init=initial is null?null:new(Resolve(representation.Address,Expand(initial,representation,startNumber,0)));
            var timeline=Children(template,"SegmentTimeline").SingleOrDefault();
            var segments=new List<Segment>();
            if(timeline is not null)
            {
                var entries=Children(timeline,"S").ToArray();
                if(entries.Length>MaxSegments)throw Unsupported("Слишком длинная шкала DASH.");
                long time=0,number=startNumber;
                for(int index=0;index<entries.Length;index++)
                {
                    var entry=entries[index];time=Long(entry.Attribute("t")?.Value,time);
                    long duration=Long(entry.Attribute("d")?.Value,0);if(duration<=0)throw Unsupported("Некорректная длительность сегмента DASH.");
                    long repeat=0;
                    if(entry.Attribute("r") is {} repeated && (!long.TryParse(repeated.Value,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out repeat) || repeat< -1))throw Unsupported("Некорректный повтор DASH.");
                    long count;
                    if(repeat>=0)count=checked(repeat+1);
                    else
                    {
                        double end=index+1<entries.Length && entries[index+1].Attribute("t") is {} next?Long(next.Value,0):
                            periodDuration is {} known?offset+known*scale:throw Unsupported("DASH не задаёт конец повторяемых сегментов.");
                        if(end<=time)throw Unsupported("Некорректная шкала DASH.");count=(long)Math.Ceiling((end-time)/duration);
                    }
                    if(count<=0 || count>10000000)throw Unsupported("Некорректное число сегментов DASH.");
                    double relative=(seconds-periodStart)*scale+offset;
                    long target=(long)Math.Floor((relative-time)/duration);
                    long final=(long)Math.Floor((((endSeconds??seconds)-.0001-periodStart)*scale+offset-time)/duration);
                    long first=Math.Max(0,target-2),last=Math.Min(count-1,Math.Max(target+1,final));
                    if(last-first>MaxSegments)throw Unsupported("Слишком много сегментов фрагмента DASH.");
                    for(long step=first;step<=last;step++)
                    {
                        long at=checked(time+step*duration),sequence=checked(number+step);
                        double begin=periodStart+(at-offset)/(double)scale;
                        if(begin<0 && begin+duration/(double)scale<=0)continue;
                        if(segments.Count>=64)throw Unsupported("Окно DASH требует слишком много сетевых обращений.");
                        segments.Add(new(new(Resolve(representation.Address,Expand(media,representation,sequence,at))),begin,duration/(double)scale,init));
                    }
                    time=checked(time+count*duration);number=checked(number+count);
                }
            }
            else
            {
                long duration=Long(template.Attribute("duration")?.Value,0);if(duration<=0 || periodDuration is null)throw Unsupported("DASH не задаёт длительность сегментов.");
                long count=(long)Math.Ceiling(periodDuration.Value*scale/duration);
                if(count<=0 || count>10000000)throw Unsupported("Некорректное число сегментов DASH.");
                long target=(long)Math.Floor((seconds-periodStart)*scale/duration);
                long last=Math.Min(count-1,Math.Max(target+1,(long)Math.Floor(((endSeconds??seconds)-.0001-periodStart)*scale/duration)));
                if(last-Math.Max(0,target-2)>MaxSegments)throw Unsupported("Слишком много сегментов фрагмента DASH.");
                for(long index=Math.Max(0,target-2);index<=last;index++)
                {
                    long at=checked(index*duration),number=checked(startNumber+index);
                    if(segments.Count>=64)throw Unsupported("Окно DASH требует слишком много сетевых обращений.");
                    segments.Add(new(new(Resolve(representation.Address,Expand(media,representation,number,at))),periodStart+at/(double)scale,
                        Math.Min(duration/(double)scale,periodDuration.Value-at/(double)scale),init));
                }
            }
            return new(segments.OrderBy(segment=>segment.Start).ToList(),container);
        }
        private static string Expand(string text,Representation representation,long number,long time)
        {
            if(text.Length>16384)throw Unsupported("Слишком длинный шаблон DASH.");
            const string escaped="\u0001";text=text.Replace("$$",escaped,StringComparison.Ordinal);
            string replaced=Regex.Replace(text,@"\$(RepresentationID|Bandwidth|Number|Time)(?:%0([0-9]{1,2})d)?\$",match=>
            {
                string key=match.Groups[1].Value;
                if(key=="RepresentationID")
                {if(match.Groups[2].Success)throw Unsupported("Некорректный шаблон DASH ID.");return representation.Id;}
                long value=key=="Bandwidth"?representation.Bandwidth:key=="Number"?number:time;
                if(!match.Groups[2].Success)return value.ToString(CultureInfo.InvariantCulture);
                int width=int.Parse(match.Groups[2].Value,CultureInfo.InvariantCulture);if(width>16)throw Unsupported("Слишком широкое число DASH.");
                return value.ToString("D"+width,CultureInfo.InvariantCulture);
            },RegexOptions.CultureInvariant);
            if(replaced.Contains('$'))throw Unsupported("Неподдерживаемый параметр шаблона DASH.");
            return replaced.Replace(escaped,"$",StringComparison.Ordinal);
        }
        private static Plan FromList(XElement list,Representation representation,double periodStart,double? periodDuration,string container)
        {
            long scale=Long(list.Attribute("timescale")?.Value,1),duration=Long(list.Attribute("duration")?.Value,0),offset=Long(list.Attribute("presentationTimeOffset")?.Value,0);
            if(scale is <=0 or >1000000000)throw Unsupported("Некорректный timescale DASH.");
            Resource? init=null;
            if(Children(list,"Initialization").SingleOrDefault() is {} initialization)
                init=ResourceFor(representation.Address,initialization.Attribute("sourceURL")?.Value??"",initialization.Attribute("range")?.Value);
            var urls=Children(list,"SegmentURL").ToArray();if(urls.Length>MaxSegments)throw Unsupported("Слишком много DASH-сегментов.");
            var timeline=Children(list,"SegmentTimeline").SingleOrDefault();
            var times=new List<(long Start,long Duration)>();
            if(timeline is not null)
            {
                long at=0;
                foreach(var entry in Children(timeline,"S"))
                {
                    at=Long(entry.Attribute("t")?.Value,at);long d=Long(entry.Attribute("d")?.Value,0),r=Long(entry.Attribute("r")?.Value,0);
                    if(d<=0 || r>MaxSegments || times.Count+r+1>MaxSegments)throw Unsupported("Некорректная шкала SegmentList DASH.");
                    for(long index=0;index<=r;index++){times.Add((at,d));at=checked(at+d);}
                }
            }
            else
            {
                if(duration<=0 && periodDuration is {} known && urls.Length>0)duration=(long)Math.Round(known*scale/urls.Length);
                if(duration<=0)throw Unsupported("Неизвестная длительность SegmentList DASH.");
                for(int index=0;index<urls.Length;index++)times.Add((checked(index*duration),duration));
            }
            if(times.Count!=urls.Length)throw Unsupported("Не совпадает число сегментов и времён DASH.");
            var result=new List<Segment>();
            for(int index=0;index<urls.Length;index++)
            {
                var resource=ResourceFor(representation.Address,urls[index].Attribute("media")?.Value??"",urls[index].Attribute("mediaRange")?.Value);
                result.Add(new(resource,periodStart+(times[index].Start-offset)/(double)scale,times[index].Duration/(double)scale,init));
            }
            return new(result,container);
        }
        private static Resource ResourceFor(Uri basis,string address,string? range)
        {
            var uri=address.Length==0?basis:Resolve(basis,address);
            if(range is null)return new(uri);
            var parts=range.Split('-');
            if(parts.Length!=2 || !long.TryParse(parts[0],NumberStyles.None,CultureInfo.InvariantCulture,out long first) ||
                !long.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out long last) || first<0 || last<first || last-first>=MediaFrameService.MaxMediaBytes)
                throw Unsupported("Некорректный диапазон DASH.");
            return new(uri,first,last);
        }
    }
}
