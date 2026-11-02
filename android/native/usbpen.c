#include <jni.h>
#include <android/bitmap.h>
#include <stdint.h>
#include <string.h>
#include <stdlib.h>
#include <GLES2/gl2.h>
#include "vendor/lz4.h"

static void bad(JNIEnv *env, const char *message) {
    jclass error = (*env)->FindClass(env, "java/io/IOException");
    if (error) (*env)->ThrowNew(env, error, message);
}

JNIEXPORT jbyteArray JNICALL Java_dev_usbpendisplay_Lz4Block_nativeDecode
  (JNIEnv *env, jclass cls, jbyteArray source, jint size) {
    (void)cls;
    if (size < 0 || size > 64*1024*1024) { bad(env, "LZ4 output too large"); return NULL; }
    jsize sourceSize = (*env)->GetArrayLength(env, source);
    jbyteArray output = (*env)->NewByteArray(env, size);
    if (!output) return NULL;
    jbyte *src = (*env)->GetPrimitiveArrayCritical(env, source, NULL);
    if (!src) return NULL;
    jbyte *dst = (*env)->GetPrimitiveArrayCritical(env, output, NULL);
    if (!dst) { (*env)->ReleasePrimitiveArrayCritical(env, source, src, JNI_ABORT); return NULL; }
    int decoded = LZ4_decompress_safe((char *)src, (char *)dst, sourceSize, size);
    (*env)->ReleasePrimitiveArrayCritical(env, output, dst, 0);
    (*env)->ReleasePrimitiveArrayCritical(env, source, src, JNI_ABORT);
    if (decoded != size) { bad(env, "Malformed LZ4 block"); return NULL; }
    return output;
}

static uint32_t be32(const unsigned char *p) {
    return ((uint32_t)p[0]<<24)|((uint32_t)p[1]<<16)|((uint32_t)p[2]<<8)|p[3];
}

JNIEXPORT void JNICALL Java_dev_usbpendisplay_Lz4Block_nativeApply
  (JNIEnv *env, jclass cls, jobject bitmap, jbyteArray source, jint count, jboolean full) {
    (void)cls;
    AndroidBitmapInfo info;
    if (AndroidBitmap_getInfo(env, bitmap, &info) != ANDROID_BITMAP_RESULT_SUCCESS ||
        info.format != ANDROID_BITMAP_FORMAT_RGBA_8888 || info.width > 3840 || info.height > 3840) {
        bad(env, "Unsupported bitmap"); return;
    }
    size_t cells = ((info.width+63)/64)*((info.height+63)/64);
    if (count < 1 || (size_t)count > cells || cells > 3600 || (full && (size_t)count != cells)) {
        bad(env, "Invalid tile count"); return;
    }
    unsigned char seen[3600] = {0};
    void *pixels = NULL;
    if (AndroidBitmap_lockPixels(env, bitmap, &pixels) != ANDROID_BITMAP_RESULT_SUCCESS) { bad(env,"Bitmap lock failed"); return; }
    size_t length = (size_t)(*env)->GetArrayLength(env, source);
    unsigned char *raw = (*env)->GetPrimitiveArrayCritical(env, source, NULL);
    if (!raw) { AndroidBitmap_unlockPixels(env, bitmap); return; }
    size_t pos=0; int valid=1;
    for (int t=0;t<count && valid;t++) {
        if (length-pos < 16) {valid=0;break;}
        uint32_t x=be32(raw+pos),y=be32(raw+pos+4),w=be32(raw+pos+8),h=be32(raw+pos+12); pos+=16;
        if (x>=info.width||y>=info.height||x%64||y%64||w==0||h==0||w>64||h>64||
            x+w>info.width||y+h>info.height||w!=(info.width-x<64?info.width-x:64)||
            h!=(info.height-y<64?info.height-y:64)||length-pos<(size_t)w*h*4) {valid=0;break;}
        size_t cell=(y/64)*((info.width+63)/64)+x/64;
        if (seen[cell]) {valid=0;break;} seen[cell]=1;
        for (uint32_t row=0;row<h;row++) {
            const uint32_t *src=(const uint32_t *)(raw+pos+(size_t)row*w*4);
            uint32_t *dst=(uint32_t *)((unsigned char *)pixels+(size_t)(y+row)*info.stride)+x;
            // Wire BGRA -> Android RGBA. Clang vectorizes this loop on ARM64.
            for (uint32_t col=0;col<w;col++) {
                uint32_t p=src[col];dst[col]=(p&0xff00ff00)|((p&0xff)<<16)|((p>>16)&0xff);
            }
        }
        pos+=(size_t)w*h*4;
    }
    if (pos!=length) valid=0;
    (*env)->ReleasePrimitiveArrayCritical(env, source, raw, JNI_ABORT);
    AndroidBitmap_unlockPixels(env, bitmap);
    if (!valid) bad(env,"Malformed pixel tile packet");
}

