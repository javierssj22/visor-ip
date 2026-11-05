package com.visorip.tv;

final class ReconnectSchedule {
    static final long REFRESH_MS=300000,RETRY_MS=15000,STALL_MS=30000;
    long attempt,progress;boolean started,failed;
    void begin(long now){started=true;failed=false;attempt=progress=now;}
    void advanced(long now){progress=now;failed=false;}
    boolean due(long now,boolean enabled,boolean busy){return enabled&&!busy&&(!started||now-attempt>=REFRESH_MS||(failed?now-attempt>=RETRY_MS:now-progress>=STALL_MS));}
}
