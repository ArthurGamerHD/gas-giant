using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace GasGiantNet.Export
{
    internal static class Png16Writer
    {
        private static readonly byte[] Signature = new byte[] { 137,80,78,71,13,10,26,10 };
        private static readonly uint[] CrcTable = MakeCrcTable();

        public static ushort Quantize(float x)
        {
            if (float.IsNaN(x) || float.IsNegativeInfinity(x)) x = 0.0f;
            if (float.IsPositiveInfinity(x)) x = 1.0f;
            if (x < 0.0f) x = 0.0f;
            if (x > 1.0f) x = 1.0f;
            // Upstream contract: (clip(x,0,1) * 65535.0 + 0.5).astype(uint16)
            return (ushort)(x * 65535.0f + 0.5f);
        }

        public static void WriteRgb(string path, int width, int height, ushort[] rgb, int compression)
        {
            if (rgb == null || rgb.Length != checked(width * height * 3)) throw new ArgumentException("RGB buffer size mismatch");
            Write(path,width,height,2,compression,delegate(Stream z)
            {
                byte[] row=new byte[1+width*6]; row[0]=0;
                for(int y=0;y<height;y++)
                {
                    int si=y*width*3,di=1;
                    for(int x=0;x<width;x++)
                    {
                        Put16(row,di,rgb[si++]);di+=2;
                        Put16(row,di,rgb[si++]);di+=2;
                        Put16(row,di,rgb[si++]);di+=2;
                    }
                    z.Write(row,0,row.Length);
                }
            });
        }

        public static void WriteGray(string path, int width, int height, ushort[] gray, int compression)
        {
            if (gray == null || gray.Length != checked(width * height)) throw new ArgumentException("gray buffer size mismatch");
            Write(path,width,height,0,compression,delegate(Stream z)
            {
                byte[] row=new byte[1+width*2]; row[0]=0;
                for(int y=0;y<height;y++)
                {
                    int si=y*width,di=1;
                    for(int x=0;x<width;x++){Put16(row,di,gray[si++]);di+=2;}
                    z.Write(row,0,row.Length);
                }
            });
        }

        private static void Write(string path,int width,int height,byte colorType,int compression,Action<Stream> rows)
        {
            string dir=Path.GetDirectoryName(Path.GetFullPath(path)); if(!string.IsNullOrEmpty(dir))Directory.CreateDirectory(dir);
            byte[] ihdr=new byte[13];Put32(ihdr,0,(uint)width);Put32(ihdr,4,(uint)height);ihdr[8]=16;ihdr[9]=colorType;
            byte[] compressed;
            using(MemoryStream ms=new MemoryStream())
            {
                CompressionLevel level=compression<=0?CompressionLevel.NoCompression:(compression<=3?CompressionLevel.Fastest:CompressionLevel.Optimal);
                using(ZLibStream zs=new ZLibStream(ms,level,true)){rows(zs);} compressed=ms.ToArray();
            }
            using(FileStream fs=new FileStream(path,FileMode.Create,FileAccess.Write,FileShare.None))
            {
                fs.Write(Signature,0,Signature.Length);Chunk(fs,"IHDR",ihdr);Chunk(fs,"IDAT",compressed);Chunk(fs,"IEND",new byte[0]);
            }
        }

        private static void Put16(byte[] b,int o,ushort v){b[o]=(byte)(v>>8);b[o+1]=(byte)v;}
        private static void Put32(byte[] b,int o,uint v){b[o]=(byte)(v>>24);b[o+1]=(byte)(v>>16);b[o+2]=(byte)(v>>8);b[o+3]=(byte)v;}
        private static void Chunk(Stream s,string type,byte[] payload)
        {
            byte[] tb=Encoding.ASCII.GetBytes(type), len=new byte[4], cb=new byte[4];Put32(len,0,(uint)payload.Length);s.Write(len,0,4);s.Write(tb,0,4);if(payload.Length>0)s.Write(payload,0,payload.Length);
            uint crc=0xffffffffU;crc=Update(crc,tb,0,tb.Length);crc=Update(crc,payload,0,payload.Length);crc^=0xffffffffU;Put32(cb,0,crc);s.Write(cb,0,4);
        }
        private static uint Update(uint crc,byte[] data,int offset,int count){for(int i=0;i<count;i++)crc=CrcTable[(int)((crc^data[offset+i])&255)]^(crc>>8);return crc;}
        private static uint[] MakeCrcTable(){uint[] t=new uint[256];for(uint n=0;n<256;n++){uint c=n;for(int k=0;k<8;k++)c=(c&1)!=0?0xedb88320U^(c>>1):c>>1;t[(int)n]=c;}return t;}
    }
}
