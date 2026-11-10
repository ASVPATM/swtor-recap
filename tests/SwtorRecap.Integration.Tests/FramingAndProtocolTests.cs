using System.Text;
using System.Buffers.Binary;
using SwtorRecap.Core;
using SwtorRecap.Engine;

namespace SwtorRecap.Integration.Tests;

public sealed class FramingAndProtocolTests
{
    [Fact]public void EverySplitPointPreservesCrLfAndUtf8ByteOffsets()
    {
        var data=Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("[one] é\r\n[two] 東京\r\n")).ToArray();var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".txt");
        try
        {
            for(var split=0;split<=data.Length;split++)
            {
                File.WriteAllBytes(path,data[..split]);long offset=0;var records=new List<SourceRecord>();
                using(var stream=ByteFramer.OpenRead(path)){var first=ByteFramer.Read(stream,0,"utf8-bom",false,chunkSize:1);records.AddRange(first.Records);if(first.Records.Length>0)offset=first.Records[^1].End;}
                using(var output=new FileStream(path,FileMode.Append))output.Write(data[split..]);
                using(var stream=ByteFramer.OpenRead(path))records.AddRange(ByteFramer.Read(stream,offset,"utf8-bom",false,chunkSize:1).Records);
                Assert.Equal(2,records.Count);Assert.Equal("[one] é",records[0].Text);Assert.Equal("[two] 東京",records[1].Text);Assert.Equal(data.Length,records[^1].End);
            }
        }
        finally{File.Delete(path);}
    }
    [Fact]public void Windows1252DecodesAccents()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);var path=Path.GetTempFileName();File.WriteAllBytes(path,Encoding.GetEncoding(1252).GetBytes("Élan\n"));
        try{using var stream=ByteFramer.OpenRead(path);Assert.Equal("Élan",ByteFramer.Read(stream,0,"windows-1252",false).Records[0].Text);}finally{File.Delete(path);}
    }
    [Theory][InlineData(-1)][InlineData(0)][InlineData(1048577)]
    public async Task InvalidIpcLengthsAreRejectedBeforeAllocation(int size)
    {var data=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(data,size);using var stream=new MemoryStream(data);await Assert.ThrowsAsync<InvalidDataException>(()=>FrameProtocol.ReadAsync<IpcRequest>(stream,CancellationToken.None));}
    [Fact]public async Task IpcRoundTripKeepsLargeIdsAsStrings()
    {using var stream=new MemoryStream();var id="16141082698337403481";await FrameProtocol.WriteAsync(stream,id,CancellationToken.None);stream.Position=0;Assert.Equal(id,await FrameProtocol.ReadAsync<string>(stream,CancellationToken.None));}
}
