package com.visorip.tv;

import android.app.*;
import android.content.*;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.StateListDrawable;
import android.net.Uri;
import android.net.wifi.WifiManager;
import android.os.*;
import android.text.InputType;
import android.view.*;
import android.view.inputmethod.InputMethodManager;
import android.widget.*;
import org.json.*;
import org.videolan.libvlc.LibVLC;
import org.videolan.libvlc.Media;
import org.videolan.libvlc.MediaPlayer;
import org.videolan.libvlc.util.VLCVideoLayout;
import java.net.URI;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicBoolean;

public final class MainActivity extends Activity {
    static final int BG=Color.rgb(14,23,37),CARD=Color.rgb(23,35,52),ACCENT=Color.rgb(58,217,185),MUTED=Color.rgb(155,175,195);
    final Handler ui=new Handler(Looper.getMainLooper());
    final ExecutorService network=Executors.newFixedThreadPool(3),cleanup=Executors.newFixedThreadPool(4);
    final TreeMap<String,NetSupport.Camera> cameras=new TreeMap<>();
    final Tile[] tiles=new Tile[16];
    CameraAlerts alerts;
    LibVLC vlc;FrameLayout grid;TextView status,target;Button search,layoutButton,fullscreenButton;LinearLayout root,header;HorizontalScrollView scroll;boolean cameraFullscreen;
    String range="172.16.0",username="",password="";boolean software=false,foreground,destroyed,resolving,muted=true;
    boolean rememberedAccess;String accessWarning="";
    int selected=0,count=4,lastCount=4;AtomicBoolean scanCancel=new AtomicBoolean(true),resolveCancel=new AtomicBoolean();
    AtomicBoolean ptzCancel;PtzDialog activePtz;int cleanupCount;WifiManager.MulticastLock multicast;

