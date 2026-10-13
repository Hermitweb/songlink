// dotnet script test - just to confirm parser works
using SongLink.Host.AudioStream;
using System;
using System.Linq;

var buf = new byte[1936];
buf[0] = 1; // ver
buf[1] = 7; // flags (FIRST|LAST|RAW_PCM)
buf[2] = 0; buf[3] = 0; buf[4] = 0; buf[5] = 1; // seq=1
buf[6] = 0; buf[7] = 0; buf[8] = 0; buf[9] = 0; buf[10] = 0; buf[11] = 0; buf[12] = 3; buf[13] = 0xc0; // pts=960
buf[14] = 7; buf[15] = 0x80; // payloadLen=1920

var frame = AudioStreamWire.Parse(buf);
Console.WriteLine($"ver={frame.Version} flags={frame.Flags} seq={frame.Seq} pts={frame.Pts} payloadLen={frame.PayloadLen}");
