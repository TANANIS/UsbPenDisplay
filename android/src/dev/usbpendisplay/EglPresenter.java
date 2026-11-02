package dev.usbpendisplay;

import android.opengl.*;
import android.view.SurfaceHolder;
import android.util.Log;
import java.io.IOException;
import java.nio.*;
import java.util.concurrent.*;

/** One active upload/draw plus at most one waiting job. ACK follows eglSwapBuffers,
 * which is a submission boundary, not a physical panel presentation timestamp. */
final class EglPresenter implements AutoCloseable {
    interface Viewport { void changed(int width,int height); }
    interface Completion {
        void done(int uploadUs,int drawUs,int swapUs,int queueUs,int verifyUs);
        void failed(Exception e);
    }
    static final class Job {
        final long sequence,base,queuedAt=System.nanoTime();
        final int flags,tiles,hash,rawLength; final ByteBuffer raw;final Completion completion;
        Job(long seq,long base,int flags,int tiles,ByteBuffer raw,int rawLength,int hash,Completion c){
            sequence=seq;this.base=base;this.flags=flags;this.tiles=tiles;this.raw=raw;this.rawLength=rawLength;this.hash=hash;completion=c;
        }
    }
    final ArrayBlockingQueue<Job> jobs=new ArrayBlockingQueue<>(1);
    final SurfaceHolder holder;final int width,height;final Viewport viewport;
    final Thread worker;volatile boolean running=true;
    EGLDisplay display=EGL14.EGL_NO_DISPLAY;
    EGLContext context=EGL14.EGL_NO_CONTEXT;
    EGLSurface surface=EGL14.EGL_NO_SURFACE;
    int texture,program,position,uv,rotation;
    long lastSequence;
    FloatBuffer vertices;
    ByteBuffer backing;
    EglPresenter(SurfaceHolder holder,int width,int height,Viewport viewport){
        this.holder=holder;this.width=width;this.height=height;this.viewport=viewport;
        worker=new Thread(()->loop(),"usb-egl-present");worker.start();
    }
    void submit(Job job)throws IOException,InterruptedException {
        if(!running||!jobs.offer(job,2,TimeUnit.SECONDS))throw new IOException("Render queue unavailable");
    }
    void loop(){
        try{
            while(running){
                Job job=jobs.poll(100,TimeUnit.MILLISECONDS);if(job==null)continue;
                try{
                    long deadline=System.nanoTime()+2_000_000_000L;
                    while(running&&!holder.getSurface().isValid()&&System.nanoTime()<deadline)Thread.sleep(10);
                    if(!running)break;
                    if(display==EGL14.EGL_NO_DISPLAY)initialize();
                    if(job.sequence!=lastSequence+1||((job.flags&1)==0&&job.base!=lastSequence))throw new IOException("GL delta dependency mismatch");
                    if(lastSequence==0&&(job.flags&1)==0)throw new IOException("GL texture requires a full frame");
                    int queueUs=micros(job.queuedAt);
                    long started=System.nanoTime();
                    Lz4Block.nativeUpload(texture,job.raw,job.rawLength,job.tiles,width,height,(job.flags&1)!=0,backing);
                    checkGl("tile upload");int uploadUs=micros(started),verifyUs=0;
                    if((job.flags&5)!=0){
                        started=System.nanoTime();int actual=Lz4Block.nativeTextureHash(texture,width,height);
                        verifyUs=micros(started);
                        if(actual!=job.hash)throw new IOException("GPU texture pixel checksum mismatch seq="+job.sequence);
                    }
                    started=System.nanoTime();draw();int drawUs=micros(started);
                    started=System.nanoTime();
                    if(!EGL14.eglSwapBuffers(display,surface))throw eglError("swap");
                    int swapUs=micros(started);lastSequence=job.sequence;
                    job.completion.done(uploadUs,drawUs,swapUs,queueUs,verifyUs);
                }catch(Exception e){job.completion.failed(e);running=false;}
            }
        }catch(InterruptedException expected){Thread.currentThread().interrupt();}
        finally{
            running=false;
            Job pending;while((pending=jobs.poll())!=null)pending.completion.failed(new IOException("Renderer closed"));
            disposeEgl();
        }
    }
    void initialize()throws IOException{
        if(!Lz4Block.NATIVE)throw new IOException("OpenGL native path unavailable; use Canvas mode");
        display=EGL14.eglGetDisplay(EGL14.EGL_DEFAULT_DISPLAY);
        int[] version=new int[2];
        if(display==EGL14.EGL_NO_DISPLAY||!EGL14.eglInitialize(display,version,0,version,1))throw eglError("initialize");
        int[] attributes={EGL14.EGL_RENDERABLE_TYPE,EGL14.EGL_OPENGL_ES2_BIT,EGL14.EGL_SURFACE_TYPE,EGL14.EGL_WINDOW_BIT,
            EGL14.EGL_RED_SIZE,8,EGL14.EGL_GREEN_SIZE,8,EGL14.EGL_BLUE_SIZE,8,EGL14.EGL_ALPHA_SIZE,8,EGL14.EGL_NONE};
        EGLConfig[] configs=new EGLConfig[1];int[] count=new int[1];
        if(!EGL14.eglChooseConfig(display,attributes,0,configs,0,1,count,0)||count[0]<1)throw eglError("choose config");
        context=EGL14.eglCreateContext(display,configs[0],EGL14.EGL_NO_CONTEXT,new int[]{EGL14.EGL_CONTEXT_CLIENT_VERSION,2,EGL14.EGL_NONE},0);
        surface=EGL14.eglCreateWindowSurface(display,configs[0],holder.getSurface(),new int[]{EGL14.EGL_NONE},0);
        if(context==EGL14.EGL_NO_CONTEXT||surface==EGL14.EGL_NO_SURFACE||!EGL14.eglMakeCurrent(display,surface,surface,context))throw eglError("make current");
        int[] minSwap=new int[1];EGL14.eglGetConfigAttrib(display,configs[0],EGL14.EGL_MIN_SWAP_INTERVAL,minSwap,0);
        EGL14.eglSwapInterval(display,minSwap[0]);
        String vertex="attribute vec2 aPosition;attribute vec2 aUv;uniform float uRotate;varying highp vec2 vUv;void main(){vec2 p=aPosition;if(uRotate>0.5)p=vec2(p.y,-p.x);gl_Position=vec4(p,0.,1.);vUv=aUv;}";
        String fragment="precision highp float;uniform sampler2D uTexture;varying highp vec2 vUv;void main(){gl_FragColor=texture2D(uTexture,vUv).bgra;}";
        int vs=shader(GLES20.GL_VERTEX_SHADER,vertex),fs=shader(GLES20.GL_FRAGMENT_SHADER,fragment);
        program=GLES20.glCreateProgram();GLES20.glAttachShader(program,vs);GLES20.glAttachShader(program,fs);GLES20.glLinkProgram(program);
        int[] ok=new int[1];GLES20.glGetProgramiv(program,GLES20.GL_LINK_STATUS,ok,0);
        if(ok[0]==0)throw new IOException("GL link: "+GLES20.glGetProgramInfoLog(program));
        GLES20.glDeleteShader(vs);GLES20.glDeleteShader(fs);
        position=GLES20.glGetAttribLocation(program,"aPosition");uv=GLES20.glGetAttribLocation(program,"aUv");rotation=GLES20.glGetUniformLocation(program,"uRotate");
        vertices=ByteBuffer.allocateDirect(64).order(ByteOrder.nativeOrder()).asFloatBuffer();
        vertices.put(new float[]{-1,1,0,0,-1,-1,0,1,1,1,1,0,1,-1,1,1}).position(0);
        int[] textures=new int[1];GLES20.glGenTextures(1,textures,0);texture=textures[0];
        GLES20.glBindTexture(GLES20.GL_TEXTURE_2D,texture);
        GLES20.glTexParameteri(GLES20.GL_TEXTURE_2D,GLES20.GL_TEXTURE_MIN_FILTER,GLES20.GL_NEAREST);
        GLES20.glTexParameteri(GLES20.GL_TEXTURE_2D,GLES20.GL_TEXTURE_MAG_FILTER,GLES20.GL_NEAREST);
        GLES20.glTexParameteri(GLES20.GL_TEXTURE_2D,GLES20.GL_TEXTURE_WRAP_S,GLES20.GL_CLAMP_TO_EDGE);
        GLES20.glTexParameteri(GLES20.GL_TEXTURE_2D,GLES20.GL_TEXTURE_WRAP_T,GLES20.GL_CLAMP_TO_EDGE);
        GLES20.glTexImage2D(GLES20.GL_TEXTURE_2D,0,GLES20.GL_RGBA,width,height,0,GLES20.GL_RGBA,GLES20.GL_UNSIGNED_BYTE,null);
        backing=ByteBuffer.allocateDirect(width*height*4);
        checkGl("initialize");
        Log.i("UsbPenDisplay","GL_READY renderer="+GLES20.glGetString(GLES20.GL_RENDERER)+" min_swap_interval="+minSwap[0]);
    }
    int shader(int type,String source)throws IOException{
        int s=GLES20.glCreateShader(type);GLES20.glShaderSource(s,source);GLES20.glCompileShader(s);
        int[] ok=new int[1];GLES20.glGetShaderiv(s,GLES20.GL_COMPILE_STATUS,ok,0);
        if(ok[0]==0)throw new IOException("GL shader: "+GLES20.glGetShaderInfoLog(s));return s;
    }
    void draw()throws IOException{
        int[] result=new int[1];
        EGL14.eglQuerySurface(display,surface,EGL14.EGL_WIDTH,result,0);int vw=result[0];
        EGL14.eglQuerySurface(display,surface,EGL14.EGL_HEIGHT,result,0);int vh=result[0];
        viewport.changed(vw,vh);boolean rotated=vh>vw;
        float scale=Math.min((rotated?vh:vw)/(float)width,(rotated?vw:vh)/(float)height);
        int drawW=Math.round((rotated?height:width)*scale),drawH=Math.round((rotated?width:height)*scale);
        GLES20.glViewport(0,0,vw,vh);GLES20.glClearColor(0,0,0,1);GLES20.glClear(GLES20.GL_COLOR_BUFFER_BIT);
        GLES20.glViewport((vw-drawW)/2,(vh-drawH)/2,drawW,drawH);
        GLES20.glUseProgram(program);GLES20.glUniform1f(rotation,rotated?1:0);
        GLES20.glActiveTexture(GLES20.GL_TEXTURE0);GLES20.glBindTexture(GLES20.GL_TEXTURE_2D,texture);
        vertices.position(0);GLES20.glVertexAttribPointer(position,2,GLES20.GL_FLOAT,false,16,vertices);GLES20.glEnableVertexAttribArray(position);
        vertices.position(2);GLES20.glVertexAttribPointer(uv,2,GLES20.GL_FLOAT,false,16,vertices);GLES20.glEnableVertexAttribArray(uv);
        GLES20.glDrawArrays(GLES20.GL_TRIANGLE_STRIP,0,4);checkGl("draw");
    }
    void checkGl(String operation)throws IOException{int error=GLES20.glGetError();if(error!=GLES20.GL_NO_ERROR)throw new IOException(operation+" GL error="+error);}
    IOException eglError(String operation){return new IOException(operation+" EGL error="+Integer.toHexString(EGL14.eglGetError()));}
    static int micros(long start){return (int)Math.min(Integer.MAX_VALUE,(System.nanoTime()-start)/1000);}
    void disposeEgl(){
        if(display!=EGL14.EGL_NO_DISPLAY){
            if(context!=EGL14.EGL_NO_CONTEXT){
                if(texture!=0)GLES20.glDeleteTextures(1,new int[]{texture},0);
                if(program!=0)GLES20.glDeleteProgram(program);
            }
            EGL14.eglMakeCurrent(display,EGL14.EGL_NO_SURFACE,EGL14.EGL_NO_SURFACE,EGL14.EGL_NO_CONTEXT);
            if(surface!=EGL14.EGL_NO_SURFACE)EGL14.eglDestroySurface(display,surface);
            if(context!=EGL14.EGL_NO_CONTEXT)EGL14.eglDestroyContext(display,context);
            EGL14.eglTerminate(display);
        }
    }
    public void close(){running=false;worker.interrupt();if(Thread.currentThread()!=worker)try{worker.join(1500);}catch(InterruptedException e){Thread.currentThread().interrupt();}}
}