    @Override public void onCreate(Bundle saved){super.onCreate(saved);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        getWindow().getDecorView().setSystemUiVisibility(View.SYSTEM_UI_FLAG_FULLSCREEN|View.SYSTEM_UI_FLAG_HIDE_NAVIGATION|View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY);
        range=getPreferences(0).getString("range","172.16.0");software=getPreferences(0).getBoolean("software",false);
        count=getPreferences(0).getInt("layout",4);if(count!=1&&count!=4&&count!=6&&count!=9&&count!=16)count=4;
        try{NetSupport.Login access=AccessVault.load(getPreferences(0));if(access!=null){username=access.user;password=access.password;rememberedAccess=true;}}catch(Exception e){accessWarning="No se pudo recuperar el acceso guardado. Ingresalo de nuevo en Acceso / red.";}
        selected=getPreferences(0).getInt("selected",0);if(selected<0||selected>=16||(count!=1&&selected>=count))selected=0;
        lastCount=getPreferences(0).getInt("lastCount",4);if(lastCount!=4&&lastCount!=6&&lastCount!=9&&lastCount!=16)lastCount=4;
        try{vlc=new LibVLC(this,new ArrayList<>(Arrays.asList("--rtsp-tcp","--network-caching=700","--no-video-title-show","--no-osd","--quiet")));}
        catch(Throwable e){new AlertDialog.Builder(this).setTitle("Motor de video").setMessage("No se pudo iniciar VLC en este dispositivo.").setPositiveButton("Cerrar",(d,w)->finish()).setCancelable(false).show();return;}
        root=new LinearLayout(this);root.setOrientation(LinearLayout.VERTICAL);root.setPadding(dp(22),dp(14),dp(22),dp(12));root.setBackgroundColor(BG);
        header=new LinearLayout(this);TextView title=label("VISOR IP TV",22,Color.WHITE);title.setTypeface(null,Typeface.BOLD);header.addView(title,new LinearLayout.LayoutParams(0,dp(36),1));target=label("VISOR 1  ·  EN VIVO",13,ACCENT);target.setGravity(Gravity.RIGHT|Gravity.CENTER_VERTICAL);header.addView(target,new LinearLayout.LayoutParams(dp(260),dp(36)));root.addView(header);
        scroll=new HorizontalScrollView(this);scroll.setHorizontalScrollBarEnabled(false);LinearLayout bar=new LinearLayout(this);bar.setOrientation(LinearLayout.HORIZONTAL);
        bar.addView(button("Acceso / red",v->settings()));search=button("Buscar",v->scan());bar.addView(search);layoutButton=button("Vista: "+count,v->layouts());bar.addView(layoutButton);fullscreenButton=button("Pantalla completa",v->setCameraFullscreen(true));bar.addView(fullscreenButton);bar.addView(button("Cámaras",v->chooseCamera()));bar.addView(button("PTZ",v->ptz()));bar.addView(button("Alertas",v->alerts.settings()));bar.addView(button("Detener",v->{Tile t=tiles[selected];t.resume=false;save();stop(t,null);t.hint.setText("Detenido · OK para elegir cámara");}));
        bar.addView(button("Sonido",v->{muted=!muted;for(Tile t:tiles)if(t.player!=null)t.player.setVolume(muted?0:100);message(muted?"Sonido desactivado":"Sonido activado");}));bar.addView(button("Ayuda",v->help()));scroll.addView(bar);root.addView(scroll,new LinearLayout.LayoutParams(-1,dp(47)));
        grid=new FrameLayout(this);root.addView(grid,new LinearLayout.LayoutParams(-1,0,1));
        status=label("Buscá cámaras, ingresá las credenciales en Acceso / red y elegí un visor.",12,MUTED);status.setMaxLines(2);root.addView(status,new LinearLayout.LayoutParams(-1,dp(40)));
        for(int i=0;i<16;i++){tiles[i]=new Tile(i);grid.addView(tiles[i]);}
        grid.addOnLayoutChangeListener((v,l,t,r,b,ol,ot,or,ob)->{if(r-l!=or-ol||b-t!=ob-ot)arrange();});setContentView(root);alerts=new CameraAlerts(this);restore();arrange();select(selected);if(!accessWarning.isEmpty())message(accessWarning);if(getPreferences(0).getBoolean("fullscreen",false))setCameraFullscreen(true);
        if(!cameraFullscreen)search.requestFocus();ui.postDelayed(()->{if(!destroyed)scan();},700);
    }
    int dp(float n){return (int)(n*getResources().getDisplayMetrics().density+0.5f);}
    TextView label(String text,int size,int color){TextView v=new TextView(this);v.setText(text);v.setTextSize(size);v.setTextColor(color);v.setGravity(Gravity.CENTER_VERTICAL);return v;}
    GradientDrawable shape(int color,int stroke){GradientDrawable d=new GradientDrawable();d.setColor(color);d.setCornerRadius(dp(3));d.setStroke(dp(2),stroke);return d;}
    StateListDrawable focusBackground(){StateListDrawable d=new StateListDrawable();d.addState(new int[]{android.R.attr.state_focused},shape(Color.rgb(39,73,83),ACCENT));d.addState(new int[]{android.R.attr.state_pressed},shape(Color.rgb(39,73,83),ACCENT));d.addState(new int[]{},shape(CARD,Color.rgb(42,59,78)));return d;}
    Button button(String text,View.OnClickListener click){Button b=new Button(this);b.setText(text);b.setAllCaps(false);b.setTextSize(13);b.setTextColor(Color.WHITE);b.setBackground(focusBackground());b.setPadding(dp(12),0,dp(12),0);b.setMinHeight(dp(40));b.setMinimumHeight(dp(40));b.setFocusable(true);LinearLayout.LayoutParams p=new LinearLayout.LayoutParams(-2,dp(40));p.setMargins(dp(3),dp(3),dp(3),dp(3));b.setLayoutParams(p);b.setOnClickListener(click);return b;}
    void message(String s){if(!destroyed&&status!=null)status.setText(s);}
    void error(Exception e){message(e instanceof NetSupport.AuthException||e instanceof IllegalArgumentException?e.getMessage():e instanceof java.io.IOException?e.getMessage():"No se pudo completar. Revisá red, credenciales y dirección de video.");}
    void settings(){
        LinearLayout box=new LinearLayout(this);box.setOrientation(LinearLayout.VERTICAL);box.setPadding(dp(22),dp(10),dp(22),dp(10));
        EditText net=input("Red local (por ejemplo 172.16.0)",range,false),user=input("Usuario compartido",username,false),pass=input("Contraseña compartida",password,true);
        box.addView(net);box.addView(user);box.addView(pass);CheckBox sw=new CheckBox(this);sw.setText("Usar decodificación por software");sw.setChecked(software);box.addView(sw);box.addView(label("El acceso se guarda cifrado en este TV.\nRefresco cada 5 minutos y reconexión automática.",12,MUTED));
        AlertDialog dialog=new AlertDialog.Builder(this).setTitle("Acceso a las cámaras").setView(box).setPositiveButton("Aplicar",null).setNegativeButton("Cancelar",null).create();
        dialog.setOnShowListener(d->dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(v->{try{
            range=NetSupport.prefix(net.getText().toString());username=user.getText().toString().trim();password=pass.getText().toString();AccessVault.save(getPreferences(0),username,password);rememberedAccess=true;software=sw.isChecked();getPreferences(0).edit().putString("range",range).putBoolean("software",software).apply();dialog.dismiss();
            for(Tile t:tiles)if(t.profile!=null){t.login=new NetSupport.Login(username,password);t.resume=true;if(t.getVisibility()==View.VISIBLE)play(t);}
            save();message("Acceso guardado cifrado. Las cámaras reintentan automáticamente si se corta la red.");
        }catch(Exception e){Toast.makeText(this,e.getMessage(),Toast.LENGTH_LONG).show();}}));dialog.show();
    }
    EditText input(String hint,String value,boolean secret){EditText e=new EditText(this);e.setSingleLine(true);e.setHint(hint);e.setText(value);e.setTextSize(15);e.setInputType(secret?InputType.TYPE_CLASS_TEXT|InputType.TYPE_TEXT_VARIATION_PASSWORD:InputType.TYPE_CLASS_TEXT|InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD);e.setSelectAllOnFocus(true);return e;}
    void setCameraFullscreen(boolean enabled){
        if(cameraFullscreen==enabled||grid==null)return;cameraFullscreen=enabled;getPreferences(0).edit().putBoolean("fullscreen",enabled).apply();
        header.setVisibility(enabled?View.GONE:View.VISIBLE);scroll.setVisibility(enabled?View.GONE:View.VISIBLE);status.setVisibility(enabled?View.GONE:View.VISIBLE);
        root.setPadding(enabled?0:dp(22),enabled?0:dp(14),enabled?0:dp(22),enabled?0:dp(12));
        if(enabled){tiles[selected].requestFocus();Toast.makeText(this,"Atrás para volver a los controles",Toast.LENGTH_SHORT).show();}else fullscreenButton.requestFocus();
        ui.post(()->arrange());
    }
    @Override public void onWindowFocusChanged(boolean focused){super.onWindowFocusChanged(focused);if(focused)getWindow().getDecorView().setSystemUiVisibility(View.SYSTEM_UI_FLAG_FULLSCREEN|View.SYSTEM_UI_FLAG_HIDE_NAVIGATION|View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY);}
    @Override public boolean onKeyDown(int code,KeyEvent event){if(code==KeyEvent.KEYCODE_F11||code==KeyEvent.KEYCODE_MENU){if(event.getRepeatCount()==0)setCameraFullscreen(!cameraFullscreen);return true;}return super.onKeyDown(code,event);}
    @Override public boolean onKeyUp(int code,KeyEvent event){if(code==KeyEvent.KEYCODE_F11||code==KeyEvent.KEYCODE_MENU)return true;return super.onKeyUp(code,event);}
    void layouts(){new AlertDialog.Builder(this).setTitle("Cantidad de cámaras").setItems(new String[]{"1 cámara","4 cámaras","6 cámaras · 4:3","9 cámaras","16 cámaras"},(d,w)->setCount(new int[]{1,4,6,9,16}[w])).show();}
    void setCount(int n){if(count!=1)lastCount=count;count=n;if(n!=1&&selected>=n)select(0);layoutButton.setText("Vista: "+n);getPreferences(0).edit().putInt("layout",n).putInt("lastCount",lastCount).putInt("selected",selected).apply();arrange();}
    void arrange(){
        if(grid==null||grid.getWidth()==0)return;
        int columns=count==6?3:(int)Math.sqrt(count),rows=count==6?2:columns,gap=dp(cameraFullscreen?1:3);
        int cellWidth=grid.getWidth()/columns,cellHeight=grid.getHeight()/rows;
        if(count==6){int width=Math.max(4,Math.min(cellWidth-2*gap-2*dp(2),(cellHeight-2*gap-2*dp(2)-dp(24))*4/3));width-=width%4;cellWidth=width+2*dp(2)+2*gap;cellHeight=width*3/4+2*dp(2)+dp(24)+2*gap;}
        int left=(grid.getWidth()-cellWidth*columns)/2,top=(grid.getHeight()-cellHeight*rows)/2;
        for(Tile t:tiles){boolean visible=count==1?t.index==selected:t.index<count;boolean was=t.getVisibility()==View.VISIBLE;t.setVisibility(visible?View.VISIBLE:View.GONE);
            if(!visible){if(t.player!=null||t.retiring)stop(t,null);continue;}
            int index=count==1?0:t.index,x=index%columns,y=index/columns;
            FrameLayout.LayoutParams p=new FrameLayout.LayoutParams(Math.max(1,cellWidth-2*gap),Math.max(1,cellHeight-2*gap));p.leftMargin=left+x*cellWidth+gap;p.topMargin=top+y*cellHeight+gap;
            t.setLayoutParams(p);t.caption.setTextSize(count>=9?10:12);t.hint.setTextSize(count>=9?10:13);
            if(!was&&t.profile!=null&&t.login!=null&&t.resume&&foreground)play(t);if(t.player!=null)t.player.updateVideoSurfaces();
        }
    }
    final Runnable watchdog=new Runnable(){public void run(){
        if(destroyed||!foreground)return;
        if(alerts!=null){alerts.sync();alerts.paint();}
        long now=SystemClock.elapsedRealtime();
        for(Tile t:tiles)if(t.reconnect.due(now,t.resume&&t.profile!=null&&t.login!=null&&t.getVisibility()==View.VISIBLE,t.retiring)){play(t);break;}
        ui.postDelayed(this,1000);
    }};
    void select(int i){selected=i;getPreferences(0).edit().putInt("selected",i).apply();target.setText("VISOR "+(i+1)+"  ·  "+(tiles[i].camera==null?"SIN CÁMARA":tiles[i].camera.ip));for(Tile t:tiles)t.setBackground(shape(CARD,t.index==i?ACCENT:Color.rgb(42,59,78)));}
    class Tile extends FrameLayout {
        final int index;final VLCVideoLayout video;final TextView caption,hint;MediaPlayer player;NetSupport.Camera camera;NetSupport.Profile profile;NetSupport.Login login;boolean resume,retiring;int generation;Runnable afterStop;final ReconnectSchedule reconnect=new ReconnectSchedule();long lastTime=-1;
        Tile(int i){super(MainActivity.this);index=i;setPadding(dp(2),dp(2),dp(2),dp(2));setBackground(shape(CARD,i==0?ACCENT:Color.rgb(42,59,78)));setFocusable(true);setClickable(true);setDescendantFocusability(ViewGroup.FOCUS_BLOCK_DESCENDANTS);
            FrameLayout frame=new FrameLayout(MainActivity.this);FrameLayout.LayoutParams fp=new FrameLayout.LayoutParams(-1,-1);fp.topMargin=dp(24);addView(frame,fp);video=new VLCVideoLayout(MainActivity.this);frame.addView(video,new FrameLayout.LayoutParams(-1,-1));hint=label("OK para elegir\nuna cámara",13,MUTED);hint.setGravity(Gravity.CENTER);frame.addView(hint,new FrameLayout.LayoutParams(-1,-1));caption=label(String.format(Locale.ROOT,"%02d  /  SIN VIDEO",i+1),12,Color.WHITE);caption.setPadding(dp(6),0,0,0);caption.setSingleLine(true);caption.setEllipsize(android.text.TextUtils.TruncateAt.END);addView(caption,new FrameLayout.LayoutParams(-1,dp(24)));
            setOnFocusChangeListener((v,focus)->{if(focus)select(index);});setOnClickListener(v->{select(index);if(cameraFullscreen&&profile!=null)setCount(count==1?lastCount:1);else{if(cameraFullscreen)setCameraFullscreen(false);chooseCamera();}});setOnLongClickListener(v->{select(index);setCount(count==1?lastCount:1);return true;});
        }
        @Override public boolean onKeyDown(int code,KeyEvent event){int side=count==6?3:(int)Math.sqrt(count),next=index;switch(code){case KeyEvent.KEYCODE_DPAD_LEFT:if(count>1&&index%side>0)next--;break;case KeyEvent.KEYCODE_DPAD_RIGHT:if(count>1&&index%side<side-1)next++;break;case KeyEvent.KEYCODE_DPAD_UP:if(count>1&&index>=side)next-=side;else{if(!cameraFullscreen)search.requestFocus();return true;}break;case KeyEvent.KEYCODE_DPAD_DOWN:if(count>1&&index+side<count)next+=side;break;default:return super.onKeyDown(code,event);}tiles[next].requestFocus();return true;}
    }
    void scan(){if(!scanCancel.get()){scanCancel.set(true);message("Cancelando búsqueda…");return;}try{range=NetSupport.prefix(range);}catch(Exception e){error(e);return;}
        scanCancel=new AtomicBoolean();final AtomicBoolean token=scanCancel;search.setText("Cancelar");message("Buscando en "+range+".1–254…");
        try{WifiManager wifi=(WifiManager)getApplicationContext().getSystemService(WIFI_SERVICE);if(wifi!=null){multicast=wifi.createMulticastLock("VisorIP-discovery");multicast.setReferenceCounted(false);multicast.acquire();}}catch(Exception ignored){}
        final WifiManager.MulticastLock lock=multicast;final String subnet=range;
        network.execute(()->{try{
            NetSupport.scan(subnet,token,new NetSupport.ScanListener(){public void found(NetSupport.Camera c){ui.post(()->{if(destroyed)return;NetSupport.Camera old=cameras.get(c.ip);if(old!=null){for(int p:c.ports)if(!old.ports.contains(p))old.ports.add(p);for(String e:c.endpoints)if(!old.endpoints.contains(e))old.endpoints.add(e);if(c.kind.equals("ONVIF")||!old.kind.equals("ONVIF")&&c.kind.startsWith("RTSP"))old.kind=c.kind;}else cameras.put(c.ip,c);});}public void progress(int n){if(n%4==0||n==254)ui.post(()->message("Buscando… "+n+"/254 · "+cameras.size()+" dispositivos"));}});
            ui.post(()->message(cameras.isEmpty()?"No se encontraron dispositivos. Revisá acceso a "+subnet+".x":"Búsqueda terminada: "+cameras.size()+" dispositivos. Elegí un visor y presioná OK."));
        }catch(Exception e){ui.post(()->message(token.get()?"Búsqueda cancelada":"No se pudo completar la búsqueda. Revisá la red."));}finally{token.set(true);if(lock!=null&&lock.isHeld())lock.release();ui.post(()->{if(!destroyed)search.setText("Buscar");});}});
    }
    void chooseCamera(){if(resolving){message("Hay una detección de video en curso.");return;}final Tile tile=tiles[selected];List<NetSupport.Camera> list=new ArrayList<>(cameras.values());String[] names=new String[list.size()];for(int i=0;i<names.length;i++)names[i]=list.get(i).toString();
        AlertDialog.Builder b=new AlertDialog.Builder(this).setTitle("Cámara para visor "+(selected+1));if(names.length>0)b.setItems(names,(d,w)->resolve(tile,list.get(w)));else b.setMessage("Todavía no hay dispositivos. Presioná Buscar o ingresá una URL manual.");b.setNegativeButton("Cerrar",null).setNeutralButton("URL manual",(d,w)->manual(tile));if(tile.profile!=null)b.setPositiveButton("Reabrir guardada",(d,w)->{tile.login=new NetSupport.Login(username,password);tile.resume=true;play(tile);});b.show();
    }
    void manual(Tile tile){EditText e=input("rtsp://172.16.0.20:554/…",tile.profile==null?"":tile.profile.url,false);LinearLayout box=new LinearLayout(this);box.setPadding(dp(22),dp(12),dp(22),dp(12));box.addView(e,new LinearLayout.LayoutParams(-1,-2));AlertDialog dialog=new AlertDialog.Builder(this).setTitle("Dirección de video").setView(box).setPositiveButton("Ver",null).setNegativeButton("Cancelar",null).create();dialog.setOnShowListener(d->dialog.getButton(-1).setOnClickListener(v->{try{String url=e.getText().toString().trim();URI u=new URI(url);if(!Arrays.asList("rtsp","http","https").contains(u.getScheme())||u.getHost()==null||u.getUserInfo()!=null)throw new IllegalArgumentException("Usá una URL RTSP/HTTP sin credenciales; cargalas en Acceso / red.");NetSupport.Camera c=cameras.get(u.getHost());if(c==null)c=new NetSupport.Camera(u.getHost());assign(tile,c,new NetSupport.Profile("Manual",url,""));dialog.dismiss();}catch(Exception ex){Toast.makeText(this,ex.getMessage(),Toast.LENGTH_LONG).show();}}));dialog.show();}
    void resolve(Tile tile,NetSupport.Camera cam){resolving=true;resolveCancel=new AtomicBoolean();final AtomicBoolean cancel=resolveCancel;final NetSupport.Login login=new NetSupport.Login(username,password);message("Detectando video de "+cam.ip+"…");network.execute(()->{try{
        List<NetSupport.Profile> p=new NetSupport.Onvif(cam.ip,login).profiles(cam,cancel);if(p.isEmpty())p=NetSupport.rtspProfiles(cam,login,cancel);final List<NetSupport.Profile> found=p;
        ui.post(()->{if(destroyed||cancel.get())return;if(found.isEmpty()){message("No se detectó la ruta. Revisá ONVIF o probá URL manual.");return;}if(found.size()==1){assign(tile,cam,found.get(0));return;}String[] names=new String[found.size()];for(int i=0;i<names.length;i++)names[i]=found.get(i).name;new AlertDialog.Builder(this).setTitle("Perfil de "+cam.ip+" · preferí secundario").setItems(names,(d,w)->assign(tile,cam,found.get(w))).setNegativeButton("Cancelar",null).show();});
    }catch(Exception e){ui.post(()->{if(!cancel.get())error(e);});}finally{ui.post(()->resolving=false);}});}
    void assign(Tile tile,NetSupport.Camera c,NetSupport.Profile p){tile.camera=c;tile.profile=p;tile.login=new NetSupport.Login(username,password);tile.resume=true;save();play(tile);}
    void play(Tile tile){if(destroyed||!foreground||tile.profile==null||tile.login==null||tile.getVisibility()!=View.VISIBLE)return;
        tile.reconnect.begin(SystemClock.elapsedRealtime());tile.lastTime=-1;
        stop(tile,()->{if(destroyed||!foreground||!tile.resume||tile.getVisibility()!=View.VISIBLE)return;try{
            MediaPlayer mp=new MediaPlayer(vlc);tile.player=mp;mp.attachViews(tile.video,null,false,true);mp.setVolume(muted?0:100);tile.hint.setVisibility(View.VISIBLE);tile.hint.setText("Conectando…");tile.caption.setText(String.format(Locale.ROOT,"%02d  /  %s",tile.index+1,tile.camera.ip));
            mp.setEventListener(event->{if(tile.player!=mp||destroyed)return;
                if(event.type==MediaPlayer.Event.TimeChanged){long time=event.getTimeChanged();if(time!=tile.lastTime){tile.lastTime=time;tile.reconnect.advanced(SystemClock.elapsedRealtime());}}
                else if(event.type==MediaPlayer.Event.Vout&&event.getVoutCount()>0){tile.reconnect.advanced(SystemClock.elapsedRealtime());tile.hint.setVisibility(View.GONE);}
                else if(event.type==MediaPlayer.Event.EncounteredError||event.type==MediaPlayer.Event.EndReached){tile.reconnect.failed=true;tile.hint.setText("Sin conexión\nReintento automático en 15 segundos");tile.hint.setVisibility(View.VISIBLE);}
            });
            Media media=new Media(vlc,Uri.parse(NetSupport.loginUrl(tile.profile.url,tile.login)));media.setHWDecoderEnabled(!software,false);media.addOption(":rtsp-tcp");media.addOption(":network-caching=700");media.addOption(":no-video-title-show");mp.setMedia(media);media.release();mp.play();
            ui.postDelayed(()->{if(tile.player==mp&&tile.hint.getVisibility()==View.VISIBLE){tile.hint.setText("Sin imagen\nReconexión automática en curso");}},22000);
        }catch(Exception e){tile.reconnect.failed=true;tile.hint.setVisibility(View.VISIBLE);tile.hint.setText("Sin conexión\nReintento automático en 15 segundos");}});
    }
    void stop(Tile tile,Runnable next){tile.generation++;tile.afterStop=next;if(tile.retiring)return;MediaPlayer old=tile.player;tile.player=null;if(old==null){tile.afterStop=null;if(next!=null)next.run();return;}tile.retiring=true;old.setEventListener(null);cleanupCount++;
        cleanup.execute(()->{try{old.stop();}catch(Exception ignored){}ui.post(()->{try{old.detachViews();old.release();}catch(Exception ignored){}cleanupCount--;tile.retiring=false;Runnable follow=tile.afterStop;tile.afterStop=null;if(follow!=null)follow.run();releaseIfDone();});});
    }
    void save(){try{JSONArray saved=new JSONArray();for(Tile t:tiles){if(t.profile==null)continue;JSONObject o=new JSONObject();o.put("slot",t.index);o.put("resume",t.resume);o.put("ip",t.camera.ip);o.put("url",NetSupport.clean(t.profile.url));o.put("name",t.profile.name);o.put("token",t.profile.token);o.put("device",t.profile.device);o.put("ports",new JSONArray(t.camera.ports));o.put("endpoints",new JSONArray(t.camera.endpoints));saved.put(o);}getPreferences(0).edit().putString("tiles",saved.toString()).apply();}catch(Exception e){message("No se pudieron guardar las direcciones.");}}
    void restore(){try{JSONArray saved=new JSONArray(getPreferences(0).getString("tiles","[]"));for(int i=0;i<saved.length();i++){JSONObject o=saved.getJSONObject(i);int slot=o.getInt("slot");if(slot<0||slot>=16)continue;Tile t=tiles[slot];t.camera=new NetSupport.Camera(o.getString("ip"));JSONArray p=o.optJSONArray("ports"),e=o.optJSONArray("endpoints");if(p!=null)for(int j=0;j<p.length();j++)t.camera.ports.add(p.getInt(j));if(e!=null)for(int j=0;j<e.length();j++)t.camera.endpoints.add(e.getString(j));t.profile=new NetSupport.Profile(o.optString("name","Guardada"),NetSupport.clean(o.getString("url")),o.optString("token",""));t.profile.device=o.optString("device","");t.resume=o.optBoolean("resume",true);if(rememberedAccess)t.login=new NetSupport.Login(username,password);t.caption.setText(String.format(Locale.ROOT,"%02d  /  %s",slot+1,t.camera.ip));t.hint.setText(rememberedAccess?"Guardada · conectando…":"Guardada\nIngresá credenciales en Acceso / red");cameras.put(t.camera.ip,t.camera);}}catch(Exception ignored){}}
    void ptz(){Tile t=tiles[selected];if(t.camera==null||t.login==null){message("Abrí la cámara en este visor antes de usar PTZ.");return;}activePtz=new PtzDialog(t);activePtz.start();}
    class PtzDialog {
        final NetSupport.Camera camera;final NetSupport.Profile stream;final NetSupport.Onvif client;final AtomicBoolean cancelled=new AtomicBoolean();AtomicBoolean moveToken;NetSupport.PtzDevice device;NetSupport.Profile profile;NetSupport.Options options;boolean busy;
        final List<Button> controls=new ArrayList<>();final TextView info;final LinearLayout root;AlertDialog dialog;Spinner selector;
        PtzDialog(Tile t){camera=t.camera;stream=t.profile;client=new NetSupport.Onvif(camera.ip,t.login);root=new LinearLayout(MainActivity.this);root.setPadding(dp(20),dp(10),dp(20),dp(10));root.setOrientation(LinearLayout.VERTICAL);info=label("Consultando ONVIF PTZ…",14,MUTED);root.addView(info,new LinearLayout.LayoutParams(-1,dp(65)));}
        void start(){ptzCancel=cancelled;dialog=new AlertDialog.Builder(MainActivity.this).setTitle("PTZ · "+camera.ip).setView(root).setNegativeButton("Cerrar",null).create();dialog.setOnDismissListener(d->{cancelled.set(true);if(moveToken!=null)moveToken.set(true);});dialog.show();
            network.execute(()->{try{device=client.ptz(camera,stream,cancelled);ui.post(()->{if(cancelled.get()||destroyed)return;String[] names=new String[device.profiles.size()];int preferred=0;for(int i=0;i<names.length;i++){names[i]=device.profiles.get(i).name;if(device.profiles.get(i).token.equals(stream.token))preferred=i;}Spinner profiles=new Spinner(MainActivity.this);selector=profiles;profiles.setAdapter(new ArrayAdapter<>(MainActivity.this,android.R.layout.simple_spinner_dropdown_item,names));root.addView(profiles,0);profiles.setOnItemSelectedListener(new AdapterView.OnItemSelectedListener(){public void onNothingSelected(AdapterView<?> p){}public void onItemSelected(AdapterView<?> p,View v,int position,long id){load(device.profiles.get(position));}});profiles.setSelection(preferred);});}catch(Exception e){ui.post(()->info.setText(e.getMessage()));}});
        }
        void load(NetSupport.Profile p){if(busy)return;busy=true;profile=p;options=null;setEnabled(false);network.execute(()->{try{NetSupport.Options o=client.options(device.endpoint,p);ui.post(()->{busy=false;if(cancelled.get())return;options=o;build();});}catch(Exception e){ui.post(()->{busy=false;if(selector!=null)selector.setEnabled(true);info.setText(e.getMessage());});}});}
        void setEnabled(boolean enabled){for(Button b:controls)b.setEnabled(enabled);if(selector!=null)selector.setEnabled(enabled);}
        void build(){if(selector!=null)selector.setEnabled(true);while(root.getChildCount()>2)root.removeViewAt(2);controls.clear();info.setText("Cada clic mueve un paso corto y se detiene.\nElegí una flecha con el control y presioná OK.");
            LinearLayout a=row(),b=row(),c=row();add(a,"Zoom −",0,0,-1);add(a,"↑",0,1,0);add(a,"Zoom +",0,0,1);add(b,"←",-1,0,0);Button stop=button("DETENER",v->{if(moveToken!=null)moveToken.set(true);else if(!busy){busy=true;setEnabled(false);network.execute(()->{try{client.stop(device.endpoint,profile);ui.post(()->info.setText("Orden de parada enviada"));}catch(Exception e){ui.post(()->info.setText(e.getMessage()));}finally{ui.post(()->{busy=false;build();});}});}});b.addView(stop,new LinearLayout.LayoutParams(0,dp(48),1));add(b,"→",1,0,0);add(c,"↓",0,-1,0);stop.requestFocus();}
        LinearLayout row(){LinearLayout r=new LinearLayout(MainActivity.this);root.addView(r,new LinearLayout.LayoutParams(-1,dp(52)));return r;}
        void add(LinearLayout row,String name,int x,int y,int z){Button b=button(name,v->pulse(x,y,z));b.setEnabled((x==0||options.pan!=null)&&(y==0||options.tilt!=null)&&(z==0||options.zoom!=null));controls.add(b);row.addView(b,new LinearLayout.LayoutParams(0,dp(48),1));}
        void pulse(int x,int y,int z){if(busy||cancelled.get()||options==null)return;busy=true;setEnabled(false);moveToken=new AtomicBoolean();final AtomicBoolean token=moveToken;final NetSupport.Profile p=profile;final NetSupport.Options o=options;
            network.execute(()->{try{client.pulse(device.endpoint,p,o,x,y,z,token);ui.post(()->info.setText("Movimiento terminado"));}catch(Exception e){ui.post(()->info.setText(token.get()?"Detenido":e.getMessage()));}finally{ui.post(()->{busy=false;moveToken=null;if(!cancelled.get())build();});}});
        }
    }
    void help(){new AlertDialog.Builder(this).setTitle("Visor IP TV · 2.3").setMessage("1. Acceso / red: ingresá usuario y contraseña compartidos.\n2. Buscar: detecta dispositivos en la red local.\n3. Vista: elegí 1, 4, 6, 9 o 16 cámaras.\n4. Elegí un visor con las flechas y presioná OK para asignar una cámara.\n5. PTZ: mueve la cámara de ese visor si admite ONVIF PTZ.\n6. Alertas: activá avisos de personas/vehículos y sonido por cámara, según las detecciones que anuncie. Habilitá también esa detección en la app de la cámara. Los avisos funcionan mientras el visor está abierto.\n\nPantalla completa oculta los menús. Atrás recupera los controles. En pantalla completa, OK amplía la cámara seleccionada y otro OK vuelve a la cuadrícula.\n\nMantener OK sobre un visor amplía la imagen. Al reducir la cuadrícula se detienen los videos ocultos para liberar recursos. Al volver, se reabren.\n\nLas direcciones y el acceso cifrado se guardan en este TV. Cada cámara se refresca cada 5 minutos y reintenta automáticamente si se corta el Wi-Fi. La vista de 6 usa áreas de video 4:3, sin recortar la imagen. Para 9/16 cámaras elegí perfiles secundarios. La cantidad de videos simultáneos depende de los decodificadores, la memoria y la red del TV.\n\nMotor: LibVLC 3.7.6, VideoLAN (LGPL 2.1).\nwww.videolan.org · code.videolan.org/videolan/libvlcjni\nCódigo propio bajo licencia MIT, incluido con la APK.").setPositiveButton("Entendido",null).show();}
    @Override public void onBackPressed(){if(cameraFullscreen){setCameraFullscreen(false);return;}new AlertDialog.Builder(this).setTitle("¿Salir de Visor IP TV?").setPositiveButton("Salir",(d,w)->finish()).setNegativeButton("Seguir viendo",null).show();}
    @Override protected void onResume(){super.onResume();foreground=true;if(grid!=null){for(Tile t:tiles)t.reconnect.started=false;ui.removeCallbacks(watchdog);ui.postDelayed(watchdog,1000);}}
    @Override protected void onStop(){super.onStop();foreground=false;if(alerts!=null)alerts.stop();ui.removeCallbacks(watchdog);scanCancel.set(true);resolveCancel.set(true);if(ptzCancel!=null)ptzCancel.set(true);if(activePtz!=null&&activePtz.moveToken!=null)activePtz.moveToken.set(true);if(grid!=null)for(Tile t:tiles)stop(t,null);}
    @Override protected void onDestroy(){destroyed=true;if(alerts!=null)alerts.close();password="";if(grid!=null)for(Tile t:tiles)t.login=null;network.shutdown();releaseIfDone();super.onDestroy();}
    void releaseIfDone(){if(destroyed&&cleanupCount==0){LibVLC engine=vlc;vlc=null;if(engine!=null)cleanup.execute(engine::release);cleanup.shutdown();}}
}
