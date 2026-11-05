package com.visorip.tv;
import android.app.AlertDialog;
import android.content.SharedPreferences;
import android.graphics.Color;
import android.media.AudioManager;
import android.media.ToneGenerator;
import android.os.SystemClock;
import android.view.View;
import android.widget.*;
import java.text.SimpleDateFormat;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicBoolean;

final class CameraAlerts {
    static class Options {boolean enabled,people=true,vehicles,sound=true;}
    static class Watch {final AtomicBoolean cancel=new AtomicBoolean();final NetSupport.Login login;Watch(NetSupport.Login l){login=l;}}
    static class Alert {String kind;long until;Alert(String k,long t){kind=k;until=t;}}
    final MainActivity host;final ExecutorService workers=Executors.newFixedThreadPool(16);
    final Map<String,Watch> watches=new HashMap<>();final Map<String,String> status=new HashMap<>();
    final Map<String,Alert> active=new HashMap<>();final List<String> history=new ArrayList<>();
    final Set<Integer> highlighted=new HashSet<>();ToneGenerator tone;long lastSound;
    CameraAlerts(MainActivity h){host=h;}
    Options options(String ip){SharedPreferences p=host.getPreferences(0);String k="alerts_"+ip+"_";Options o=new Options();o.enabled=p.getBoolean(k+"enabled",false);o.people=p.getBoolean(k+"people",true);o.vehicles=p.getBoolean(k+"vehicles",false);o.sound=p.getBoolean(k+"sound",true);return o;}
    void save(String ip,Options o){String k="alerts_"+ip+"_";host.getPreferences(0).edit().putBoolean(k+"enabled",o.enabled).putBoolean(k+"people",o.people).putBoolean(k+"vehicles",o.vehicles).putBoolean(k+"sound",o.sound).apply();sync();paint();}
    void sync(){if(host.destroyed||!host.foreground)return;Map<String,MainActivity.Tile> wanted=new HashMap<>();for(MainActivity.Tile t:host.tiles)if(t.camera!=null&&t.login!=null&&t.resume&&options(t.camera.ip).enabled)wanted.put(t.camera.ip,t);
        for(String ip:new ArrayList<>(watches.keySet())){Watch w=watches.get(ip);MainActivity.Tile t=wanted.get(ip);if(t==null||!t.login.user.equals(w.login.user)||!t.login.password.equals(w.login.password)){w.cancel.set(true);watches.remove(ip);active.remove(ip);}}
        for(Map.Entry<String,MainActivity.Tile> e:wanted.entrySet())if(!watches.containsKey(e.getKey())){Watch watch=new Watch(e.getValue().login);watches.put(e.getKey(),watch);NetSupport.Camera camera=e.getValue().camera;workers.execute(()->watch(camera,watch));}
    }
    void postStatus(String ip,Watch w,String value){host.ui.post(()->{if(!host.destroyed&&watches.get(ip)==w)status.put(ip,value);});}
    void watch(NetSupport.Camera camera,Watch watch){CameraEvents.Filter filter=new CameraEvents.Filter();while(!watch.cancel.get()){
        try{NetSupport.Onvif client=new NetSupport.Onvif(camera.ip,watch.login);postStatus(camera.ip,watch,"Consultando detecciones…");CameraEvents.Support support=CameraEvents.find(client,camera,watch.cancel);
            if(!support.people&&!support.vehicles){postStatus(camera.ip,watch,"La cámara no anuncia personas ni vehículos.");return;}
            postStatus(camera.ip,watch,"Escuchando alertas de la cámara");CameraEvents.watch(client,support,watch.cancel,d->{if(filter.accept(d,SystemClock.elapsedRealtime()))host.ui.post(()->{if(!host.destroyed&&watches.get(camera.ip)==watch&&!watch.cancel.get())detected(camera.ip,d.kind);});});
        }catch(Exception e){if(watch.cancel.get())return;postStatus(camera.ip,watch,e instanceof NetSupport.AuthException?"Acceso ONVIF rechazado. Revisá las credenciales.":"Sin conexión a alertas · reintento en 15 segundos");}
        for(int i=0;i<150&&!watch.cancel.get();i++)try{Thread.sleep(100);}catch(InterruptedException e){Thread.currentThread().interrupt();return;}
    }}
    void detected(String ip,String kind){Options o=options(ip);if(!o.enabled||(kind.equals("Persona")?!o.people:!o.vehicles))return;long now=SystemClock.elapsedRealtime();active.put(ip,new Alert(kind,now+12000));
        String line=new SimpleDateFormat("HH:mm:ss",Locale.ROOT).format(new Date())+" · "+ip+" · "+kind+" detectado/a";history.add(0,line);if(history.size()>200)history.remove(200);host.message(line);Toast.makeText(host,line,Toast.LENGTH_SHORT).show();
        if(o.sound&&now-lastSound>4000){lastSound=now;try{if(tone==null)tone=new ToneGenerator(AudioManager.STREAM_NOTIFICATION,70);tone.startTone(ToneGenerator.TONE_PROP_BEEP2,250);}catch(RuntimeException ignored){}}
        paint();
    }
    void paint(){long now=SystemClock.elapsedRealtime();for(MainActivity.Tile t:host.tiles){Alert a=t.camera==null?null:active.get(t.camera.ip);boolean show=a!=null&&a.until>now&&options(t.camera.ip).enabled;
        if(show){highlighted.add(t.index);t.setBackground(host.shape(MainActivity.CARD,Color.rgb(255,166,50)));t.caption.setText(String.format(Locale.ROOT,"%02d / %s · %s",t.index+1,t.camera.ip,a.kind.toUpperCase(Locale.ROOT)));}
        else if(highlighted.remove(t.index)){t.setBackground(host.shape(MainActivity.CARD,t.index==host.selected?MainActivity.ACCENT:Color.rgb(42,59,78)));t.caption.setText(String.format(Locale.ROOT,"%02d / %s",t.index+1,t.camera==null?"SIN VIDEO":t.camera.ip));}
    }}
    void stop(){for(Watch w:watches.values())w.cancel.set(true);watches.clear();active.clear();paint();}
    void close(){stop();workers.shutdown();if(tone!=null){tone.release();tone=null;}}
    void settings(){MainActivity.Tile tile=host.tiles[host.selected];if(tile.camera==null||tile.login==null){host.message("Abrí la cámara de este visor antes de configurar alertas.");return;}
        final NetSupport.Camera camera=tile.camera;final NetSupport.Login login=tile.login;Options o=options(camera.ip);LinearLayout panel=new LinearLayout(host);panel.setOrientation(LinearLayout.VERTICAL);panel.setPadding(host.dp(20),host.dp(10),host.dp(20),host.dp(10));
        CheckBox enabled=box(panel,"Activar alertas de esta cámara",o.enabled),people=box(panel,"Personas",o.people),vehicles=box(panel,"Vehículos",o.vehicles),sound=box(panel,"Aviso sonoro",o.sound);
        TextView info=host.label("Consultando detecciones disponibles…",13,MainActivity.MUTED);panel.addView(info);StringBuilder recent=new StringBuilder("\nAvisos recientes:\n");int n=0;for(String item:history)if(item.contains(" · "+camera.ip+" · ")){recent.append(item).append('\n');if(++n>=8)break;}panel.addView(host.label(recent.toString(),12,MainActivity.MUTED));
        ScrollView scroll=new ScrollView(host);scroll.addView(panel);AtomicBoolean cancel=new AtomicBoolean();AlertDialog dialog=new AlertDialog.Builder(host).setTitle("Alertas · "+camera.ip).setView(scroll).setPositiveButton("Guardar",(d,w)->{Options v=new Options();v.enabled=enabled.isChecked();v.people=people.isChecked();v.vehicles=vehicles.isChecked();v.sound=sound.isChecked();save(camera.ip,v);}).setNegativeButton("Cancelar",null).create();dialog.setOnDismissListener(d->cancel.set(true));dialog.show();
        host.network.execute(()->{try{CameraEvents.Support support=CameraEvents.find(new NetSupport.Onvif(camera.ip,login),camera,cancel);host.ui.post(()->{if(cancel.get()||host.destroyed)return;people.setEnabled(support.people);vehicles.setEnabled(support.vehicles);if(!support.people){people.setChecked(false);people.setText("Personas · no anunciado por la cámara");}if(!support.vehicles){vehicles.setChecked(false);vehicles.setText("Vehículos · no anunciado por la cámara");}info.setText("Activá también la detección correspondiente en la app de la cámara.\n"+status.getOrDefault(camera.ip,"Los avisos aparecen también en pantalla completa."));});}catch(Exception e){host.ui.post(()->{if(!cancel.get()&&!host.destroyed)info.setText("No se pudo consultar. Podés guardar y se reintentará al conectar.");});}});
    }
    CheckBox box(LinearLayout panel,String text,boolean checked){CheckBox b=new CheckBox(host);b.setText(text);b.setChecked(checked);panel.addView(b);return b;}
}
