using System;
using System.IO;
using System.Text;
using GasGiantNet.Sim;

namespace GasGiantNet.Export
{
    // Minimal dependency-free OpenEXR scanline writer. Pixel values and channel
    // names match upstream. Compression is NONE rather than ZIP; EXR compression
    // is lossless and does not change decoded image values or manifest format.
    internal static class OpenExrWriter
    {
        private const uint Magic=20000630U;
        private const uint Version=2U;
        private const int PixelHalf=1;
        private const int PixelFloat=2;

        public static void WriteGray32(string path,int width,int height,float[] gray)
        {
            if(gray==null||gray.Length!=checked(width*height))throw new ArgumentException("gray buffer size mismatch");
            Write(path,width,height,new string[]{"Y"},PixelFloat,delegate(BinaryWriter bw,int y,int channel)
            {
                int row=y*width;for(int x=0;x<width;x++)bw.Write(gray[row+x]);
            });
        }

        public static void WriteRgba32(string path,FloatTexture tex)
        {
            if(tex==null||tex.Channels<4)throw new ArgumentException("expected RGBA texture");
            Write(path,tex.Width,tex.Height,new string[]{"R","G","B","A"},PixelFloat,delegate(BinaryWriter bw,int y,int channel)
            {
                int row=y*tex.Width*tex.Channels+channel;for(int x=0;x<tex.Width;x++,row+=tex.Channels)bw.Write(tex.Data[row]);
            });
        }

        public static void WriteRgba32(string path,int width,int height,float[] rgba)
        {
            if(rgba==null||rgba.Length!=checked(width*height*4))throw new ArgumentException("RGBA buffer size mismatch");
            Write(path,width,height,new string[]{"R","G","B","A"},PixelFloat,delegate(BinaryWriter bw,int y,int channel)
            {
                int p=(y*width*4)+channel;for(int x=0;x<width;x++,p+=4)bw.Write(rgba[p]);
            });
        }

        public static void WriteRgba16(string path,int width,int height,float[] rgba)
        {
            if(rgba==null||rgba.Length!=checked(width*height*4))throw new ArgumentException("RGBA buffer size mismatch");
            Write(path,width,height,new string[]{"R","G","B","A"},PixelHalf,delegate(BinaryWriter bw,int y,int channel)
            {
                int p=(y*width*4)+channel;for(int x=0;x<width;x++,p+=4)bw.Write(FloatToHalfBits(rgba[p]));
            });
        }

        private delegate void WriteChannel(BinaryWriter bw,int y,int channel);

        private static void Write(string path,int width,int height,string[] channels,int pixelType,WriteChannel writeChannel)
        {
            string dir=Path.GetDirectoryName(Path.GetFullPath(path));if(!string.IsNullOrEmpty(dir))Directory.CreateDirectory(dir);
            using(FileStream fs=new FileStream(path,FileMode.Create,FileAccess.ReadWrite,FileShare.None))
            using(BinaryWriter bw=new BinaryWriter(fs,Encoding.ASCII,true))
            {
                bw.Write(Magic);bw.Write(Version);
                WriteChannelsAttr(bw,channels,pixelType);
                WriteAttr(bw,"compression","compression",new byte[]{0});
                WriteBox2i(bw,"dataWindow",0,0,width-1,height-1);
                WriteBox2i(bw,"displayWindow",0,0,width-1,height-1);
                WriteAttr(bw,"lineOrder","lineOrder",new byte[]{0});
                WriteFloatAttr(bw,"pixelAspectRatio",1.0f);
                WriteV2fAttr(bw,"screenWindowCenter",0.0f,0.0f);
                WriteFloatAttr(bw,"screenWindowWidth",1.0f);
                bw.Write((byte)0); // header terminator

                long tablePos=fs.Position;
                for(int y=0;y<height;y++)bw.Write((ulong)0);
                long[] offsets=new long[height];
                int bytesPer= pixelType==PixelHalf?2:4;
                int dataSize=checked(width*channels.Length*bytesPer);
                for(int y=0;y<height;y++)
                {
                    offsets[y]=fs.Position;bw.Write(y);bw.Write(dataSize);
                    for(int c=0;c<channels.Length;c++)writeChannel(bw,y,c);
                }
                long end=fs.Position;fs.Position=tablePos;for(int y=0;y<height;y++)bw.Write((ulong)offsets[y]);fs.Position=end;
            }
        }

        private static void WriteChannelsAttr(BinaryWriter bw,string[] channels,int pixelType)
        {
            using(MemoryStream ms=new MemoryStream())
            using(BinaryWriter cb=new BinaryWriter(ms,Encoding.ASCII,true))
            {
                for(int i=0;i<channels.Length;i++)
                {
                    CStr(cb,channels[i]);cb.Write(pixelType);cb.Write((byte)0);cb.Write(new byte[]{0,0,0});cb.Write(1);cb.Write(1);
                }
                cb.Write((byte)0);cb.Flush();WriteAttr(bw,"channels","chlist",ms.ToArray());
            }
        }
        private static void WriteBox2i(BinaryWriter bw,string name,int x0,int y0,int x1,int y1)
        {using(MemoryStream ms=new MemoryStream())using(BinaryWriter b=new BinaryWriter(ms)){b.Write(x0);b.Write(y0);b.Write(x1);b.Write(y1);b.Flush();WriteAttr(bw,name,"box2i",ms.ToArray());}}
        private static void WriteFloatAttr(BinaryWriter bw,string name,float v){byte[] b=BitConverter.GetBytes(v);WriteAttr(bw,name,"float",b);}
        private static void WriteV2fAttr(BinaryWriter bw,string name,float x,float y){byte[] b=new byte[8];Array.Copy(BitConverter.GetBytes(x),0,b,0,4);Array.Copy(BitConverter.GetBytes(y),0,b,4,4);WriteAttr(bw,name,"v2f",b);}
        private static void WriteAttr(BinaryWriter bw,string name,string type,byte[] value){CStr(bw,name);CStr(bw,type);bw.Write(value.Length);bw.Write(value);}
        private static void CStr(BinaryWriter bw,string s){bw.Write(Encoding.ASCII.GetBytes(s));bw.Write((byte)0);}

        private static ushort FloatToHalfBits(float f)
        {
            uint x=(uint)BitConverter.SingleToInt32Bits(f);uint sign=(x>>16)&0x8000U;uint mant=x&0x007fffffU;int exp=(int)((x>>23)&0xffU)-127+15;
            if(exp<=0)
            {
                if(exp<-10)return (ushort)sign;mant=(mant|0x00800000U)>>(1-exp);if((mant&0x00001000U)!=0)mant+=0x00002000U;return (ushort)(sign|(mant>>13));
            }
            if(exp>=31)
            {
                if((x&0x7fffffffU)>0x7f800000U)return (ushort)(sign|0x7e00U);return (ushort)(sign|0x7c00U);
            }
            if((mant&0x00001000U)!=0){mant+=0x00002000U;if((mant&0x00800000U)!=0){mant=0;exp++;if(exp>=31)return (ushort)(sign|0x7c00U);}}
            return (ushort)(sign|((uint)exp<<10)|(mant>>13));
        }
    }
}
