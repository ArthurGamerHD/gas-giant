using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using GasGiantNet.Sim;

namespace GasGiantNet.Export
{
    internal static class PngImageReader
    {
        private static readonly byte[] Signature=new byte[]{137,80,78,71,13,10,26,10};

        public static FloatTexture ReadMask(string path)
        {
            byte[] file=File.ReadAllBytes(path);
            if(file.Length<8)throw new InvalidDataException(path+": truncated PNG");
            for(int i=0;i<8;i++)if(file[i]!=Signature[i])throw new InvalidDataException(path+": not a PNG");
            int pos=8,w=0,h=0,bit=0,type=0,interlace=0;MemoryStream idat=new MemoryStream();
            while(pos+12<=file.Length)
            {
                int len=checked((int)Be32(file,pos));pos+=4;if(pos+4+len+4>file.Length)throw new InvalidDataException(path+": truncated PNG chunk");
                string name=Encoding.ASCII.GetString(file,pos,4);pos+=4;
                if(name=="IHDR")
                {
                    if(len!=13)throw new InvalidDataException(path+": bad IHDR");
                    w=checked((int)Be32(file,pos));h=checked((int)Be32(file,pos+4));bit=file[pos+8];type=file[pos+9];interlace=file[pos+12];
                }
                else if(name=="IDAT")idat.Write(file,pos,len);
                else if(name=="IEND")break;
                pos+=len+4; // payload + CRC
            }
            if(w<=0||h<=0)throw new InvalidDataException(path+": missing IHDR");
            if(w!=2*h)throw new InvalidDataException(path+": mask must be a 2:1 equirect (width == 2*height), got "+w+"x"+h);
            if(interlace!=0)throw new NotSupportedException(path+": WIP PNG decoder does not yet support Adam7-interlaced masks");
            if(bit!=8&&bit!=16)throw new NotSupportedException(path+": WIP PNG decoder supports 8/16-bit masks only");
            int channels=Channels(type);int bytesPerSample=bit/8;int bpp=channels*bytesPerSample;int rowBytes=checked(w*bpp);
            byte[] raw;
            idat.Position=0;using(MemoryStream decoded=new MemoryStream())using(ZLibStream zs=new ZLibStream(idat,CompressionMode.Decompress,true)){zs.CopyTo(decoded);raw=decoded.ToArray();}
            int expected=checked((rowBytes+1)*h);if(raw.Length<expected)throw new InvalidDataException(path+": truncated PNG image data");
            byte[] scan=new byte[checked(rowBytes*h)];byte[] prev=new byte[rowBytes],cur=new byte[rowBytes];int rp=0;
            for(int y=0;y<h;y++)
            {
                int filter=raw[rp++];Array.Copy(raw,rp,cur,0,rowBytes);rp+=rowBytes;Unfilter(cur,prev,bpp,filter);Array.Copy(cur,0,scan,y*rowBytes,rowBytes);byte[] tmp=prev;prev=cur;cur=tmp;
            }
            FloatTexture tex=new FloatTexture(w,h,1);tex.RepeatX=true;tex.RepeatY=false;
            for(int y=0;y<h;y++)for(int x=0;x<w;x++)tex.Set(x,y,0,PixelLuma(scan,y*rowBytes+x*bpp,bit,type));
            return tex;
        }

        private static int Channels(int type)
        {
            if(type==0)return 1;if(type==2)return 3;if(type==4)return 2;if(type==6)return 4;
            throw new NotSupportedException("WIP PNG decoder supports grayscale/RGB/grayscale-alpha/RGBA masks (color type "+type+" unsupported)");
        }

        private static float PixelLuma(byte[] b,int p,int bit,int type)
        {
            if(bit==8)
            {
                if(type==0||type==4)return b[p]/255.0f;
                int r=b[p],g=b[p+1],bl=b[p+2];int y=(r*4899+g*9617+bl*1868+8192)>>14;return y/255.0f;
            }
            if(type==0||type==4)return U16(b,p)/65535.0f;
            long r16=U16(b,p),g16=U16(b,p+2),b16=U16(b,p+4);long yy=(r16*4899L+g16*9617L+b16*1868L+8192L)>>14;return yy/65535.0f;
        }

        private static int U16(byte[] b,int p){return (b[p]<<8)|b[p+1];}
        private static void Unfilter(byte[] row,byte[] prev,int bpp,int filter)
        {
            if(filter==0)return;
            for(int i=0;i<row.Length;i++)
            {
                int a=i>=bpp?row[i-bpp]:0,b=prev[i],c=i>=bpp?prev[i-bpp]:0,v=row[i];
                if(filter==1)v=(v+a)&255;
                else if(filter==2)v=(v+b)&255;
                else if(filter==3)v=(v+((a+b)>>1))&255;
                else if(filter==4)v=(v+Paeth(a,b,c))&255;
                else throw new InvalidDataException("unsupported PNG filter "+filter);
                row[i]=(byte)v;
            }
        }
        private static int Paeth(int a,int b,int c){int p=a+b-c,pa=Math.Abs(p-a),pb=Math.Abs(p-b),pc=Math.Abs(p-c);return pa<=pb&&pa<=pc?a:(pb<=pc?b:c);}
        private static uint Be32(byte[] b,int p){return ((uint)b[p]<<24)|((uint)b[p+1]<<16)|((uint)b[p+2]<<8)|b[p+3];}
    }
}