JNIEXPORT void JNICALL Java_dev_usbpendisplay_Lz4Block_nativeUpload
  (JNIEnv *env,jclass cls,jint texture,jobject source,jint rawLength,jint count,jint width,jint height,jboolean full,jobject backing) {
    (void)cls;
    if(width<1||height<1||width>3840||height>3840){bad(env,"Invalid texture dimensions");return;}
    size_t cells=((width+63)/64)*((height+63)/64);
    if(count<1||(size_t)count>cells||cells>3600||(full&&(size_t)count!=cells)){bad(env,"Invalid texture tile count");return;}
    unsigned char *image=(*env)->GetDirectBufferAddress(env,backing);
    if(!image||(*env)->GetDirectBufferCapacity(env,backing)<(jlong)width*height*4){bad(env,"Invalid upload backing");return;}
    unsigned char seen[3600]={0};
    typedef struct {uint32_t x,y,w,h;size_t offset;} Tile;
    Tile *tiles=malloc((size_t)count*sizeof(Tile));if(!tiles){bad(env,"Tile allocation failed");return;}
    size_t length=(size_t)rawLength,pos=0,dirtyPixels=0;
    unsigned char *raw=(*env)->GetDirectBufferAddress(env,source);
    if(!raw||rawLength<0||(*env)->GetDirectBufferCapacity(env,source)<rawLength){free(tiles);bad(env,"Invalid raw frame buffer");return;}
    int valid=1;uint32_t minx=width,miny=height,maxx=0,maxy=0;
    for(int t=0;t<count&&valid;t++){
        if(length-pos<16){valid=0;break;}
        uint32_t x=be32(raw+pos),y=be32(raw+pos+4),w=be32(raw+pos+8),h=be32(raw+pos+12);pos+=16;
        if(x>=(uint32_t)width||y>=(uint32_t)height||x%64||y%64||w==0||h==0||w>64||h>64||
           x+w>(uint32_t)width||y+h>(uint32_t)height||w!=((uint32_t)width-x<64?(uint32_t)width-x:64)||
           h!=((uint32_t)height-y<64?(uint32_t)height-y:64)||length-pos<(size_t)w*h*4){valid=0;break;}
        size_t cell=(y/64)*((width+63)/64)+x/64;if(seen[cell]){valid=0;break;}seen[cell]=1;
        tiles[t]=(Tile){x,y,w,h,pos};
        for(uint32_t row=0;row<h;row++)memcpy(image+((size_t)(y+row)*width+x)*4,raw+pos+(size_t)row*w*4,w*4);
        if(x<minx)minx=x;if(y<miny)miny=y;if(x+w>maxx)maxx=x+w;if(y+h>maxy)maxy=y+h;
        dirtyPixels+=(size_t)w*h;pos+=(size_t)w*h*4;
    }
    if(pos!=length)valid=0;
    if(valid){
        glBindTexture(GL_TEXTURE_2D,(GLuint)texture);glPixelStorei(GL_UNPACK_ALIGNMENT,1);
        uint32_t bw=maxx-minx,bh=maxy-miny;
        if(count>=16&&dirtyPixels*2>=(size_t)bw*bh){
            if(bw==(uint32_t)width){
                glTexSubImage2D(GL_TEXTURE_2D,0,0,miny,bw,bh,GL_RGBA,GL_UNSIGNED_BYTE,image+(size_t)miny*width*4);
            }else{
                unsigned char *packed=malloc((size_t)bw*bh*4);
                if(!packed)valid=0;
                else{
                    for(uint32_t row=0;row<bh;row++)memcpy(packed+(size_t)row*bw*4,image+((size_t)(miny+row)*width+minx)*4,bw*4);
                    glTexSubImage2D(GL_TEXTURE_2D,0,minx,miny,bw,bh,GL_RGBA,GL_UNSIGNED_BYTE,packed);free(packed);
                }
            }
        }else{
            for(int t=0;t<count;t++){Tile tile=tiles[t];glTexSubImage2D(GL_TEXTURE_2D,0,tile.x,tile.y,tile.w,tile.h,GL_RGBA,GL_UNSIGNED_BYTE,raw+tile.offset);}
        }
    }
    free(tiles);
    if(!valid)bad(env,"Malformed texture tile packet or allocation failure");
}

