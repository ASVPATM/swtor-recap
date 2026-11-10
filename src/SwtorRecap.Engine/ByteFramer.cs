using System.Security.Cryptography;
using System.Text;
using SwtorRecap.Core;

namespace SwtorRecap.Engine;

public sealed record FramedBatch(SourceRecord[] Records, bool DiscardPartial);
public static class ByteFramer
{
    public const int MaximumRecordBytes = 64 * 1024;
    static ByteFramer() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    public static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
    public static string DetectEncoding(FileStream stream)
    {
        Span<byte> bom = stackalloc byte[3]; var previous=stream.Position;stream.Position=0;var n=stream.Read(bom);stream.Position=previous;
        return n==3 && bom.SequenceEqual(new byte[]{239,187,191}) ? "utf8-bom" : "windows-1252";
    }
    public static FramedBatch Read(FileStream stream, long offset, string encoding, bool sealedSource, bool discardPartial=false, int maxRecords=2000, int maxBytes=1024*1024, int chunkSize=65536)
    {
        if(maxRecords<1||maxBytes<1||chunkSize<1) throw new ArgumentOutOfRangeException(nameof(maxRecords));
        stream.Position=offset;var result=new List<SourceRecord>();var buffer=new byte[Math.Min(chunkSize,65536)];var line=new MemoryStream();
        var decoder=encoding=="utf8-bom" ? new UTF8Encoding(false,true) : encoding=="windows-1252" ? Encoding.GetEncoding(1252,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback) : throw new ArgumentException("Unsupported encoding profile.");
        var start=offset;var position=offset;var oversized=false;var dropping=discardPartial;var finished=false;
        while(!finished)
        {
            var read=stream.Read(buffer,0,buffer.Length);if(read==0)break;
            for(var i=0;i<read;i++)
            {
                var value=buffer[i];position++;
                if(value==10)
                {
                    if(dropping||oversized) result.Add(new(start,position,null,dropping?"discarded_partial_boundary":"oversized_record"));
                    else result.Add(Decode(line.ToArray(),start,position,decoder,encoding));
                    line.SetLength(0);dropping=false;oversized=false;start=position;
                    if(result.Count>=maxRecords||position-offset>=maxBytes){finished=true;break;}
                }
                else if(!dropping&&!oversized)
                {
                    if(line.Length==MaximumRecordBytes){oversized=true;line.SetLength(0);}else line.WriteByte(value);
                }
                if((oversized||dropping)&&position-offset>=maxBytes)
                {result.Add(new(start,position,null,oversized?"oversized_record_pending":"discarded_partial_boundary"));dropping=true;finished=true;break;}
            }
        }
        if(!finished&&position>start&&sealedSource)
        {
            result.Add(dropping||oversized ? new(start,position,null,"rejected_sealed_tail") : Decode(line.ToArray(),start,position,decoder,encoding));dropping=false;
        }
        return new(result.ToArray(),dropping);
    }
    private static SourceRecord Decode(byte[] data,long start,long end,Encoding encoding,string profile)
    {
        var begin=start==0&&profile=="utf8-bom"&&data.Length>=3?3:0;
        var length=data.Length-begin;if(length>0&&data[^1]==13)length--;
        try{return new(start,end,encoding.GetString(data,begin,length));}catch(DecoderFallbackException){return new(start,end,null,"invalid_encoding");}
    }
    public static string HashRange(FileStream stream,long start,int count)
    {
        var previous=stream.Position;stream.Position=start;var data=new byte[count];var read=0;
        while(read<count){var n=stream.Read(data,read,count-read);if(n==0)break;read+=n;}
        stream.Position=previous;return Convert.ToHexString(SHA256.HashData(data.AsSpan(0,read))).ToLowerInvariant();
    }
    public static string Digest(string path)
    {using var stream=OpenRead(path);return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();}
}
