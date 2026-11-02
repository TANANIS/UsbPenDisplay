package dev.usbpendisplay;

import android.app.Activity;
import android.content.Intent;
import android.graphics.*;
import android.os.*;
import android.util.Log;
import android.view.*;
import android.widget.*;
import java.io.*;
import java.net.*;
import java.nio.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.*;
import java.util.zip.Inflater;

public final class MainActivity extends Activity {
    static final String TAG = "UsbPenDisplay";
    static final int MAGIC=0x55504431, FRAME=0x46524d31, ACK=0x41434b31, PEN=0x50454e31;
    static final int MAX_PACKET = 64 * 1024 * 1024;
    final Handler main = new Handler(Looper.getMainLooper());
    final AtomicInteger generation = new AtomicInteger();
    final ArrayBlockingQueue<PenSample> samples = new ArrayBlockingQueue<>(512);
    final AtomicLong eventSequence = new AtomicLong();
    MirrorView mirror;
    CursorOverlay cursor;
    TextView status;
    TextView reconnect;
    volatile Socket videoSocket, inputSocket;
    volatile boolean active, inputReady, diagnostic, diagnosticScheduled;
    volatile long frames, inputCount, lastApplyUs, lastSubmitUs;
    volatile int inputRttUs;
    volatile boolean useGl = true;
    volatile boolean videoReady;
    volatile EglPresenter presenter;
    volatile float lastPressure;
    String token = "";

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        immersive();
        FrameLayout root = new FrameLayout(this);
        mirror = new MirrorView(); root.addView(mirror);
        cursor = new CursorOverlay(); root.addView(cursor);
        status = new TextView(this);
        status.setTextColor(Color.WHITE); status.setTextSize(12);
        status.setPadding(12, 4, 12, 4); status.setBackgroundColor(0xB0182028);
        FrameLayout.LayoutParams lp = new FrameLayout.LayoutParams(-2,-2,Gravity.TOP|Gravity.LEFT);
        root.addView(status,lp);
        reconnect=new TextView(this);reconnect.setTextColor(Color.WHITE);reconnect.setTextSize(18);
        reconnect.setGravity(Gravity.CENTER);reconnect.setPadding(32,32,32,32);reconnect.setBackgroundColor(0xE0182028);
        reconnect.setText("請接上 USB，並在電腦開始連線。");
        reconnect.setClickable(true); // Never pass touches through a disconnected image.
        root.addView(reconnect,new FrameLayout.LayoutParams(-1,-1));setContentView(root);
        status.setOnClickListener(v -> status.setVisibility(View.GONE));
        startSession(getIntent());
    }
    void immersive() {
        getWindow().getDecorView().setSystemUiVisibility(View.SYSTEM_UI_FLAG_FULLSCREEN |
            View.SYSTEM_UI_FLAG_HIDE_NAVIGATION | View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY |
            View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION |
            View.SYSTEM_UI_FLAG_LAYOUT_STABLE);
    }
    @Override public void onWindowFocusChanged(boolean focus) { super.onWindowFocusChanged(focus); if(focus) immersive(); }
    @Override protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        // A launcher tap must not replace a valid in-memory session with an empty Intent.
        if(!intent.hasExtra("session")&&token.length()==48){if(!active)startSession(getIntent());return;}
        setIntent(intent);
        boolean nextGl=!"canvas".equals(intent.getStringExtra("renderer"));
        if(nextGl!=useGl){
            stopSession();FrameLayout root=(FrameLayout)mirror.getParent();MirrorView old=mirror;
            mirror=new MirrorView();root.removeView(old);root.addView(mirror,0,new FrameLayout.LayoutParams(-1,-1));
        }
        startSession(intent);
    }
    @Override protected void onDestroy() { stopSession(); super.onDestroy(); }
    @Override protected void onStop() { stopSession(); super.onStop(); }
    @Override protected void onStart() {
        super.onStart();
        if(mirror!=null && !active && !token.isEmpty()) startSession(getIntent());
    }
    void stopSession() {
        active=false; inputReady=false; videoReady=false; generation.incrementAndGet();
        close(videoSocket); close(inputSocket); samples.clear();
        EglPresenter old=presenter;presenter=null;if(old!=null)old.close();
        main.post(()->{if(!active)showConnectionState();});
    }
    static void close(Socket s) { if(s!=null) try{s.close();}catch(IOException ignored){} }
    void startSession(Intent intent) {
        stopSession(); token=intent.getStringExtra("session"); if(token==null) token="";
        diagnostic=intent.getBooleanExtra("run_test",false); diagnosticScheduled=false;
        useGl=!"canvas".equals(intent.getStringExtra("renderer"));
        frames=0; inputCount=0; status.setVisibility(View.VISIBLE);
        if(token.length()!=48) { setStatus("請接上 USB，並在電腦開始連線。");showConnectionState();return; }
        active=true; int session=generation.get(); String sessionToken=token;
        setStatus("Connecting over USB…");
        new Thread(() -> videoLoop(session, sessionToken),"usb-video").start();
        new Thread(() -> inputLoop(session, sessionToken),"usb-pen").start();
        main.post(new Runnable(){ public void run(){
            if(!current(session)) return;
            showConnectionState();
            setStatus((videoReady&&inputReady?"USB connected  ":"USB reconnecting  ")+mirror.imageWidth+"×"+mirror.imageHeight+"  frames "+frames+
                "  apply "+(lastApplyUs/1000.0)+"ms  pen "+inputCount+"  p "+String.format(java.util.Locale.ROOT,"%.2f",lastPressure));
            main.postDelayed(this,500);
        }});
    }
    boolean current(int session) { return active && generation.get()==session; }
    void showConnectionState(){
        boolean ready=active&&videoReady&&inputReady;
        reconnect.setVisibility(ready?View.GONE:View.VISIBLE);
        if(!ready){
            mirror.penContact=false;cursor.shown=false;cursor.invalidate();
            reconnect.setText(token.length()!=48?"請接上 USB，並在電腦開始連線。":
                "USB 連線暫停，正在等候恢復\n\n請確認線材接穩、平板已解鎖。\n完整畫面同步後將恢復筆輸入。\n若電腦已停止連線，請在電腦重新開始。");
        }
    }
    void setStatus(String s) { main.post(() -> status.setText(s)); }
    Socket connect(int port,String sessionToken) throws IOException {
        Socket s=new Socket();
        try {
            s.setTcpNoDelay(true); s.setSendBufferSize(32768); s.setReceiveBufferSize(65536);
            s.connect(new InetSocketAddress("127.0.0.1",port),2000); s.setSoTimeout(6000);
            DataOutputStream out=new DataOutputStream(new BufferedOutputStream(s.getOutputStream(),4096));
            byte[] key=sessionToken.getBytes(java.nio.charset.StandardCharsets.US_ASCII);
            out.writeInt(MAGIC); out.writeInt(2); out.writeInt(key.length); out.write(key); out.flush();
            return s;
        } catch(IOException e) {close(s);throw e;}
    }
    void videoLoop(int session,String sessionToken) {
        while(current(session)) {
            Socket socket=null;EglPresenter localPresenter=null;
            try {
                socket=connect(37491,sessionToken); if(!current(session)){close(socket);return;} videoSocket=socket;
                DataInputStream in=new DataInputStream(new BufferedInputStream(socket.getInputStream(),65536));
                DataOutputStream out=new DataOutputStream(new BufferedOutputStream(socket.getOutputStream(),4096));
                if(in.readInt()!=MAGIC || in.readInt()!=2) throw new IOException("Protocol mismatch");
                final boolean glMode=useGl,directMode=Lz4Block.NATIVE;
                final MirrorView targetView=mirror;
                int w=in.readInt(), h=in.readInt(), sw=in.readInt(), sh=in.readInt(), tile=in.readInt(),window=in.readInt();
                if(w<1||h<1||w>3840||h>3840||(long)w*h>12_000_000||tile!=64)throw new IOException("Invalid screen dimensions");
                targetView.initialize(w,h);
                videoReady=false;
                socket.setSoTimeout(0); // a static screen legitimately produces no updates
                Log.i(TAG,"VIDEO connected "+w+"x"+h+" desktop="+sw+"x"+sh+" native_pixels="+Lz4Block.NATIVE+" renderer="+(useGl?"GL":"Canvas")+" window="+window);
                final Socket connection=socket;
                final ArrayBlockingQueue<ByteBuffer> rawBuffers=new ArrayBlockingQueue<>(2);
                final int maxRaw=w*h*4+((w+63)/64)*((h+63)/64)*16;
                if(directMode){rawBuffers.add(ByteBuffer.allocateDirect(maxRaw));if(glMode)rawBuffers.add(ByteBuffer.allocateDirect(maxRaw));}
                if(glMode){localPresenter=new EglPresenter(targetView.getHolder(),w,h,(vw,vh)->targetView.updateViewport(vw,vh));presenter=localPresenter;}
                long expected=1;
                while(current(session)) {
                    if(in.readInt()!=FRAME)throw new IOException("Invalid frame marker");
                    long seq=in.readLong(),base=in.readLong(); int flags=in.readInt(), tiles=in.readInt();
                    int rawLength=in.readInt(), compression=in.readInt(), length=in.readInt(), expectedHash=in.readInt();
                    if(seq!=expected++ || tiles<1 || tiles>((w+63)/64)*((h+63)/64) ||
                       rawLength<1 || rawLength>maxRaw || length<1 || length>MAX_PACKET ||
                       (compression!=0&&compression!=1&&compression!=2))throw new IOException("Invalid frame header");
                    if(seq==1 && (flags&1)==0)throw new IOException("Missing initial full frame");
                    if(((flags&1)!=0&&base!=0)||((flags&1)==0&&base!=seq-1))throw new IOException("Invalid delta dependency");
                    long receiveStart=System.nanoTime();
                    byte[] wire=new byte[length]; in.readFully(wire);
                    final int receiveUs=(int)((System.nanoTime()-receiveStart)/1000);
                    long bufferWait=System.nanoTime();
                    final ByteBuffer direct=directMode?rawBuffers.poll(2,TimeUnit.SECONDS):null;
                    if(directMode&&direct==null)throw new IOException("Native frame buffers exhausted");
                    final int bufferWaitUs=(int)((System.nanoTime()-bufferWait)/1000);
                    try{
                    long begin=System.nanoTime(); byte[] raw=null;
                    if(directMode&&compression==2){Lz4Block.nativeDecodeInto(wire,direct,rawLength);}
                    else if(directMode&&compression==0){if(length!=rawLength)throw new IOException("Invalid raw frame");direct.clear();direct.put(wire);}
                    else{
                    if(compression==1) {
                        raw=new byte[rawLength]; Inflater z=new Inflater();
                        try {
                            z.setInput(wire);int pos=0;
                            while(pos<raw.length) {int n=z.inflate(raw,pos,raw.length-pos);if(n==0)break;pos+=n;}
                            if(pos!=raw.length||!z.finished()||z.getRemaining()!=0)throw new IOException("Invalid compressed frame");
                        } finally {z.end();}
                    } else if(compression==2) {raw=Lz4Block.decode(wire,rawLength);}
                    else {raw=wire;if(raw.length!=rawLength)throw new IOException("Invalid raw frame");}
                    if(directMode){direct.clear();direct.put(raw);}
                    }
                    final int decodeUs=(int)((System.nanoTime()-begin)/1000);
                    if(!current(session)||videoSocket!=connection)throw new IOException("Session replaced");
                    if(glMode){
                        final long frameSeq=seq;final int frameFlags=flags;
                        localPresenter.submit(new EglPresenter.Job(seq,base,flags,tiles,direct,rawLength,expectedHash,new EglPresenter.Completion(){
                            public void done(int upload,int draw,int swap,int queue,int verify){
                                try{if(!current(session)||videoSocket!=connection)return;
                                    frameAck(out,frameSeq,receiveUs,decodeUs,upload,draw,swap,queue+bufferWaitUs,verify);
                                    presented(session,frameSeq,frameFlags,decodeUs+upload,draw+swap);
                                }catch(Exception e){Log.w(TAG,"ACK: "+e);close(connection);}finally{rawBuffers.offer(direct);}
                            }
                            public void failed(Exception e){rawBuffers.offer(direct);Log.e(TAG,"GL_FRAME_FAILED: "+e);close(connection);}
                        }));
                    }else{
                        try{
                        long applyStart=System.nanoTime();
                        if(directMode)targetView.applyDirect(direct,rawLength,tiles,(flags&1)!=0);else targetView.apply(raw,tiles,(flags&1)!=0);
                        int uploadUs=(int)((System.nanoTime()-applyStart)/1000),verifyUs=0;
                        if((flags&5)!=0){long check=System.nanoTime();if(targetView.pixelHash()!=expectedHash)throw new IOException("Pixel checksum mismatch");verifyUs=(int)((System.nanoTime()-check)/1000);}
                        int drawUs=(int)targetView.render();
                        if(drawUs==0)throw new IOException("Surface not ready; full synchronization required");
                        frameAck(out,seq,receiveUs,decodeUs,uploadUs,drawUs,0,0,verifyUs);
                        presented(session,seq,flags,decodeUs+uploadUs,drawUs);
                        }finally{if(direct!=null)rawBuffers.offer(direct);}
                    }
                    }catch(Exception e){if(direct!=null&&glMode)rawBuffers.offer(direct);throw e;}
                }
            } catch(Exception e) {
                if(current(session))Log.w(TAG,"VIDEO reconnect: "+e);
            } finally {close(socket);if(localPresenter!=null)localPresenter.close();if(presenter==localPresenter)presenter=null;if(videoSocket==socket){videoSocket=null;videoReady=false;}}
            if(current(session)) sleep(500);
        }
    }
    void frameAck(DataOutputStream out,long seq,int receive,int decode,int upload,int draw,int swap,int queue,int verify)throws IOException{
        synchronized(out){out.writeInt(ACK);out.writeLong(seq);out.writeInt(receive);out.writeInt(decode);out.writeInt(upload);out.writeInt(draw);out.writeInt(swap);out.writeInt(queue);out.writeInt(verify);out.flush();}
    }
    void presented(int session,long seq,int flags,int apply,int submit){
        if(!current(session))return;
        videoReady=true;
        lastApplyUs=apply;lastSubmitUs=submit;frames++;
        if(seq==1)Log.i(TAG,"FIRST_FRAME checksum verified; renderer="+(useGl?"GL":"Canvas"));
        if((flags&2)!=0&&diagnostic&&inputReady){diagnosticScheduled=true;main.postDelayed(()->runDiagnostic(session),100);}
        if(diagnostic&&!diagnosticScheduled&&inputReady){diagnosticScheduled=true;main.postDelayed(()->runDiagnostic(session),700);}
    }
    void inputLoop(int session,String sessionToken) {
        while(current(session)) {
            Socket socket=null;
            try {
                socket=connect(37492,sessionToken); if(!current(session)){close(socket);return;} inputSocket=socket;
                DataOutputStream out=new DataOutputStream(new BufferedOutputStream(socket.getOutputStream(),4096));
                final Socket connection=socket;
                new Thread(()->{
                    try{DataInputStream replies=new DataInputStream(connection.getInputStream());
                        while(current(session)&&inputSocket==connection){
                            if(replies.readInt()!=0x50414b32)throw new IOException("Invalid input echo");
                            long seq=replies.readLong(),sentNs=replies.readLong();int injectUs=replies.readInt();
                            inputRttUs=(int)Math.min(Integer.MAX_VALUE,Math.max(0,(System.nanoTime()-sentNs)/1000));
                        }
                    }catch(Exception e){if(current(session)&&inputSocket==connection){inputReady=false;videoReady=false;samples.clear();close(videoSocket);Log.w(TAG,"Input echo: "+e);close(connection);}}
                },"usb-input-echo").start();
                samples.clear(); inputReady=true; Log.i(TAG,"INPUT connected");
                while(current(session)) {
                    PenSample sample=samples.poll(1500,TimeUnit.MILLISECONDS);
                    if(sample==null||!videoReady)sample=new PenSample(0,0,0,0,0,0,0,2,0);
                    writeSample(out,sample); out.flush();
                }
            } catch(Exception e) {if(current(session))Log.w(TAG,"INPUT reconnect: "+e);}
            finally {close(socket);if(current(session)){inputReady=false;if(inputSocket==socket)inputSocket=null;samples.clear();}}
            if(current(session))sleep(500);
        }
    }
    static void sleep(long ms){try{Thread.sleep(ms);}catch(InterruptedException e){Thread.currentThread().interrupt();}}
    void writeSample(DataOutputStream out,PenSample s)throws IOException {
        out.writeInt(PEN);out.writeInt(s.kind);out.writeFloat(s.x);out.writeFloat(s.y);out.writeFloat(s.pressure);
        out.writeFloat(s.tx);out.writeFloat(s.ty);out.writeInt(s.buttons);out.writeInt(s.tool);
        out.writeLong(eventSequence.incrementAndGet());out.writeLong(s.timeNs);
        out.writeLong(System.nanoTime());out.writeInt(inputRttUs);out.writeInt((int)Math.min(Integer.MAX_VALUE,(System.nanoTime()-s.createdNs)/1000));
    }
    void enqueue(PenSample s) {
        if(!inputReady||!videoReady)return;
        if(!samples.offer(s)) {
            // Only coalesce motion. Never silently discard down/up transitions.
            for(PenSample old:samples)if(old.kind==2||old.kind==4){samples.remove(old);break;}
            if(!samples.offer(s)) {close(inputSocket);Log.w(TAG,"Input overflow; resetting connection to release pen");}
        }
        inputCount++;lastPressure=s.pressure;
    }
    static final class PenSample {
        final int kind,buttons,tool; final float x,y,pressure,tx,ty;final long timeNs;final long createdNs=System.nanoTime();
        PenSample(int kind,float x,float y,float p,float tx,float ty,int buttons,int tool,long time){
            this.kind=kind;this.x=x;this.y=y;pressure=p;this.tx=tx;this.ty=ty;this.buttons=buttons;this.tool=tool;timeNs=time;
        }
    }

    final class CursorOverlay extends View {
        float x,y; boolean shown; final Paint brush=new Paint(Paint.ANTI_ALIAS_FLAG);
        CursorOverlay(){super(MainActivity.this);setClickable(false);setFocusable(false);}
        void update(MotionEvent e){
            x=e.getX(e.getActionIndex());y=e.getY(e.getActionIndex());
            shown=e.getActionMasked()!=MotionEvent.ACTION_HOVER_EXIT&&e.getActionMasked()!=MotionEvent.ACTION_CANCEL;
            invalidate();
        }
        @Override protected void onDraw(Canvas c){
            if(!shown)return;
            brush.setStyle(Paint.Style.STROKE);brush.setStrokeWidth(4);brush.setColor(Color.BLACK);c.drawCircle(x,y,7,brush);
            brush.setStrokeWidth(2);brush.setColor(Color.WHITE);c.drawCircle(x,y,7,brush);
        }
    }

    final class MirrorView extends SurfaceView implements SurfaceHolder.Callback {
        final Object imageLock=new Object();
        final Paint paint=new Paint();
        Bitmap image;volatile int imageWidth,imageHeight;
        volatile float left,top,drawWidth,drawHeight;
        volatile boolean rotated;
        boolean penContact;
        MirrorView(){super(MainActivity.this);getHolder().setFormat(PixelFormat.RGBA_8888);getHolder().addCallback(this);setFocusable(true);paint.setFilterBitmap(false);}
        void initialize(int w,int h){synchronized(imageLock){imageWidth=w;imageHeight=h;image=useGl?null:Bitmap.createBitmap(w,h,Bitmap.Config.ARGB_8888);}}
        void updateViewport(int vw,int vh){
            rotated=vh>vw;int logicalW=rotated?vh:vw,logicalH=rotated?vw:vh;
            float scale=Math.min(logicalW/(float)imageWidth,logicalH/(float)imageHeight);
            drawWidth=imageWidth*scale;drawHeight=imageHeight*scale;left=(logicalW-drawWidth)/2;top=(logicalH-drawHeight)/2;
        }
        void apply(byte[] raw,int count,boolean full)throws IOException {
            if(Lz4Block.NATIVE){synchronized(imageLock){Lz4Block.nativeApply(image,raw,count,full);}return;}
            ByteBuffer b=ByteBuffer.wrap(raw).order(ByteOrder.BIG_ENDIAN);
            boolean[] seen=new boolean[((imageWidth+63)/64)*((imageHeight+63)/64)];
            if(full&&count!=seen.length)throw new IOException("Incomplete full frame");
            synchronized(imageLock) {
                for(int t=0;t<count;t++) {
                    if(b.remaining()<16)throw new IOException("Truncated tile");
                    int x=b.getInt(),y=b.getInt(),w=b.getInt(),h=b.getInt();
                    if(x<0||y<0||x%64!=0||y%64!=0||w!=Math.min(64,imageWidth-x)||h!=Math.min(64,imageHeight-y)||w<1||h<1||
                       x+w>imageWidth||y+h>imageHeight||b.remaining()<w*h*4)throw new IOException("Invalid tile rectangle");
                    int idx=(y/64)*((imageWidth+63)/64)+x/64;
                    if(seen[idx])throw new IOException("Duplicate tile");seen[idx]=true;
                    int[] pixels=new int[w*h];
                    b.order(ByteOrder.LITTLE_ENDIAN);b.asIntBuffer().get(pixels);b.position(b.position()+pixels.length*4);b.order(ByteOrder.BIG_ENDIAN);
                    image.setPixels(pixels,0,w,x,y,w,h);
                }
                if(b.hasRemaining())throw new IOException("Trailing tile data");
            }
        }
        void applyDirect(ByteBuffer raw,int length,int count,boolean full)throws IOException {
            synchronized(imageLock){Lz4Block.nativeApplyDirect(image,raw,length,count,full);}
        }
        int pixelHash(){
            synchronized(imageLock){
                int[] pixels=new int[imageWidth*imageHeight];image.getPixels(pixels,0,imageWidth,0,0,imageWidth,imageHeight);
                int hash=0x811c9dc5;
                for(int p:pixels){hash=(hash^(p&255))*16777619;hash=(hash^((p>>>8)&255))*16777619;
                    hash=(hash^((p>>>16)&255))*16777619;hash=(hash^((p>>>24)&255))*16777619;}
                return hash;
            }
        }
        long render() {
            if(!getHolder().getSurface().isValid())return 0;
            long start=System.nanoTime();Canvas canvas=null;
            try {
                canvas=getHolder().lockHardwareCanvas(); if(canvas==null)return 0;
                canvas.drawColor(Color.BLACK);
                synchronized(imageLock) {
                    if(image==null)return 0;
                    rotated=canvas.getHeight()>canvas.getWidth();
                    int viewWidth=rotated?canvas.getHeight():canvas.getWidth(),viewHeight=rotated?canvas.getWidth():canvas.getHeight();
                    if(rotated){canvas.translate(canvas.getWidth(),0);canvas.rotate(90);}
                    float scale=Math.min(viewWidth/(float)imageWidth,viewHeight/(float)imageHeight);
                    drawWidth=imageWidth*scale;drawHeight=imageHeight*scale;
                    left=(viewWidth-drawWidth)/2;top=(viewHeight-drawHeight)/2;
                    canvas.drawBitmap(image,null,new RectF(left,top,left+drawWidth,top+drawHeight),paint);
                }
            } catch(Exception e){Log.w(TAG,"Render: "+e);return 0;}
            finally{if(canvas!=null)getHolder().unlockCanvasAndPost(canvas);}
            return (System.nanoTime()-start)/1000;
        }
        public void surfaceCreated(SurfaceHolder h){if(!useGl)render();}
        public void surfaceChanged(SurfaceHolder h,int f,int w,int he){if(!useGl)render();}
        public void surfaceDestroyed(SurfaceHolder h){if(MainActivity.this.mirror==this&&useGl)close(videoSocket);}
        @Override public boolean onTouchEvent(MotionEvent e){return handlePen(e);}
        @Override public boolean onHoverEvent(MotionEvent e){return handlePen(e);}
        boolean handlePen(MotionEvent e) {
            if(!inputReady||!videoReady){penContact=false;return true;}
            int a=e.getActionMasked(),i=e.getActionIndex(),tool=e.getToolType(i);
            if(a==MotionEvent.ACTION_MOVE){for(int p=0;p<e.getPointerCount();p++)if(e.getToolType(p)==MotionEvent.TOOL_TYPE_STYLUS||e.getToolType(p)==MotionEvent.TOOL_TYPE_ERASER){i=p;tool=e.getToolType(p);break;}}
            if(tool!=MotionEvent.TOOL_TYPE_STYLUS&&tool!=MotionEvent.TOOL_TYPE_ERASER)return true; // palm rejection; finger gestures are not v0.1
            cursor.update(e);
            if(drawWidth<=0||drawHeight<=0)return true;
            float eventX=rotated?e.getY(i):e.getX(i),eventY=rotated?getWidth()-e.getX(i):e.getY(i);
            boolean outside=eventX<left||eventY<top||eventX>left+drawWidth||eventY>top+drawHeight;
            if(a==MotionEvent.ACTION_DOWN&&outside)return true;
            int kind;
            switch(a){
                case MotionEvent.ACTION_DOWN: case MotionEvent.ACTION_POINTER_DOWN: kind=1;penContact=true;break;
                case MotionEvent.ACTION_MOVE: if(!penContact)return true;kind=2;break;
                case MotionEvent.ACTION_UP: case MotionEvent.ACTION_POINTER_UP: if(!penContact)return true;kind=3;penContact=false;break;
                case MotionEvent.ACTION_CANCEL:kind=6;penContact=false;break;
                case MotionEvent.ACTION_HOVER_EXIT:kind=5;break;
                default:kind=4;break;
            }
            if(kind==2)for(int h=0;h<e.getHistorySize();h++) emit(kind,e.getHistoricalX(i,h),e.getHistoricalY(i,h),e.getHistoricalPressure(i,h),
                e.getHistoricalAxisValue(MotionEvent.AXIS_TILT,i,h),e.getHistoricalAxisValue(MotionEvent.AXIS_ORIENTATION,i,h),e.getButtonState(),tool,e.getHistoricalEventTime(h)*1_000_000);
            emit(kind,e.getX(i),e.getY(i),e.getPressure(i),e.getAxisValue(MotionEvent.AXIS_TILT,i),e.getAxisValue(MotionEvent.AXIS_ORIENTATION,i),e.getButtonState(),tool,e.getEventTime()*1_000_000);
            return true;
        }
        void emit(int kind,float x,float y,float p,float tilt,float orientation,int buttons,int tool,long time){
            if(rotated){float logicalX=y; y=getWidth()-x;x=logicalX;orientation-=(float)(Math.PI/2);}
            float tx=(float)Math.toDegrees(Math.atan(Math.tan(tilt)*Math.sin(orientation)));
            float ty=(float)Math.toDegrees(Math.atan(-Math.tan(tilt)*Math.cos(orientation)));
            enqueue(new PenSample(kind,Math.max(0,Math.min(1,(x-left)/drawWidth)),Math.max(0,Math.min(1,(y-top)/drawHeight)),p,tx,ty,buttons,tool,time));
        }
    }
    void runDiagnostic(int session) {
        if(!current(session)||!diagnostic)return;
        final long start=SystemClock.uptimeMillis();
        for(int n=0;n<=61;n++){
            final int step=n;
            main.postDelayed(()->{
                if(!current(session)||!diagnostic)return;
                int action=step==0?MotionEvent.ACTION_DOWN:step==61?MotionEvent.ACTION_UP:MotionEvent.ACTION_MOVE;
                float t=Math.min(step,60)/60f;
                MotionEvent.PointerProperties prop=new MotionEvent.PointerProperties();prop.id=0;prop.toolType=MotionEvent.TOOL_TYPE_STYLUS;
                MotionEvent.PointerCoords coords=new MotionEvent.PointerCoords();
                coords.x=mirror.left+mirror.drawWidth*(.2f+.6f*t);coords.y=mirror.top+mirror.drawHeight*(.45f+.12f*(float)Math.sin(t*6.28));
                if(mirror.rotated){float px=mirror.getWidth()-coords.y;coords.y=coords.x;coords.x=px;}
                coords.pressure=action==MotionEvent.ACTION_UP?0:.1f+.9f*t;coords.size=.01f;
                MotionEvent e=MotionEvent.obtain(start,SystemClock.uptimeMillis(),action,1,new MotionEvent.PointerProperties[]{prop},new MotionEvent.PointerCoords[]{coords},0,0,1,1,0,0,android.view.InputDevice.SOURCE_STYLUS,0);
                mirror.onTouchEvent(e);e.recycle();
                if(step==61)Log.i(TAG,"DIAGNOSTIC stylus ramp sent via MotionEvent path; not a physical S Pen test");
            },step*20L);
        }
    }
}
