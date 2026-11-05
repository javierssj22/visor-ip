package com.visorip.tv;
public class RecoveryTests {
    static void check(boolean ok,String text){if(!ok)throw new AssertionError(text);System.out.println("PASS: "+text);}
    public static void main(String[] args){
        ReconnectSchedule s=new ReconnectSchedule();
        check(s.due(0,true,false),"Arranque automático de cámara guardada");
        s.begin(1000);s.failed=true;
        check(!s.due(15999,true,false)&&s.due(16000,true,false),"Error espera 15 segundos antes de reintentar");
        s.begin(16000);check(!s.due(16001,true,false),"Nuevo intento no crea un bucle inmediato");
        s.advanced(315999);check(!s.due(315999,true,false)&&s.due(316000,true,false),"Refresco a los 5 minutos con video activo");
        s.begin(0);check(!s.due(29999,true,false)&&s.due(30000,true,false),"Falta de progreso durante 30 segundos reconecta");
        check(!s.due(400000,false,false),"Detener y cámaras ocultas no reconectan");
        check(!s.due(400000,true,true),"Cierre pendiente no acumula reintentos");
        s.begin(400000);s.failed=true;s.advanced(401000);check(!s.due(415000,true,false),"Recuperación del video limpia el error anterior");
    }
}