JNIEXPORT void JNICALL Java_dev_usbpendisplay_Lz4Block_nativeDecodeInto
  (JNIEnv *env,jclass cls,jbyteArray source,jobject destination,jint size) {
    (void)cls;
    unsigned char *dst=(*env)->GetDirectBufferAddress(env,destination);
    if(!dst||size<0||size>64*1024*1024||(*env)->GetDirectBufferCapacity(env,destination)<size){bad(env,"Invalid decode buffer");return;}
    jsize length=(*env)->GetArrayLength(env,source);
    jbyte *src=(*env)->GetPrimitiveArrayCritical(env,source,NULL);if(!src)return;
    int decoded=LZ4_decompress_safe((char*)src,(char*)dst,length,size);
    (*env)->ReleasePrimitiveArrayCritical(env,source,src,JNI_ABORT);
    if(decoded!=size)bad(env,"Malformed LZ4 block");
}
JNIEXPORT jint JNICALL Java_dev_usbpendisplay_Lz4Block_nativeTextureHash
  (JNIEnv *env,jclass cls,jint texture,jint width,jint height) {
    (void)cls;
    if(width<1||height<1||width>3840||height>3840){bad(env,"Invalid hash dimensions");return 0;}
    size_t size=(size_t)width*height*4;unsigned char *pixels=malloc(size);
    if(!pixels){bad(env,"Texture readback allocation failed");return 0;}
    GLint previous=0;glGetIntegerv(GL_FRAMEBUFFER_BINDING,&previous);
    GLuint framebuffer=0;glGenFramebuffers(1,&framebuffer);glBindFramebuffer(GL_FRAMEBUFFER,framebuffer);
    glFramebufferTexture2D(GL_FRAMEBUFFER,GL_COLOR_ATTACHMENT0,GL_TEXTURE_2D,(GLuint)texture,0);
    uint32_t hash=2166136261u;
    if(glCheckFramebufferStatus(GL_FRAMEBUFFER)!=GL_FRAMEBUFFER_COMPLETE){
        glBindFramebuffer(GL_FRAMEBUFFER,(GLuint)previous);glDeleteFramebuffers(1,&framebuffer);free(pixels);bad(env,"Texture readback framebuffer incomplete");return 0;
    }
    glPixelStorei(GL_PACK_ALIGNMENT,1);glReadPixels(0,0,width,height,GL_RGBA,GL_UNSIGNED_BYTE,pixels);
    GLenum error=glGetError();
    glBindFramebuffer(GL_FRAMEBUFFER,(GLuint)previous);glDeleteFramebuffers(1,&framebuffer);
    for(size_t i=0;i<size;i++)hash=(hash^pixels[i])*16777619u;
    free(pixels);if(error!=GL_NO_ERROR){bad(env,"Texture readback GL error");return 0;}return (jint)hash;
}

JNIEXPORT void JNICALL Java_dev_usbpendisplay_Lz4Block_nativeApplyDirect
  (JNIEnv *env, jclass cls, jobject bitmap, jobject source, jint rawLength, jint count, jboolean full) {
    (void)cls;
    AndroidBitmapInfo info;
    if (AndroidBitmap_getInfo(env, bitmap, &info) != ANDROID_BITMAP_RESULT_SUCCESS ||
        info.format != ANDROID_BITMAP_FORMAT_RGBA_8888 || info.width > 3840 || info.height > 3840) {
        bad(env, "Unsupported bitmap"); return;
    }
    size_t cells = ((info.width+63)/64)*((info.height+63)/64);
    if (count < 1 || (size_t)count > cells || cells > 3600 || (full && (size_t)count != cells)) {
        bad(env, "Invalid tile count"); return;
    }
    unsigned char seen[3600] = {0};
    void *pixels = NULL;
    if (AndroidBitmap_lockPixels(env, bitmap, &pixels) != ANDROID_BITMAP_RESULT_SUCCESS) { bad(env,"Bitmap lock failed"); return; }
    size_t length = (size_t)rawLength;
    unsigned char *raw = (*env)->GetDirectBufferAddress(env, source);
    if (!raw || rawLength < 0 || (*env)->GetDirectBufferCapacity(env, source) < rawLength) { AndroidBitmap_unlockPixels(env, bitmap); bad(env,"Invalid direct tile buffer"); return; }
    size_t pos=0; int valid=1;
    for (int t=0;t<count && valid;t++) {
        if (length-pos < 16) {valid=0;break;}
        uint32_t x=be32(raw+pos),y=be32(raw+pos+4),w=be32(raw+pos+8),h=be32(raw+pos+12); pos+=16;
        if (x>=info.width||y>=info.height||x%64||y%64||w==0||h==0||w>64||h>64||
            x+w>info.width||y+h>info.height||w!=(info.width-x<64?info.width-x:64)||
            h!=(info.height-y<64?info.height-y:64)||length-pos<(size_t)w*h*4) {valid=0;break;}
        size_t cell=(y/64)*((info.width+63)/64)+x/64;
        if (seen[cell]) {valid=0;break;} seen[cell]=1;
        for (uint32_t row=0;row<h;row++) {
            const uint32_t *src=(const uint32_t *)(raw+pos+(size_t)row*w*4);
            uint32_t *dst=(uint32_t *)((unsigned char *)pixels+(size_t)(y+row)*info.stride)+x;
            // Wire BGRA -> Android RGBA. Clang vectorizes this loop on ARM64.
            for (uint32_t col=0;col<w;col++) {
                uint32_t p=src[col];dst[col]=(p&0xff00ff00)|((p&0xff)<<16)|((p>>16)&0xff);
            }
        }
        pos+=(size_t)w*h*4;
    }
    if (pos!=length) valid=0;

    AndroidBitmap_unlockPixels(env, bitmap);
    if (!valid) bad(env,"Malformed pixel tile packet");
}
