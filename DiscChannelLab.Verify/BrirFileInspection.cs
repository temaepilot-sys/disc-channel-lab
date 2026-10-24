using Disc2Flac;
using PureHDF;
using System.IO;

internal static class BrirFileInspection
{
    public static void Run(string file, string output)
    {
        using var h = H5File.OpenRead(file);
        var attributes = new Dictionary<string,string>(); var data = new Dictionary<string,object>();
        foreach (string key in new[]{"SOFAConventions","DataType","Title","Comment","History","License","Origin","DatabaseName","ListenerShortName","RoomType","RoomDescription","ApplicationName","AuthorContact","References","ReceiverDescription","EmitterDescription"})
        { try { attributes[key] = h.Attribute(key).Read<string>(); } catch(Exception) {} }
        foreach(string key in new[]{"Data.IR","Data.SamplingRate","Data.Delay","SourcePosition","ListenerPosition","ListenerView","ListenerUp","ReceiverPosition","EmitterPosition","EmitterView","EmitterUp"})
        {
            try
            {
                var d = h.Dataset(key); string type="",units="";
                try{type=d.Attribute("Type").Read<string>();}catch(Exception){}
                try{units=d.Attribute("Units").Read<string>();}catch(Exception){}
                double[]? values = null;
                if(key!="Data.IR"&&d.Space.Dimensions.Aggregate(1UL,(a,b)=>checked(a*b))<1_000_000)
                    values=d.Type.Size==4?d.Read<float[]>().Select(x=>(double)x).ToArray():d.Read<double[]>();
                data[key]=new{dimensions=d.Space.Dimensions,type,units,values};
            }
            catch(Exception ex){data[key]=new{error=ex.Message};}
        }
        SpatialJson.Save(output,new{file=Path.GetFullPath(file),attributes,data});
        Console.WriteLine("SOFA metadata saved: "+output);
    }
}
