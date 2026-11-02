package dev.usbpendisplay;

import java.io.IOException;

/** Raw LZ4 block decoder. Bounds checked; no JNI or native library is required.
 * Format reference: https://github.com/lz4/lz4/blob/dev/doc/lz4_Block_format.md
 */
final class Lz4Block {
    static final boolean NATIVE;
    static {
        boolean available=false;
        try {System.loadLibrary("usbpen");available=true;}catch(UnsatisfiedLinkError ignored){}
        NATIVE=available;
    }
    private static native byte[] nativeDecode(byte[] source,int size)throws IOException;
    static native void nativeApply(android.graphics.Bitmap bitmap,byte[] raw,int count,boolean full)throws IOException;
    static native void nativeApplyDirect(android.graphics.Bitmap bitmap,java.nio.ByteBuffer raw,int rawLength,int count,boolean full)throws IOException;
    static native void nativeDecodeInto(byte[] source,java.nio.ByteBuffer destination,int size)throws IOException;
    static native void nativeUpload(int texture,java.nio.ByteBuffer raw,int rawLength,int count,int width,int height,boolean full,java.nio.ByteBuffer backing)throws IOException;
    static native int nativeTextureHash(int texture,int width,int height)throws IOException;
    static byte[] decode(byte[] src,int size)throws IOException {
        if(NATIVE)return nativeDecode(src,size);
        if(size<0||size>64*1024*1024)throw new IOException("LZ4 output too large");
        byte[] dst=new byte[size];int ip=0,op=0;
        while(ip<src.length) {
            int token=src[ip++]&255,literals=token>>>4;
            if(literals==15) {
                int more;
                do{if(ip>=src.length)throw bad();more=src[ip++]&255;literals+=more;if(literals>size)throw bad();}while(more==255);
            }
            if(literals>src.length-ip||literals>size-op)throw bad();
            System.arraycopy(src,ip,dst,op,literals);ip+=literals;op+=literals;
            if(ip==src.length){if(op!=size)throw bad();return dst;}
            if(ip+2>src.length)throw bad();
            int offset=(src[ip]&255)|((src[ip+1]&255)<<8);ip+=2;
            if(offset==0||offset>op)throw bad();
            int match=(token&15)+4;
            if((token&15)==15){
                int more;
                do{if(ip>=src.length)throw bad();more=src[ip++]&255;match+=more;if(match>size)throw bad();}while(more==255);
            }
            if(match>size-op)throw bad();
            int base=op-offset,end=op+match;
            // Copy already-available bytes, doubling the available pattern each
            // time. This handles overlapping runs without one Java copy/pixel.
            while(op<end){int n=Math.min(op-base,end-op);System.arraycopy(dst,base,dst,op,n);op+=n;}
        }
        if(op!=size)throw bad();return dst;
    }
    static IOException bad(){return new IOException("Malformed LZ4 block");}
}
